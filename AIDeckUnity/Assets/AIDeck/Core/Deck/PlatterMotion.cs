using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Deck
{
    /// <summary>What the transport must do when a motion effect finishes.</summary>
    public enum MotionOutcome
    {
        /// <summary>Nothing to do.</summary>
        None = 0,

        /// <summary>The platter came to rest — pause the transport (end of BRAKE).</summary>
        Stop = 1,

        /// <summary>Hand control back to the tempo rate, keeping the previous transport state.</summary>
        Release = 2
    }

    /// <summary>
    /// Produces the rate multiplier that sits on top of the tempo rate, covering jog nudge,
    /// simple scratching, BRAKE and BACKSPIN (FR-032, FR-033, FR-034, FR-072).
    ///
    /// The class is pure and frame-rate independent: the audio layer calls <see cref="Tick"/>
    /// with the elapsed time and reads <see cref="Rate"/>. Because it holds no Unity types
    /// the whole gesture vocabulary is covered by EditMode tests, including the safety rule
    /// that any interruption (disconnect, app backgrounding, touch cancel) collapses the
    /// motion back to a defined state — see <see cref="Cancel"/>.
    /// </summary>
    public sealed class PlatterMotion
    {
        /// <summary>Default time for BRAKE to bring the platter to a stop.</summary>
        public const float DefaultBrakeSeconds = 1.2f;

        /// <summary>Default time for BACKSPIN to decay back to rest.</summary>
        public const float DefaultBackspinSeconds = 0.9f;

        /// <summary>Peak reverse rate at the start of a BACKSPIN.</summary>
        public const float BackspinPeakRate = -4f;

        /// <summary>Highest forward rate a scratch gesture may command.</summary>
        public const float MaxScratchRate = 6f;

        /// <summary>Time constant for the jog nudge to decay away once the finger leaves.</summary>
        public const float NudgeDecaySeconds = 0.25f;

        /// <summary>Largest rate offset a jog nudge can add while the deck is playing.</summary>
        public const float MaxNudge = 0.6f;

        /// <summary>Time the rate takes to glide back to 1.0 when a scratch is released.</summary>
        public const float ScratchReleaseSeconds = 0.12f;

        private float _brakeSeconds = DefaultBrakeSeconds;
        private float _backspinSeconds = DefaultBackspinSeconds;
        private float _elapsed;
        private float _rateAtEffectStart = 1f;
        private float _nudge;
        private float _scratchRate = 1f;
        private float _releaseFrom = 1f;
        private bool _releasing;

        public MotionMode Mode { get; private set; } = MotionMode.Normal;

        /// <summary>Multiplier applied to the tempo rate. 1.0 is free running, negative is reverse.</summary>
        public float Rate { get; private set; } = 1f;

        /// <summary>True while an effect owns the platter and the tempo fader is not in charge.</summary>
        public bool IsEffectActive => Mode != MotionMode.Normal;

        /// <summary>Duration of BRAKE. Clamped to a musically useful window.</summary>
        public float BrakeSeconds
        {
            get => _brakeSeconds;
            set => _brakeSeconds = AudioSafety.Sanitize(value, 0.1f, 8f, DefaultBrakeSeconds);
        }

        /// <summary>Duration of BACKSPIN. Clamped to a musically useful window.</summary>
        public float BackspinSeconds
        {
            get => _backspinSeconds;
            set => _backspinSeconds = AudioSafety.Sanitize(value, 0.1f, 8f, DefaultBackspinSeconds);
        }

        /// <summary>
        /// Jog nudge while the deck runs free (FR-072). The offset decays back to zero,
        /// so a flick briefly pushes the track ahead or behind without changing tempo.
        /// </summary>
        public void Nudge(float amount)
        {
            if (Mode != MotionMode.Normal)
            {
                return;
            }

            var delta = AudioSafety.Sanitize(amount, -MaxNudge, MaxNudge, 0f);
            _nudge = AudioSafety.Sanitize(_nudge + delta, -MaxNudge, MaxNudge, 0f);
            _releasing = false;
        }

        /// <summary>Finger down on the platter: the gesture now drives the rate directly.</summary>
        public void BeginScratch()
        {
            Mode = MotionMode.Scratching;
            _scratchRate = 0f;
            _nudge = 0f;
            _releasing = false;
            Rate = 0f;
        }

        /// <summary>
        /// Updates the scratch rate from the gesture. 1.0 means "moving at normal speed
        /// forward"; negative values play backwards.
        /// </summary>
        public void UpdateScratch(float rate)
        {
            if (Mode != MotionMode.Scratching)
            {
                return;
            }

            _scratchRate = AudioSafety.Sanitize(rate, -MaxScratchRate, MaxScratchRate, 0f);
            Rate = _scratchRate;
        }

        /// <summary>Finger up: glide the rate back to free running over a few tens of milliseconds.</summary>
        public void EndScratch()
        {
            if (Mode != MotionMode.Scratching)
            {
                return;
            }

            Mode = MotionMode.Normal;
            _releasing = true;
            _releaseFrom = Rate;
            _elapsed = 0f;
        }

        /// <summary>Starts BRAKE (FR-033). Restarting an in-flight brake is a no-op.</summary>
        public bool StartBrake()
        {
            if (Mode == MotionMode.Braking)
            {
                return false;
            }

            Mode = MotionMode.Braking;
            _rateAtEffectStart = Math.Abs(Rate) < 1e-4f ? 1f : Rate;
            _elapsed = 0f;
            _nudge = 0f;
            _releasing = false;
            return true;
        }

        /// <summary>Starts BACKSPIN (FR-034). Restarting an in-flight backspin is a no-op.</summary>
        public bool StartBackspin()
        {
            if (Mode == MotionMode.Backspin)
            {
                return false;
            }

            Mode = MotionMode.Backspin;
            _rateAtEffectStart = Rate;
            _elapsed = 0f;
            _nudge = 0f;
            _releasing = false;
            Rate = BackspinPeakRate;
            return true;
        }

        /// <summary>
        /// Collapses any in-flight gesture back to free running immediately.
        /// Called on touch cancel (FR-073), app backgrounding (FR-074) and network
        /// disconnect (FR-066) — the three cases where the driving input has vanished and
        /// leaving the platter under gesture control would freeze or reverse the audio.
        /// </summary>
        public void Cancel()
        {
            Mode = MotionMode.Normal;
            _nudge = 0f;
            _scratchRate = 1f;
            _releasing = false;
            _elapsed = 0f;
            Rate = 1f;
        }

        /// <summary>Advances the motion model. Returns what the transport should do, if anything.</summary>
        public MotionOutcome Tick(float deltaSeconds)
        {
            var dt = AudioSafety.Sanitize(deltaSeconds, 0f, 0.25f, 0f);
            if (dt <= 0f)
            {
                return MotionOutcome.None;
            }

            switch (Mode)
            {
                case MotionMode.Scratching:
                    Rate = _scratchRate;
                    return MotionOutcome.None;

                case MotionMode.Braking:
                    return TickBrake(dt);

                case MotionMode.Backspin:
                    return TickBackspin(dt);

                default:
                    return TickNormal(dt);
            }
        }

        private MotionOutcome TickNormal(float dt)
        {
            if (_releasing)
            {
                _elapsed += dt;
                var t = _elapsed / ScratchReleaseSeconds;
                if (t >= 1f)
                {
                    _releasing = false;
                    Rate = 1f;
                    return MotionOutcome.Release;
                }

                Rate = _releaseFrom + (1f - _releaseFrom) * t;
                return MotionOutcome.None;
            }

            if (Math.Abs(_nudge) > 1e-4f)
            {
                // Exponential decay: independent of frame rate, never overshoots zero.
                var decay = (float)Math.Exp(-dt / NudgeDecaySeconds);
                _nudge *= decay;
                if (Math.Abs(_nudge) <= 1e-4f)
                {
                    _nudge = 0f;
                }
            }

            Rate = 1f + _nudge;
            return MotionOutcome.None;
        }

        private MotionOutcome TickBrake(float dt)
        {
            _elapsed += dt;
            var t = _elapsed / _brakeSeconds;
            if (t >= 1f)
            {
                Rate = 0f;
                Mode = MotionMode.Normal;
                _elapsed = 0f;
                // Leave Rate at 0 for this tick; the transport stops and the next
                // Cancel()/play restores 1.0.
                return MotionOutcome.Stop;
            }

            // Ease-out: most of the slow-down happens early, like a real platter's friction.
            var eased = 1f - t;
            Rate = _rateAtEffectStart * eased * eased;
            return MotionOutcome.None;
        }

        private MotionOutcome TickBackspin(float dt)
        {
            _elapsed += dt;
            var t = _elapsed / _backspinSeconds;
            if (t >= 1f)
            {
                Mode = MotionMode.Normal;
                _elapsed = 0f;
                Rate = _rateAtEffectStart <= 0f ? 1f : _rateAtEffectStart;
                _nudge = 0f;
                return MotionOutcome.Release;
            }

            // Reverse spike that loses energy, then settles back toward forward motion.
            var decay = (float)Math.Exp(-3d * t);
            Rate = BackspinPeakRate * decay;
            return MotionOutcome.None;
        }
    }
}
