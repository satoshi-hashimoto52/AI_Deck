using System;
using AIDeck.Core.Analysis;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers FR-027 (waveform) and FR-029 (simple BPM analysis).</summary>
    [TestFixture]
    public class AnalysisTests
    {
        private const int SampleRate = 44100;

        // ------------------------------------------------------------------ waveform

        [Test]
        public void WaveformHasTheExpectedResolutionAndDuration()
        {
            var samples = Tone(440f, SampleRate * 2, 2); // 2 seconds, stereo
            var waveform = WaveformBuilder.Build(samples, 2, SampleRate);

            Assert.That(waveform.DurationSeconds, Is.EqualTo(2d).Within(0.01d));
            Assert.That(waveform.BucketsPerSecond, Is.EqualTo(WaveformData.DefaultBucketsPerSecond));
            Assert.That(waveform.BucketCount, Is.EqualTo((int)Math.Ceiling(2d * WaveformData.DefaultBucketsPerSecond)));
        }

        [Test]
        public void WaveformPeaksTrackTheSignalLevel()
        {
            var samples = new float[SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                // First half quiet, second half loud.
                samples[i] = (i < samples.Length / 2 ? 0.1f : 0.9f) *
                             (float)Math.Sin(2d * Math.PI * 440d * i / SampleRate);
            }

            var waveform = WaveformBuilder.Build(samples, 1, SampleRate);
            var quiet = waveform.PeakAt(waveform.BucketCount / 4);
            var loud = waveform.PeakAt(waveform.BucketCount * 3 / 4);

            Assert.That(loud, Is.GreaterThan(quiet * 3f));
            Assert.That(loud, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void WaveformRmsIsNeverAboveThePeak()
        {
            var waveform = WaveformBuilder.Build(Tone(440f, SampleRate, 1), 1, SampleRate);
            for (var i = 0; i < waveform.BucketCount; i++)
            {
                Assert.That(waveform.RmsAt(i), Is.LessThanOrEqualTo(waveform.PeakAt(i) + 1f / 255f));
            }
        }

        [Test]
        public void BucketAtMapsPositionToIndexAndStaysInRange()
        {
            var waveform = WaveformBuilder.Build(Tone(440f, SampleRate * 2, 1), 1, SampleRate);

            Assert.That(waveform.BucketAt(0d), Is.EqualTo(0));
            Assert.That(waveform.BucketAt(-5d), Is.EqualTo(0));
            Assert.That(waveform.BucketAt(double.NaN), Is.EqualTo(0));
            Assert.That(waveform.BucketAt(1000d), Is.EqualTo(waveform.BucketCount - 1));
            Assert.That(waveform.BucketAt(1d), Is.EqualTo(WaveformData.DefaultBucketsPerSecond));
        }

        [Test]
        public void OutOfRangeBucketReadsReturnZero()
        {
            var waveform = WaveformBuilder.Build(Tone(440f, SampleRate, 1), 1, SampleRate);
            Assert.That(waveform.PeakAt(-1), Is.EqualTo(0f));
            Assert.That(waveform.PeakAt(waveform.BucketCount + 10), Is.EqualTo(0f));
            Assert.That(waveform.RmsAt(-1), Is.EqualTo(0f));
        }

        [Test]
        public void DegenerateInputProducesTheEmptyWaveformRatherThanThrowing()
        {
            Assert.That(WaveformBuilder.Build(null, 2, SampleRate).IsEmpty, Is.True);
            Assert.That(WaveformBuilder.Build(Array.Empty<float>(), 2, SampleRate).IsEmpty, Is.True);
            Assert.That(WaveformBuilder.Build(new float[100], 0, SampleRate).IsEmpty, Is.True);
            Assert.That(WaveformBuilder.Build(new float[100], 2, 0).IsEmpty, Is.True);
        }

        [Test]
        public void NonFiniteSamplesAreIgnoredRatherThanPoisoningTheEnvelope()
        {
            var samples = new float[SampleRate];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.5f;
            }

            samples[100] = float.NaN;
            samples[200] = float.PositiveInfinity;

            var waveform = WaveformBuilder.Build(samples, 1, SampleRate);
            for (var i = 0; i < waveform.BucketCount; i++)
            {
                Assert.That(waveform.PeakAt(i), Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void ProgressIsReportedAndEndsAtOne()
        {
            var last = 0f;
            var count = 0;
            WaveformBuilder.Build(Tone(440f, SampleRate * 2, 1), 1, SampleRate, progress: p =>
            {
                Assert.That(p, Is.InRange(0f, 1f));
                last = p;
                count++;
            });

            Assert.That(count, Is.GreaterThan(1));
            Assert.That(last, Is.EqualTo(1f));
        }

        // ------------------------------------------------------------------ BPM

        [Test]
        public void ClickTrackTempoIsDetected()
        {
            const double bpm = 128d;
            var samples = ClickTrack(bpm, 90);
            var result = BpmAnalyzer.Analyse(samples, 1, SampleRate);

            Assert.That(result.IsUsable, Is.True, $"detected {result.Bpm} at confidence {result.Confidence}");
            Assert.That(result.Bpm, Is.EqualTo(bpm).Within(2d));
        }

        [TestCase(100d)]
        [TestCase(120d)]
        [TestCase(140d)]
        [TestCase(174d)]
        public void SeveralTemposAreDetected(double bpm)
        {
            var result = BpmAnalyzer.Analyse(ClickTrack(bpm, 90), 1, SampleRate);
            Assert.That(result.IsUsable, Is.True, $"detected {result.Bpm} at confidence {result.Confidence}");
            Assert.That(result.Bpm, Is.EqualTo(bpm).Within(3d));
        }

        [Test]
        public void SilenceProducesNoTempoRatherThanAGuess()
        {
            var result = BpmAnalyzer.Analyse(new float[SampleRate * 80], 1, SampleRate);
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.Bpm, Is.EqualTo(0d));
        }

        [Test]
        public void TooShortAudioProducesNoTempo()
        {
            var result = BpmAnalyzer.Analyse(new float[1000], 1, SampleRate);
            Assert.That(result.IsUsable, Is.False);
        }

        [Test]
        public void DegenerateInputIsHandled()
        {
            Assert.That(BpmAnalyzer.Analyse(null, 1, SampleRate).IsUsable, Is.False);
            Assert.That(BpmAnalyzer.Analyse(new float[1000], 0, SampleRate).IsUsable, Is.False);
            Assert.That(BpmAnalyzer.Analyse(new float[1000], 1, 0).IsUsable, Is.False);
        }

        [Test]
        public void FoldIntoRangeCollapsesHalfAndDoubleTime()
        {
            Assert.That(BpmAnalyzer.FoldIntoRange(128d), Is.EqualTo(128d).Within(1e-9));
            Assert.That(BpmAnalyzer.FoldIntoRange(64d), Is.EqualTo(128d).Within(1e-9));
            Assert.That(BpmAnalyzer.FoldIntoRange(256d), Is.EqualTo(128d).Within(1e-9));
            Assert.That(BpmAnalyzer.FoldIntoRange(0d), Is.EqualTo(0d));
            Assert.That(BpmAnalyzer.FoldIntoRange(double.NaN), Is.EqualTo(0d));
        }

        [Test]
        public void ResultConfidenceIsBounded()
        {
            var result = BpmAnalyzer.Analyse(ClickTrack(128d, 90), 1, SampleRate);
            Assert.That(result.Confidence, Is.InRange(0d, 1d));
        }

        // ------------------------------------------------------------------ helpers

        private static float[] Tone(float frequency, int frames, int channels)
        {
            var samples = new float[frames * channels];
            for (var frame = 0; frame < frames; frame++)
            {
                var value = 0.5f * (float)Math.Sin(2d * Math.PI * frequency * frame / SampleRate);
                for (var ch = 0; ch < channels; ch++)
                {
                    samples[frame * channels + ch] = value;
                }
            }

            return samples;
        }

        /// <summary>
        /// Percussive click track: a short decaying burst on every beat. This is the clearest
        /// possible onset pattern, which is what the simple analyser is specified to handle.
        /// </summary>
        private static float[] ClickTrack(double bpm, double seconds)
        {
            var total = (int)(seconds * SampleRate);
            var samples = new float[total];
            var interval = 60d / bpm * SampleRate;
            var random = new Random(1234);

            for (double position = 0; position < total; position += interval)
            {
                var start = (int)position;
                for (var i = 0; i < 1500 && start + i < total; i++)
                {
                    var envelope = (float)Math.Exp(-i / 300d);
                    samples[start + i] += envelope * (float)(random.NextDouble() * 2d - 1d) * 0.8f;
                }
            }

            return samples;
        }
    }
}
