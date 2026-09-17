namespace AIDeck.Core.Model
{
    /// <summary>The two decks of AI Deck V1. Values are wire-stable — see NETWORK_PROTOCOL.md.</summary>
    public enum DeckId : byte
    {
        A = 0,
        B = 1
    }

    public static class DeckIdExtensions
    {
        public static DeckId Other(this DeckId deck) => deck == DeckId.A ? DeckId.B : DeckId.A;

        public static string ToDisplayName(this DeckId deck) => deck == DeckId.A ? "A" : "B";

        /// <summary>Wire value to <see cref="DeckId"/>. Unknown values fall back to A.</summary>
        public static DeckId FromByte(byte value) => value == 1 ? DeckId.B : DeckId.A;
    }
}
