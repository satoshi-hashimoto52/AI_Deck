using System;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// Covers the "通信メッセージの直列化／復元" item of §10.1 and FR-068 (explicit rejection of
    /// an incompatible protocol version).
    /// </summary>
    [TestFixture]
    public class MessageCodecTests
    {
        [Test]
        public void RoundTrip_PreservesEveryHeaderField()
        {
            var payload = Messages.Float(0.42f);
            var original = new NetMessage(MessageType.ChannelGain, DeckId.B, 12345u, 1700000000123L, payload);

            var bytes = MessageCodec.Encode(original);
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);

            Assert.That(result.Success, Is.True);
            Assert.That(result.BytesConsumed, Is.EqualTo(bytes.Length));
            Assert.That(result.Message.Type, Is.EqualTo(MessageType.ChannelGain));
            Assert.That(result.Message.Deck, Is.EqualTo(DeckId.B));
            Assert.That(result.Message.Sequence, Is.EqualTo(12345u));
            Assert.That(result.Message.TimestampMs, Is.EqualTo(1700000000123L));
            Assert.That(result.Message.ProtocolVersion, Is.EqualTo(ProtocolInfo.Version));
            Assert.That(Messages.ReadFloat(result.Message), Is.EqualTo(0.42f).Within(1e-6f));
        }

        [Test]
        public void HeaderIsExactlyTheDocumentedSize()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Ping));
            Assert.That(bytes.Length, Is.EqualTo(ProtocolInfo.HeaderSize));
            Assert.That(bytes[0], Is.EqualTo(ProtocolInfo.Magic0));
            Assert.That(bytes[1], Is.EqualTo(ProtocolInfo.Magic1));
        }

        [Test]
        public void MessageWithNoDeck_RoundTripsAsNull()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.RequestLibrary));
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(result.Success, Is.True);
            Assert.That(result.Message.Deck, Is.Null);
        }

        [Test]
        public void AllPayloadShapes_RoundTrip()
        {
            AssertRoundTrip(Messages.Float(-0.75f), m => Messages.ReadFloat(m), -0.75f);
            AssertRoundTrip(Messages.Double(123.456d), m => Messages.ReadDouble(m), 123.456d);
            AssertRoundTrip(Messages.Bool(true), m => Messages.ReadBool(m), true);
            AssertRoundTrip(Messages.Byte(7), m => Messages.ReadByte(m), (byte)7);
            AssertRoundTrip(Messages.Text("トラック名 / track"), m => Messages.ReadText(m), "トラック名 / track");
        }

        private static void AssertRoundTrip<T>(byte[] payload, Func<NetMessage, T> read, T expected)
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Notice, null, 1u, 0L, payload));
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(result.Success, Is.True);
            Assert.That(read(result.Message), Is.EqualTo(expected));
        }

        [Test]
        public void DoublePair_RoundTripsForLoopRegions()
        {
            var bytes = MessageCodec.Encode(
                new NetMessage(MessageType.LoopSetRegion, DeckId.A, 1u, 0L, Messages.DoublePair(12.5d, 18.25d)));
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Messages.ReadDoublePair(result.Message, out var a, out var b);
            Assert.That(a, Is.EqualTo(12.5d));
            Assert.That(b, Is.EqualTo(18.25d));
        }

        [Test]
        public void NonFiniteFloats_AreNeverPutOnTheWire()
        {
            var bytes = MessageCodec.Encode(
                new NetMessage(MessageType.Crossfader, DeckId.A, 1u, 0L, Messages.Float(float.NaN)));
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(float.IsNaN(Messages.ReadFloat(result.Message)), Is.False);
            Assert.That(Messages.ReadFloat(result.Message), Is.EqualTo(0f));
        }

        [Test]
        public void ShortBuffer_IsReportedAsIncompleteNotCorrupt()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Ping));
            var result = MessageCodec.Decode(bytes, 0, bytes.Length - 1);
            Assert.That(result.Error, Is.EqualTo(DecodeError.Incomplete));
            Assert.That(result.BytesConsumed, Is.EqualTo(0));
        }

        [Test]
        public void TruncatedPayload_IsReportedAsIncomplete()
        {
            var bytes = MessageCodec.Encode(
                new NetMessage(MessageType.Notice, null, 1u, 0L, Messages.Text("a long enough notice")));
            var result = MessageCodec.Decode(bytes, 0, ProtocolInfo.HeaderSize + 2);
            Assert.That(result.Error, Is.EqualTo(DecodeError.Incomplete));
        }

        [Test]
        public void BadMagic_IsRejected()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Ping));
            bytes[0] = 0x00;
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(result.Error, Is.EqualTo(DecodeError.BadMagic));
        }

        [Test]
        public void IncompatibleVersion_IsRejectedExplicitlyAndTheFrameIsConsumed()
        {
            var bytes = MessageCodec.Encode(
                new NetMessage(MessageType.Hello, null, 1u, 0L, null, 9999));
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);

            Assert.That(result.Error, Is.EqualTo(DecodeError.UnsupportedVersion));
            Assert.That(result.PeerVersion, Is.EqualTo(9999));
            Assert.That(result.BytesConsumed, Is.EqualTo(bytes.Length),
                "a mismatched peer's frame is discarded whole so the stream does not wedge");
        }

        [Test]
        public void ReservedFlagBits_AreRejected()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Ping));
            bytes[6] = 0x01;
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(result.Error, Is.EqualTo(DecodeError.BadFlags));
        }

        [Test]
        public void UnknownMessageType_DecodesAsUnknownRatherThanThrowing()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Ping));
            bytes[4] = 200;
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(result.Success, Is.True);
            Assert.That(result.Message.Type, Is.EqualTo(MessageType.Unknown));
        }

        [Test]
        public void UnknownDeckByte_FallsBackToDeckA()
        {
            var bytes = MessageCodec.Encode(new NetMessage(MessageType.Play, DeckId.A));
            bytes[5] = 200;
            var result = MessageCodec.Decode(bytes, 0, bytes.Length);
            Assert.That(result.Message.Deck, Is.EqualTo(DeckId.A));
        }

        [Test]
        public void OversizedPayload_IsRefusedAtEncodeTime()
        {
            var payload = new byte[ProtocolInfo.MaxPayloadSize + 1];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                MessageCodec.Encode(new NetMessage(MessageType.Notice, null, 1u, 0L, payload)));
        }

        [Test]
        public void BackToBackFrames_DecodeOneAtATime()
        {
            var first = MessageCodec.Encode(new NetMessage(MessageType.Play, DeckId.A, 1u));
            var second = MessageCodec.Encode(new NetMessage(MessageType.Pause, DeckId.B, 2u));
            var stream = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, stream, 0, first.Length);
            Buffer.BlockCopy(second, 0, stream, first.Length, second.Length);

            var a = MessageCodec.Decode(stream, 0, stream.Length);
            Assert.That(a.Success, Is.True);
            Assert.That(a.Message.Type, Is.EqualTo(MessageType.Play));

            var b = MessageCodec.Decode(stream, a.BytesConsumed, stream.Length - a.BytesConsumed);
            Assert.That(b.Success, Is.True);
            Assert.That(b.Message.Type, Is.EqualTo(MessageType.Pause));
            Assert.That(b.Message.Deck, Is.EqualTo(DeckId.B));
        }

        [Test]
        public void FindNextMagic_ResynchronisesAfterGarbage()
        {
            var frame = MessageCodec.Encode(new NetMessage(MessageType.Play, DeckId.A, 1u));
            var stream = new byte[4 + frame.Length];
            stream[0] = 0xEE;
            stream[1] = 0xEE;
            stream[2] = 0xEE;
            stream[3] = 0xEE;
            Buffer.BlockCopy(frame, 0, stream, 4, frame.Length);

            var offset = MessageCodec.FindNextMagic(stream, 0, stream.Length);
            Assert.That(offset, Is.EqualTo(4));
            Assert.That(MessageCodec.Decode(stream, offset, stream.Length - offset).Success, Is.True);
        }

        [Test]
        public void ByteReader_ReturnsFallbacksInsteadOfThrowingOnAShortPayload()
        {
            var reader = new ByteReader(new byte[] { 1, 2 });
            Assert.That(reader.ReadDouble(7d), Is.EqualTo(7d));
            Assert.That(reader.Failed, Is.True);
            Assert.That(reader.ReadString("fallback"), Is.EqualTo("fallback"));
        }

        [Test]
        public void ChannelClassification_MatchesTheProtocolDocument()
        {
            Assert.That(MessageType.Crossfader.Channel(), Is.EqualTo(MessageChannel.Fast));
            Assert.That(MessageType.JogNudge.Channel(), Is.EqualTo(MessageChannel.Fast));
            Assert.That(MessageType.StateSnapshot.Channel(), Is.EqualTo(MessageChannel.Fast));
            Assert.That(MessageType.LoadTrack.Channel(), Is.EqualTo(MessageChannel.Reliable));

            // Heartbeats are reliable on purpose: liveness must not depend on the lossy
            // channel, or a network that drops UDP between clients would make the session
            // time out and reconnect forever while TCP was perfectly healthy.
            Assert.That(MessageType.Ping.Channel(), Is.EqualTo(MessageChannel.Reliable));
            Assert.That(MessageType.Pong.Channel(), Is.EqualTo(MessageChannel.Reliable));
            Assert.That(MessageType.Play.Channel(), Is.EqualTo(MessageChannel.Reliable));
            Assert.That(MessageType.RecordStart.Channel(), Is.EqualTo(MessageChannel.Reliable));
            Assert.That(MessageType.AllStop.Channel(), Is.EqualTo(MessageChannel.Reliable),
                "the safety stop must never be droppable");
        }

        [Test]
        public void VersionCompatibility_AcceptsOnlyTheSupportedWindow()
        {
            Assert.That(ProtocolInfo.IsCompatible(ProtocolInfo.Version), Is.True);
            Assert.That(ProtocolInfo.IsCompatible((ushort)(ProtocolInfo.Version + 1)), Is.False);
            Assert.That(ProtocolInfo.IsCompatible(0), Is.False);
        }
    }
}
