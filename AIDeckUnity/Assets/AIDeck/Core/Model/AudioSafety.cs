using System;

namespace AIDeck.Core.Model
{
    /// <summary>
    /// Central guard rails for every value that can reach the audio graph.
    /// NFR-009 and the safety requirements in §9 of the master issue require that
    /// NaN, Infinity and out-of-range values never reach an audio parameter, and
    /// that gain is limited before output. Everything here is pure and side-effect
    /// free so the EditMode suite can pin the behaviour down exactly.
    /// </summary>
    public static class AudioSafety
    {
        /// <summary>Absolute ceiling applied to any sample leaving the master bus.</summary>
        public const float MasterCeiling = 0.98f;

        /// <summary>A sample at or above this magnitude counts as clipping (FR-045).</summary>
        public const float ClipThreshold = 0.999f;

        /// <summary>
        /// Replaces NaN/Infinity with <paramref name="fallback"/> and clamps to
        /// [<paramref name="min"/>, <paramref name="max"/>].
        /// </summary>
        public static float Sanitize(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = fallback;
            }

            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        /// <summary>Sanitize into [0,1] with 0 as the safe fallback.</summary>
        public static float Sanitize01(float value) => Sanitize(value, 0f, 1f, 0f);

        /// <summary>Sanitize into [-1,1] with 0 as the safe fallback (bipolar knobs).</summary>
        public static float SanitizeBipolar(float value) => Sanitize(value, -1f, 1f, 0f);

        public static double SanitizeDouble(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = fallback;
            }

            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        /// <summary>True when the value is safe to hand to an audio parameter.</summary>
        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// Soft-knee limiter. Below <see cref="MasterCeiling"/> the signal is returned
        /// untouched; above it the excess is compressed with tanh so that an overloaded
        /// mix fades into saturation instead of producing the hard-edged crackle that
        /// FR-047 forbids. Output magnitude never exceeds 1.0.
        /// </summary>
        public static float SoftLimit(float sample)
        {
            if (float.IsNaN(sample) || float.IsInfinity(sample))
            {
                return 0f;
            }

            var magnitude = sample < 0f ? -sample : sample;
            if (magnitude <= MasterCeiling)
            {
                return sample;
            }

            var sign = sample < 0f ? -1f : 1f;
            var over = magnitude - MasterCeiling;
            var headroom = 1f - MasterCeiling;
            var compressed = MasterCeiling + headroom * (float)Math.Tanh(over / headroom);
            return sign * compressed;
        }

        /// <summary>Linear amplitude to decibels, floored at <paramref name="floorDb"/>.</summary>
        public static float LinearToDb(float linear, float floorDb = -60f)
        {
            if (!IsFinite(linear) || linear <= 0f)
            {
                return floorDb;
            }

            var db = 20f * (float)Math.Log10(linear);
            return db < floorDb ? floorDb : db;
        }

        /// <summary>Decibels to linear amplitude. -inf and NaN map to silence.</summary>
        public static float DbToLinear(float db)
        {
            if (float.IsNaN(db) || db < -120f)
            {
                return 0f;
            }

            if (float.IsPositiveInfinity(db))
            {
                return 1f;
            }

            return (float)Math.Pow(10d, db / 20d);
        }

        /// <summary>
        /// Per-sample gain smoothing coefficient for a given time constant. Used for the
        /// short fades required on play / pause / load so that no step change in gain
        /// reaches the speakers (§9).
        /// </summary>
        public static float SmoothingCoefficient(float timeConstantSeconds, int sampleRate)
        {
            if (sampleRate <= 0 || !IsFinite(timeConstantSeconds) || timeConstantSeconds <= 0f)
            {
                return 1f;
            }

            var coefficient = 1f - (float)Math.Exp(-1d / (timeConstantSeconds * sampleRate));
            return Sanitize(coefficient, 0f, 1f, 1f);
        }
    }
}
