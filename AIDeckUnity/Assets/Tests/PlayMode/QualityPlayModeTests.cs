using System;
using System.Collections;
using System.Diagnostics;
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
    /// The non-functional requirements that can only be shown by running the thing: audio
    /// surviving a stalled main thread (NFR-001), the main thread not being blocked by a load
    /// (NFR-002), and a long run without a dropout or unbounded memory growth (NFR-005).
    /// </summary>
    [TestFixture]
    public class QualityPlayModeTests
    {
        private GameObject _host;
        private AudioEngine _engine;
        private string _tonePath;
        private TrackInfo _track;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("QualityTestHost");
            _engine = _host.AddComponent<AudioEngine>();
            _engine.Initialise(new DiagnosticLog(), new AppSettings());

            _tonePath = TestAudioFile.CreateTone(6d);
            _track = new TrackInfo(null, _tonePath, "Soak Tone", "Generator", 6d,
                TrackFormat.Wav, DateTime.UtcNow, 0d, new FileInfo(_tonePath).Length);
        }

        [TearDown]
        public void TearDown()
        {
            _engine?.Shutdown();

            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }

            TestAudioFile.Delete(_tonePath);
        }

        private IEnumerator LoadDeck(DeckId deck)
        {
            _engine.LoadTrack(deck, _track);

            var timeout = Time.realtimeSinceStartup + 25f;
            while (_engine.Deck(deck).Transport.State == DeckPlaybackState.Loading)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    Assert.Fail($"Deck {deck} did not finish loading.");
                }

                yield return null;
            }

            Assert.That(_engine.Deck(deck).Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                _engine.Deck(deck).Transport.ErrorReason);
        }

        private AudioOutput Output => _host.GetComponentInChildren<AudioOutput>();

        private bool AudioGraphIsRunning => Output != null && Output.BlocksRendered > 0;

        [UnityTest]
        public IEnumerator AudioKeepsRunningWhileTheMainThreadStalls()
        {
            // NFR-001. The audio path allocates nothing, takes no lock and calls no Unity API,
            // so a main thread that stops for a third of a second must not interrupt it.
            yield return LoadDeck(DeckId.A);

            _engine.Mixer.ChannelGainA = 0.8f;
            _engine.DeckA.Play();
            yield return new WaitForSeconds(0.5f);

            if (!AudioGraphIsRunning)
            {
                Assert.Inconclusive("No audio device in this environment.");
            }

            var before = Output.BlocksRendered;

            // A deliberate stall, of the kind a heavy UI rebuild would cause.
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 300)
            {
                // Busy wait: sleeping would let Unity schedule around it.
            }

            stopwatch.Stop();

            var during = Output.BlocksRendered - before;
            Assert.That(during, Is.GreaterThan(0),
                "the audio thread stopped producing while the main thread was blocked");

            // A 300 ms stall at 48 kHz is dozens of DSP blocks; a handful would mean the audio
            // thread was being starved rather than running independently.
            Assert.That(during, Is.GreaterThan(5),
                $"only {during} audio blocks were produced during a 300 ms main-thread stall");
        }

        [UnityTest]
        public IEnumerator LoadingDoesNotBlockTheMainThread()
        {
            // NFR-002. The decode runs on a coroutine, so frames must keep going while it does.
            var frames = 0;
            var longestFrameMs = 0f;

            _engine.LoadTrack(DeckId.A, _track);

            var timeout = Time.realtimeSinceStartup + 25f;
            while (_engine.DeckA.Transport.State == DeckPlaybackState.Loading)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    Assert.Fail("The load never finished.");
                }

                frames++;
                longestFrameMs = Mathf.Max(longestFrameMs, Time.unscaledDeltaTime * 1000f);
                yield return null;
            }

            Assert.That(frames, Is.GreaterThan(3), "the load appears to have blocked the main thread");
            Assert.That(longestFrameMs, Is.LessThan(2000f),
                $"the longest frame during a load was {longestFrameMs:0} ms");
        }

        [UnityTest]
        public IEnumerator ShortSoakKeepsPlayingWithoutDrift()
        {
            // A fast version of the 30-minute run, so the shape is covered on every test run.
            // The full duration is exercised by SoakTests, which is excluded by default.
            yield return LoadDeck(DeckId.A);
            yield return LoadDeck(DeckId.B);

            if (!AudioGraphIsRunning)
            {
                yield return new WaitForSeconds(0.3f);
            }

            if (!AudioGraphIsRunning)
            {
                Assert.Inconclusive("No audio device in this environment.");
            }

            // Loop both decks so neither runs out of track.
            foreach (var deck in new[] { DeckId.A, DeckId.B })
            {
                var model = _engine.Deck(deck);
                model.Loop.SetRegion(1d, 3d);
                Assert.That(model.Loop.Enable(), Is.True);
                model.Seek(1d);
                model.Play();
            }

            yield return new WaitForSeconds(5f);

            Assert.That(_engine.DeckA.IsPlaying, Is.True);
            Assert.That(_engine.DeckB.IsPlaying, Is.True);
            Assert.That(_engine.DeckA.PositionSeconds, Is.InRange(1d, 3.05d),
                "deck A escaped its loop");
            Assert.That(_engine.DeckB.PositionSeconds, Is.InRange(1d, 3.05d),
                "deck B escaped its loop");
            Assert.That(_engine.MasterPeak, Is.GreaterThan(0f), "the master bus fell silent");
        }

        [UnityTest]
        public IEnumerator DiagnosticLogNeverCarriesAFullPath()
        {
            // NFR-006. A recording failure is the most likely place for a path to leak, because
            // the underlying exception message contains one.
            var settings = new AppSettings { RecordingFolder = "/definitely/not/a/folder/aideck" };
            var log = new DiagnosticLog();

            var host = new GameObject("PathLeakTestHost");
            try
            {
                var engine = host.AddComponent<AudioEngine>();
                engine.Initialise(log, settings);

                Assert.That(engine.StartRecording(), Is.False);

                foreach (var entry in log.Recent(100))
                {
                    Assert.That(entry.Message, Does.Not.Contain("/definitely/not/a/folder"),
                        "a full path reached the diagnostic log");
                }

                engine.Shutdown();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator TheSnapshotCarriesAFileNameNotAPath()
        {
            // NFR-006: the recording's location stays on the host.
            yield return LoadDeck(DeckId.A);

            Assert.That(_engine.StartRecording(), Is.True, _engine.RecordingFailure);
            yield return new WaitForSeconds(0.3f);

            var snapshot = _engine.BuildSnapshot(0);
            Assert.That(snapshot.RecordingFileName, Is.Not.Empty);
            Assert.That(snapshot.RecordingFileName, Does.Not.Contain("/"),
                "the full recording path must not travel the wire");

            var path = _engine.StopRecording();
            TestAudioFile.Delete(path);
        }
    }
}
