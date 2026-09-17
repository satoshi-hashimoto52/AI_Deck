using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>Why a frame could not be decoded.</summary>
    public enum DecodeError
    {
        None = 0,

        /// <summary>Fewer bytes than a header, or the declared payload has not all arrived yet.</summary>
        Incomplete = 1,

        /// <summary>The frame does not start with the AI Deck magic bytes.</summary>
        BadMagic = 2,

        /// <summary>The peer speaks a protocol version this build cannot interpret (FR-068).</summary>
        UnsupportedVersion = 3,

        /// <summary>The declared payload length exceeds <see cref="ProtocolInfo.MaxPayloadSize"/>.</summary>
        PayloadTooLarge = 4,

        /// <summary>Reserved flag bits were set, which means the sender used a feature we do not know.</summary>
        BadFlags = 5
    }

    /// <summary>Result of decoding one frame from a byte stream.</summary>
    public readonly struct DecodeResult
    {
        private DecodeResult(NetMessage message, int consumed, DecodeError error, ushort peerVersion)
        {
            Message = message;
            BytesConsumed = consumed;
            Error = error;
            PeerVersion = peerVersion;
        }

        public NetMessage Message { get; }

        /// <summary>Bytes to advance the stream by. Zero when nothing could be consumed.</summary>
        public int BytesConsumed { get; }

        public DecodeError Error { get; }

        /// <summary>The version the sender claimed; meaningful for <see cref="DecodeError.UnsupportedVersion"/>.</summary>
        public ushort PeerVersion { get; }

        public bool Success => Error == DecodeError.None && Message != null;

        public static DecodeResult Ok(NetMessage message, int consumed) =>
            new DecodeResult(message, consumed, DecodeError.None, message.ProtocolVersion);

        public static DecodeResult Fail(DecodeError error, int consumed = 0, ushort peerVersion = 0) =>
            new DecodeResult(null, consumed, error, peerVersion);
    }

    /// <summary>Encodes and decodes <see cref="NetMessage"/> frames.</summary>
    public static class MessageCodec
    {
        /// <summary>Serialises a message into a new byte array.</summary>
        public static byte[] Encode(NetMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var payloadLength = message.PayloadLength;
            if (payloadLength > ProtocolInfo.MaxPayloadSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    $"payload of {payloadLength} bytes exceeds the {ProtocolInfo.MaxPayloadSize} byte limit");
            }

            var writer = new ByteWriter(ProtocolInfo.HeaderSize + payloadLength);
            writer.WriteByte(ProtocolInfo.Magic0);
            writer.WriteByte(ProtocolInfo.Magic1);
            writer.WriteUInt16(message.ProtocolVersion);
            writer.WriteByte((byte)message.Type);
            writer.WriteByte(message.DeckByte);
            writer.WriteUInt16(0); // flags, reserved
            writer.WriteUInt32(message.Sequence);
            writer.WriteInt64(message.TimestampMs);
            writer.WriteUInt16((ushort)payloadLength);
            if (payloadLength > 0)
            {
                writer.WriteBytes(message.Payload, 0, payloadLength);
            }

            return writer.ToArray();
        }

        /// <summary>
        /// Decodes one frame starting at <paramref name="offset"/>.
        ///
        /// On <see cref="DecodeError.BadMagic"/> the caller should resynchronise; on
        /// <see cref="DecodeError.Incomplete"/> it should wait for more bytes. Every other
        /// error consumes the frame so a single bad packet cannot wedge the stream.
        /// </summary>
        public static DecodeResult Decode(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count < ProtocolInfo.HeaderSize)
            {
                return DecodeResult.Fail(DecodeError.Incomplete);
            }

            if (offset < 0 || offset + count > buffer.Length)
            {
                return DecodeResult.Fail(DecodeError.Incomplete);
            }

            if (buffer[offset] != ProtocolInfo.Magic0 || buffer[offset + 1] != ProtocolInfo.Magic1)
            {
                return DecodeResult.Fail(DecodeError.BadMagic);
            }

            var reader = new ByteReader(buffer, offset, count);
            reader.ReadByte();
            reader.ReadByte();
            var version = reader.ReadUInt16();
            var typeByte = reader.ReadByte();
            var deckByte = reader.ReadByte();
            var flags = reader.ReadUInt16();
            var sequence = reader.ReadUInt32();
            var timestamp = reader.ReadInt64();
            var payloadLength = reader.ReadUInt16();

            if (payloadLength > ProtocolInfo.MaxPayloadSize)
            {
                return DecodeResult.Fail(DecodeError.PayloadTooLarge, ProtocolInfo.HeaderSize, version);
            }

            var frameSize = ProtocolInfo.HeaderSize + payloadLength;
            if (count < frameSize)
            {
                return DecodeResult.Fail(DecodeError.Incomplete);
            }

            // Version is checked only once the whole frame is present, so that the caller
            // can consume and discard the frame rather than resynchronising blindly.
            if (!ProtocolInfo.IsCompatible(version))
            {
                return DecodeResult.Fail(DecodeError.UnsupportedVersion, frameSize, version);
            }

            if (flags != 0)
            {
                return DecodeResult.Fail(DecodeError.BadFlags, frameSize, version);
            }

            var payload = payloadLength > 0 ? new byte[payloadLength] : NetMessage.EmptyPayload;
            if (payloadLength > 0)
            {
                Buffer.BlockCopy(buffer, offset + ProtocolInfo.HeaderSize, payload, 0, payloadLength);
            }

            DeckId? deck = deckByte == ProtocolInfo.NoDeck
                ? (DeckId?)null
                : DeckIdExtensions.FromByte(deckByte);

            var type = Enum.IsDefined(typeof(MessageType), typeByte)
                ? (MessageType)typeByte
                : MessageType.Unknown;

            var message = new NetMessage(type, deck, sequence, timestamp, payload, version);
            return DecodeResult.Ok(message, frameSize);
        }

        /// <summary>
        /// Finds the next plausible frame start after a resynchronisation, returning the
        /// offset of the magic bytes or -1 when none is present in the window.
        /// </summary>
        public static int FindNextMagic(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count < 2)
            {
                return -1;
            }

            var end = Math.Min(buffer.Length, offset + count) - 1;
            for (var i = offset + 1; i < end; i++)
            {
                if (buffer[i] == ProtocolInfo.Magic0 && buffer[i + 1] == ProtocolInfo.Magic1)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
