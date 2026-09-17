using System;
using System.Collections;
using System.IO;
using AIDeck.Audio;
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
    /// The long run of NFR-005: 30 minutes of continuous two-deck playback with no crash, no
    /// dropout and no unbounded memory growth.
    ///
    /// It is opt-in. Set <c>AIDECK_SOAK_MINUTES</c> to run it:
    ///
    /// <code>
    /// AIDECK_SOAK_MINUTES=30 Unity -batchmode -nographics -projectPath AIDeckUnity \
    ///     -runTests -testPlatform PlayMode -testResults soak.xml
    /// </code>
    ///
    /// Without the variable it reports <b>ignored</b>, never passed — §14 draws a hard line
    /// between a test that was not run and one that succeeded, and a half-hour test silently
    /// counted as passing would be exactly that mistake.
    /// </summary>
    [TestFixture]
    public class SoakTests
    {
        public const string DurationVariable = "AIDECK_SOAK_MINUTES";

        /// <summary>Managed heap growth allowed across the run before it counts as a leak.</summary>
        private const long MaxHeapGrowthBytes = 64L * 1024L * 1024L;

        /// <summary>The longest the master bus may read silent while both decks are looping.</summary>
        private const float MaxSilentSeconds = 1.5f;

        private GameObject _host;
        private AudioEngine _engine;
        private string _tonePath;
        private TrackInfo _track;

        private static double RequestedMinutes
        {
            get
            {
                var text = Environment.GetEnvironmentVariable(DurationVariable);
                return double.TryParse(text, out var minutes) && minutes > 0d ? minutes : 0d;
            }
        }

        [SetUp]
        public void SetUp()
        {
            if (RequestedMinutes <= 0d)
            {
                return;
            }

            _host = new GameObject("SoakTestHost");
            _engine = _host.AddComponent<AudioEngine>();
            _engine.Initialise(new DiagnosticLog(), new AppSettings());

            _tonePath = TestAudioFile.CreateTone(10d, 220f);
            _track = new TrackInfo(null, _tonePath, "Soak Tone", "Generator", 10d,
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

        /// <summary>
        /// Unity Test Framework applies a 180-second timeout to a <c>UnityTest</c> unless one is
        /// given, which is far short of a soak run. This ceiling is the practical limit on
        /// <see cref="DurationVariable"/>.
        /// </summary>
        public const int TimeoutMilliseconds = 45 * 60 * 1000;

        [UnityTest]
        [Timeout(TimeoutMilliseconds)]
        public IEnumerator ContinuousPlaybackForTheConfiguredDuration()
        {
            var minutes = RequestedMinutes;
            if (minutes <= 0d)
            {
                Assert.Ignore(
                    $"Set {DurationVariable} to run the soak test, e.g. {DurationVariable}=30.");
            }

            // Leaving headroom under the test timeout, so a run that would be cut off is
            // refused up front rather than reported as a failure at the 45-minute mark.
            Assert.That(minutes * 60d, Is.LessThan(TimeoutMilliseconds / 1000d - 300d),
                $"{DurationVariable}={minutes} exceeds what the test timeout allows.");

            foreach (var deck in new[] { DeckId.A, DeckId.B })
            {
                _engine.LoadTrack(deck, _track);
            }

            var loadDeadline = Time.realtimeSinceStartup + 40f;
            while (_engine.DeckA.Transport.State == DeckPlaybackState.Loading ||
                   _engine.DeckB.Transport.State == DeckPlaybackState.Loading)
            {
                if (Time.realtimeSinceStartup > loadDeadline)
                {
                    Assert.Fail("The decks did not load.");
                }

                yield return null;
            }

            Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                _engine.DeckA.Transport.ErrorReason);
            Assert.That(_engine.DeckB.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                _engine.DeckB.Transport.ErrorReason);

            var output = _host.GetComponentInChildren<AudioOutput>();
            Assert.That(output, Is.Not.Null);

            // Loop both decks so neither runs out of track over half an hour.
            foreach (var deck in new[] { DeckId.A, DeckId.B })
            {
                var model = _engine.Deck(deck);
                model.Loop.SetRegion(1d, 8d);
                Assert.That(model.Loop.Enable(), Is.True);
                model.Seek(1d);
                model.Play();
            }

            _engine.Mixer.ChannelGainA = 0.7f;
            _engine.Mixer.ChannelGainB = 0.7f;
            _engine.Mixer.MasterGain = 0.7f;

            yield return new WaitForSeconds(1f);

            if (output.BlocksRendered == 0)
            {
                Assert.Inconclusive("No audio device in this environment, so playback cannot be observed.");
            }

            GC.Collect();
            yield return null;
            var startingHeap = GC.GetTotalMemory(false);

            var started = Time.realtimeSinceStartup;
            var duration = (float)(minutes * 60d);
            var lastBlocks = output.BlocksRendered;
            var lastAudible = started;
            var lastProgressLog = started;
            var longestSilence = 0f;
            var peakHeap = startingHeap;

            while (Time.realtimeSinceStartup - started < duration)
            {
                yield return new WaitForSeconds(0.25f);

                var now = Time.realtimeSinceStartup;
                var blocks = output.BlocksRendered;

                Assert.That(blocks, Is.GreaterThan(lastBlocks),
                    $"the audio thread stopped producing after {now - started:0} s");
                lastBlocks = blocks;

                if (_engine.MasterPeak > 0.001f)
                {
                    lastAudible = now;
                }
                else
                {
                    longestSilence = Mathf.Max(longestSilence, now - lastAudible);
                    Assert.That(now - lastAudible, Is.LessThan(MaxSilentSeconds),
                        $"the master bus went silent for {now - lastAudible:0.0} s at {now - started:0} s");
                }

                Assert.That(_engine.DeckA.IsPlaying, Is.True, $"deck A stopped after {now - started:0} s");
                Assert.That(_engine.DeckB.IsPlaying, Is.True, $"deck B stopped after {now - started:0} s");
                Assert.That(_engine.DeckA.PositionSeconds, Is.InRange(0.9d, 8.2d), "deck A escaped its loop");
                Assert.That(_engine.DeckB.PositionSeconds, Is.InRange(0.9d, 8.2d), "deck B escaped its loop");

                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));

                if (now - lastProgressLog >= 60f)
                {
                    lastProgressLog = now;
                    UnityEngine.Debug.Log(
                        $"[AI Deck soak] {(now - started) / 60f:0.0} min · " +
                        $"peak {_engine.MasterPeak:0.00} · heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
                }
            }

            GC.Collect();
            yield return null;
            var endingHeap = GC.GetTotalMemory(false);
            var growth = endingHeap - startingHeap;

            UnityEngine.Debug.Log(
                $"[AI Deck soak] finished {minutes:0.#} min · heap {startingHeap / (1024 * 1024)} MB → " +
                $"{endingHeap / (1024 * 1024)} MB (peak {peakHeap / (1024 * 1024)} MB) · " +
                $"longest silence {longestSilence:0.00} s");

            Assert.That(growth, Is.LessThan(MaxHeapGrowthBytes),
                $"the managed heap grew by {growth / (1024 * 1024)} MB over {minutes:0.#} minutes");

            Assert.That(_engine.DeckA.IsPlaying, Is.True);
            Assert.That(_engine.DeckB.IsPlaying, Is.True);
        }
    }
}
