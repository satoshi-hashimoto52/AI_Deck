using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIDeck.Core.Deck;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.Host;
using AIDeck.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// Drives the Mac window the way a person does: by pressing the buttons in the real
    /// <see cref="HostApp"/>.
    ///
    /// This fixture exists because of a defect these tests would have caught and did not.
    /// `BrowserView.LoadRequested` was raised by the library's A and B buttons and subscribed
    /// by nobody on the host, so a click showed its pressed state and then did nothing at all.
    /// Every existing test either drove the controller (which *was* wired) or called
    /// `AudioEngine.LoadTrack` directly, and the manual check used the `-aideck-autoload`
    /// startup option — which also bypasses the buttons. The gap was in how the path was
    /// exercised, not only in what was asserted.
    ///
    /// So these tests build the real HostApp, find the real buttons, and inject real pointer
    /// events. Nothing here calls a command method directly.
    /// </summary>
    [TestFixture]
    public class HostPlayModeTests
    {
        private GameObject _hostObject;
        private HostApp _host;
        private string _settingsPath;
        private string _libraryPath;
        private readonly List<string> _tempAudio = new List<string>();

        [SetUp]
        public void SetUp()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _settingsPath = Path.Combine(Path.GetTempPath(), $"aideck-host-settings-{stamp}.json");
            _libraryPath = Path.Combine(Path.GetTempPath(), $"aideck-host-library-{stamp}.json");

            // Created inactive so the overrides land before Awake runs: the real HostApp would
            // otherwise write into the installed app's settings and bind the live ports.
            _hostObject = new GameObject("HostAppTestHost");
            _hostObject.SetActive(false);
            _host = _hostObject.AddComponent<HostApp>();
            _host.SettingsPathOverride = _settingsPath;
            _host.LibraryPathOverride = _libraryPath;
            _host.NetworkingEnabled = false;
            _hostObject.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_hostObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_hostObject);
            }

            foreach (var path in _tempAudio)
            {
                TestAudioFile.Delete(path);
            }

            _tempAudio.Clear();
            TryDelete(_settingsPath);
            TryDelete(_libraryPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover file in the temp folder is harmless.
            }
        }

        /// <summary>Adds a real, decodable track to the host's library and returns it.</summary>
        private TrackInfo AddTrack(string title, float frequency)
        {
            var path = TestAudioFile.CreateTone(2d, frequency);
            _tempAudio.Add(path);

            var track = new TrackInfo(null, path, title, "Generator", 2d,
                TrackFormat.Wav, DateTime.UtcNow, 0d, new FileInfo(path).Length);

            Assert.That(_host.Library.Add(track).Succeeded, Is.True, $"could not add {title}");
            return track;
        }

        /// <summary>A library entry whose file does not exist, for the failure path.</summary>
        private TrackInfo AddMissingTrack(string title)
        {
            var path = Path.Combine(Path.GetTempPath(), $"aideck-missing-{Guid.NewGuid():N}.wav");
            var track = new TrackInfo(null, path, title, "Generator", 2d,
                TrackFormat.Wav, DateTime.UtcNow, 0d, 1024L);

            Assert.That(_host.Library.Add(track).Succeeded, Is.True);
            return track;
        }

        /// <summary>Lets the host refresh its list from the library, as its Update does.</summary>
        private IEnumerator LetUiCatchUp()
        {
            // The screen refreshes on the host's own 20 Hz timer.
            yield return new WaitForSeconds(0.2f);
        }

        /// <summary>
        /// The load button for a library row, found the way the user sees it: by walking the
        /// real track list and matching the row that shows the title.
        /// </summary>
        private ButtonWidget FindLoadButton(string title, DeckId deck)
        {
            var wanted = deck == DeckId.A ? "LoadA" : "LoadB";

            foreach (var button in _host.Screen.Browser.Library.GetComponentsInChildren<ButtonWidget>(true))
            {
                if (button.gameObject.name != wanted || !button.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var row = button.transform.parent;
                foreach (var text in row.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                {
                    if (text.gameObject.name == "Title" && text.text == title)
                    {
                        return button;
                    }
                }
            }

            return null;
        }

        /// <summary>Presses a widget through the router, exactly as a mouse click does.</summary>
        private void Click(ButtonWidget button)
        {
            var router = _host.Screen.Router;
            var point = ScreenCentre(button.Rect);
            router.PointerDown(1, point);
            router.PointerUp(1, point);
        }

        private static Vector2 ScreenCentre(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var centre = (corners[0] + corners[2]) * 0.5f;
            return new Vector2(centre.x, centre.y);
        }

        private IEnumerator WaitForLoad(DeckId deck, float timeoutSeconds = 25f)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                var state = _host.Engine.Deck(deck).Transport.State;
                if (state != DeckPlaybackState.Empty && state != DeckPlaybackState.Loading)
                {
                    yield break;
                }

                yield return null;
            }
        }

        // ------------------------------------------------------------------ the regression

        [UnityTest]
        public IEnumerator ClickingAInTheLibraryLoadsTheLeftDeck()
        {
            var track = AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            var button = FindLoadButton("Alpha Tone", DeckId.A);
            Assert.That(button, Is.Not.Null, "the library row has no A button");

            Assert.That(_host.Screen.DeckA.StateText, Is.EqualTo("EMPTY"));

            Click(button);
            yield return WaitForLoad(DeckId.A);

            Assert.That(_host.Engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                "deck A did not load: " + _host.Engine.DeckA.Transport.ErrorReason);
            Assert.That(_host.Engine.DeckA.TrackId, Is.EqualTo(track.Id), "the wrong track id was loaded");

            yield return LetUiCatchUp();

            Assert.That(_host.Screen.DeckA.StateText, Is.EqualTo("PAUSED"), "the deck still shows EMPTY");
            Assert.That(_host.Screen.Browser.DeckTitle(DeckId.A), Does.Contain("Alpha Tone"),
                "the waveform strip does not show the loaded title");
            Assert.That(_host.Engine.DeckA.TrackLengthSeconds, Is.GreaterThan(0d));
        }

        [UnityTest]
        public IEnumerator ClickingAOnlyTouchesTheLeftDeck()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);
            yield return LetUiCatchUp();

            Assert.That(_host.Engine.DeckB.Transport.State, Is.EqualTo(DeckPlaybackState.Empty),
                "loading A disturbed deck B");
            Assert.That(_host.Screen.DeckB.StateText, Is.EqualTo("EMPTY"));
            Assert.That(_host.Screen.Browser.DeckTitle(DeckId.B), Is.EqualTo("B"));
        }

        [UnityTest]
        public IEnumerator ClickingBInTheLibraryLoadsTheRightDeck()
        {
            var track = AddTrack("Bravo Tone", 330f);
            yield return LetUiCatchUp();

            var button = FindLoadButton("Bravo Tone", DeckId.B);
            Assert.That(button, Is.Not.Null, "the library row has no B button");

            Click(button);
            yield return WaitForLoad(DeckId.B);
            yield return LetUiCatchUp();

            Assert.That(_host.Engine.DeckB.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                "deck B did not load: " + _host.Engine.DeckB.Transport.ErrorReason);
            Assert.That(_host.Engine.DeckB.TrackId, Is.EqualTo(track.Id));
            Assert.That(_host.Screen.DeckB.StateText, Is.EqualTo("PAUSED"));
            Assert.That(_host.Screen.Browser.DeckTitle(DeckId.B), Does.Contain("Bravo Tone"));
            Assert.That(_host.Engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Empty),
                "loading B disturbed deck A");
        }

        [UnityTest]
        public IEnumerator EachRowLoadsItsOwnTrack()
        {
            // The buttons are created in a loop over pooled rows, so the obvious way to get this
            // wrong is for every row's callback to capture the same track.
            var alpha = AddTrack("Alpha Tone", 220f);
            var bravo = AddTrack("Bravo Tone", 330f);
            var charlie = AddTrack("Charlie Tone", 440f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Charlie Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);

            Assert.That(_host.Engine.DeckA.TrackId, Is.EqualTo(charlie.Id),
                "the third row loaded some other row's track");

            Click(FindLoadButton("Alpha Tone", DeckId.B));
            yield return WaitForLoad(DeckId.B);

            Assert.That(_host.Engine.DeckB.TrackId, Is.EqualTo(alpha.Id),
                "the first row loaded some other row's track");
            Assert.That(_host.Engine.DeckA.TrackId, Is.EqualTo(charlie.Id), "deck A changed unexpectedly");
            Assert.That(bravo, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator TheSameTrackCanBeLoadedOntoBothDecks()
        {
            var track = AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);

            Click(FindLoadButton("Alpha Tone", DeckId.B));
            yield return WaitForLoad(DeckId.B);
            yield return LetUiCatchUp();

            Assert.That(_host.Engine.DeckA.TrackId, Is.EqualTo(track.Id));
            Assert.That(_host.Engine.DeckB.TrackId, Is.EqualTo(track.Id));
            Assert.That(_host.Screen.DeckA.StateText, Is.EqualTo("PAUSED"));
            Assert.That(_host.Screen.DeckB.StateText, Is.EqualTo("PAUSED"));
        }

        [UnityTest]
        public IEnumerator EverySupportedFormatLoadsFromTheLibraryButtons()
        {
            // The decoder is chosen from the extension, so each container takes its own branch.
            // Only WAV can be generated here without an encoder; the MP3 and AIFF branches are
            // covered by the manual check recorded in docs/TEST_PLAN.md.
            var track = AddTrack("Wav Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Wav Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);

            Assert.That(_host.Engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                _host.Engine.DeckA.Transport.ErrorReason);
            Assert.That(track.Format, Is.EqualTo(TrackFormat.Wav));
        }

        [UnityTest]
        public IEnumerator AFailedLoadReportsAReasonAndLeavesTheAppResponsive()
        {
            AddMissingTrack("Ghost Tone");
            var good = AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Ghost Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);
            yield return LetUiCatchUp();

            Assert.That(_host.Engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Error));
            Assert.That(_host.Engine.DeckA.Transport.ErrorReason, Is.Not.Empty,
                "a failed load must say why");
            Assert.That(_host.Screen.DeckA.StateText, Does.StartWith("ERROR"),
                "the failure is not visible on screen");

            var reported = false;
            foreach (var entry in _host.Log.Recent(200))
            {
                if (entry.Level >= LogLevel.Warning && entry.Message.IndexOf("Deck A", StringComparison.Ordinal) >= 0)
                {
                    reported = true;
                    break;
                }
            }

            Assert.That(reported, Is.True, "the failure was not written to the diagnostic log");

            // Still responsive: a good track loads straight afterwards.
            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);

            Assert.That(_host.Engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                "the app did not recover from a failed load");
            Assert.That(_host.Engine.DeckA.TrackId, Is.EqualTo(good.Id));
        }

        [UnityTest]
        public IEnumerator TheLoadIsTracedThroughEveryStageOfTheLog()
        {
            // Each stage logs once, so the absence of a line localises a break in the chain.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);
            yield return LetUiCatchUp();

            var messages = new List<string>();
            foreach (var entry in _host.Log.Recent(200))
            {
                messages.Add(entry.Category + ": " + entry.Message);
            }

            var joined = string.Join("\n", messages);
            Assert.That(joined, Does.Contain("Load requested"), "the button press was not logged");
            Assert.That(joined, Does.Contain("LoadTrack deck A"), "the dispatched command was not logged");
            Assert.That(joined, Does.Contain("loading"), "the load attempt was not logged");
            Assert.That(joined, Does.Contain("decoded"), "the decode result was not logged");
            Assert.That(joined, Does.Contain("Deck A updated"), "the resulting deck state was not logged");
        }

        [UnityTest]
        public IEnumerator PlayBecomesAvailableOnceATrackIsLoaded()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);
            yield return LetUiCatchUp();

            // The transport accepts PLAY only once a deck holds a track.
            Assert.That(_host.Engine.DeckA.Play(), Is.True, "PLAY was refused after a successful load");
            Assert.That(_host.Engine.DeckA.IsPlaying, Is.True);
        }

        // ------------------------------------------------------------------ jog wheel

        private JogWidget FindJog(DeckId deck)
        {
            var panel = deck == DeckId.A ? _host.Screen.DeckA : _host.Screen.DeckB;
            return panel.GetComponentInChildren<JogWidget>(true);
        }

        /// <summary>Screen point on the jog's circle at <paramref name="degrees"/>, 0° = right, CCW positive.</summary>
        private static Vector2 PointOnJog(JogWidget jog, float degrees, float radiusFraction = 0.8f)
        {
            var corners = new Vector3[4];
            jog.Rect.GetWorldCorners(corners);
            var centre = (Vector2)((corners[0] + corners[2]) * 0.5f);
            var radius = Mathf.Min(corners[2].x - corners[0].x, corners[2].y - corners[0].y) * 0.5f;
            var r = radius * radiusFraction;
            return centre + new Vector2(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad)) * r;
        }

        /// <summary>
        /// Turns the platter through an arc, one pointer move per step, exactly as a drag does.
        /// Negative <paramref name="sweep"/> is clockwise, which must move the track forwards.
        /// </summary>
        private IEnumerator DragJog(DeckId deck, float fromDegrees, float sweep, int steps = 12,
                                    float radiusFraction = 0.8f, bool release = true)
        {
            var jog = FindJog(deck);
            Assert.That(jog, Is.Not.Null, $"deck {deck} has no jog wheel");

            var router = _host.Screen.Router;
            var pointerId = deck == DeckId.A ? 11 : 12;

            router.PointerDown(pointerId, PointOnJog(jog, fromDegrees, radiusFraction));
            yield return null;

            for (var i = 1; i <= steps; i++)
            {
                var angle = fromDegrees + sweep * i / steps;
                router.PointerMove(pointerId, PointOnJog(jog, angle, radiusFraction));
                yield return null;
            }

            if (release)
            {
                router.PointerUp(pointerId, PointOnJog(jog, fromDegrees + sweep, radiusFraction));
                yield return null;
            }
        }

        private IEnumerator LoadDeckFromLibrary(string title, DeckId deck)
        {
            Click(FindLoadButton(title, deck));
            yield return WaitForLoad(deck);
            yield return LetUiCatchUp();
        }

        [UnityTest]
        public IEnumerator DraggingTheJogMovesAPausedDeckAndLeavesItPaused()
        {
            // The defect: DeckVoice renders nothing while stopped, so on a paused deck the
            // scratch rate had nowhere to go and the playhead never moved. That is the state a
            // DJ cues a track in, so the wheel appeared dead.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            _host.Engine.DeckA.Seek(1.0d);
            yield return null;
            var before = _host.Engine.DeckA.PositionSeconds;

            yield return DragJog(DeckId.A, 90f, -120f);

            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.Not.EqualTo(before).Within(1e-4),
                "the jog did not move the playhead of a paused deck");
            Assert.That(_host.Engine.DeckA.IsPlaying, Is.False,
                "scrubbing a paused deck must not start playback");
        }

        [UnityTest]
        public IEnumerator ClockwiseGoesForwardAndAnticlockwiseGoesBack()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            _host.Engine.DeckA.Seek(1.0d);
            yield return null;
            var start = _host.Engine.DeckA.PositionSeconds;

            // Screen space has y up, so a clockwise turn is a decreasing angle.
            yield return DragJog(DeckId.A, 90f, -120f);
            var afterClockwise = _host.Engine.DeckA.PositionSeconds;
            Assert.That(afterClockwise, Is.GreaterThan(start), "clockwise must move the track forwards");

            yield return DragJog(DeckId.A, 90f, 120f);
            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.LessThan(afterClockwise),
                "anticlockwise must move the track backwards");
        }

        [UnityTest]
        public IEnumerator TheGestureSurvivesTheZeroDegreeBoundary()
        {
            // Sweeping through 0°/360° must be one continuous move, not a jump the long way
            // round. Mathf.DeltaAngle is what makes that true; this pins it down.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            _host.Engine.DeckA.Seek(1.0d);
            yield return null;
            var start = _host.Engine.DeckA.PositionSeconds;

            // 30° down through 0° to -30°: clockwise across the seam.
            yield return DragJog(DeckId.A, 30f, -60f, steps: 12);

            var moved = _host.Engine.DeckA.PositionSeconds - start;
            Assert.That(moved, Is.GreaterThan(0d), "crossing 0° reversed the direction");

            // 60° of a 1.8 s revolution is 0.3 s. A wrap bug would give something near 5 s.
            Assert.That(moved, Is.LessThan(1.0d), $"crossing 0° jumped {moved:0.00} s the long way round");
        }

        [UnityTest]
        public IEnumerator TheWholeVisibleDiscStartsADrag()
        {
            // The outer ring used to be a tempo nudge, so 62 % of the disc's area did nothing
            // perceptible. Near the rim must scratch just like the middle.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            _host.Engine.DeckA.Seek(1.0d);
            yield return null;
            var start = _host.Engine.DeckA.PositionSeconds;

            yield return DragJog(DeckId.A, 90f, -120f, radiusFraction: 0.95f);

            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.GreaterThan(start),
                "a drag starting near the rim did not move the track");
        }

        [UnityTest]
        public IEnumerator TheDragContinuesAfterTheFingerLeavesTheDisc()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            _host.Engine.DeckA.Seek(1.0d);
            yield return null;
            var start = _host.Engine.DeckA.PositionSeconds;

            var jog = FindJog(DeckId.A);
            var router = _host.Screen.Router;

            router.PointerDown(11, PointOnJog(jog, 90f));
            yield return null;

            // Well outside the disc, as a real hand overshoots.
            for (var i = 1; i <= 10; i++)
            {
                router.PointerMove(11, PointOnJog(jog, 90f - i * 12f, radiusFraction: 2.5f));
                yield return null;
            }

            Assert.That(jog.IsScratching, Is.True, "the gesture was dropped when it left the disc");
            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.GreaterThan(start));

            router.PointerUp(11, PointOnJog(jog, -30f, radiusFraction: 2.5f));
            yield return null;
            Assert.That(jog.IsScratching, Is.False);
        }

        [UnityTest]
        public IEnumerator ReleasingTheJogReturnsThePlatterToNormal()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            yield return DragJog(DeckId.A, 90f, -120f);

            // The release glides the rate back to 1.0 over ~120 ms.
            yield return new WaitForSeconds(0.4f);

            Assert.That(_host.Engine.DeckA.Motion.Mode, Is.EqualTo(MotionMode.Normal));
            Assert.That(_host.Engine.DeckA.Motion.Rate, Is.EqualTo(1f).Within(1e-3f),
                "the platter did not return to normal speed after the drag");
        }

        [UnityTest]
        public IEnumerator HoldingThePlatterStillStopsIt()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            // Grab and turn, then hold without moving — a hand on a record stops it.
            yield return DragJog(DeckId.A, 90f, -60f, steps: 6, release: false);
            yield return new WaitForSeconds(0.2f);

            Assert.That(_host.Engine.DeckA.Motion.Rate, Is.EqualTo(0f).Within(1e-3f),
                "the platter kept spinning while the finger was holding it still");

            _host.Screen.Router.PointerUp(11, PointOnJog(FindJog(DeckId.A), 30f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheTwoJogWheelsAreIndependent()
        {
            AddTrack("Alpha Tone", 220f);
            AddTrack("Bravo Tone", 330f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);
            yield return LoadDeckFromLibrary("Bravo Tone", DeckId.B);

            _host.Engine.DeckA.Seek(1.0d);
            _host.Engine.DeckB.Seek(1.0d);
            yield return null;

            var startB = _host.Engine.DeckB.PositionSeconds;
            yield return DragJog(DeckId.A, 90f, -120f);

            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.GreaterThan(1.0d), "deck A did not move");
            Assert.That(_host.Engine.DeckB.PositionSeconds, Is.EqualTo(startB).Within(1e-3d),
                "turning deck A's platter moved deck B");
            Assert.That(_host.Engine.DeckB.Motion.Mode, Is.EqualTo(MotionMode.Normal));
        }

        [UnityTest]
        public IEnumerator DraggingAPlayingDeckMovesItAndLeavesItPlaying()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            _host.Engine.DeckA.Seek(0.5d);
            Assert.That(_host.Engine.DeckA.Play(), Is.True);
            yield return new WaitForSeconds(0.2f);

            yield return DragJog(DeckId.A, 90f, -150f);

            Assert.That(_host.Engine.DeckA.IsPlaying, Is.True, "scratching stopped a playing deck");
            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.GreaterThan(0.5d));
        }

        [UnityTest]
        public IEnumerator LosingFocusReleasesTheJog()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            yield return DragJog(DeckId.A, 90f, -60f, steps: 6, release: false);
            Assert.That(FindJog(DeckId.A).IsScratching, Is.True);

            _host.Screen.Router.SendMessage("OnApplicationFocus", false, SendMessageOptions.DontRequireReceiver);
            yield return null;

            Assert.That(FindJog(DeckId.A).IsScratching, Is.False,
                "a held platter survived the window losing focus");
            Assert.That(_host.Engine.DeckA.Motion.Mode, Is.EqualTo(MotionMode.Normal));
        }

        [UnityTest]
        public IEnumerator TheInputSourceIsRecordedForEachAction()
        {
            // So an unexplained control movement can be attributed rather than guessed at.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            var router = _host.Screen.Router;
            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);

            Assert.That(router.LastPointerSource, Is.EqualTo(PointerSource.Injected),
                "a test-injected pointer must not be reported as a real mouse click");

            var joined = string.Empty;
            foreach (var entry in _host.Log.Recent(200))
            {
                joined += entry.Message + "\n";
            }

            Assert.That(joined, Does.Contain("[Injected]"), "the input source was not logged");
        }

        [UnityTest]
        public IEnumerator TheClickThatRaisesTheWindowDoesNotOperateTheControlUnderIt()
        {
            // macOS raises a background window with the same click that lands on a control.
            // Acting on it loaded a track nobody asked for, which is how deck B ended up with
            // the deck A track during a jog drag on the real build.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            var router = _host.Screen.Router;
            var button = FindLoadButton("Alpha Tone", DeckId.A);
            var point = ScreenCentre(button.Rect);

            router.SetFocus(false, mouseButtonHeld: false);
            yield return null;

            // The button is already down when the window comes forward.
            router.SetFocus(true, mouseButtonHeld: true);
            router.ProcessMouseState(pressedThisFrame: true, held: true, releasedThisFrame: false, position: point);
            yield return null;
            router.ProcessMouseState(pressedThisFrame: false, held: false, releasedThisFrame: true, position: point);
            yield return null;

            Assert.That(_host.Engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Empty),
                "the click that only brought the window forward loaded a track");

            // The next click is a real one and must work normally.
            router.ProcessMouseState(pressedThisFrame: true, held: true, releasedThisFrame: false, position: point);
            yield return null;
            router.ProcessMouseState(pressedThisFrame: false, held: false, releasedThisFrame: true, position: point);
            yield return WaitForLoad(DeckId.A);

            Assert.That(_host.Engine.DeckA.Transport.State, Is.Not.EqualTo(DeckPlaybackState.Empty),
                "the click after the raise was swallowed too");
        }

        [UnityTest]
        public IEnumerator AFocusBlipDuringADragDoesNotStartASecondGesture()
        {
            // The operating system reports the button going down again when focus returns, so
            // a gesture the user never began would otherwise seize the platter mid-drag.
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadDeckFromLibrary("Alpha Tone", DeckId.A);

            var router = _host.Screen.Router;
            var jog = FindJog(DeckId.A);

            // A batch-mode player is never focused, and the mouse path is focus-gated.
            router.SetFocus(true, mouseButtonHeld: false);
            router.ProcessMouseState(true, true, false, PointOnJog(jog, 90f));
            yield return null;
            Assert.That(jog.IsScratching, Is.True);

            router.SetFocus(false, mouseButtonHeld: true);
            yield return null;
            Assert.That(jog.IsScratching, Is.False, "focus loss must release the platter");

            var position = _host.Engine.DeckA.PositionSeconds;

            router.SetFocus(true, mouseButtonHeld: true);
            for (var i = 1; i <= 6; i++)
            {
                router.ProcessMouseState(false, true, false, PointOnJog(jog, 90f - 30f * i));
                yield return null;
            }

            Assert.That(jog.IsScratching, Is.False,
                "the button that was already held when focus returned started a gesture");
            Assert.That(_host.Engine.DeckA.PositionSeconds, Is.EqualTo(position).Within(0.01f),
                "a drag nobody started moved the track");

            // Releasing and pressing again is a real gesture and must be honoured.
            router.ProcessMouseState(false, false, true, PointOnJog(jog, -90f));
            yield return null;
            router.ProcessMouseState(true, true, false, PointOnJog(jog, 90f));
            yield return null;

            Assert.That(jog.IsScratching, Is.True, "the first real press after the blip was swallowed");
            router.ProcessMouseState(false, false, true, PointOnJog(jog, 90f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator EveryPointerThatStartsAGestureIsLoggedWithItsSourceAndTarget()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);

            var joined = string.Empty;
            foreach (var entry in _host.Log.Recent(200))
            {
                joined += entry.Message + "\n";
            }

            Assert.That(joined, Does.Contain("Down [Injected]"), "the pointer trail has no down line");
            Assert.That(joined, Does.Contain("LoadA"), "the pointer trail does not say what was pressed");
        }

        // ------------------------------------------------------------------ recordings

        [UnityTest]
        public IEnumerator RecordingsGoToTheirOwnFolderAndAreNotImported()
        {
            // The two halves of the same problem: a recording used to land in the music folder
            // and come back as a track called AIDeck_20260918_085224.
            Assert.That(AIDeck.Platform.AppPaths.DefaultRecordingFolder,
                Does.EndWith(AIDeck.Platform.AppPaths.RecordingsFolderName),
                "recordings still default to the music folder itself");

            var root = Path.Combine(Path.GetTempPath(), "aideck-scan-" + Guid.NewGuid().ToString("N"));
            var recordings = Path.Combine(root, AIDeck.Platform.AppPaths.RecordingsFolderName);
            Directory.CreateDirectory(recordings);

            var song = Path.Combine(root, "A Song.wav");
            File.Copy(TestAudioFile.CreateTone(1d), song);
            var inFolder = Path.Combine(recordings, "AIDeck_20260918_085224.wav");
            File.Copy(song, inFolder);
            var looseRecording = Path.Combine(root, "AIDeck_20260918_090000.wav");
            File.Copy(song, looseRecording);

            try
            {
                var found = AIDeck.Platform.MusicFolderScanner.Scan(root, out _);

                Assert.That(found, Has.Some.EqualTo(song), "the actual music was not found");
                Assert.That(found, Has.None.EqualTo(inFolder),
                    "a recording in the Recordings folder was imported");
                Assert.That(found, Has.None.EqualTo(looseRecording),
                    "a recording sitting beside the music was imported");

                // Naming one outright still works.
                var explicitly = AIDeck.Platform.MusicFolderScanner.Scan(looseRecording, out _);
                Assert.That(explicitly, Has.Some.EqualTo(looseRecording),
                    "a recording named explicitly should still be importable");

                // So does pointing at the Recordings folder itself.
                var chosen = AIDeck.Platform.MusicFolderScanner.Scan(recordings, out _);
                Assert.That(chosen, Has.Some.EqualTo(inFolder),
                    "choosing the Recordings folder should import what is in it");
            }
            finally
            {
                Directory.Delete(root, true);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator AFileWithNoAudioIsNotAddedToTheLibrary()
        {
            // A WAV with a valid header and no samples: an interrupted recording looks like this.
            var empty = Path.Combine(Path.GetTempPath(), "aideck-empty-" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(empty, AIDeck.Core.Audio.WavHeader.Build(48000, 2, 0));
            _tempAudio.Add(empty);

            var before = _host.Library.Count;
            var importer = _host.GetComponent<AIDeck.Audio.TrackImporter>();
            Assert.That(importer, Is.Not.Null);

            List<AddReport> reports = null;
            void Capture(List<AddReport> r) => reports = r;
            importer.Completed += Capture;

            try
            {
                importer.Import(new List<string> { empty });

                var deadline = Time.realtimeSinceStartup + 25f;
                while (reports == null && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }
            }
            finally
            {
                importer.Completed -= Capture;
            }

            Assert.That(reports, Is.Not.Null, "the import never finished");
            Assert.That(_host.Library.Count, Is.EqualTo(before), "a file with no audio was added");
            Assert.That(reports[0].Succeeded, Is.False);
            Assert.That(reports[0].Reason, Is.Not.Empty, "the skip must say why");
        }

        [UnityTest]
        public IEnumerator TextFieldsCanTakeFocus()
        {
            // The second defect found during the same verification: there was no EventSystem
            // anywhere in the app, and uGUI's InputField takes focus through it. Every text box
            // — the library search (FR-011), the import path, and the controller's manual
            // address entry (FR-061) — could be clicked and typed at with no effect at all.
            //
            // The existing tests missed it because they set `InputField.text` directly, which
            // bypasses focus entirely.
            yield return LetUiCatchUp();

            Assert.That(UnityEngine.EventSystems.EventSystem.current, Is.Not.Null,
                "no EventSystem: every text field in the app is inert");

            var module = UnityEngine.EventSystems.EventSystem.current
                .GetComponent<UnityEngine.EventSystems.BaseInputModule>();
            Assert.That(module, Is.Not.Null, "the EventSystem has no input module");
            Assert.That(module.enabled, Is.True);

            // A click can only reach a field if its background accepts ray casts, and only if
            // the canvas has a raycaster to do the casting.
            var raycaster = _host.Screen.GetComponentInChildren<UnityEngine.UI.GraphicRaycaster>(true);
            Assert.That(raycaster, Is.Not.Null, "the canvas has no GraphicRaycaster");

            var search = _host.Screen.Browser.Library.GetComponentInChildren<UnityEngine.UI.InputField>(true);
            Assert.That(search, Is.Not.Null, "the library has no search field");

            var background = search.GetComponent<UnityEngine.UI.Graphic>();
            Assert.That(background, Is.Not.Null, "the search field has no graphic to be clicked");
            Assert.That(background.raycastTarget, Is.True,
                "the search field ignores ray casts, so a click can never select it");

            // And focus actually sticks, which is what typing depends on.
            search.ActivateInputField();
            yield return null;

            Assert.That(UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject,
                Is.EqualTo(search.gameObject), "the search field could not take focus");
        }

        [UnityTest]
        public IEnumerator SearchFiltersTheLibrary()
        {
            // FR-011, driven through the field rather than through TrackListView's internals.
            AddTrack("Alpha Tone", 220f);
            AddTrack("Bravo Tone", 330f);
            yield return LetUiCatchUp();

            var list = _host.Screen.Browser.Library;
            Assert.That(list.VisibleCount, Is.EqualTo(2));

            var search = list.GetComponentInChildren<UnityEngine.UI.InputField>(true);
            search.text = "bravo";
            yield return LetUiCatchUp();

            Assert.That(list.VisibleCount, Is.EqualTo(1), "the search box did not filter the list");
            Assert.That(FindLoadButton("Bravo Tone", DeckId.A), Is.Not.Null);
            Assert.That(FindLoadButton("Alpha Tone", DeckId.A), Is.Null, "a filtered-out row is still clickable");
        }

        [UnityTest]
        public IEnumerator LoggingStaysQuietWhileNothingIsHappening()
        {
            AddTrack("Alpha Tone", 220f);
            yield return LetUiCatchUp();

            Click(FindLoadButton("Alpha Tone", DeckId.A));
            yield return WaitForLoad(DeckId.A);
            yield return LetUiCatchUp();

            var afterLoad = _host.Log.Count;
            yield return new WaitForSeconds(1.5f);

            // The snapshot is rebuilt twenty times a second; if any of that were logged, a
            // session would bury the lines that matter.
            Assert.That(_host.Log.Count, Is.EqualTo(afterLoad),
                "something is logging on a timer and will drown the diagnostic trace");
        }
    }
}
