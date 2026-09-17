using System.Collections.Generic;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// Drops out-of-order and replayed messages (FR-067).
    ///
    /// The fast channel runs over UDP, so a jog or fader packet can arrive after a newer one
    /// it has already been superseded by. Applying it would jerk the control backwards. The
    /// gate keeps the highest sequence number seen per (message type, deck) pair and rejects
    /// anything not strictly newer.
    ///
    /// Reliable messages are gated on the same key but only against exact replays, because a
    /// TCP stream already delivers them in order and a legitimate command must never be lost.
    /// </summary>
    public sealed class SequenceGate
    {
        /// <summary>
        /// Distance beyond which a sequence number is treated as a fresh start rather than a
        /// stale packet. Covers a controller that reconnected and reset its counter.
        /// </summary>
        public const uint ResyncThreshold = 1u << 20;

        private readonly Dictionary<int, uint> _lastAccepted = new Dictionary<int, uint>();

        /// <summary>Messages rejected as stale since the last reset. Surfaced in diagnostics.</summary>
        public long RejectedCount { get; private set; }

        public long AcceptedCount { get; private set; }

        /// <summary>
        /// Returns true when the message should be applied. Also records the sequence number,
        /// so a caller must not call this twice for the same message.
        /// </summary>
        public bool Accept(MessageType type, DeckId? deck, uint sequence)
        {
            var key = MakeKey(type, deck);
            if (!_lastAccepted.TryGetValue(key, out var last))
            {
                _lastAccepted[key] = sequence;
                AcceptedCount++;
                return true;
            }

            // Unsigned wrap-safe "is newer": the difference read as a signed value is
            // positive for up to 2^31 messages ahead, which no session will ever exceed.
            var delta = unchecked((int)(sequence - last));
            if (delta > 0)
            {
                _lastAccepted[key] = sequence;
                AcceptedCount++;
                return true;
            }

            // A counter that jumped far backwards is a peer that restarted, not a stale
            // packet. Adopt the new baseline rather than ignoring the peer forever.
            var backwards = unchecked(last - sequence);
            if (backwards > ResyncThreshold)
            {
                _lastAccepted[key] = sequence;
                AcceptedCount++;
                return true;
            }

            RejectedCount++;
            return false;
        }

        public bool Accept(NetMessage message) =>
            message != null && Accept(message.Type, message.Deck, message.Sequence);

        /// <summary>Last accepted sequence for a key, or 0 when the key has not been seen.</summary>
        public uint LastAccepted(MessageType type, DeckId? deck) =>
            _lastAccepted.TryGetValue(MakeKey(type, deck), out var value) ? value : 0u;

        /// <summary>
        /// Forgets all history. Called when a session ends so that a new controller starting
        /// its counter at 1 is not rejected against the previous controller's numbers.
        /// </summary>
        public void Reset()
        {
            _lastAccepted.Clear();
            RejectedCount = 0;
            AcceptedCount = 0;
        }

        private static int MakeKey(MessageType type, DeckId? deck)
        {
            var deckPart = deck.HasValue ? (int)deck.Value + 1 : 0;
            return ((int)type << 4) | deckPart;
        }
    }
}
