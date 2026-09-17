using System.Collections.Generic;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// Bounded send queue that collapses superseded continuous-control messages (NFR-004).
    ///
    /// A finger dragging the crossfader produces a message every input frame. If the network
    /// thread falls behind, queueing all of them would add latency that grows without bound
    /// and would replay a gesture that is already over. Instead, a message on the fast
    /// channel replaces any queued message with the same (type, deck) key — only the newest
    /// value survives — while reliable messages are kept in order and never dropped silently.
    ///
    /// When even the reliable backlog exceeds <see cref="Capacity"/> the queue reports the
    /// overflow instead of growing: the caller treats that as a dead link and disconnects.
    /// </summary>
    public sealed class OutboundQueue
    {
        private readonly LinkedList<NetMessage> _queue = new LinkedList<NetMessage>();
        private readonly Dictionary<int, LinkedListNode<NetMessage>> _supersedable =
            new Dictionary<int, LinkedListNode<NetMessage>>();
        private readonly object _lock = new object();

        public OutboundQueue(int capacity = 512)
        {
            Capacity = capacity < 8 ? 8 : capacity;
        }

        public int Capacity { get; }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>Messages replaced by a newer value of the same control.</summary>
        public long CoalescedCount { get; private set; }

        /// <summary>Messages refused because the queue was full. A non-zero value means trouble.</summary>
        public long DroppedCount { get; private set; }

        /// <summary>
        /// Enqueues a message. Returns false only when the queue is full of reliable traffic,
        /// which the caller must treat as a failed link rather than ignore.
        /// </summary>
        public bool Enqueue(NetMessage message)
        {
            if (message == null)
            {
                return true;
            }

            lock (_lock)
            {
                if (message.Type.IsSupersedable())
                {
                    var key = MakeKey(message.Type, message.Deck);
                    if (_supersedable.TryGetValue(key, out var existing) && existing.List != null)
                    {
                        existing.Value = message;
                        CoalescedCount++;
                        return true;
                    }

                    var node = _queue.AddLast(message);
                    _supersedable[key] = node;
                    TrimIfNeeded();
                    return true;
                }

                if (_queue.Count >= Capacity)
                {
                    DroppedCount++;
                    return false;
                }

                _queue.AddLast(message);
                return true;
            }
        }

        /// <summary>Removes and returns the next message, or null when empty.</summary>
        public NetMessage Dequeue()
        {
            lock (_lock)
            {
                var node = _queue.First;
                if (node == null)
                {
                    return null;
                }

                _queue.RemoveFirst();
                var message = node.Value;
                if (message.Type.IsSupersedable())
                {
                    var key = MakeKey(message.Type, message.Deck);
                    if (_supersedable.TryGetValue(key, out var tracked) && tracked == node)
                    {
                        _supersedable.Remove(key);
                    }
                }

                return message;
            }
        }

        /// <summary>Drains up to <paramref name="max"/> messages into the supplied list.</summary>
        public int DrainTo(List<NetMessage> destination, int max = int.MaxValue)
        {
            if (destination == null)
            {
                return 0;
            }

            var taken = 0;
            while (taken < max)
            {
                var message = Dequeue();
                if (message == null)
                {
                    break;
                }

                destination.Add(message);
                taken++;
            }

            return taken;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _queue.Clear();
                _supersedable.Clear();
            }
        }

        public void ResetCounters()
        {
            lock (_lock)
            {
                CoalescedCount = 0;
                DroppedCount = 0;
            }
        }

        /// <summary>
        /// Last-resort trim for the fast channel: drop the oldest superseded value. Only
        /// reachable when more distinct fast controls are in flight than the capacity allows.
        /// </summary>
        private void TrimIfNeeded()
        {
            while (_queue.Count > Capacity)
            {
                var node = _queue.First;
                if (node == null)
                {
                    return;
                }

                _queue.RemoveFirst();
                var key = MakeKey(node.Value.Type, node.Value.Deck);
                if (_supersedable.TryGetValue(key, out var tracked) && tracked == node)
                {
                    _supersedable.Remove(key);
                }

                DroppedCount++;
            }
        }

        private static int MakeKey(MessageType type, DeckId? deck) =>
            ((int)type << 4) | (deck.HasValue ? (int)deck.Value + 1 : 0);
    }
}
