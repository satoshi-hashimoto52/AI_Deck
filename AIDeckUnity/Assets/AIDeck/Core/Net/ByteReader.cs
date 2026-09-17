using System;
using System.Text;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// Little-endian binary reader.
    ///
    /// Every read is bounds-checked and returns a caller-supplied fallback when the frame is
    /// short. A malformed packet therefore yields harmless defaults instead of an exception
    /// on the network thread; <see cref="Failed"/> tells the caller the frame was damaged so
    /// it can be counted and dropped rather than acted on.
    /// </summary>
    public sealed class ByteReader
    {
        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _end;
        private int _position;

        public ByteReader(byte[] buffer, int offset = 0, int count = -1)
        {
            _buffer = buffer ?? Array.Empty<byte>();
            _start = offset < 0 ? 0 : offset;
            if (_start > _buffer.Length)
            {
                _start = _buffer.Length;
            }

            var available = _buffer.Length - _start;
            var length = count < 0 || count > available ? available : count;
            _end = _start + length;
            _position = _start;
        }

        /// <summary>True once a read ran past the end of the frame.</summary>
        public bool Failed { get; private set; }

        public int Remaining => _end - _position;

        public int Position => _position - _start;

        private bool Take(int count)
        {
            if (_position + count > _end)
            {
                Failed = true;
                return false;
            }

            return true;
        }

        public byte ReadByte(byte fallback = 0)
        {
            if (!Take(1))
            {
                return fallback;
            }

            return _buffer[_position++];
        }

        public bool ReadBool(bool fallback = false) => ReadByte(fallback ? (byte)1 : (byte)0) != 0;

        public ushort ReadUInt16(ushort fallback = 0)
        {
            if (!Take(2))
            {
                return fallback;
            }

            var value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public uint ReadUInt32(uint fallback = 0)
        {
            if (!Take(4))
            {
                return fallback;
            }

            var value = (uint)(_buffer[_position]
                               | (_buffer[_position + 1] << 8)
                               | (_buffer[_position + 2] << 16)
                               | (_buffer[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt32(int fallback = 0) => unchecked((int)ReadUInt32(unchecked((uint)fallback)));

        public ulong ReadUInt64(ulong fallback = 0)
        {
            if (!Take(8))
            {
                return fallback;
            }

            ulong value = 0;
            for (var i = 0; i < 8; i++)
            {
                value |= (ulong)_buffer[_position + i] << (i * 8);
            }

            _position += 8;
            return value;
        }

        public long ReadInt64(long fallback = 0) => unchecked((long)ReadUInt64(unchecked((ulong)fallback)));

        /// <summary>Reads a float; a non-finite encoded value is replaced by the fallback.</summary>
        public float ReadSingle(float fallback = 0f)
        {
            if (!Take(4))
            {
                return fallback;
            }

            var bits = ReadUInt32();
            var value = BitConverterLE.UInt32ToSingle(bits);
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }

        public double ReadDouble(double fallback = 0d)
        {
            if (!Take(8))
            {
                return fallback;
            }

            var bits = ReadUInt64();
            var value = BitConverterLE.UInt64ToDouble(bits);
            return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
        }

        public string ReadString(string fallback = "")
        {
            var length = ReadUInt16();
            if (length == 0)
            {
                return Failed ? fallback : string.Empty;
            }

            if (!Take(length))
            {
                return fallback;
            }

            try
            {
                var text = Encoding.UTF8.GetString(_buffer, _position, length);
                _position += length;
                return text;
            }
            catch (Exception)
            {
                Failed = true;
                _position += length;
                return fallback;
            }
        }

        public byte[] ReadBlob()
        {
            var length = ReadUInt16();
            if (length == 0 || !Take(length))
            {
                return Array.Empty<byte>();
            }

            var result = new byte[length];
            Buffer.BlockCopy(_buffer, _position, result, 0, length);
            _position += length;
            return result;
        }
    }

    /// <summary>
    /// Endianness-explicit float bit conversion.
    /// <see cref="BitConverter"/> follows the host's endianness; AI Deck's wire format is
    /// always little-endian, so the conversion is done through an explicit union.
    /// </summary>
    internal static class BitConverterLE
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float Single;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint UInt;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct DoubleUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public double Double;
            [System.Runtime.InteropServices.FieldOffset(0)] public ulong ULong;
        }

        public static uint SingleToUInt32(float value) => new FloatUnion { Single = value }.UInt;

        public static float UInt32ToSingle(uint value) => new FloatUnion { UInt = value }.Single;

        public static ulong DoubleToUInt64(double value) => new DoubleUnion { Double = value }.ULong;

        public static double UInt64ToDouble(ulong value) => new DoubleUnion { ULong = value }.Double;
    }
}
