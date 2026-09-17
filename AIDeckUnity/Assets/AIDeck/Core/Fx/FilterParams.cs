using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Fx
{
    /// <summary>Which side of the bipolar FILTER knob is engaged.</summary>
    public enum FilterKind : byte
    {
        Bypass = 0,
        LowPass = 1,
        HighPass = 2
    }

    /// <summary>
    /// Maps the single bipolar FILTER knob of the mock-up onto a filter kind and cutoff.
    /// Turning left sweeps a low-pass down from the top of the band; turning right sweeps a
    /// high-pass up from the bottom. A small centre dead zone guarantees a true bypass, so
    /// a knob resting at 0.001 does not quietly colour the sound.
    /// </summary>
    public static class FilterParams
    {
        /// <summary>Knob magnitude below which the filter is bypassed entirely.</summary>
        public const float DeadZone = 0.02f;

        /// <summary>Lowest cutoff the low-pass sweep reaches.</summary>
        public const float MinCutoffHz = 40f;

        /// <summary>Highest cutoff the high-pass sweep reaches.</summary>
        public const float MaxCutoffHz = 12000f;

        /// <summary>Cutoff at which each sweep starts, i.e. effectively out of the way.</summary>
        public const float LowPassTopHz = 20000f;

        public const float HighPassBottomHz = 20f;

        /// <summary>Resonance (Q) used by both sweeps. Kept modest so the filter cannot self-oscillate.</summary>
        public const float Resonance = 0.9f;

        public static FilterKind KindFor(float knob)
        {
            var k = AudioSafety.SanitizeBipolar(knob);
            if (Math.Abs(k) < DeadZone)
            {
                return FilterKind.Bypass;
            }

            return k < 0f ? FilterKind.LowPass : FilterKind.HighPass;
        }

        /// <summary>
        /// Cutoff in Hz for the knob position. Bypass returns <see cref="LowPassTopHz"/>,
        /// a value that is inert for either filter kind.
        /// The sweep is exponential so the knob feels even across the audible band.
        /// </summary>
        public static float CutoffHz(float knob)
        {
            var k = AudioSafety.SanitizeBipolar(knob);
            var magnitude = Math.Abs(k);
            if (magnitude < DeadZone)
            {
                return LowPassTopHz;
            }

            // Re-normalise so the sweep starts at the edge of the dead zone, not at 0.
            var t = (magnitude - DeadZone) / (1f - DeadZone);
            t = AudioSafety.Sanitize01(t);

            if (k < 0f)
            {
                // Low-pass: 20 kHz down to 40 Hz as the knob travels to -1.
                return Exponential(LowPassTopHz, MinCutoffHz, t);
            }

            // High-pass: 20 Hz up to 12 kHz as the knob travels to +1.
            return Exponential(HighPassBottomHz, MaxCutoffHz, t);
        }

        /// <summary>Geometric interpolation, which is how frequency is perceived.</summary>
        private static float Exponential(float from, float to, float t)
        {
            var value = from * (float)Math.Pow(to / (double)from, t);
            return AudioSafety.Sanitize(value, MinCutoffHz, LowPassTopHz, LowPassTopHz);
        }
    }
}
