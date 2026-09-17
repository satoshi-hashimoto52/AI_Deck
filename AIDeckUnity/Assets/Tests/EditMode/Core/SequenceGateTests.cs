using AIDeck.Core.Model;
using AIDeck.Core.Net;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "シーケンス番号による古い操作の破棄" item of §10.1 and FR-067.</summary>
    [TestFixture]
    public class SequenceGateTests
    {
        private SequenceGate _gate;

        [SetUp]
        public void SetUp() => _gate = new SequenceGate();

        [Test]
        public void FirstMessageForAKeyIsAlwaysAccepted()
        {
            Assert.That(_gate.Accept(MessageType.Crossfader, null, 500u), Is.True);
        }

        [Test]
        public void NewerSequenceNumbersAreAccepted()
        {
            Assert.That(_gate.Accept(MessageType.JogNudge, DeckId.A, 1u), Is.True);
            Assert.That(_gate.Accept(MessageType.JogNudge, DeckId.A, 2u), Is.True);
            Assert.That(_gate.Accept(MessageType.JogNudge, DeckId.A, 10u), Is.True);
        }

        [Test]
        public void StaleSequenceNumbersAreDropped()
        {
            _gate.Accept(MessageType.JogNudge, DeckId.A, 10u);
            Assert.That(_gate.Accept(MessageType.JogNudge, DeckId.A, 9u), Is.False,
                "a jog packet that arrived late would jerk the platter backwards");
            Assert.That(_gate.Accept(MessageType.JogNudge, DeckId.A, 1u), Is.False);
            Assert.That(_gate.RejectedCount, Is.EqualTo(2));
        }

        [Test]
        public void ExactReplayIsDropped()
        {
            _gate.Accept(MessageType.Play, DeckId.A, 5u);
            Assert.That(_gate.Accept(MessageType.Play, DeckId.A, 5u), Is.False);
        }

        [Test]
        public void EachDeckHasItsOwnCounter()
        {
            _gate.Accept(MessageType.JogNudge, DeckId.A, 100u);
            Assert.That(_gate.Accept(MessageType.JogNudge, DeckId.B, 1u), Is.True,
                "a busy deck must not starve the other one");
        }

        [Test]
        public void EachMessageTypeHasItsOwnCounter()
        {
            _gate.Accept(MessageType.Crossfader, null, 100u);
            Assert.That(_gate.Accept(MessageType.MasterGain, null, 1u), Is.True);
        }

        [Test]
        public void CounterWrapAroundIsHandled()
        {
            _gate.Accept(MessageType.Crossfader, null, uint.MaxValue - 1);
            Assert.That(_gate.Accept(MessageType.Crossfader, null, uint.MaxValue), Is.True);
            Assert.That(_gate.Accept(MessageType.Crossfader, null, 0u), Is.True, "0 follows uint.MaxValue");
            Assert.That(_gate.Accept(MessageType.Crossfader, null, 1u), Is.True);
        }

        [Test]
        public void APeerThatRestartsItsCounterIsAdoptedRatherThanIgnoredForever()
        {
            _gate.Accept(MessageType.Crossfader, null, 5_000_000u);
            Assert.That(_gate.Accept(MessageType.Crossfader, null, 1u), Is.True,
                "a reconnected controller starts counting from 1 again");
        }

        [Test]
        public void SmallBackwardsJumpsAreStillTreatedAsStale()
        {
            _gate.Accept(MessageType.Crossfader, null, 5_000_000u);
            Assert.That(_gate.Accept(MessageType.Crossfader, null, 4_999_000u), Is.False);
        }

        [Test]
        public void ResetForgetsEveryCounter()
        {
            _gate.Accept(MessageType.Crossfader, null, 100u);
            _gate.Reset();
            Assert.That(_gate.Accept(MessageType.Crossfader, null, 1u), Is.True);
            Assert.That(_gate.RejectedCount, Is.EqualTo(0));
            Assert.That(_gate.AcceptedCount, Is.EqualTo(1));
        }

        [Test]
        public void AcceptOverloadTakesAMessage()
        {
            var message = new NetMessage(MessageType.Filter, DeckId.B, 3u);
            Assert.That(_gate.Accept(message), Is.True);
            Assert.That(_gate.Accept(message), Is.False);
            Assert.That(_gate.Accept((NetMessage)null), Is.False);
        }

        [Test]
        public void SequenceSourceProducesStrictlyIncreasingNumbersPerKey()
        {
            var source = new SequenceSource();
            var previous = 0u;
            for (var i = 0; i < 100; i++)
            {
                var next = source.Next(MessageType.JogNudge, DeckId.A);
                Assert.That(next, Is.GreaterThan(previous));
                previous = next;
            }

            Assert.That(source.Next(MessageType.JogNudge, DeckId.B), Is.EqualTo(1u));
        }

        [Test]
        public void SequenceSourceOutputIsAcceptedByTheGate()
        {
            var source = new SequenceSource();
            for (var i = 0; i < 50; i++)
            {
                Assert.That(_gate.Accept(MessageType.Crossfader, null, source.Next(MessageType.Crossfader, null)), Is.True);
            }
        }

        [Test]
        public void SequenceSourceNeverReturnsZero()
        {
            var source = new SequenceSource();
            Assert.That(source.Next(MessageType.Ping, null), Is.Not.EqualTo(0u));
            Assert.That(source.NextGlobal(), Is.Not.EqualTo(0u));
        }
    }
}
