using System;
using System.IO;

namespace AIDeck.Core.Audio
{
    /// <summary>Why a recording stopped.</summary>
    public enum RecorderState
    {
        Idle,
        Recording,
        Stopping,
        Failed
    }

    /// <summary>
    /// Streams the master bus to a 16-bit WAV file (FR-050, FR-053).
    ///
    /// Design notes that the requirements force:
    /// * The audio thread only calls <see cref="Submit"/>, which writes into a lock-free
    ///   ring buffer. File I/O happens on whichever thread calls <see cref="Drain"/>
    ///   (NFR-001: the disk must never stall the mixer).
    /// * The header's two size fields are rewritten every <see cref="HeaderRefreshSeconds"/>,
    ///   so a recording interrupted by a crash or a power loss is still a valid, playable
    ///   WAV up to the last refresh (§9, recoverable partial recording).
    /// * Any I/O failure moves the recorder to <see cref="RecorderState.Failed"/> and stops
    ///   consuming audio, but never propagates an exception into the audio path (FR-055).
    /// </summary>
    public sealed class WavRecorder : IDisposable
    {
        /// <summary>How often the RIFF/data size fields are refreshed on disk.</summary>
        public const double HeaderRefreshSeconds = 5d;

        /// <summary>Ring buffer depth in seconds of audio. Generous, because disk hiccups happen.</summary>
        public const double BufferSeconds = 4d;

        private readonly object _stateLock = new object();
        private AudioRingBuffer _ring;
        private Stream _stream;
        private byte[] _scratchBytes;
        private float[] _scratchFloats;
        private long _dataByteCount;
        private double _secondsSinceHeaderRefresh;
        private bool _ownsStream;

        public RecorderState State { get; private set; } = RecorderState.Idle;

        public int SampleRate { get; private set; }

        public int Channels { get; private set; }

        /// <summary>Path of the file being written, or null when recording to a caller-supplied stream.</summary>
        public string OutputPath { get; private set; }

        /// <summary>Seconds of audio committed to disk plus what is still queued.</summary>
        public double ElapsedSeconds
        {
            get
            {
                var bytesPerFrame = Channels * (WavHeader.BitsPerSample / 8);
                if (bytesPerFrame <= 0 || SampleRate <= 0)
                {
                    return 0d;
                }

                var queued = _ring?.Available ?? 0;
                var frames = _dataByteCount / bytesPerFrame + queued / Math.Max(1, Channels);
                return frames / (double)SampleRate;
            }
        }

        /// <summary>Bytes of PCM written so far, excluding the header.</summary>
        public long DataByteCount => _dataByteCount;

        /// <summary>Samples dropped because the writer could not keep up. Reported, never hidden.</summary>
        public long DroppedSamples => _ring?.OverflowCount ?? 0L;

        /// <summary>User-facing failure reason; empty unless <see cref="State"/> is Failed.</summary>
        public string FailureReason { get; private set; } = string.Empty;

        public bool IsRecording => State == RecorderState.Recording;

        /// <summary>
        /// Starts a recording into <paramref name="path"/>. Returns false and sets
        /// <see cref="FailureReason"/> rather than throwing, so a failed record button
        /// never interrupts playback (FR-055).
        /// </summary>
        public bool Start(string path, int sampleRate, int channels)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Fail("No output path was configured.");
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                if (!Start(stream, sampleRate, channels, true))
                {
                    return false;
                }

