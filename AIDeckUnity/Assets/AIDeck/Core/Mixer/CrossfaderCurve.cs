using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Mixer
{
    /// <summary>Shape of the crossfader response (FR-041).</summary>
    public enum CrossfaderCurveType : byte
    {
        /// <summary>Constant-power. Centre sits at -3 dB per side so a blend holds level.</summary>
        Smooth = 0,

        /// <summary>Linear. Centre sits at -6 dB per side; predictable for slow blends.</summary>
        Linear = 1,

        /// <summary>Sharp cut, for transform and chop work. Full level across most of the throw.</summary>
        Sharp = 2
    }

    /// <summary>
    /// Maps crossfader position to the pair of deck gains.
    /// Position -1 is deck A alone, +1 is deck B alone, 0 is the centre blend.
    /// Every curve is monotonic and returns finite gains in [0,1] for any input including
    /// NaN, which is what keeps a corrupt network packet from producing a gain spike.
    /// </summary>
    public static class CrossfaderCurve
    {
        /// <summary>Half-width of the dead zone at the extremes where a deck is fully cut.</summary>
        private const float SharpCutPoint = 0.2f;

        /// <summary>Gain applied to deck A at the given position.</summary>
        public static float GainA(float position, CrossfaderCurveType curve = CrossfaderCurveType.Smooth) =>
            Evaluate(position, curve, true);

        /// <summary>Gain applied to deck B at the given position.</summary>
        public static float GainB(float position, CrossfaderCurveType curve = CrossfaderCurveType.Smooth) =>
            Evaluate(position, curve, false);

        private static float Evaluate(float position, CrossfaderCurveType curve, bool isA)
        {
            var p = AudioSafety.SanitizeBipolar(position);

            // Normalised 0..1 travel from the A end to the B end.
            var t = (p + 1f) * 0.5f;
            var own = isA ? 1f - t : t;

            switch (curve)
            {
                case CrossfaderCurveType.Linear:
                    return AudioSafety.Sanitize01(own);

                case CrossfaderCurveType.Sharp:
                {
                    // Full level until the fader is within SharpCutPoint of this deck's cut end.
                    if (own >= SharpCutPoint)
                    {
                        return 1f;
                    }

                    return AudioSafety.Sanitize01(own / SharpCutPoint);
                }

                default:
                {
                    // Constant power: gain = sin(own * pi/2) keeps GainA^2 + GainB^2 == 1.
                    var g = (float)Math.Sin(own * Math.PI * 0.5d);
                    return AudioSafety.Sanitize01(g);
                }
            }
        }

        /// <summary>
        /// Convenience for the audio thread: both gains in one call, avoiding a second
        /// sanitise pass per buffer.
        /// </summary>
        public static void Evaluate(float position, CrossfaderCurveType curve, out float gainA, out float gainB)
        {
            gainA = GainA(position, curve);
            gainB = GainB(position, curve);
        }
    }
}
