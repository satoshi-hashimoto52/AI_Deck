using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIDeck.Core.Deck;
using AIDeck.Core.Diagnostics;
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
