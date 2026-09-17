using System.Collections.Generic;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// Covers NFR-004: the send queue must be bounded and must not replay a gesture that is
    /// already over. Matches the "高頻度フェーダー入力でキューが増え続けない" integration item.
    /// </summary>
    [TestFixture]
    public class OutboundQueueTests
    {
        [Test]
        public void ContinuousControlsAreCoalescedToTheNewestValue()
        {
            var queue = new OutboundQueue(64);
            for (var i = 1; i <= 500; i++)
            {
                queue.Enqueue(new NetMessage(MessageType.Crossfader, null, (uint)i, 0L, Messages.Float(i / 500f)));
            }

            Assert.That(queue.Count, Is.EqualTo(1), "only the newest crossfader value is worth sending");
            Assert.That(queue.CoalescedCount, Is.EqualTo(499));

            var message = queue.Dequeue();
            Assert.That(message.Sequence, Is.EqualTo(500u));
        }

        [Test]
        public void CoalescingIsScopedPerControlAndPerDeck()
        {
            var queue = new OutboundQueue();
            queue.Enqueue(new NetMessage(MessageType.JogNudge, DeckId.A, 1u));
            queue.Enqueue(new NetMessage(MessageType.JogNudge, DeckId.B, 1u));
            queue.Enqueue(new NetMessage(MessageType.Crossfader, null, 1u));
            Assert.That(queue.Count, Is.EqualTo(3));

            queue.Enqueue(new NetMessage(MessageType.JogNudge, DeckId.A, 2u));
            Assert.That(queue.Count, Is.EqualTo(3));
        }

        [Test]
        public void ReliableMessagesAreNeverCoalesced()
        {
            var queue = new OutboundQueue();
            for (var i = 1; i <= 5; i++)
            {
                queue.Enqueue(new NetMessage(MessageType.Play, DeckId.A, (uint)i));
            }

            Assert.That(queue.Count, Is.EqualTo(5), "a transport command must not be swallowed by a newer one");
        }

        [Test]
        public void ReliableOrderIsPreserved()
        {
            var queue = new OutboundQueue();
            queue.Enqueue(new NetMessage(MessageType.LoadTrack, DeckId.A, 1u));
            queue.Enqueue(new NetMessage(MessageType.Play, DeckId.A, 2u));
            queue.Enqueue(new NetMessage(MessageType.CueSet, DeckId.A, 3u));

            Assert.That(queue.Dequeue().Type, Is.EqualTo(MessageType.LoadTrack));
            Assert.That(queue.Dequeue().Type, Is.EqualTo(MessageType.Play));
            Assert.That(queue.Dequeue().Type, Is.EqualTo(MessageType.CueSet));
            Assert.That(queue.Dequeue(), Is.Null);
        }

        [Test]
        public void AFullReliableBacklogIsReportedRatherThanGrowing()
        {
            var queue = new OutboundQueue(8);
            var accepted = 0;
            for (var i = 0; i < 100; i++)
            {
                if (queue.Enqueue(new NetMessage(MessageType.Play, DeckId.A, (uint)i)))
                {
                    accepted++;
                }
            }

            Assert.That(accepted, Is.EqualTo(8));
            Assert.That(queue.Count, Is.EqualTo(8), "the queue is bounded, not unbounded");
            Assert.That(queue.DroppedCount, Is.EqualTo(92));
        }

        [Test]
        public void ADequeuedControlIsNoLongerCoalescedInto()
        {
            var queue = new OutboundQueue();
            queue.Enqueue(new NetMessage(MessageType.Crossfader, null, 1u));
            var first = queue.Dequeue();
            queue.Enqueue(new NetMessage(MessageType.Crossfader, null, 2u));

            Assert.That(first.Sequence, Is.EqualTo(1u));
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.Dequeue().Sequence, Is.EqualTo(2u));
        }

        [Test]
        public void DrainToMovesMessagesInOrder()
        {
            var queue = new OutboundQueue();
            queue.Enqueue(new NetMessage(MessageType.Play, DeckId.A, 1u));
            queue.Enqueue(new NetMessage(MessageType.Pause, DeckId.A, 2u));

            var drained = new List<NetMessage>();
            Assert.That(queue.DrainTo(drained), Is.EqualTo(2));
            Assert.That(drained[0].Type, Is.EqualTo(MessageType.Play));
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void DrainToRespectsItsLimit()
        {
            var queue = new OutboundQueue();
            for (var i = 0; i < 10; i++)
            {
                queue.Enqueue(new NetMessage(MessageType.Play, DeckId.A, (uint)i));
            }

            var drained = new List<NetMessage>();
            Assert.That(queue.DrainTo(drained, 4), Is.EqualTo(4));
            Assert.That(queue.Count, Is.EqualTo(6));
        }

        [Test]
        public void NullMessagesAreIgnored()
        {
            var queue = new OutboundQueue();
            Assert.That(queue.Enqueue(null), Is.True);
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void ClearEmptiesTheQueueAndTheCoalesceIndex()
        {
            var queue = new OutboundQueue();
            queue.Enqueue(new NetMessage(MessageType.Crossfader, null, 1u));
            queue.Clear();
            queue.Enqueue(new NetMessage(MessageType.Crossfader, null, 2u));
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.Dequeue().Sequence, Is.EqualTo(2u));
        }
    }
}
