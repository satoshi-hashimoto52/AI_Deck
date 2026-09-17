using AIDeck.Core.Fx;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "エフェクト値の境界" item of §10.1, plus FR-043 to FR-047.</summary>
    [TestFixture]
    public class EffectTests
    {
        private const int SampleRate = 48000;

        // ------------------------------------------------------------------ filter

        [Test]
        public void FilterKnob_HasATrueBypassDeadZoneAtTheCentre()
        {
            Assert.That(FilterParams.KindFor(0f), Is.EqualTo(FilterKind.Bypass));
            Assert.That(FilterParams.KindFor(0.001f), Is.EqualTo(FilterKind.Bypass));
            Assert.That(FilterParams.KindFor(-0.001f), Is.EqualTo(FilterKind.Bypass));
            Assert.That(FilterParams.KindFor(0.5f), Is.EqualTo(FilterKind.HighPass));
            Assert.That(FilterParams.KindFor(-0.5f), Is.EqualTo(FilterKind.LowPass));
        }

        [Test]
        public void FilterKnob_HandlesCorruptValues()
        {
            Assert.That(FilterParams.KindFor(float.NaN), Is.EqualTo(FilterKind.Bypass));
            Assert.That(FilterParams.CutoffHz(float.NaN), Is.EqualTo(FilterParams.LowPassTopHz));
            Assert.That(FilterParams.CutoffHz(99f), Is.InRange(FilterParams.MinCutoffHz, FilterParams.LowPassTopHz));
        }

        [Test]
        public void FilterCutoff_SweepsMonotonicallyOnBothSides()
        {
            var previous = FilterParams.CutoffHz(-0.05f);
            for (var k = -0.1f; k >= -1f; k -= 0.05f)
            {
                var cutoff = FilterParams.CutoffHz(k);
                Assert.That(cutoff, Is.LessThanOrEqualTo(previous + 1e-3f), $"low-pass sweep not monotonic at {k}");
                previous = cutoff;
            }

            previous = FilterParams.CutoffHz(0.05f);
            for (var k = 0.1f; k <= 1f; k += 0.05f)
            {
                var cutoff = FilterParams.CutoffHz(k);
                Assert.That(cutoff, Is.GreaterThanOrEqualTo(previous - 1e-3f), $"high-pass sweep not monotonic at {k}");
                previous = cutoff;
            }
        }

        [Test]
        public void FilterEnds_ReachTheDocumentedLimits()
        {
            Assert.That(FilterParams.CutoffHz(-1f), Is.EqualTo(FilterParams.MinCutoffHz).Within(1f));
            Assert.That(FilterParams.CutoffHz(1f), Is.EqualTo(FilterParams.MaxCutoffHz).Within(1f));
        }

        [Test]
        public void BypassedFilter_PassesTheSignalThroughUnchanged()
        {
            var filter = new MultiChannelFilter(SampleRate);
            filter.SetKnob(0f);
            var buffer = new[] { 0.5f, -0.25f, 0.75f, -1f };
            var expected = (float[])buffer.Clone();
            filter.ProcessInterleaved(buffer, 2);
            Assert.That(buffer, Is.EqualTo(expected));
        }

        [Test]
        public void LowPass_AttenuatesAHighFrequencyTone()
        {
            var filter = new MultiChannelFilter(SampleRate, 1);
            filter.SetKnob(-1f); // cutoff at 40 Hz
            var input = Tone(8000f, 4800);
            var output = (float[])input.Clone();
            filter.ProcessInterleaved(output, 1);

            Assert.That(Rms(output, 2400), Is.LessThan(Rms(input, 2400) * 0.2f));
        }

        [Test]
        public void HighPass_AttenuatesALowFrequencyTone()
        {
            var filter = new MultiChannelFilter(SampleRate, 1);
            filter.SetKnob(1f); // cutoff at 12 kHz
            var input = Tone(60f, 4800);
            var output = (float[])input.Clone();
            filter.ProcessInterleaved(output, 1);

            Assert.That(Rms(output, 2400), Is.LessThan(Rms(input, 2400) * 0.2f));
        }

        [Test]
        public void Filter_StaysBoundedAcrossAFastSweep()
        {
            var filter = new MultiChannelFilter(SampleRate, 1);
            var input = Tone(440f, 48000);
            var buffer = new float[512];

            for (var block = 0; block < input.Length / buffer.Length; block++)
            {
                System.Array.Copy(input, block * buffer.Length, buffer, 0, buffer.Length);
                filter.SetKnob(-1f + 2f * block / (input.Length / (float)buffer.Length));
                filter.ProcessInterleaved(buffer, 1);
                foreach (var sample in buffer)
                {
                    Assert.That(float.IsNaN(sample) || float.IsInfinity(sample), Is.False, "filter produced a non-finite sample");
                    Assert.That(System.Math.Abs(sample), Is.LessThan(8f), "filter resonance ran away during the sweep");
                }
            }
        }

        [Test]
        public void Filter_RecoversFromANonFiniteInputSample()
        {
            var filter = new StateVariableFilter(SampleRate);
            filter.Configure(FilterKind.LowPass, 500f);
            Assert.That(filter.Process(float.NaN), Is.EqualTo(0f));
            for (var i = 0; i < 100; i++)
            {
                var output = filter.Process(0.5f);
                Assert.That(float.IsNaN(output), Is.False, "a bad sample must not persist in the integrator state");
            }
        }

        // ------------------------------------------------------------------ echo

        [Test]
        public void Echo_IsInertUntilEnabled()
        {
            var echo = new EchoProcessor(SampleRate);
            var buffer = new[] { 0.5f, 0.5f, -0.5f, -0.5f };
            var expected = (float[])buffer.Clone();
            echo.ProcessInterleaved(buffer, 2);
            Assert.That(buffer, Is.EqualTo(expected));
        }

        [Test]
        public void Echo_RepeatsTheSignalAfterTheDelay()
        {
            var echo = new EchoProcessor(SampleRate, 1);
            echo.DelaySeconds = 0.01f; // 480 samples
            echo.WetLevel = 1f;
            echo.SetEnabled(true);

            var buffer = new float[2048];
            buffer[0] = 1f;
            // The wet ramp takes 50 ms, so run enough blocks for it to open first.
            for (var i = 0; i < 4; i++)
            {
                echo.ProcessInterleaved(buffer, 1);
                if (i == 0)
                {
                    System.Array.Clear(buffer, 0, buffer.Length);
                }
            }

            var peak = 0f;
            foreach (var sample in buffer)
            {
                peak = System.Math.Max(peak, System.Math.Abs(sample));
            }

            Assert.That(peak, Is.GreaterThan(0f), "the delayed repeat never arrived");
        }

        [Test]
        public void EchoFeedback_IsCappedSoTheTailAlwaysDecays()
        {
            var echo = new EchoProcessor(SampleRate, 1) { Feedback = 5f };
            Assert.That(echo.Feedback, Is.LessThanOrEqualTo(EchoProcessor.MaxFeedback));

            echo.DelaySeconds = 0.005f;
            echo.WetLevel = 1f;
            echo.SetEnabled(true);

            var buffer = new float[1024];
            buffer[0] = 1f;
            var peak = 0f;
            for (var block = 0; block < 400; block++)
            {
                echo.ProcessInterleaved(buffer, 1);
                foreach (var sample in buffer)
                {
                    peak = System.Math.Max(peak, System.Math.Abs(sample));
                }

                System.Array.Clear(buffer, 0, buffer.Length);
            }

            Assert.That(peak, Is.LessThan(20f), "capped feedback must not let the tail build without bound");
        }

        [Test]
        public void EchoWetLevel_RampsRatherThanSwitching()
        {
            var echo = new EchoProcessor(SampleRate, 1);
            echo.SetEnabled(true);
            Assert.That(echo.CurrentWet, Is.EqualTo(0f), "the wet level starts closed");

            var buffer = new float[64];
            echo.ProcessInterleaved(buffer, 1);
            Assert.That(echo.CurrentWet, Is.GreaterThan(0f).And.LessThan(echo.WetLevel),
                "toggling ECHO must fade, not step");
        }

        [Test]
        public void EchoReset_SilencesTheTailImmediately()
        {
            var echo = new EchoProcessor(SampleRate, 1);
            echo.WetLevel = 1f;
            echo.SetEnabled(true);
            var buffer = new float[4096];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = 0.5f;
            }

            echo.ProcessInterleaved(buffer, 1);
            echo.SetEnabled(false);
            echo.Reset();
            Assert.That(echo.IsTailActive, Is.False, "a disconnect must not leave an echo tail ringing");
        }

        [Test]
        public void EchoParameters_AreClamped()
        {
            var echo = new EchoProcessor(SampleRate);
            echo.DelaySeconds = 100f;
            Assert.That(echo.DelaySeconds, Is.LessThanOrEqualTo(EchoProcessor.MaxDelaySeconds));
            echo.DelaySeconds = float.NaN;
            Assert.That(echo.DelaySeconds, Is.EqualTo(EchoProcessor.DefaultDelaySeconds));
            echo.WetLevel = 9f;
            Assert.That(echo.WetLevel, Is.EqualTo(1f));
        }

        [Test]
        public void Echo_TurnsNonFiniteInputIntoSilenceRatherThanPropagatingIt()
        {
            var echo = new EchoProcessor(SampleRate, 1);
            echo.WetLevel = 1f;
            echo.SetEnabled(true);
            var buffer = new[] { float.NaN, float.PositiveInfinity, 0.5f, 0.5f };
            echo.ProcessInterleaved(buffer, 1);
            foreach (var sample in buffer)
            {
                Assert.That(float.IsNaN(sample) || float.IsInfinity(sample), Is.False);
            }
        }

        // ------------------------------------------------------------------ meter

        [Test]
        public void LevelMeter_ReportsPeakAndRms()
        {
            var meter = new LevelMeter(SampleRate);
            meter.Analyse(new[] { 0.5f, -0.5f, 0.5f, -0.5f }, 2);
            Assert.That(meter.Peak, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(meter.Rms, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void LevelMeter_LatchesClippingSoABriefTransientIsNotMissed()
        {
            var meter = new LevelMeter(SampleRate);
            meter.Analyse(new[] { 0.1f, 1f, 0.1f, 0.1f }, 2);
            Assert.That(meter.IsClipping, Is.True);
            Assert.That(meter.ClipCount, Is.EqualTo(1));

            meter.ResetClip();
            Assert.That(meter.IsClipping, Is.False);
        }

        [Test]
        public void LevelMeter_CountsNonFiniteSamplesAsAnOverload()
        {
            var meter = new LevelMeter(SampleRate);
            meter.Analyse(new[] { float.NaN, 0.1f }, 1);
            Assert.That(meter.ClipCount, Is.EqualTo(1));
            Assert.That(float.IsNaN(meter.Rms), Is.False);
        }

        [Test]
        public void LevelMeter_PeakFallsBackOverTime()
        {
            var meter = new LevelMeter(SampleRate);
            meter.Analyse(new[] { 1f }, 1);
            var loud = meter.Peak;

            var silence = new float[SampleRate / 2];
            meter.Analyse(silence, 1);
            Assert.That(meter.Peak, Is.LessThan(loud));
            Assert.That(meter.Peak, Is.GreaterThanOrEqualTo(0f));
        }

        // ------------------------------------------------------------------ helpers

        private static float[] Tone(float frequency, int sampleCount)
        {
            var samples = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = 0.5f * (float)System.Math.Sin(2d * System.Math.PI * frequency * i / SampleRate);
            }

            return samples;
        }

        /// <summary>RMS of the tail of a buffer, past the filter's settling time.</summary>
        private static float Rms(float[] samples, int skip)
        {
            double sum = 0d;
            var count = 0;
            for (var i = skip; i < samples.Length; i++)
            {
                sum += (double)samples[i] * samples[i];
                count++;
            }

            return count == 0 ? 0f : (float)System.Math.Sqrt(sum / count);
        }
    }
}
