using System.Collections.Generic;
using System.Threading;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// Allocates the sequence numbers a sender stamps onto outgoing messages.
    ///
    /// One counter per (type, deck) pair, matching the key <see cref="SequenceGate"/> uses,
    /// so a burst of crossfader messages cannot make a jog message on the other deck look
    /// stale. Counters start at 1; 0 is reserved for "never sent".
    /// </summary>
    public sealed class SequenceSource
    {
        private readonly Dictionary<int, uint> _counters = new Dictionary<int, uint>();
        private readonly object _lock = new object();
        private int _global;

        /// <summary>Next sequence number for a message class.</summary>
        public uint Next(MessageType type, DeckId? deck)
        {
            var key = ((int)type << 4) | (deck.HasValue ? (int)deck.Value + 1 : 0);
            lock (_lock)
            {
                _counters.TryGetValue(key, out var value);
                unchecked
                {
                    value++;
                }

                if (value == 0)
                {
                    value = 1;
                }

                _counters[key] = value;
                return value;
            }
        }

        /// <summary>A monotonically increasing number that is not scoped to a message class.</summary>
        public uint NextGlobal()
        {
            var value = unchecked((uint)Interlocked.Increment(ref _global));
            return value == 0 ? 1u : value;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _counters.Clear();
            }

            Interlocked.Exchange(ref _global, 0);
        }
    }
}