                OutputPath = path;
                return true;
            }
            catch (Exception ex)
            {
                Fail(Describe(ex));
                return false;
            }
        }

        /// <summary>Starts a recording into a caller-supplied seekable stream. Used by the tests.</summary>
        public bool Start(Stream stream, int sampleRate, int channels, bool ownsStream = false)
        {
            lock (_stateLock)
            {
                if (State == RecorderState.Recording)
                {
                    Fail("A recording is already in progress.");
                    return false;
                }

                if (stream == null || !stream.CanWrite || !stream.CanSeek)
                {
                    Fail("The output stream is not writable.");
                    return false;
                }

                if (sampleRate <= 0 || channels < 1 || channels > 8)
                {
                    Fail("Unsupported audio format for recording.");
                    return false;
                }

                try
                {
                    SampleRate = sampleRate;
                    Channels = channels;
                    _stream = stream;
                    _ownsStream = ownsStream;
                    _dataByteCount = 0;
                    _secondsSinceHeaderRefresh = 0d;
                    OutputPath = null;

                    var header = WavHeader.Build(sampleRate, channels, 0);
                    _stream.Seek(0, SeekOrigin.Begin);
                    _stream.Write(header, 0, header.Length);
                    _stream.Flush();

                    var capacity = (int)(BufferSeconds * sampleRate * channels);
                    _ring = new AudioRingBuffer(capacity);
                    _scratchFloats = new float[Math.Max(1024, sampleRate * channels / 8)];
                    _scratchBytes = new byte[_scratchFloats.Length * 2];

                    FailureReason = string.Empty;
                    State = RecorderState.Recording;
                    return true;
                }
                catch (Exception ex)
                {
                    Fail(Describe(ex));
                    return false;
                }
            }
        }

        /// <summary>
        /// Audio-thread entry point. Copies the block into the ring buffer and returns
        /// immediately. Never allocates, never locks, never throws.
        /// </summary>
        public void Submit(float[] samples, int count)
        {
            var ring = _ring;
            if (ring == null || State != RecorderState.Recording || samples == null)
            {
                return;
            }

            ring.Write(samples, 0, count);
        }

        /// <summary>
        /// Writer-thread entry point. Moves queued audio to disk and periodically refreshes
        /// the header. Returns the number of sample frames written this call.
        /// </summary>
        public int Drain()
        {
            lock (_stateLock)
            {
                if (State != RecorderState.Recording && State != RecorderState.Stopping)
                {
                    return 0;
                }

                var ring = _ring;
                var stream = _stream;
                if (ring == null || stream == null)
                {
                    return 0;
                }

                var totalSamples = 0;
                try
                {
                    while (true)
                    {
                        var read = ring.Read(_scratchFloats, 0, _scratchFloats.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        var byteCount = WavHeader.WritePcm16(_scratchFloats, read, _scratchBytes, 0);
                        stream.Write(_scratchBytes, 0, byteCount);
                        _dataByteCount += byteCount;
                        totalSamples += read;
                    }

                    if (totalSamples > 0 && SampleRate > 0 && Channels > 0)
                    {
                        _secondsSinceHeaderRefresh += totalSamples / (double)(SampleRate * Channels);
                        if (_secondsSinceHeaderRefresh >= HeaderRefreshSeconds)
                        {
                            _secondsSinceHeaderRefresh = 0d;
                            RefreshHeader(stream);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Fail(Describe(ex));
                    return totalSamples;
                }

                return totalSamples;
            }
        }

        /// <summary>
        /// Finishes the recording: drains the queue, writes the final sizes and closes the
        /// file. Returns the output path on success, or null when the recording failed.
        /// </summary>
        public string Stop()
        {
            lock (_stateLock)
            {
                if (State == RecorderState.Idle)
                {
                    return null;
                }

                if (State == RecorderState.Recording)
                {
                    State = RecorderState.Stopping;
                }
            }

            Drain();

            lock (_stateLock)
            {
                var stream = _stream;
                var path = OutputPath;
                var failed = State == RecorderState.Failed;

                try
                {
                    if (stream != null)
                    {
                        RefreshHeader(stream);
                        stream.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Fail(Describe(ex));
                    failed = true;
                }
                finally
                {
                    if (_ownsStream)
                    {
                        try
                        {
                            stream?.Dispose();
                        }
                        catch (Exception)
                        {
                            // The data is already on disk; a close failure must not mask that.
                        }
                    }

                    _stream = null;
                    _ring = null;
                    if (State != RecorderState.Failed)
                    {
                        State = RecorderState.Idle;
                    }
                }

                return failed ? null : path;
            }
        }

        private void RefreshHeader(Stream stream)
        {
            if (stream == null || !stream.CanSeek)
            {
                return;
            }

            var dataBytes = _dataByteCount > int.MaxValue - WavHeader.HeaderSize
                ? int.MaxValue - WavHeader.HeaderSize
                : (int)_dataByteCount;

            var position = stream.Position;

            stream.Seek(WavHeader.RiffSizeOffset, SeekOrigin.Begin);
            var riff = WavHeader.Int32Bytes(36 + dataBytes);
            stream.Write(riff, 0, riff.Length);

            stream.Seek(WavHeader.DataSizeOffset, SeekOrigin.Begin);
            var data = WavHeader.Int32Bytes(dataBytes);
            stream.Write(data, 0, data.Length);

            stream.Seek(position, SeekOrigin.Begin);
            stream.Flush();
        }

        private void Fail(string reason)
        {
            FailureReason = string.IsNullOrWhiteSpace(reason) ? "Recording failed." : reason;
            State = RecorderState.Failed;
        }

        /// <summary>
        /// Turns an exception into a short user-facing sentence. Exception messages from the
        /// file system can contain the full path; that is fine for the user's own machine but
        /// the diagnostic detail stays out of the broadcast state (NFR-006, NFR-007).
        /// </summary>
        private static string Describe(Exception ex)
        {
            switch (ex)
            {
                case UnauthorizedAccessException _:
                    return "The recording folder is not writable.";
                case DirectoryNotFoundException _:
                    return "The recording folder no longer exists.";
                case IOException _:
                    return "Writing the recording failed (disk full or file in use).";
                default:
                    return "Recording failed: " + ex.GetType().Name;
            }
        }

        /// <summary>Clears a failed state so the user can try again.</summary>
        public void ClearFailure()
        {
            lock (_stateLock)
            {
                if (State == RecorderState.Failed)
                {
                    State = RecorderState.Idle;
                    FailureReason = string.Empty;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                Stop();
            }
            catch (Exception)
            {
                // Dispose must not throw.
            }
        }

        /// <summary>
        /// Builds the default recording file name: AIDeck_YYYYMMDD_HHMMSS.wav.
        /// Deterministic and sortable, and it never contains anything derived from the
        /// user's files (NFR-006).
        /// </summary>
        public static string BuildFileName(DateTime localTime) =>
            $"AIDeck_{localTime:yyyyMMdd_HHmmss}.wav";
    }
}
