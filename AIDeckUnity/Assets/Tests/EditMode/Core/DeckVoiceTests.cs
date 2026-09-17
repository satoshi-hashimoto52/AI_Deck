using System;
using AIDeck.Core.Audio;
using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// The playback voice is where FR-032 (scratch) and FR-034 (backspin) actually happen, so
    /// it is tested directly rather than only through the engine. Because it holds no Unity
    /// types, rendering can be driven here block by block with no audio device.
    /// </summary>
    [TestFixture]
    public class DeckVoiceTests
    {
        private const int SourceRate = 48000;
        private const int OutRate = 48000;

        /// <summary>A ramp from 0 to 1 over the whole file: the sample value identifies the frame.</summary>
        private static VoiceSource Ramp(int frames, int channels = 1, int sampleRate = SourceRate)
        {
            var samples = new float[frames * channels];
            for (var frame = 0; frame < frames; frame++)
            {
                var value = frame / (float)Math.Max(1, frames - 1);
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[frame * channels + channel] = value;
                }
            }

            return new VoiceSource(samples, channels, sampleRate);
        }

        private static DeckVoice Loaded(int frames = SourceRate, int channels = 1)
        {
            var voice = new DeckVoice();
            voice.SetSource(Ramp(frames, channels));
            return voice;
        }

        [Test]
        public void NewVoiceIsSilentAndHasNoSource()
        {
            var voice = new DeckVoice();
            Assert.That(voice.HasSource, Is.False);

            var buffer = new float[256];
            Assert.That(voice.Render(buffer, 2, OutRate), Is.EqualTo(0));
            Assert.That(buffer, Is.All.EqualTo(0f));
        }

        [Test]
        public void StoppedVoiceRendersSilence()
        {
            var voice = Loaded();
            voice.IsPlaying = false;

            var buffer = new float[256];
            Assert.That(voice.Render(buffer, 1, OutRate), Is.EqualTo(0));
            Assert.That(buffer, Is.All.EqualTo(0f));
        }

        [Test]
        public void PlaybackAdvancesOneFramePerOutputFrameAtUnityRate()
        {
            var voice = Loaded();
            voice.IsPlaying = true;

            var buffer = new float[512];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.EqualTo(512d / SourceRate).Within(1e-6));
        }

        [Test]
        public void SourceSampleRateDifferenceIsResampled()
        {
            var voice = new DeckVoice();
            voice.SetSource(Ramp(SourceRate, 1, 24000)); // half the device rate
            voice.IsPlaying = true;

            var buffer = new float[480];
            voice.Render(buffer, 1, OutRate);

            // 480 output frames at half rate consume 240 source frames.
            Assert.That(voice.PositionSeconds, Is.EqualTo(240d / 24000d).Within(1e-6));
        }

        [Test]
        public void RateScalesThePlayhead()
        {
            var voice = Loaded();
            voice.IsPlaying = true;
            voice.Rate = 2f;

            var buffer = new float[512];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.EqualTo(1024d / SourceRate).Within(1e-6));
        }

        [Test]
        public void NegativeRatePlaysBackwards()
        {
            var voice = Loaded();
            voice.SeekSeconds(0.5d);
            voice.IsPlaying = true;
            voice.Rate = -1f;

            var buffer = new float[512];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.LessThan(0.5d), "reverse must move the playhead backwards");
            Assert.That(voice.PositionSeconds, Is.EqualTo(0.5d - 512d / SourceRate).Within(1e-6));
        }

        [Test]
        public void ReversePastTheStartParksAtZeroRatherThanWrapping()
        {
            var voice = Loaded();
            voice.SeekSeconds(0.001d);
            voice.IsPlaying = true;
            voice.Rate = -8f;

            var buffer = new float[4096];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.EqualTo(0d).Within(1e-9));
        }

        [Test]
        public void RateZeroHoldsThePlayheadStill()
        {
            var voice = Loaded();
            voice.SeekSeconds(0.25d);
            voice.IsPlaying = true;
            voice.Rate = 0f;

            var buffer = new float[1024];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.EqualTo(0.25d).Within(1e-9));
        }

        [Test]
        public void RateIsClampedAndNaNSafe()
        {
            var voice = Loaded();
            voice.Rate = 1000f;
            Assert.That(voice.Rate, Is.LessThanOrEqualTo(16f));
            voice.Rate = float.NaN;
            Assert.That(voice.Rate, Is.EqualTo(1f));
        }

        [Test]
        public void RenderedSamplesFollowTheSource()
        {
            var voice = Loaded(frames: 1000);
            voice.IsPlaying = true;

            var buffer = new float[100];
            voice.Render(buffer, 1, OutRate);

            // The ramp is monotonic, so the rendered block must be too.
            for (var i = 1; i < buffer.Length; i++)
            {
                Assert.That(buffer[i], Is.GreaterThanOrEqualTo(buffer[i - 1]));
            }

            Assert.That(buffer[0], Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void MonoSourceFeedsEveryOutputChannel()
        {
            var voice = Loaded(frames: 1000);
            voice.SeekSeconds(0.005d);
            voice.IsPlaying = true;

            var buffer = new float[64];
            voice.Render(buffer, 2, OutRate);

            for (var i = 0; i < buffer.Length; i += 2)
            {
                Assert.That(buffer[i], Is.EqualTo(buffer[i + 1]).Within(1e-6f));
            }
        }

        [Test]
        public void StereoSourceKeepsItsChannels()
        {
            var samples = new float[1000 * 2];
            for (var frame = 0; frame < 1000; frame++)
            {
                samples[frame * 2] = 0.5f;
                samples[frame * 2 + 1] = -0.5f;
            }

            var voice = new DeckVoice();
            voice.SetSource(new VoiceSource(samples, 2, SourceRate));
            voice.IsPlaying = true;

            var buffer = new float[64];
            voice.Render(buffer, 2, OutRate);

            for (var i = 0; i < buffer.Length; i += 2)
            {
                Assert.That(buffer[i], Is.EqualTo(0.5f).Within(1e-5f));
                Assert.That(buffer[i + 1], Is.EqualTo(-0.5f).Within(1e-5f));
            }
        }

        [Test]
        public void ReachingTheEndStopsPlaybackAndReportsItExactlyOnce()
        {
            var voice = Loaded(frames: 512);
            voice.IsPlaying = true;

            var buffer = new float[1024];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.IsPlaying, Is.False);
            Assert.That(voice.ConsumeReachedEnd(), Is.True);
            Assert.That(voice.ConsumeReachedEnd(), Is.False, "the end is reported once, not every block");
        }

        [Test]
        public void SeekMovesThePlayheadOnTheNextBlock()
        {
            var voice = Loaded();
            voice.IsPlaying = true;
            voice.SeekSeconds(0.4d);

            var buffer = new float[64];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.EqualTo(0.4d + 64d / SourceRate).Within(1e-6));
        }

        [Test]
        public void SeekIsClampedToTheTrack()
        {
            var voice = Loaded(frames: 1000);
            voice.SeekSeconds(999d);
            var buffer = new float[16];
            voice.Render(buffer, 1, OutRate);
            Assert.That(voice.PositionSeconds, Is.LessThanOrEqualTo(1000d / SourceRate));

            voice.SeekSeconds(-5d);
            voice.Render(buffer, 1, OutRate);
            Assert.That(voice.PositionSeconds, Is.GreaterThanOrEqualTo(0d));

            voice.SeekSeconds(double.NaN);
            voice.Render(buffer, 1, OutRate);
            Assert.That(double.IsNaN(voice.PositionSeconds), Is.False);
        }

        [Test]
        public void ActiveLoopWrapsThePlayheadWithoutStopping()
        {
            var voice = Loaded(frames: SourceRate * 4);
            voice.IsPlaying = true;
            voice.SeekSeconds(1d);
            voice.SetLoop(true, 1d, 1.01d); // 480 frames

            var buffer = new float[4800];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.IsPlaying, Is.True);
            Assert.That(voice.PositionSeconds, Is.InRange(1d, 1.0101d));
        }

        [Test]
        public void LoopWrapsInReverseToo()
        {
            var voice = Loaded(frames: SourceRate * 4);
            voice.IsPlaying = true;
            voice.SeekSeconds(1.005d);
            voice.SetLoop(true, 1d, 1.01d);
            voice.Rate = -1f;

            var buffer = new float[4800];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.InRange(1d, 1.0101d),
                "a reverse scratch must not escape an armed loop");
        }

        [Test]
        public void ClearingTheLoopReleasesThePlayhead()
        {
            var voice = Loaded(frames: SourceRate * 4);
            voice.IsPlaying = true;
            voice.SeekSeconds(1d);
            voice.SetLoop(true, 1d, 1.01d);
            voice.SetLoop(false, 0d, 0d);

            var buffer = new float[4800];
            voice.Render(buffer, 1, OutRate);

            Assert.That(voice.PositionSeconds, Is.GreaterThan(1.05d));
        }

        [Test]
        public void SettingANewSourceStopsPlaybackAndRewinds()
        {
            var voice = Loaded();
            voice.IsPlaying = true;
            voice.SeekSeconds(0.5d);

            voice.SetSource(Ramp(2000));

            Assert.That(voice.IsPlaying, Is.False, "the outgoing track must not keep playing over the new one");
            Assert.That(voice.PositionSeconds, Is.EqualTo(0d).Within(1e-9));
        }

        [Test]
        public void ClearLeavesTheVoiceSilentAndHarmless()
        {
            var voice = Loaded();
            voice.IsPlaying = true;
            voice.Clear();

            var buffer = new float[256];
            Assert.That(voice.Render(buffer, 2, OutRate), Is.EqualTo(0));
            Assert.That(buffer, Is.All.EqualTo(0f));
            Assert.That(voice.HasSource, Is.False);
        }

        [Test]
        public void RenderedOutputIsAlwaysFinite()
        {
            var voice = Loaded(frames: 4000);
            voice.IsPlaying = true;

            var buffer = new float[512];
            foreach (var rate in new[] { 1f, -1f, 0.25f, 6f, -6f, 0f })
            {
                voice.Rate = rate;
                voice.SeekSeconds(0.02d);
                voice.Render(buffer, 2, OutRate);
                foreach (var sample in buffer)
                {
                    Assert.That(float.IsNaN(sample) || float.IsInfinity(sample), Is.False,
                        $"non-finite sample at rate {rate}");
                }
            }
        }

        [Test]
        public void DegenerateRenderArgumentsAreHarmless()
        {
            var voice = Loaded();
            voice.IsPlaying = true;
            Assert.That(voice.Render(null, 2, OutRate), Is.EqualTo(0));
            Assert.That(voice.Render(new float[64], 0, OutRate), Is.EqualTo(0));
            Assert.That(voice.Render(new float[64], 2, 0), Is.EqualTo(0));
        }
    }
}
