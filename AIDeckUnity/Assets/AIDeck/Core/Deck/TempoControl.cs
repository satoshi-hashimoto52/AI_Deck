using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Deck
{
    /// <summary>
    /// Tempo fader for one deck (FR-028).
    /// V1 deliberately uses a rate change that moves pitch with tempo (§8 of the master
    /// issue allows this). <see cref="ITimeStretch"/> in AUDIO_ENGINE.md is the seam where
    /// a key-lock implementation would be dropped in for V2.
    /// </summary>
    public sealed class TempoControl
    {
        /// <summary>Available fader ranges, in percent either side of nominal.</summary>
        public static readonly float[] AvailableRangesPercent = { 8f, 16f, 50f };

        public const float DefaultRangePercent = 8f;

        /// <summary>Hard floor on playback rate. Below this the resampler output is not useful audio.</summary>
        public const float MinRate = 0.25f;

        /// <summary>Hard ceiling on playback rate.</summary>
        public const float MaxRate = 3f;

        private float _rangePercent = DefaultRangePercent;
        private float _fader;

        /// <summary>
        /// Fader position in [-1, 1]. -1 is the slowest end of the range, +1 the fastest.
        /// Matches the mock-up's vertical tempo slider with centre detent.
        /// </summary>
        public float Fader
        {
            get => _fader;
            set => _fader = AudioSafety.SanitizeBipolar(value);
        }

        /// <summary>Fader range in percent. Unsupported values snap to the nearest supported one.</summary>
        public float RangePercent
        {
            get => _rangePercent;
            set => _rangePercent = NearestRange(value);
        }

        /// <summary>Playback rate multiplier applied by the tempo fader alone (SYNC excluded).</summary>
        public float FaderRate => 1f + _fader * (_rangePercent / 100f);

        /// <summary>
        /// Extra multiplier contributed by SYNC (FR-030). 1.0 when SYNC is off.
        /// Kept separate from the fader so that releasing SYNC restores exactly the
        /// fader's own rate with no drift.
        /// </summary>
        public float SyncRatio { get; private set; } = 1f;

        public bool SyncEnabled { get; private set; }

        /// <summary>Final rate handed to the audio layer. Always finite and inside [MinRate, MaxRate].</summary>
        public float EffectiveRate =>
            AudioSafety.Sanitize(FaderRate * SyncRatio, MinRate, MaxRate, 1f);

        /// <summary>Displayed tempo deviation, e.g. "+3.2%".</summary>
        public float DisplayPercent => (EffectiveRate - 1f) * 100f;

        /// <summary>Resulting tempo for a track whose analysed tempo is <paramref name="baseBpm"/>.</summary>
        public double EffectiveBpm(double baseBpm)
        {
            if (!AudioSafety.IsFinite(baseBpm) || baseBpm <= 0d)
            {
                return 0d;
            }

            return baseBpm * EffectiveRate;
        }

        public void ResetFader() => _fader = 0f;

        /// <summary>
        /// Engages SYNC: matches this deck's effective tempo to <paramref name="targetBpm"/>.
        /// Returns false — and leaves SYNC off — when either tempo is unknown, because a
        /// guessed ratio would produce an audible and unrecoverable tempo jump.
        /// </summary>
        public bool EnableSync(double ownBpm, double targetBpm)
        {
            if (!AudioSafety.IsFinite(ownBpm) || ownBpm <= 0d ||
                !AudioSafety.IsFinite(targetBpm) || targetBpm <= 0d)
            {
                return false;
            }

            var faderRate = FaderRate;
            if (faderRate <= 0f)
            {
                return false;
            }

            // The tempo fader stays where the DJ left it; SYNC supplies only the remainder.
            var desiredRate = targetBpm / ownBpm;
            var ratio = (float)(desiredRate / faderRate);
            var clamped = AudioSafety.Sanitize(ratio, MinRate, MaxRate, 1f);
            if (Math.Abs(clamped - ratio) > 1e-4f)
            {
                // The requested match would need a rate outside the safe window
                // (typically a half/double-time BPM analysis error). Refuse rather than
                // clamp to a tempo that is not actually in sync.
                return false;
            }

            SyncRatio = clamped;
            SyncEnabled = true;
            return true;
        }

        public void DisableSync()
        {
            SyncEnabled = false;
            SyncRatio = 1f;
        }

        private static float NearestRange(float value)
        {
            if (!AudioSafety.IsFinite(value))
            {
                return DefaultRangePercent;
            }

            var best = AvailableRangesPercent[0];
            var bestDelta = Math.Abs(value - best);
            for (var i = 1; i < AvailableRangesPercent.Length; i++)
            {
                var delta = Math.Abs(value - AvailableRangesPercent[i]);
                if (delta < bestDelta)
                {
                    best = AvailableRangesPercent[i];
                    bestDelta = delta;
                }
            }

            return best;
        }
    }
}
