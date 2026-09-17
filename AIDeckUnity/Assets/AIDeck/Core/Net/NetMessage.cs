using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// One protocol frame: a fixed header plus an opaque payload.
    ///
    /// Header layout (little-endian), 22 bytes — see docs/NETWORK_PROTOCOL.md:
    ///   0  u8   magic 'A'
    ///   1  u8   magic 'D'
    ///   2  u16  protocol version
    ///   4  u8   message type
    ///   5  u8   deck (0 = A, 1 = B, 0xFF = not deck scoped)
    ///   6  u16  flags (reserved, must be 0)
    ///   8  u32  sequence number
    ///  12  i64  sender timestamp, Unix milliseconds UTC
    ///  20  u16  payload length
    /// </summary>
    public sealed class NetMessage
    {
        public static readonly byte[] EmptyPayload = Array.Empty<byte>();

        public NetMessage(
            MessageType type,
            DeckId? deck = null,
            uint sequence = 0,
            long timestampMs = 0,
            byte[] payload = null,
            ushort protocolVersion = ProtocolInfo.Version)
        {
            Type = type;
            Deck = deck;
            Sequence = sequence;
            TimestampMs = timestampMs;
            Payload = payload ?? EmptyPayload;
            ProtocolVersion = protocolVersion;
        }

        public ushort ProtocolVersion { get; }
        public MessageType Type { get; }

        /// <summary>Target deck, or null for messages that are not deck scoped.</summary>
        public DeckId? Deck { get; }

        public uint Sequence { get; }

        /// <summary>Sender clock in Unix milliseconds. Diagnostic only — never used to drive audio.</summary>
        public long TimestampMs { get; }

        public byte[] Payload { get; }

        public int PayloadLength => Payload?.Length ?? 0;

        public MessageChannel Channel => Type.Channel();

        /// <summary>Wire byte for the deck field.</summary>
        public byte DeckByte => Deck.HasValue ? (byte)Deck.Value : ProtocolInfo.NoDeck;

        public ByteReader OpenPayload() => new ByteReader(Payload);

        public override string ToString() =>
            $"{Type} deck={(Deck.HasValue ? Deck.Value.ToDisplayName() : "-")} seq={Sequence} len={PayloadLength}";

        /// <summary>Current wall clock in the units the protocol uses.</summary>
        public static long NowMs() =>
            (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
    }
}
