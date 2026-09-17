using System;
using System.Collections;
using System.IO;
using AIDeck.Core.Audio;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.Networking;

namespace AIDeck.Audio
{
    /// <summary>Outcome of decoding one audio file.</summary>
    public readonly struct TrackLoadResult
    {
        private TrackLoadResult(bool success, string error, VoiceSource source)
        {
            Success = success;
            Error = error ?? string.Empty;
            Source = source ?? VoiceSource.Empty;
        }

        public bool Success { get; }

        /// <summary>Short, user-facing reason. Never contains file contents (NFR-006).</summary>
        public string Error { get; }

        public VoiceSource Source { get; }

        public double DurationSeconds => Source.DurationSeconds;

        public static TrackLoadResult Ok(VoiceSource source) => new TrackLoadResult(true, null, source);

        public static TrackLoadResult Fail(string error) => new TrackLoadResult(false, error, null);
    }

    /// <summary>
    /// Decodes MP3, WAV and AIFF into raw PCM (FR-001…FR-003).
    ///
    /// Decoding goes through <see cref="UnityWebRequestMultimedia"/> against a <c>file://</c>
    /// URL, which on macOS uses the system decoders for all three containers. That keeps the
    /// project free of any third-party audio decoder, which §14 requires where a licence is
    /// unclear.
    ///
    /// The file is opened read-only and is never written, moved or deleted (FR-010, NFR-008).
    /// </summary>
    public static class TrackLoader
    {
        /// <summary>Refuses to decode anything larger than this, so a stray huge file cannot exhaust memory.</summary>
        public const long MaxFileSizeBytes = 512L * 1024L * 1024L;

        public static AudioType ToAudioType(TrackFormat format)
        {
            switch (format)
            {
                case TrackFormat.Mp3: return AudioType.MPEG;
                case TrackFormat.Wav: return AudioType.WAV;
                case TrackFormat.Aiff: return AudioType.AIFF;
                default: return AudioType.UNKNOWN;
            }
        }

        /// <summary>
        /// Decodes <paramref name="path"/> and calls <paramref name="onComplete"/> with the
        /// result. Runs as a coroutine so the main thread keeps rendering while a large file
        /// decodes (NFR-002).
        /// </summary>
        public static IEnumerator Load(string path, Action<TrackLoadResult> onComplete)
        {
            TrackLoadResult failure = default;
            var failed = false;

            if (string.IsNullOrWhiteSpace(path))
            {
                failed = true;
                failure = TrackLoadResult.Fail("No file was selected.");
            }

            var format = TrackFormat.Unknown;
            if (!failed)
            {
                format = TrackInfo.FormatFromPath(path);
                if (format == TrackFormat.Unknown)
                {
                    failed = true;
                    failure = TrackLoadResult.Fail("Only MP3, WAV and AIFF files are supported.");
                }
            }

            if (!failed)
            {
                // File system probing is cheap but can throw on a disconnected volume, so it
                // is guarded here rather than allowed to escape into the coroutine machinery.
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        failed = true;
                        failure = TrackLoadResult.Fail("The file could not be found.");
                    }
                    else if (info.Length <= 0)
                    {
                        failed = true;
                        failure = TrackLoadResult.Fail("The file is empty.");
                    }
                    else if (info.Length > MaxFileSizeBytes)
                    {
                        failed = true;
                        failure = TrackLoadResult.Fail("The file is too large to load.");
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    failure = TrackLoadResult.Fail("The file could not be read: " + ex.GetType().Name);
                }
            }

            if (failed)
            {
                onComplete?.Invoke(failure);
                yield break;
            }

            var url = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            using var request = UnityWebRequestMultimedia.GetAudioClip(url, ToAudioType(format));

            if (request.downloadHandler is DownloadHandlerAudioClip handler)
            {
                // Fully decompressed: GetData needs it for waveform and BPM analysis, and the
                // deck voice reads the samples directly.
                handler.streamAudio = false;
                handler.compressed = false;
            }

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(TrackLoadResult.Fail(DescribeRequestFailure(request)));
                yield break;
            }

            AudioClip clip = null;
            TrackLoadResult result;
            try
            {
                clip = DownloadHandlerAudioClip.GetContent(request);
                result = Extract(clip);
            }
            catch (Exception ex)
            {
                result = TrackLoadResult.Fail("The file could not be decoded: " + ex.GetType().Name);
            }
            finally
            {
                // The clip was only a decode vehicle; the samples now live in a plain array.
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }
            }

            onComplete?.Invoke(result);
        }

        private static TrackLoadResult Extract(AudioClip clip)
        {
            if (clip == null)
            {
                return TrackLoadResult.Fail("The file could not be decoded.");
            }

            if (clip.loadState == AudioDataLoadState.Failed)
            {
                return TrackLoadResult.Fail("The audio data could not be decoded.");
            }

            var samples = clip.samples;
            var channels = clip.channels;
            if (samples <= 0 || channels <= 0)
            {
                return TrackLoadResult.Fail("The file contains no audio.");
            }

            var data = new float[samples * channels];
            if (!clip.GetData(data, 0))
            {
                return TrackLoadResult.Fail("The audio data could not be read.");
            }

            return TrackLoadResult.Ok(new VoiceSource(data, channels, clip.frequency));
        }

        /// <summary>
        /// Turns a request failure into one short sentence. The raw error can contain the full
        /// path, which stays out of anything broadcast or logged (NFR-006).
        /// </summary>
        private static string DescribeRequestFailure(UnityWebRequest request)
        {
            switch (request.result)
            {
                case UnityWebRequest.Result.ConnectionError:
                    return "The file could not be opened.";
                case UnityWebRequest.Result.DataProcessingError:
                    return "The file is damaged or is not a supported audio format.";
                case UnityWebRequest.Result.ProtocolError:
                    return "The file could not be read.";
                default:
                    return "The file could not be loaded.";
            }
        }
    }
}
