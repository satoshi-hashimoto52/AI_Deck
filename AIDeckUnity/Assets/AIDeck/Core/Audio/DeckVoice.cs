using System;
using System.Threading;
using AIDeck.Core.Model;

namespace AIDeck.Core.Audio
{
    /// <summary>Decoded PCM for one track, swapped atomically so the audio thread never sees a half-set source.</summary>
    public sealed class VoiceSource
    {
        public VoiceSource(float[] samples, int channels, int sampleRate)
        {
            Samples = samples ?? Array.Empty<float>();
            Channels = channels < 1 ? 1 : channels;
            SampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            FrameCount = Samples.Length / Channels;
        }

        public float[] Samples { get; }
        public int Channels { get; }
        public int SampleRate { get; }
        public long FrameCount { get; }

        public double DurationSeconds => FrameCount / (double)SampleRate;

        public static readonly VoiceSource Empty = new VoiceSource(Array.Empty<float>(), 1, 48000);
    }

    /// <summary>Loop window in source frames. Immutable so it can be published with a single reference write.</summary>
    public sealed class VoiceLoop
    {
        public static readonly VoiceLoop Inactive = new VoiceLoop(false, 0, 0);

        public VoiceLoop(bool active, long inFrame, long outFrame)
        {
            Active = active && outFrame > inFrame;
            InFrame = inFrame < 0 ? 0 : inFrame;
            OutFrame = outFrame;
        }

        public bool Active { get; }
        public long InFrame { get; }
        public long OutFrame { get; }
        public long Length => OutFrame - InFrame;
    }

    /// <summary>
    /// Sample-accurate playback voice for one deck.
    ///
    /// AI Deck renders its own audio rather than driving <c>AudioSource.pitch</c>. The reason
    /// is scratching and BACKSPIN (FR-032, FR-034): those need a signed, continuously variable
    /// rate including reverse, applied per sample, and Unity's pitch control reverses
    /// unreliably and only quantises rate changes to DSP block boundaries. Owning the read
    /// pointer gives exact reverse, exact loop wrapping with no device seek, and — because
    /// this class has no Unity dependency — a playback engine that the EditMode suite can
    /// test with no audio device at all.
    ///
    /// Threading: <see cref="Render"/> is the audio thread and owns <c>_position</c>.
    /// Every other member is called from the main thread and communicates through atomics.
    /// Nothing here allocates once a source is set.
    /// </summary>
    public sealed class DeckVoice
    {
        private VoiceSource _source = VoiceSource.Empty;
        private volatile VoiceLoop _loop = VoiceLoop.Inactive;
        private volatile bool _playing;
        private volatile float _rate = 1f;

        /// <summary>Playhead in source frames. Owned by the audio thread.</summary>
        private double _position;

        /// <summary>Last position published to the main thread, as the bits of a double.</summary>
        private long _publishedPosition;

        private long _pendingSeek;
        private int _hasPendingSeek;

        /// <summary>Set by the audio thread when playback ran past the end of the track.</summary>
        private int _reachedEnd;

        public VoiceSource Source => _source;

        public bool HasSource => _source.FrameCount > 0;

        public bool IsPlaying
        {
            get => _playing;
            set => _playing = value;
        }

        /// <summary>Signed playback rate. Negative plays backwards. Clamped on assignment.</summary>
        public float Rate
        {
            get => _rate;
            set => _rate = AudioSafety.Sanitize(value, -16f, 16f, 1f);
        }

        public double DurationSeconds => _source.DurationSeconds;

