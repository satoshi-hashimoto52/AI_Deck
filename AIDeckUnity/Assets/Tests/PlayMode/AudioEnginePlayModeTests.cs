using System;
using System.Collections;
using System.IO;
using AIDeck.Audio;
using AIDeck.Core.Audio;
using AIDeck.Core.Deck;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Model;
using AIDeck.Core.Settings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// Exercises the real <see cref="AudioEngine"/> in a running player: loading a real file,
    /// two decks playing at once, and recording the master output (§10.2).
    ///
    /// Batch mode may run with no audio device, in which case Unity never calls
    /// <c>OnAudioFilterRead</c> and no samples can be produced. Tests that depend on that are
    /// reported <b>inconclusive</b> rather than passed — §14 draws a hard line between a test
    /// that was not run and a test that succeeded.
    /// </summary>
    [TestFixture]
    public class AudioEnginePlayModeTests
    {
        private GameObject _host;
        private AudioEngine _engine;
        private string _tonePath;
        private TrackInfo _track;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("AudioEngineTestHost");
            _engine = _host.AddComponent<AudioEngine>();
            _engine.Initialise(new DiagnosticLog(), new AppSettings());

            _tonePath = TestAudioFile.CreateTone(3d);
            _track = new TrackInfo(
                null, _tonePath, "Test Tone", "Generator", 3d,
                TrackFormat.Wav, DateTime.UtcNow, 0d, new FileInfo(_tonePath).Length);
        }

        [TearDown]
        public void TearDown()
        {
            if (_engine != null)
            {
                _engine.Shutdown();
            }

            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }

            TestAudioFile.Delete(_tonePath);
        }

        private IEnumerator LoadDeck(DeckId deck)
        {
            _engine.LoadTrack(deck, _track);

            var timeout = Time.realtimeSinceStartup + 20f;
            while (_engine.Deck(deck).Transport.State == DeckPlaybackState.Loading)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    Assert.Fail($"Deck {deck} did not finish loading within 20 s.");
                }

                yield return null;
            }
        }

        /// <summary>True when Unity is actually producing audio callbacks in this environment.</summary>
        private bool AudioGraphIsRunning
        {
            get
            {
                var output = _host.GetComponentInChildren<AudioOutput>();
                return output != null && output.BlocksRendered > 0;
            }
        }

        [UnityTest]
        public IEnumerator EngineInitialisesWithASaneOutputFormat()
        {
            Assert.That(_engine.SampleRate, Is.GreaterThan(0));
            Assert.That(_engine.ChannelCount, Is.InRange(1, 8));
            Assert.That(_engine.DeckA, Is.Not.Null);
            Assert.That(_engine.DeckB, Is.Not.Null);
            Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Empty));
            yield return null;
        }

        [UnityTest]
        public IEnumerator LoadingARealFilePopulatesTheDeck()
        {
            yield return LoadDeck(DeckId.A);

            var model = _engine.DeckA;
            Assert.That(model.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                "load failure: " + model.Transport.ErrorReason);
            Assert.That(model.TrackId, Is.EqualTo(_track.Id));
            Assert.That(model.TrackLengthSeconds, Is.EqualTo(3d).Within(0.1d));
            Assert.That(_engine.Channel(DeckId.A).Voice.HasSource, Is.True);
        }

        [UnityTest]
        public IEnumerator LoadingAMissingFileFailsWithoutDisturbingTheOtherDeck()
        {
            yield return LoadDeck(DeckId.A);
            Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused));

            var missing = new TrackInfo(
                null, Path.Combine(Path.GetTempPath(), "aideck-not-here.mp3"), "Missing", "—",
                0d, TrackFormat.Mp3, DateTime.UtcNow);

            _engine.LoadTrack(DeckId.B, missing);
            var timeout = Time.realtimeSinceStartup + 20f;
            while (_engine.DeckB.Transport.State == DeckPlaybackState.Loading)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    Assert.Fail("The failing load never settled.");
                }

                yield return null;
            }

            Assert.That(_engine.DeckB.Transport.State, Is.EqualTo(DeckPlaybackState.Error));
            Assert.That(_engine.DeckB.Transport.ErrorReason, Is.Not.Empty);
            Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                "one deck failing must not disturb the other");
        }

        [UnityTest]
        public IEnumerator EjectReturnsTheDeckToEmpty()
        {
            yield return LoadDeck(DeckId.A);
            _engine.DeckA.Play();
            yield return null;

            _engine.Eject(DeckId.A);
            yield return null;

            Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Empty));
            Assert.That(_engine.Channel(DeckId.A).Voice.HasSource, Is.False);
        }

        [UnityTest]
        public IEnumerator PlayingAdvancesThePlayhead()
        {
            yield return LoadDeck(DeckId.A);

            if (!AudioGraphIsRunning)
            {
                yield return new WaitForSeconds(0.25f);
            }

            if (!AudioGraphIsRunning)
            {
                Assert.Inconclusive("No audio device in this environment, so playback cannot be observed.");
            }

            _engine.DeckA.Play();
            yield return new WaitForSeconds(0.5f);

            Assert.That(_engine.DeckA.PositionSeconds, Is.GreaterThan(0.1d));
            Assert.That(_engine.DeckA.IsPlaying, Is.True);
        }

        [UnityTest]
        public IEnumerator PauseStopsTheVoiceOnlyAfterTheFade()
        {
            yield return LoadDeck(DeckId.A);

            if (!AudioGraphIsRunning)
            {
                yield return new WaitForSeconds(0.25f);
            }

            if (!AudioGraphIsRunning)
            {
                Assert.Inconclusive("No audio device in this environment, so the fade cannot be observed.");
            }

            _engine.DeckA.Play();
            yield return new WaitForSeconds(0.3f);

            _engine.DeckA.Pause();

            // The voice must still be running for at least one frame while the gain ramps out:
            // stopping it instantly would be the click that §9 forbids.
            yield return null;
            var stoppedImmediately = !_engine.Channel(DeckId.A).Voice.IsPlaying;

            yield return new WaitForSeconds(0.3f);
            Assert.That(_engine.Channel(DeckId.A).Voice.IsPlaying, Is.False, "the voice must stop after the fade");
            Assert.That(stoppedImmediately, Is.False, "the voice must not be cut before the fade runs");
        }

        [UnityTest]
        public IEnumerator TwoDecksPlayTogetherAndRecordToAWavFile()
        {
            yield return LoadDeck(DeckId.A);
            yield return LoadDeck(DeckId.B);

            if (!AudioGraphIsRunning)
            {
                yield return new WaitForSeconds(0.25f);
            }

            _engine.Mixer.Crossfader = 0f;
            _engine.Mixer.ChannelGainA = 0.8f;
            _engine.Mixer.ChannelGainB = 0.8f;
            _engine.Mixer.MasterGain = 0.8f;

            Assert.That(_engine.StartRecording(), Is.True, _engine.RecordingFailure);
            var path = _engine.RecordingPath;

            _engine.DeckA.Play();
            _engine.DeckB.Play();
            yield return new WaitForSeconds(1.2f);

            Assert.That(_engine.DeckA.IsPlaying, Is.True);
            Assert.That(_engine.DeckB.IsPlaying, Is.True);

            var saved = _engine.StopRecording();
            Assert.That(saved, Is.EqualTo(path));
            Assert.That(File.Exists(path), Is.True);

            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length, Is.GreaterThan(WavHeader.HeaderSize), "the recording has no data chunk");
            Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("RIFF"));
            Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 8, 4), Is.EqualTo("WAVE"));

            try
            {
                if (!AudioGraphIsRunning)
                {
                    Assert.Inconclusive(
                        "The WAV file is structurally valid, but there is no audio device here, " +
                        "so whether it contains audio could not be checked.");
                }

                var loudest = 0;
                for (var offset = WavHeader.HeaderSize; offset + 1 < bytes.Length; offset += 2)
                {
                    var value = (short)(bytes[offset] | (bytes[offset + 1] << 8));
                    loudest = Math.Max(loudest, Math.Abs((int)value));
                }

                Assert.That(loudest, Is.GreaterThan(1000), "the recording is silent");
            }
            finally
            {
                TestAudioFile.Delete(path);
            }
        }

        [UnityTest]
        public IEnumerator AllStopReleasesGesturesAndStopsBothDecks()
        {
            yield return LoadDeck(DeckId.A);
            yield return LoadDeck(DeckId.B);

            _engine.DeckA.Play();
            _engine.DeckB.Play();
            _engine.DeckA.Motion.BeginScratch();
            _engine.DeckA.Motion.UpdateScratch(-3f);
            yield return null;

            _engine.AllStop(true);
            yield return null;

            Assert.That(_engine.DeckA.IsPlaying, Is.False);
            Assert.That(_engine.DeckB.IsPlaying, Is.False);
            Assert.That(_engine.DeckA.Motion.Mode, Is.EqualTo(MotionMode.Normal));
            Assert.That(_engine.DeckA.Motion.Rate, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator DisconnectPolicyDefaultsToStoppingPlayback()
        {
            yield return LoadDeck(DeckId.A);
            _engine.DeckA.Play();
            yield return null;

            // §9 requires the safe stop as the default when the controller disappears.
            _engine.ApplyDisconnectPolicy();
            yield return null;

            Assert.That(_engine.DeckA.IsPlaying, Is.False);
        }

        [UnityTest]
        public IEnumerator RecordingFailureDoesNotDisturbPlayback()
        {
            yield return LoadDeck(DeckId.A);
            _engine.DeckA.Play();
            yield return null;

            var settings = new AppSettings { RecordingFolder = "/dev/null/definitely-not-a-folder" };
            var engineHost = new GameObject("FailingRecorderHost");
            try
            {
                var engine = engineHost.AddComponent<AudioEngine>();
                engine.Initialise(new DiagnosticLog(), settings);

                Assert.That(engine.StartRecording(), Is.False);
                Assert.That(engine.IsRecording, Is.False);

                // The original engine's deck is untouched (FR-055).
                Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Playing));
                engine.Shutdown();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(engineHost);
            }
        }

        [UnityTest]
        public IEnumerator ShutdownLeavesNothingPlaying()
        {
            yield return LoadDeck(DeckId.A);
            _engine.DeckA.Play();
            yield return null;

            _engine.Shutdown();
            yield return null;

            Assert.That(_engine.Channel(DeckId.A).Voice.IsPlaying, Is.False);
            Assert.That(_engine.Channel(DeckId.A).Voice.HasSource, Is.False);
        }
    }
}
