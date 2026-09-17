using System;
using System.Text;

namespace AIDeck.Core.Audio
{
    /// <summary>
    /// Canonical 44-byte RIFF/WAVE header for 16-bit PCM (FR-053).
    /// Built by hand so the recorder can patch the two length fields in place after the
    /// fact — that is what makes a recording that ended in a crash still openable (§9).
    /// </summary>
    public static class WavHeader
    {
        public const int HeaderSize = 44;
        public const int BitsPerSample = 16;

        /// <summary>Byte offset of the RIFF chunk size field.</summary>
        public const int RiffSizeOffset = 4;

        /// <summary>Byte offset of the data chunk size field.</summary>
        public const int DataSizeOffset = 40;

        /// <summary>
        /// Builds a header describing <paramref name="dataByteCount"/> bytes of PCM payload.
        /// </summary>
        public static byte[] Build(int sampleRate, int channels, int dataByteCount)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            if (channels < 1 || channels > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(channels));
            }

            if (dataByteCount < 0)
            {
                dataByteCount = 0;
            }

            var header = new byte[HeaderSize];
            var blockAlign = (short)(channels * BitsPerSample / 8);
            var byteRate = sampleRate * blockAlign;

            WriteAscii(header, 0, "RIFF");
            WriteInt32(header, RiffSizeOffset, 36 + dataByteCount);
            WriteAscii(header, 8, "WAVE");

            WriteAscii(header, 12, "fmt ");
            WriteInt32(header, 16, 16);              // PCM fmt chunk length
            WriteInt16(header, 20, 1);               // format tag: PCM
            WriteInt16(header, 22, (short)channels);
            WriteInt32(header, 24, sampleRate);
            WriteInt32(header, 28, byteRate);
            WriteInt16(header, 32, blockAlign);
            WriteInt16(header, 34, BitsPerSample);

            WriteAscii(header, 36, "data");
            WriteInt32(header, DataSizeOffset, dataByteCount);
            return header;
        }

        /// <summary>Little-endian int32, for patching the size fields of an existing header.</summary>
        public static byte[] Int32Bytes(int value) => new[]
        {
            (byte)(value & 0xff),
            (byte)((value >> 8) & 0xff),
            (byte)((value >> 16) & 0xff),
            (byte)((value >> 24) & 0xff)
        };

        /// <summary>
        /// Converts float samples in [-1,1] to little-endian 16-bit PCM.
        /// Values outside the range are clamped, and non-finite samples become silence —
        /// a recording must never contain the fault that produced it.
        /// </summary>
        public static int WritePcm16(float[] samples, int count, byte[] destination, int destinationOffset)
        {
            if (samples == null || destination == null)
            {
                return 0;
            }

            var limit = Math.Min(count, samples.Length);
            var written = 0;
            for (var i = 0; i < limit; i++)
            {
                var sample = samples[i];
                if (float.IsNaN(sample) || float.IsInfinity(sample))
                {
                    sample = 0f;
                }

                if (sample > 1f)
                {
                    sample = 1f;
                }
                else if (sample < -1f)
                {
                    sample = -1f;
                }

                // 32767 rather than 32768 keeps +1.0 and -1.0 symmetric and in range.
                var value = (short)Math.Round(sample * 32767f);
                var offset = destinationOffset + written;
                if (offset + 1 >= destination.Length)
                {
                    break;
                }

                destination[offset] = (byte)(value & 0xff);
                destination[offset + 1] = (byte)((value >> 8) & 0xff);
                written += 2;
            }

            return written;
        }

        private static void WriteAscii(byte[] buffer, int offset, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xff);
            buffer[offset + 1] = (byte)((value >> 8) & 0xff);
            buffer[offset + 2] = (byte)((value >> 16) & 0xff);
            buffer[offset + 3] = (byte)((value >> 24) & 0xff);
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xff);
            buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        }
    }
}
