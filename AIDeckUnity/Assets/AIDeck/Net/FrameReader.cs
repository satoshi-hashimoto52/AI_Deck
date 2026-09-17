using System;
using System.Collections.Generic;
using AIDeck.Core.Net;

namespace AIDeck.Net
{
    /// <summary>
    /// Turns a byte stream into frames.
    ///
    /// TCP delivers bytes, not messages: a frame can arrive split across three reads or three
    /// frames can arrive in one. This accumulates into a buffer and hands out whole frames.
    ///
    /// Damage is recoverable. A frame that does not start with the AI Deck magic bytes causes a
    /// scan forward to the next plausible start rather than closing the connection, and a frame
    /// with a bad version or bad flags is consumed and counted. The only unrecoverable case is
    /// a buffer that grows past the largest legal frame without ever containing one, which
    /// means the peer is not speaking this protocol at all.
    /// </summary>
    public sealed class FrameReader
    {
        /// <summary>Counters for the diagnostics overlay; a non-zero value is worth noticing.</summary>
        public long ResyncCount { get; private set; }

        public long RejectedCount { get; private set; }

        /// <summary>The peer version from the last <see cref="DecodeError.UnsupportedVersion"/>.</summary>
        public ushort LastUnsupportedVersion { get; private set; }

        /// <summary>Set when the stream is unusable and the caller should close the connection.</summary>
        public string FatalError { get; private set; }

        private byte[] _buffer = new byte[ProtocolInfo.MaxFrameSize * 2];
        private int _length;

        public int Buffered => _length;

        public bool HasFatalError => FatalError != null;

        /// <summary>
        /// Appends received bytes and appends every complete frame to <paramref name="output"/>.
        /// Returns false when the stream can no longer be interpreted.
        /// </summary>
        public bool Append(byte[] data, int offset, int count, List<NetMessage> output)
        {
            if (HasFatalError)
            {
                return false;
            }

            if (data != null && count > 0)
            {
                EnsureCapacity(_length + count);
                Buffer.BlockCopy(data, offset, _buffer, _length, count);
                _length += count;
            }

            var consumed = 0;
            while (_length - consumed >= ProtocolInfo.HeaderSize)
            {
                var result = MessageCodec.Decode(_buffer, consumed, _length - consumed);

                if (result.Success)
                {
                    output?.Add(result.Message);
                    consumed += result.BytesConsumed;
                    continue;
                }

                switch (result.Error)
                {
                    case DecodeError.Incomplete:
                        // Wait for more bytes.
                        Compact(consumed);
                        return CheckOverflow();

                    case DecodeError.BadMagic:
                    {
                        var next = MessageCodec.FindNextMagic(_buffer, consumed, _length - consumed);
                        ResyncCount++;
                        if (next < 0)
                        {
                            // Nothing plausible left; drop everything but the last byte, which
                            // could still be the first half of a magic pair.
                            consumed = Math.Max(consumed, _length - 1);
                            Compact(consumed);
                            return CheckOverflow();
                        }

                        consumed = next;
                        continue;
                    }

                    case DecodeError.UnsupportedVersion:
                        // FR-068: an incompatible peer is rejected explicitly, and the frame is
                        // discarded whole so the stream does not wedge on it.
                        LastUnsupportedVersion = result.PeerVersion;
                        RejectedCount++;
                        consumed += Math.Max(ProtocolInfo.HeaderSize, result.BytesConsumed);
                        continue;

                    default:
                        RejectedCount++;
                        consumed += Math.Max(ProtocolInfo.HeaderSize, result.BytesConsumed);
                        continue;
                }
            }

            Compact(consumed);
            return CheckOverflow();
        }

        private bool CheckOverflow()
        {
            if (_length <= ProtocolInfo.MaxFrameSize)
            {
                return true;
            }

            // More than one maximal frame is buffered with no frame found in it: the peer is
            // not speaking this protocol.
            FatalError = "The connection sent data AI Deck could not interpret.";
            return false;
        }

        private void Compact(int consumed)
        {
            if (consumed <= 0)
            {
                return;
            }

            var remaining = _length - consumed;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, consumed, _buffer, 0, remaining);
            }

            _length = remaining;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
            {
                return;
            }

            var capacity = _buffer.Length;
            while (capacity < required)
            {
                capacity *= 2;
            }

            Array.Resize(ref _buffer, capacity);
        }

        public void Reset()
        {
            _length = 0;
            FatalError = null;
            ResyncCount = 0;
            RejectedCount = 0;
            LastUnsupportedVersion = 0;
        }
    }
}