        /// <summary>Playhead in seconds, as last published by the audio thread.</summary>
        public double PositionSeconds
        {
            get
            {
                var frames = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _publishedPosition));
                var rate = _source.SampleRate;
                return rate > 0 ? frames / rate : 0d;
            }
        }

        /// <summary>
        /// Replaces the audio. Playback stops first so the audio thread cannot be mid-render
        /// against the outgoing buffer.
        /// </summary>
        public void SetSource(VoiceSource source)
        {
            _playing = false;
            _loop = VoiceLoop.Inactive;
            Volatile.Write(ref _source, source ?? VoiceSource.Empty);
            SeekSeconds(0d);
            ApplyPendingSeek();
            Interlocked.Exchange(ref _reachedEnd, 0);
        }

        public void Clear() => SetSource(VoiceSource.Empty);

        /// <summary>Queues a playhead move. Applied at the start of the next render block.</summary>
        public void SeekSeconds(double seconds)
        {
            var source = _source;
            var frames = AudioSafety.SanitizeDouble(seconds, 0d, source.DurationSeconds, 0d) * source.SampleRate;
            Interlocked.Exchange(ref _pendingSeek, BitConverter.DoubleToInt64Bits(frames));
            Interlocked.Exchange(ref _hasPendingSeek, 1);
        }

        /// <summary>Sets the loop window, in seconds. Pass <c>active: false</c> to clear it.</summary>
        public void SetLoop(bool active, double inSeconds, double outSeconds)
        {
            var source = _source;
            if (!active || source.FrameCount == 0)
            {
                _loop = VoiceLoop.Inactive;
                return;
            }

            var inFrame = (long)(AudioSafety.SanitizeDouble(inSeconds, 0d, source.DurationSeconds, 0d) * source.SampleRate);
            var outFrame = (long)(AudioSafety.SanitizeDouble(outSeconds, 0d, source.DurationSeconds, 0d) * source.SampleRate);
            _loop = new VoiceLoop(true, inFrame, outFrame);
        }

        /// <summary>
        /// True once since the last call if playback ran off the end of the track.
        /// Consuming it resets the flag, so the host raises the end-of-track stop exactly once.
        /// </summary>
        public bool ConsumeReachedEnd() => Interlocked.Exchange(ref _reachedEnd, 0) != 0;

        /// <summary>
        /// Audio thread. Fills <paramref name="buffer"/> with <paramref name="outChannels"/>
        /// interleaved channels at <paramref name="outSampleRate"/>, replacing its contents.
        /// Returns the number of frames that contained audio.
        /// </summary>
        public int Render(float[] buffer, int outChannels, int outSampleRate)
        {
            if (buffer == null || outChannels < 1)
            {
                return 0;
            }

            Array.Clear(buffer, 0, buffer.Length);

            ApplyPendingSeek();

            var source = Volatile.Read(ref _source);
            var frameCount = buffer.Length / outChannels;

            if (!_playing || source.FrameCount == 0 || outSampleRate <= 0)
            {
                PublishPosition();
                return 0;
            }

            // Source frames advanced per output frame. Combines the requested rate with any
            // difference between the file's sample rate and the device's.
            var step = _rate * (source.SampleRate / (double)outSampleRate);
            if (!AudioSafety.IsFinite(step))
            {
                PublishPosition();
                return 0;
            }

            var loop = _loop;
            var samples = source.Samples;
            var sourceChannels = source.Channels;
            var lastFrame = source.FrameCount - 1;
            var rendered = 0;

            for (var frame = 0; frame < frameCount; frame++)
            {
                if (loop.Active)
                {
                    WrapIntoLoop(loop);
                }
                else if (_position > lastFrame)
                {
                    // Off the end. Stop here rather than emitting the final sample repeatedly.
                    _position = lastFrame;
                    _playing = false;
                    Interlocked.Exchange(ref _reachedEnd, 1);
                    break;
                }
                else if (_position < 0d)
                {
                    // Reverse motion ran off the front: park at the start (matches DeckModel).
                    _position = 0d;
                }

                var index = (long)_position;
                if (index < 0)
                {
                    index = 0;
                }
                else if (index > lastFrame)
                {
                    index = lastFrame;
                }

                var fraction = (float)(_position - index);
                var nextIndex = index + 1 > lastFrame ? lastFrame : index + 1;

                var baseA = index * sourceChannels;
                var baseB = nextIndex * sourceChannels;
                var destination = frame * outChannels;

                for (var channel = 0; channel < outChannels; channel++)
                {
                    // Mono sources feed every output channel; wider sources are taken
                    // channel-for-channel and any extra channels are dropped.
                    var sourceChannel = sourceChannels == 1 ? 0 : channel % sourceChannels;
                    var a = samples[baseA + sourceChannel];
                    var b = samples[baseB + sourceChannel];
                    buffer[destination + channel] = a + (b - a) * fraction;
                }

                _position += step;
                rendered++;
            }

            if (!loop.Active)
            {
                // The per-frame guards above keep the *read* index in range, but the final
                // increment can still leave the playhead just outside the track. Clamping here
                // means the position handed to the host is always a position the track has.
                if (_position < 0d)
                {
                    _position = 0d;
                }
                else if (_position > lastFrame)
                {
                    _position = lastFrame;
                }
            }

            PublishPosition();
            return rendered;
        }

        /// <summary>
        /// Folds the playhead into the loop window, in both directions. A scratch running
        /// backwards through the loop start wraps to the end, which is what a real looping
        /// player does and what stops a reverse gesture from silently escaping the loop.
        /// </summary>
        private void WrapIntoLoop(VoiceLoop loop)
        {
            var length = loop.Length;
            if (length <= 0)
            {
                return;
            }

            if (_position >= loop.OutFrame)
            {
                var over = (_position - loop.InFrame) % length;
                if (over < 0d)
                {
                    over += length;
                }

                _position = loop.InFrame + over;
            }
            else if (_position < loop.InFrame)
            {
                // Only wrap backwards if the playhead was inside the loop to begin with;
                // a loop armed ahead of the playhead must not drag it forward.
                if (_position >= loop.InFrame - length)
                {
                    _position = loop.OutFrame - (loop.InFrame - _position);
                }
            }
        }

        private void ApplyPendingSeek()
        {
            if (Interlocked.Exchange(ref _hasPendingSeek, 0) == 0)
            {
                return;
            }

            var frames = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _pendingSeek));
            var source = Volatile.Read(ref _source);
            if (frames < 0d)
            {
                frames = 0d;
            }
            else if (source.FrameCount > 0 && frames > source.FrameCount - 1)
            {
                frames = source.FrameCount - 1;
            }

            _position = frames;
            PublishPosition();
        }

        private void PublishPosition() =>
            Interlocked.Exchange(ref _publishedPosition, BitConverter.DoubleToInt64Bits(_position));
    }
}
