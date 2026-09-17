using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Analysis
{
    /// <summary>
    /// A downsampled min/max envelope of a track, used for the scrolling waveform of §5.2.
    ///
    /// Stored as two parallel byte arrays (0..255) rather than floats: a three minute track
    /// at the default resolution is a few hundred kB, which is small enough to cache on disk
    /// and to stream to the iPad without a dedicated compression step.
    /// </summary>
    public sealed class WaveformData
    {
        /// <summary>Envelope buckets per second of audio. ~86 px/s is enough for a readable scroll.</summary>
        public const int DefaultBucketsPerSecond = 86;

        public const int MaxBuckets = 2_000_000;

        public WaveformData(byte[] peaks, byte[] rms, double durationSeconds, int bucketsPerSecond)
        {
            Peaks = peaks ?? System.Array.Empty<byte>();
            Rms = rms ?? System.Array.Empty<byte>();
            if (Rms.Length != Peaks.Length)
            {
                Rms = new byte[Peaks.Length];
            }

            DurationSeconds = AudioSafety.SanitizeDouble(durationSeconds, 0d, 24d * 60d * 60d, 0d);
            BucketsPerSecond = bucketsPerSecond <= 0 ? DefaultBucketsPerSecond : bucketsPerSecond;
        }

        /// <summary>Absolute peak per bucket, scaled to 0..255.</summary>
        public byte[] Peaks { get; }

        /// <summary>RMS per bucket, scaled to 0..255. Drawn inside the peak envelope.</summary>
        public byte[] Rms { get; }

        public double DurationSeconds { get; }
        public int BucketsPerSecond { get; }
        public int BucketCount => Peaks.Length;
        public bool IsEmpty => Peaks.Length == 0;

        public static WaveformData Empty { get; } = new WaveformData(System.Array.Empty<byte>(), System.Array.Empty<byte>(), 0d, DefaultBucketsPerSecond);

        /// <summary>Bucket index covering the given playback position. Always in range.</summary>
        public int BucketAt(double seconds)
        {
            if (BucketCount == 0)
            {
                return 0;
            }

            if (!AudioSafety.IsFinite(seconds) || seconds <= 0d)
            {
                return 0;
            }

            var index = (int)(seconds * BucketsPerSecond);
            if (index >= BucketCount)
            {
                return BucketCount - 1;
            }

            return index;
        }

        /// <summary>Normalised peak (0..1) at a bucket index. Out-of-range reads return 0.</summary>
        public float PeakAt(int bucket)
        {
            if (bucket < 0 || bucket >= Peaks.Length)
            {
                return 0f;
            }

            return Peaks[bucket] / 255f;
        }

        public float RmsAt(int bucket)
        {
            if (bucket < 0 || bucket >= Rms.Length)
            {
                return 0f;
            }

            return Rms[bucket] / 255f;
        }
    }
}
