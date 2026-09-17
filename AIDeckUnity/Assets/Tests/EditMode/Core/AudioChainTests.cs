using System;
using System.IO;
using AIDeck.Core.Audio;
using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// The deck channel and master bus together are the whole signal path. Because neither
    /// touches Unity, the path can be driven block by block here — which is the only practical
    /// way to prove the §9 rules about fades and the NFR-009 rule about output limiting.
    /// </summary>
    [TestFixture]
    public class AudioChainTests
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BlockFrames = 512;

        private static VoiceSource Constant(float value, int frames = SampleRate * 4)
        {
            var samples = new float[frames * Channels];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = value;
            }

            return new VoiceSource(samples, Channels, SampleRate);
        }

        private static DeckChannel PlayingChannel(float level = 0.5f, float gain = 1f)
        {
            var channel = new DeckChannel(DeckId.A);
            channel.Prepare(SampleRate, Channels);
            channel.Voice.SetSource(Constant(level));
            channel.Voice.IsPlaying = true;
            channel.TargetGain = gain;
            return channel;
        }

        private static float Peak(float[] buffer)
        {
            var peak = 0f;
            foreach (var sample in buffer)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            return peak;
        }

        // ------------------------------------------------------------------ channel

        [Test]
        public void ChannelGainRampsUpRatherThanStepping()
        {
            var channel = PlayingChannel();
            var mix = new float[BlockFrames * Channels];

            channel.RenderInto(mix, null, Channels, SampleRate);

            // The 12 ms ramp is longer than one 512-frame block, so the first block must still
            // be climbing. A step change here would be the click §9 rules out.
            Assert.That(channel.CurrentGain, Is.GreaterThan(0f));
            Assert.That(channel.CurrentGain, Is.LessThan(1f));
            Assert.That(mix[0], Is.LessThan(Math.Abs(mix[mix.Length - 1])),
                "the block should be quieter at its start than at its end");
        }

        [Test]
        public void ChannelReachesFullGainAfterTheFadeTime()
        {
            var channel = PlayingChannel();
            var mix = new float[BlockFrames * Channels];

            // 12 ms at 48 kHz is 576 frames; run comfortably past it.
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
            }

            Assert.That(channel.CurrentGain, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(Peak(mix), Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void ChannelFadesOutAndReportsSilence()
        {
            var channel = PlayingChannel();
            var mix = new float[BlockFrames * Channels];
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
            }

            channel.TargetGain = 0f;
            Assert.That(channel.IsSilent, Is.False);

            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
            }

            Assert.That(channel.IsSilent, Is.True, "the host waits for this before stopping the voice");
            Assert.That(Peak(mix), Is.LessThan(1e-3f));
        }

        [Test]
        public void MuteSilencesTheChannelThroughTheSameRamp()
        {
            var channel = PlayingChannel();
            var mix = new float[BlockFrames * Channels];
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
            }

            channel.Muted = true;
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
            }

            Assert.That(Peak(mix), Is.LessThan(1e-3f));
        }

        [Test]
        public void ChannelsSumIntoTheMixRatherThanReplacingIt()
        {
            var a = PlayingChannel(0.25f);
            var b = new DeckChannel(DeckId.B);
            b.Prepare(SampleRate, Channels);
            b.Voice.SetSource(Constant(0.25f));
            b.Voice.IsPlaying = true;
            b.TargetGain = 1f;

            var mix = new float[BlockFrames * Channels];
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                a.RenderInto(mix, null, Channels, SampleRate);
                b.RenderInto(mix, null, Channels, SampleRate);
            }

            Assert.That(Peak(mix), Is.EqualTo(0.5f).Within(1e-2f), "both decks must be audible at once");
        }

        [Test]
        public void ResetDspSilencesTheChannelImmediately()
        {
            var channel = PlayingChannel();
            var mix = new float[BlockFrames * Channels];
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
            }

            channel.ResetDsp();
            Assert.That(channel.CurrentGain, Is.EqualTo(0f));
        }

        [Test]
        public void ChannelWithNoSourceContributesNothing()
        {
            var channel = new DeckChannel(DeckId.A);
            channel.Prepare(SampleRate, Channels);
            channel.TargetGain = 1f;

            var mix = new float[BlockFrames * Channels];
            channel.RenderInto(mix, null, Channels, SampleRate);

            Assert.That(Peak(mix), Is.EqualTo(0f));
        }

        [Test]
        public void ChannelSkipsTheBlockWhenTheDeviceFormatDoesNotMatch()
        {
            var channel = PlayingChannel();
            var mix = new float[BlockFrames * Channels];

            // Rebuilding the DSP objects here would allocate on the audio thread, so a format
            // mismatch is skipped and the host rebuilds on the next frame.
            channel.RenderInto(mix, null, Channels, 44100);
            Assert.That(Peak(mix), Is.EqualTo(0f));

            channel.RenderInto(mix, null, 1, SampleRate);
            Assert.That(Peak(mix), Is.EqualTo(0f));
        }

        [Test]
        public void ChannelToleratesDegenerateArguments()
        {
            var channel = PlayingChannel();
            Assert.DoesNotThrow(() => channel.RenderInto(null, null, Channels, SampleRate));
            Assert.DoesNotThrow(() => channel.RenderInto(new float[8], null, 0, SampleRate));
        }

        [Test]
        public void FilterAndEchoAreAppliedThroughTheChannel()
        {
            var channel = new DeckChannel(DeckId.A);
            channel.Prepare(SampleRate, Channels);

            // A steady tone well above the low-pass cutoff.
            var frames = SampleRate;
            var samples = new float[frames * Channels];
            for (var frame = 0; frame < frames; frame++)
            {
                var value = 0.5f * (float)Math.Sin(2d * Math.PI * 8000d * frame / SampleRate);
                samples[frame * Channels] = value;
                samples[frame * Channels + 1] = value;
            }

            channel.Voice.SetSource(new VoiceSource(samples, Channels, SampleRate));
            channel.Voice.IsPlaying = true;
            channel.TargetGain = 1f;
            channel.FilterKnob = -1f;

            var mix = new float[BlockFrames * Channels];
            var peak = 0f;
            for (var i = 0; i < 40; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                channel.RenderInto(mix, null, Channels, SampleRate);
                if (i >= 20)
                {
                    peak = Math.Max(peak, Peak(mix));
                }
            }

            Assert.That(peak, Is.LessThan(0.15f), "a fully closed low-pass must remove an 8 kHz tone");
        }

        [Test]
        public void CueIsTakenBeforeTheFaderSoAFadedOutDeckIsStillAudibleOnIt()
        {
            // Pre-fade listen. Cueing a deck that is faded all the way out is the entire point:
            // it is how a DJ hears the next track before bringing it in.
            var channel = PlayingChannel(0.5f);
            channel.TargetGain = 0f;
            channel.CueEnabled = true;

            var mix = new float[BlockFrames * Channels];
            var cue = new float[BlockFrames * Channels];

            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                Array.Clear(cue, 0, cue.Length);
                channel.RenderInto(mix, cue, Channels, SampleRate);
            }

            Assert.That(Peak(mix), Is.LessThan(1e-3f), "a faded-out deck must not be in the master");
            Assert.That(Peak(cue), Is.EqualTo(0.5f).Within(1e-3f), "but it must be on the cue bus");
        }

        [Test]
        public void MuteDoesNotSilenceTheCueBus()
        {
            var channel = PlayingChannel(0.5f);
            channel.Muted = true;
            channel.CueEnabled = true;

            var mix = new float[BlockFrames * Channels];
            var cue = new float[BlockFrames * Channels];
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                Array.Clear(cue, 0, cue.Length);
                channel.RenderInto(mix, cue, Channels, SampleRate);
            }

            Assert.That(Peak(mix), Is.LessThan(1e-3f));
            Assert.That(Peak(cue), Is.GreaterThan(0.4f));
        }

        [Test]
        public void ADeckThatIsNotCuedContributesNothingToTheCueBus()
        {
            var channel = PlayingChannel(0.5f);
            channel.CueEnabled = false;

            var mix = new float[BlockFrames * Channels];
            var cue = new float[BlockFrames * Channels];
            for (var i = 0; i < 8; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                Array.Clear(cue, 0, cue.Length);
                channel.RenderInto(mix, cue, Channels, SampleRate);
            }

            Assert.That(Peak(cue), Is.EqualTo(0f));
        }

        [Test]
        public void SplitCuePutsTheCueLeftAndTheMasterRight()
        {
            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f };
            var mix = new float[BlockFrames * Channels];
            var cue = new float[BlockFrames * Channels];

            for (var i = 0; i < 10; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = 0.4f;
                    cue[s] = -0.6f;
                }

                master.Process(mix, cue, Channels);
            }

            // Left carries the cue, right carries the master.
            Assert.That(mix[0], Is.EqualTo(-0.6f).Within(1e-2f));
            Assert.That(mix[1], Is.EqualTo(0.4f).Within(1e-2f));
        }

        [Test]
        public void SplitCueIsNotAppliedWhenNothingIsCued()
        {
            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f };
            var mix = new float[BlockFrames * Channels];

            for (var i = 0; i < 10; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = 0.4f;
                }

                master.Process(mix, null, Channels);
            }

            Assert.That(mix[0], Is.EqualTo(0.4f).Within(1e-2f));
            Assert.That(mix[1], Is.EqualTo(0.4f).Within(1e-2f));
        }

        [Test]
        public void TheRecordingCapturesTheMasterNotTheCue()
        {
            // What was recorded must be what the audience heard, not what was in the
            // headphones.
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            recorder.Start(stream, SampleRate, Channels);

            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f, Recorder = recorder };
            var mix = new float[BlockFrames * Channels];
            var cue = new float[BlockFrames * Channels];

            for (var i = 0; i < 40; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = 0.25f;
                    cue[s] = -0.9f;
                }

                master.Process(mix, cue, Channels);
                recorder.Drain();
            }

            recorder.Stop();
            var bytes = stream.ToArray();

            // Every recorded sample should be near the master level and positive; the cue was
            // loud and negative, so its presence would be obvious.
            var negatives = 0;
            for (var offset = WavHeader.HeaderSize; offset + 1 < bytes.Length; offset += 2)
            {
                var value = (short)(bytes[offset] | (bytes[offset + 1] << 8));
                if (value < -1000)
                {
                    negatives++;
                }
            }

            Assert.That(negatives, Is.EqualTo(0), "the cue bus leaked into the recording");
        }

        // ------------------------------------------------------------------ master

        [Test]
        public void MasterGainRampsAndReachesItsTarget()
        {
            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f };
            var mix = new float[BlockFrames * Channels];

            for (var i = 0; i < 8; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = 0.5f;
                }

                master.Process(mix, Channels);
            }

            Assert.That(Peak(mix), Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void MasterLimiterKeepsOutputInsideUnity()
        {
            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f };
            var mix = new float[BlockFrames * Channels];

            for (var i = 0; i < 10; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = s % 2 == 0 ? 8f : -8f;
                }

                master.Process(mix, Channels);

                foreach (var sample in mix)
                {
                    Assert.That(Math.Abs(sample), Is.LessThanOrEqualTo(1f),
                        "an overloaded mix must saturate, never exceed full scale");
                }
            }
        }

        [Test]
        public void MasterReportsClipping()
        {
            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f };
            var mix = new float[BlockFrames * Channels];
            for (var i = 0; i < 10; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = 4f;
                }

                master.Process(mix, Channels);
            }

            Assert.That(master.IsClipping, Is.True);
            master.ResetClip();
            Assert.That(master.IsClipping, Is.False);
        }

        [Test]
        public void MasterTurnsNonFiniteSamplesIntoSilence()
        {
            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f };
            var mix = new float[64];
            for (var i = 0; i < mix.Length; i++)
            {
                mix[i] = i % 3 == 0 ? float.NaN : float.PositiveInfinity;
            }

            master.Process(mix, Channels);

            foreach (var sample in mix)
            {
                Assert.That(float.IsNaN(sample) || float.IsInfinity(sample), Is.False);
            }
        }

        [Test]
        public void MasterFeedsTheRecorderAfterTheLimiter()
        {
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            Assert.That(recorder.Start(stream, SampleRate, Channels), Is.True);

            var master = new MasterBus(SampleRate, Channels) { TargetGain = 1f, Recorder = recorder };
            var mix = new float[BlockFrames * Channels];

            for (var i = 0; i < 20; i++)
            {
                for (var s = 0; s < mix.Length; s++)
                {
                    mix[s] = 4f; // deliberately over full scale
                }

                master.Process(mix, Channels);
                recorder.Drain();
            }

            recorder.Stop();

            var bytes = stream.ToArray();
            Assert.That(bytes.Length, Is.GreaterThan(WavHeader.HeaderSize));

            // Every recorded sample must be inside full scale, because the limiter ran first.
            for (var offset = WavHeader.HeaderSize; offset + 1 < bytes.Length; offset += 2)
            {
                var value = (short)(bytes[offset] | (bytes[offset + 1] << 8));
                Assert.That(Math.Abs((int)value), Is.LessThanOrEqualTo(32767));
            }
        }

        [Test]
        public void MasterToleratesDegenerateArguments()
        {
            var master = new MasterBus(SampleRate, Channels);
            Assert.DoesNotThrow(() => master.Process(null, Channels));
            Assert.DoesNotThrow(() => master.Process(new float[8], 0));
        }

        [Test]
        public void TwoDecksPlayingWhileRecordingProducesAudioInTheFile()
        {
            // The §10.2 item "2デッキ再生とWAV録音を同時実行できる", at the signal-path level.
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            recorder.Start(stream, SampleRate, Channels);

            var master = new MasterBus(SampleRate, Channels) { TargetGain = 0.8f, Recorder = recorder };
            var a = PlayingChannel(0.3f);
            var b = new DeckChannel(DeckId.B);
            b.Prepare(SampleRate, Channels);
            b.Voice.SetSource(Constant(0.3f));
            b.Voice.IsPlaying = true;
            b.TargetGain = 1f;

            var mix = new float[BlockFrames * Channels];
            for (var i = 0; i < 100; i++)
            {
                Array.Clear(mix, 0, mix.Length);
                a.RenderInto(mix, null, Channels, SampleRate);
                b.RenderInto(mix, null, Channels, SampleRate);
                master.Process(mix, Channels);
                recorder.Drain();
            }

            recorder.Stop();
            var bytes = stream.ToArray();

            var loudest = 0;
            for (var offset = WavHeader.HeaderSize; offset + 1 < bytes.Length; offset += 2)
            {
                var value = (short)(bytes[offset] | (bytes[offset + 1] << 8));
                loudest = Math.Max(loudest, Math.Abs((int)value));
            }

            Assert.That(loudest, Is.GreaterThan(1000), "the recording should contain both decks, not silence");
        }
    }
}
