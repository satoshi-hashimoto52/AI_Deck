using AIDeck.Core.Model;
using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>
    /// Colours and metrics, taken from <c>docs/mockups/ai-dj-controller-mockup.html</c>, which
    /// §5 names as the design basis.
    ///
    /// Both applications share this so the Mac and the iPad look like one product, and so a
    /// change to the mock-up has exactly one place to land.
    /// </summary>
    public static class Theme
    {
        // ---- surfaces ----
        public static readonly Color Background = Hex(0x08090D);
        public static readonly Color Panel = Hex(0x12141C);
        public static readonly Color PanelRaised = Hex(0x191C26);
        public static readonly Color Line = Hex(0x262B39);
        public static readonly Color Knob = Hex(0x39405A);
        public static readonly Color KnobEdge = Hex(0x5A648A);

        // ---- text ----
        public static readonly Color Text = Hex(0xE8ECF4);
        public static readonly Color TextDim = Hex(0x8B93A7);
        public static readonly Color TextOnAccent = Hex(0x05141A);

        // ---- deck accents (§5.1: A is cyan, B is pink) ----
        public static readonly Color DeckA = Hex(0x22D3EE);
        public static readonly Color DeckB = Hex(0xF472B6);
        public static readonly Color DeckASoft = new Color(0.133f, 0.827f, 0.933f, 0.16f);
        public static readonly Color DeckBSoft = new Color(0.957f, 0.447f, 0.714f, 0.16f);

        // ---- status ----
        public static readonly Color Ok = Hex(0x34D399);
        public static readonly Color Warning = Hex(0xFBBF24);
        public static readonly Color Danger = Hex(0xF87171);

        // ---- state overlays (§5.1: pressed, selected, disabled, waiting, error) ----
        public static readonly Color Pressed = Hex(0x2C3242);
        public static readonly Color PressedEdge = Hex(0x46506A);
        public const float DisabledAlpha = 0.32f;

        /// <summary>
        /// Minimum touch target, in reference pixels. §5.1 requires 44 pt; the controller
        /// canvas is scaled so one reference pixel is one point.
        /// </summary>
        public const float TouchSize = 44f;

        public const float Gap = 8f;
        public const float PanelRadiusApprox = 10f;

        public const int FontSizeSmall = 10;
        public const int FontSizeBody = 13;
        public const int FontSizeLabel = 11;
        public const int FontSizeLarge = 18;

        public static Color Accent(DeckId deck) => deck == DeckId.A ? DeckA : DeckB;

        public static Color AccentSoft(DeckId deck) => deck == DeckId.A ? DeckASoft : DeckBSoft;

        /// <summary>Text colour that reads on top of a deck accent fill.</summary>
        public static Color OnAccent(DeckId deck) =>
            deck == DeckId.A ? Hex(0x05141A) : Hex(0x1A0713);

        public static Color WithAlpha(Color color, float alpha) =>
            new Color(color.r, color.g, color.b, alpha);

        private static Color Hex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            1f);
    }
}
