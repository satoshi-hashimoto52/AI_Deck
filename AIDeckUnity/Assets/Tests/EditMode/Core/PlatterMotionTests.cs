using AIDeck.Core.Deck;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers FR-032 (scratch), FR-033 (brake), FR-034 (backspin) and FR-072 (jog).</summary>
    [TestFixture]
    public class PlatterMotionTests
    {
        private PlatterMotion _motion;

        [SetUp]
        public void SetUp() => _motion = new PlatterMotion();

        /// <summary>
        /// Runs the model forward in 10 ms steps until an effect reports an outcome, or the
        /// time runs out. It stops at the outcome so a test can inspect the rate at the exact
        /// moment the effect completed, before free running resets it to 1.0.
        /// </summary>
        private MotionOutcome Run(float seconds)
        {
            var steps = (int)(seconds / 0.01f);
            for (var i = 0; i < steps; i++)
            {
                var outcome = _motion.Tick(0.01f);
                if (outcome != MotionOutcome.None)
                {
                    return outcome;
                }
            }

            return MotionOutcome.None;
        }

        [Test]
        public void Default_IsFreeRunningAtNormalSpeed()
        {
            Assert.That(_motion.Mode, Is.EqualTo(MotionMode.Normal));
            Assert.That(_motion.Rate, Is.EqualTo(1f));
            Assert.That(_motion.IsEffectActive, Is.False);
        }

        [Test]
        public void Nudge_PushesTheRateThenDecaysBackToNormal()
        {
            _motion.Nudge(0.3f);
            _motion.Tick(0.001f);
            Assert.That(_motion.Rate, Is.GreaterThan(1f));

            Run(2f);
            Assert.That(_motion.Rate, Is.EqualTo(1f).Within(1e-3f), "a flick must not change the tempo permanently");
        }

        [Test]
        public void Nudge_IsClampedSoAFlickCannotJumpTheTrack()
        {
            _motion.Nudge(50f);
            _motion.Tick(0.001f);
            Assert.That(_motion.Rate, Is.LessThanOrEqualTo(1f + PlatterMotion.MaxNudge + 1e-3f));
        }

        [Test]
        public void Scratch_DrivesTheRateDirectlyIncludingBackwards()
        {
            _motion.BeginScratch();
            Assert.That(_motion.Mode, Is.EqualTo(MotionMode.Scratching));
            Assert.That(_motion.Rate, Is.EqualTo(0f), "the platter is held still the moment a finger lands");

            _motion.UpdateScratch(2.5f);
            Assert.That(_motion.Rate, Is.EqualTo(2.5f));

            _motion.UpdateScratch(-1.8f);
            Assert.That(_motion.Rate, Is.EqualTo(-1.8f));
        }

        [Test]
        public void ScratchRate_IsClampedToTheSafeWindow()
        {
            _motion.BeginScratch();
            _motion.UpdateScratch(500f);
            Assert.That(_motion.Rate, Is.EqualTo(PlatterMotion.MaxScratchRate));
            _motion.UpdateScratch(float.NaN);
            Assert.That(_motion.Rate, Is.EqualTo(0f));
        }

        [Test]
        public void EndScratch_GlidesBackToNormalAndReportsRelease()
        {
            _motion.BeginScratch();
            _motion.UpdateScratch(3f);
            _motion.EndScratch();
            Assert.That(_motion.Mode, Is.EqualTo(MotionMode.Normal));

            var outcome = Run(0.5f);
            Assert.That(outcome, Is.EqualTo(MotionOutcome.Release));
            Assert.That(_motion.Rate, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void UpdateScratch_IsIgnoredWhenNoGestureIsActive()
        {
            _motion.UpdateScratch(4f);
            Assert.That(_motion.Rate, Is.EqualTo(1f));
        }

        [Test]
        public void Brake_RampsToRestAndAsksTheTransportToStop()
        {
            _motion.BrakeSeconds = 0.5f;
            Assert.That(_motion.StartBrake(), Is.True);
            Assert.That(_motion.StartBrake(), Is.False, "restarting an in-flight brake is a no-op");

            _motion.Tick(0.1f);
            var midway = _motion.Rate;
            Assert.That(midway, Is.LessThan(1f).And.GreaterThan(0f));

            var outcome = Run(1f);
            Assert.That(outcome, Is.EqualTo(MotionOutcome.Stop));
            Assert.That(_motion.Rate, Is.EqualTo(0f), "the platter is at rest the moment BRAKE completes");
            Assert.That(_motion.Mode, Is.EqualTo(MotionMode.Normal));

            // Once the transport has stopped, free running restores the normal rate so the
            // next PLAY starts at full speed instead of from a standstill.
            _motion.Tick(0.01f);
            Assert.That(_motion.Rate, Is.EqualTo(1f));
        }

        [Test]
        public void Brake_DecreasesMonotonically()
        {
            _motion.BrakeSeconds = 0.5f;
            _motion.StartBrake();
            var previous = float.MaxValue;
            for (var i = 0; i < 40; i++)
            {
                _motion.Tick(0.01f);
                Assert.That(_motion.Rate, Is.LessThanOrEqualTo(previous + 1e-5f));
                previous = _motion.Rate;
            }
        }

        [Test]
        public void Backspin_SpikesBackwardsThenSettlesForward()
        {
            Assert.That(_motion.StartBackspin(), Is.True);
            Assert.That(_motion.Rate, Is.EqualTo(PlatterMotion.BackspinPeakRate));

            _motion.BackspinSeconds = 0.4f;
            _motion.Tick(0.05f);
            Assert.That(_motion.Rate, Is.LessThan(0f), "still running backwards part way through");

            var outcome = Run(1f);
            Assert.That(outcome, Is.EqualTo(MotionOutcome.Release));
            Assert.That(_motion.Rate, Is.GreaterThan(0f));
            Assert.That(_motion.Mode, Is.EqualTo(MotionMode.Normal));
        }

        [Test]
        public void Cancel_CollapsesAnyGestureImmediately()
        {
            _motion.BeginScratch();
            _motion.UpdateScratch(-4f);
            _motion.Cancel();
            Assert.That(_motion.Mode, Is.EqualTo(MotionMode.Normal));
            Assert.That(_motion.Rate, Is.EqualTo(1f), "a lost input must not leave the platter reversed");

            _motion.StartBrake();
            _motion.Cancel();
            Assert.That(_motion.Rate, Is.EqualTo(1f));

            _motion.StartBackspin();
            _motion.Cancel();
            Assert.That(_motion.Rate, Is.EqualTo(1f));
        }

        [Test]
        public void Tick_IgnoresNonFiniteAndNegativeDeltas()
        {
            _motion.StartBrake();
            var before = _motion.Rate;
            Assert.That(_motion.Tick(float.NaN), Is.EqualTo(MotionOutcome.None));
            Assert.That(_motion.Tick(-1f), Is.EqualTo(MotionOutcome.None));
            Assert.That(_motion.Rate, Is.EqualTo(before));
        }

        [Test]
        public void Tick_ClampsAHugeDeltaSoAStalledFrameCannotSkipAnEffect()
        {
            _motion.BrakeSeconds = 1f;
            _motion.StartBrake();
            _motion.Tick(60f);
            Assert.That(_motion.Rate, Is.GreaterThan(0f), "a one-minute frame must not teleport the brake to the end");
        }

        [Test]
        public void NudgeDuringAnEffect_IsIgnored()
        {
            _motion.StartBrake();
            var before = _motion.Rate;
            _motion.Nudge(0.5f);
            Assert.That(_motion.Rate, Is.EqualTo(before));
        }

        [Test]
        public void BrakeAndBackspinDurations_AreClamped()
        {
            _motion.BrakeSeconds = 0f;
            Assert.That(_motion.BrakeSeconds, Is.GreaterThan(0f));
            _motion.BrakeSeconds = 1000f;
            Assert.That(_motion.BrakeSeconds, Is.LessThanOrEqualTo(8f));
            _motion.BackspinSeconds = float.NaN;
            Assert.That(_motion.BackspinSeconds, Is.EqualTo(PlatterMotion.DefaultBackspinSeconds));
        }
    }
}
