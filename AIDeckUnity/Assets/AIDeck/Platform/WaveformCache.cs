using System;
using AIDeck.Core.Analysis;

namespace AIDeck.Platform
{
    /// <summary>
    /// On-disk cache of waveform envelopes (FR-027, "生成・キャッシュ・表示").
    ///
    /// Analysing a five-minute track takes a noticeable moment; re-analysing it every time it
    /// is loaded would make deck loading feel slow for no reason. The envelope is small — a
    /// three-minute track is about 31 kB — so caching it is cheap.
    ///
    /// The format is deliberately trivial: a tiny header plus the two byte arrays. A cache
    /// file that does not match is discarded and rebuilt, so there is no migration path to
    /// maintain.
    /// </summary>
    public static class WaveformCache
    {
        private const int Magic = 0x57464D31; // "WFM1"
        private const int HeaderSize = 20;

        public static bool TrySave(string trackId, WaveformData waveform)
        {
            if (string.IsNullOrEmpty(trackId) || waveform == null || waveform.IsEmpty)
            {
                return false;
            }

            var count = waveform.BucketCount;
            var bytes = new byte[HeaderSize + count * 2];
            WriteInt(bytes, 0, Magic);
            WriteInt(bytes, 4, count);
            WriteInt(bytes, 8, waveform.BucketsPerSecond);
            WriteInt(bytes, 12, (int)Math.Round(waveform.DurationSeconds * 1000d));
            WriteInt(bytes, 16, 0); // reserved

            Buffer.BlockCopy(waveform.Peaks, 0, bytes, HeaderSize, count);
            Buffer.BlockCopy(waveform.Rms, 0, bytes, HeaderSize + count, count);

            AppPaths.EnsureFolder(AppPaths.WaveformCacheFolder);
            return AtomicFile.TryWriteAllBytes(AppPaths.WaveformCacheFile(trackId), bytes, out _);
        }

        /// <summary>Returns null when there is no usable cache entry.</summary>
        public static WaveformData TryLoad(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
            {
                return null;
            }

            var bytes = AtomicFile.TryReadAllBytes(AppPaths.WaveformCacheFile(trackId));
            if (bytes == null || bytes.Length < HeaderSize)
            {
                return null;
            }

            if (ReadInt(bytes, 0) != Magic)
            {
                return null;
            }

            var count = ReadInt(bytes, 4);
            if (count <= 0 || count > WaveformData.MaxBuckets || bytes.Length < HeaderSize + count * 2)
            {
                return null;
            }

            var bucketsPerSecond = ReadInt(bytes, 8);
            var durationMs = ReadInt(bytes, 12);

            var peaks = new byte[count];
            var rms = new byte[count];
            Buffer.BlockCopy(bytes, HeaderSize, peaks, 0, count);
            Buffer.BlockCopy(bytes, HeaderSize + count, rms, 0, count);

            return new WaveformData(peaks, rms, durationMs / 1000d, bucketsPerSecond);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xff);
            buffer[offset + 1] = (byte)((value >> 8) & 0xff);
            buffer[offset + 2] = (byte)((value >> 16) & 0xff);
            buffer[offset + 3] = (byte)((value >> 24) & 0xff);
        }

        private static int ReadInt(byte[] buffer, int offset) =>
            buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
    }
}
