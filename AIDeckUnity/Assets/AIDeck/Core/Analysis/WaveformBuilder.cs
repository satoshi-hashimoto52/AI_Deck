using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Analysis
{
    /// <summary>
    /// Builds a <see cref="WaveformData"/> envelope from decoded PCM (FR-027).
    ///
    /// The build runs off the main thread on the host (NFR-002); nothing here touches Unity
    /// types or shared state, so it is safe to call from a worker.
    /// </summary>
    public static class WaveformBuilder
    {
        /// <summary>
        /// Reduces interleaved PCM to a peak/RMS envelope.
        /// </summary>
        /// <param name="samples">Interleaved PCM in [-1,1]. Values outside are handled.</param>
        /// <param name="channels">Channel count, >= 1.</param>
        /// <param name="sampleRate">Sample rate in Hz, > 0.</param>
        /// <param name="bucketsPerSecond">Envelope resolution.</param>
        /// <param name="progress">Optional 0..1 progress callback, invoked sparsely.</param>
        public static WaveformData Build(
            float[] samples,
            int channels,
            int sampleRate,
            int bucketsPerSecond = WaveformData.DefaultBucketsPerSecond,
            Action<float> progress = null)
        {
            if (samples == null || samples.Length == 0 || channels < 1 || sampleRate <= 0)
            {
                return WaveformData.Empty;
            }

            var resolution = bucketsPerSecond <= 0 ? WaveformData.DefaultBucketsPerSecond : bucketsPerSecond;
            var frames = samples.Length / channels;
            if (frames <= 0)
            {
                return WaveformData.Empty;
            }

            var duration = frames / (double)sampleRate;
            var bucketCount = (int)Math.Ceiling(duration * resolution);
            if (bucketCount <= 0)
            {
                bucketCount = 1;
            }

            if (bucketCount > WaveformData.MaxBuckets)
            {
                // Extremely long file: drop the resolution rather than allocate unbounded memory.
                resolution = Math.Max(1, (int)(WaveformData.MaxBuckets / Math.Max(1d, duration)));
                bucketCount = Math.Min(WaveformData.MaxBuckets, (int)Math.Ceiling(duration * resolution));
            }

            var peaks = new byte[bucketCount];
            var rms = new byte[bucketCount];
            var framesPerBucket = Math.Max(1, frames / bucketCount);

            var reportEvery = Math.Max(1, bucketCount / 50);

            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                var startFrame = (long)bucket * frames / bucketCount;
                var endFrame = (long)(bucket + 1) * frames / bucketCount;
                if (endFrame <= startFrame)
                {
                    endFrame = Math.Min(frames, startFrame + framesPerBucket);
                }

                var peak = 0f;
                double sum = 0d;
                var counted = 0;

                for (var frame = startFrame; frame < endFrame; frame++)
                {
                    var baseIndex = frame * channels;
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var index = baseIndex + ch;
                        if (index >= samples.Length)
                        {
                            break;
                        }

                        var sample = samples[index];
                        if (float.IsNaN(sample) || float.IsInfinity(sample))
                        {
                            continue;
                        }

                        var magnitude = sample < 0f ? -sample : sample;
                        if (magnitude > peak)
                        {
                            peak = magnitude;
                        }

                        sum += (double)sample * sample;
                        counted++;
                    }
                }

                peaks[bucket] = ToByte(peak);
                rms[bucket] = ToByte(counted > 0 ? (float)Math.Sqrt(sum / counted) : 0f);

                if (progress != null && bucket % reportEvery == 0)
                {
                    progress(bucket / (float)bucketCount);
                }
            }

            progress?.Invoke(1f);
            return new WaveformData(peaks, rms, duration, resolution);
        }

        private static byte ToByte(float normalised)
        {
            var v = AudioSafety.Sanitize01(normalised);
            var scaled = (int)Math.Round(v * 255f);
            if (scaled < 0)
            {
                return 0;
            }

            return scaled > 255 ? (byte)255 : (byte)scaled;
        }
    }
}
