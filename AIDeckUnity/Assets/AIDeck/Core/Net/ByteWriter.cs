using System;
using System.Text;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// Little-endian binary writer over a growable byte buffer.
    ///
    /// A hand-written codec rather than a serializer package: the wire format is tiny, it
    /// must be byte-for-byte stable across builds, and the project may not take on
    /// dependencies of unclear licence.
    /// </summary>
    public sealed class ByteWriter
    {
        private byte[] _buffer;
        private int _length;

        public ByteWriter(int initialCapacity = 256)
        {
            _buffer = new byte[initialCapacity < 16 ? 16 : initialCapacity];
        }

        public int Length => _length;

        public void Reset() => _length = 0;

        private void Ensure(int extra)
        {
            var required = _length + extra;
            if (required <= _buffer.Length)
            {
                return;
            }

            var capacity = _buffer.Length * 2;
            while (capacity < required)
            {
                capacity *= 2;
            }

            Array.Resize(ref _buffer, capacity);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            _buffer[_length++] = (byte)(value & 0xff);
            _buffer[_length++] = (byte)((value >> 8) & 0xff);
        }

        public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

        public void WriteUInt32(uint value)
        {
            Ensure(4);
            _buffer[_length++] = (byte)(value & 0xff);
            _buffer[_length++] = (byte)((value >> 8) & 0xff);
            _buffer[_length++] = (byte)((value >> 16) & 0xff);
            _buffer[_length++] = (byte)((value >> 24) & 0xff);
        }

        public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

        public void WriteUInt64(ulong value)
        {
            Ensure(8);
            for (var i = 0; i < 8; i++)
            {
                _buffer[_length++] = (byte)((value >> (i * 8)) & 0xff);
            }
        }

        /// <summary>
        /// Writes a float. Non-finite values are written as 0: NaN must never travel the
        /// wire, because the receiving end would feed it to an audio parameter.
        /// </summary>
        public void WriteSingle(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0f;
            }

            WriteUInt32(BitConverterLE.SingleToUInt32(value));
        }

        /// <summary>Writes a double. Non-finite values are written as 0, as for floats.</summary>
        public void WriteDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0d;
            }

            WriteUInt64(BitConverterLE.DoubleToUInt64(value));
        }

        /// <summary>UTF-8 string with a 16-bit byte-length prefix. Null is written as length 0.</summary>
        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteUInt16(0);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
            {
                // Truncating on a byte boundary could split a multi-byte character, so cut
                // on characters and re-encode.
                var safeChars = Math.Max(0, value.Length * ushort.MaxValue / bytes.Length - 1);
                bytes = Encoding.UTF8.GetBytes(value.Substring(0, safeChars));
            }

            WriteUInt16((ushort)bytes.Length);
            Ensure(bytes.Length);
            Buffer.BlockCopy(bytes, 0, _buffer, _length, bytes.Length);
            _length += bytes.Length;
        }

        public void WriteBytes(byte[] value, int offset, int count)
        {
            if (value == null || count <= 0)
            {
                return;
            }

            Ensure(count);
            Buffer.BlockCopy(value, offset, _buffer, _length, count);
            _length += count;
        }

        /// <summary>Byte array with a 16-bit length prefix.</summary>
        public void WriteBlob(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                WriteUInt16(0);
                return;
            }

            var count = value.Length > ushort.MaxValue ? ushort.MaxValue : value.Length;
            WriteUInt16((ushort)count);
            WriteBytes(value, 0, count);
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        /// <summary>Exposes the backing buffer to avoid a copy on the send path.</summary>
        public byte[] GetBuffer() => _buffer;
    }
}
