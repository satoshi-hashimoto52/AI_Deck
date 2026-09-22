using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIDeck.Core.Model;
using AIDeck.Host;
using AIDeck.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// The library list has to scroll, and scrolling must not load anything.
    ///
    /// Reported from the machine: the list ran past the bottom of its box and neither the
    /// wheel, the trackpad nor a drag moved it, so the tracks below the fold could not be
    /// reached at all. The ScrollRect that was supposed to do this had no raycast target on
    /// its viewport and the rows swallowed their own drags, so nothing ever reached it.
    ///
    /// These drive the same <see cref="TouchRouter"/> the deck uses, because that is the input
    /// path the list actually has.
    /// </summary>
    [TestFixture]
    public class TrackListScrollTests
    {
        private GameObject _root;
        private HostApp _host;
        private string _settingsPath;
        private string _libraryPath;
        private readonly List<string> _tones = new List<string>();

        [SetUp]
        public void SetUp()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _settingsPath = Path.Combine(Path.GetTempPath(), $"aideck-scroll-settings-{stamp}.json");
            _libraryPath = Path.Combine(Path.GetTempPath(), $"aideck-scroll-library-{stamp}.json");

            _root = new GameObject("ScrollTestHost");
            _root.SetActive(false);
            _host = _root.AddComponent<HostApp>();
            _host.SettingsPathOverride = _settingsPath;
            _host.LibraryPathOverride = _libraryPath;
            _host.NetworkingEnabled = false;
            _root.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
            }

            foreach (var tone in _tones)
            {
                TestAudioFile.Delete(tone);
            }

            _tones.Clear();

            foreach (var path in new[] { _settingsPath, _libraryPath })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // A leftover file in the temp folder is harmless.
                }
            }
        }

        private TrackListView List => _host.Screen.Browser.Library;

        private IEnumerator LetUiCatchUp()
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;
        }

        /// <summary>Adds tracks without decoding anything: the list only needs their metadata.</summary>
        private void AddTracks(int count)
        {
            var path = TestAudioFile.CreateTone(0.2d);
            _tones.Add(path);
            var size = new FileInfo(path).Length;

            for (var i = 0; i < count; i++)
            {
                _host.Library.Add(new TrackInfo(
                    null, path + "#" + i, $"Track {i:00}", "Tester", 120d,
                    TrackFormat.Wav, DateTime.UtcNow, 0d, size));
            }

            // Pushed straight in rather than waiting for the host's throttled refresh: this
            // fixture is about the list, and a test that raced the refresh interval would be
            // measuring the wrong thing.
            List.SetTracks(_host.Library.All);
        }

        private RectTransform Viewport =>
            (RectTransform)List.transform.Find("Scroll/Viewport");

        private RowHitArea RowFor(string title)
        {
            foreach (var hit in List.GetComponentsInChildren<RowHitArea>(true))
            {
                foreach (var text in hit.GetComponentsInChildren<Text>(true))
                {
                    if (text.gameObject.name == "Title" && text.text == title)
                    {
                        return hit;
                    }
                }
            }

            return null;
        }

        private ButtonWidget LoadButtonFor(string title, DeckId deck)
        {
            var wanted = deck == DeckId.A ? "LoadA" : "LoadB";
            foreach (var button in List.GetComponentsInChildren<ButtonWidget>(true))
            {
                if (button.gameObject.name != wanted)
                {
                    continue;
                }

                foreach (var text in button.transform.parent.GetComponentsInChildren<Text>(true))
                {
                    if (text.gameObject.name == "Title" && text.text == title)
                    {
                        return button;
                    }
                }
            }

            return null;
        }

        private static Vector2 Centre(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var middle = (corners[0] + corners[2]) * 0.5f;
            return new Vector2(middle.x, middle.y);
        }

        /// <summary>A drag through the router, in steps, exactly as a finger arrives.</summary>
        private IEnumerator Drag(Vector2 from, float deltaY, int steps = 8, bool release = true)
        {
            var router = _host.Screen.Router;
            router.PointerDown(31, from);
            yield return null;

            for (var i = 1; i <= steps; i++)
            {
                router.PointerMove(31, from + new Vector2(0f, deltaY * i / steps));
                yield return null;
            }

            if (release)
            {
                router.PointerUp(31, from + new Vector2(0f, deltaY));
                yield return null;
            }
        }

        // ------------------------------------------------------------------ reach

        [UnityTest]
        public IEnumerator TheLastTrackCanBeReachedByScrolling()
        {
            AddTracks(20);
            yield return LetUiCatchUp();

            Assert.That(List.MaxScroll, Is.GreaterThan(0f),
                "twenty tracks fit the viewport, so this test proves nothing");
            Assert.That(RowFor("Track 19"), Is.Not.Null, "the last row was never built");

            List.ScrollBy(100000f);
            yield return LetUiCatchUp();

            Assert.That(List.ScrollOffset, Is.EqualTo(List.MaxScroll).Within(0.5f),
                "the list would not scroll to its end");

            // The last row must now be inside the viewport, not below it.
            var lastRow = RowFor("Track 19").GetComponent<RectTransform>();
            var viewport = Viewport;
            var rowCentre = Centre(lastRow);
            var corners = new Vector3[4];
            viewport.GetWorldCorners(corners);

            Assert.That(rowCentre.y, Is.GreaterThanOrEqualTo(corners[0].y),
                "the last track is still below the visible area");
            Assert.That(rowCentre.y, Is.LessThanOrEqualTo(corners[2].y),
                "the last track is above the visible area");
        }

        [UnityTest]
        public IEnumerator ScrollingStopsAtBothEnds()
        {
            AddTracks(20);
            yield return LetUiCatchUp();

            List.ScrollBy(-5000f);
            yield return LetUiCatchUp();
            Assert.That(List.ScrollOffset, Is.EqualTo(0f).Within(0.01f),
                "the list scrolled above its first track");

            List.ScrollBy(999999f);
            yield return LetUiCatchUp();
            Assert.That(List.ScrollOffset, Is.EqualTo(List.MaxScroll).Within(0.01f),
                "the list scrolled past its last track");
        }

        // ------------------------------------------------------------------ input

        [UnityTest]
        public IEnumerator TheWheelMovesTheList()
        {
            AddTracks(20);
            yield return LetUiCatchUp();
            var before = List.ScrollOffset;

            List.ScrollByWheel(-3f);          // a wheel notch towards the end of the list
            yield return LetUiCatchUp();

            Assert.That(List.ScrollOffset, Is.GreaterThan(before), "the wheel did nothing");

            var afterDown = List.ScrollOffset;
            List.ScrollByWheel(3f);
            yield return LetUiCatchUp();
            Assert.That(List.ScrollOffset, Is.LessThan(afterDown), "the wheel only works one way");
        }

        [UnityTest]
        public IEnumerator AMouseDragMovesTheList()
        {
            AddTracks(20);
            yield return LetUiCatchUp();
            var before = List.ScrollOffset;

            // Upwards on the screen: the content follows the finger and later tracks appear.
            yield return Drag(Centre(RowFor("Track 02").GetComponent<RectTransform>()), 120f);

            Assert.That(List.ScrollOffset, Is.GreaterThan(before), "dragging a row did not scroll");
        }

        [UnityTest]
        public IEnumerator ATouchDragThroughTheRouterMovesTheList()
        {
            // The iPad's one-finger path: a real TouchPhase sequence, not an injected pointer.
            AddTracks(20);
            yield return LetUiCatchUp();
            var before = List.ScrollOffset;

            var router = _host.Screen.Router;
            var start = Centre(RowFor("Track 02").GetComponent<RectTransform>());

            router.ProcessTouchState(0, TouchPhase.Began, start);
            yield return null;
            for (var i = 1; i <= 8; i++)
            {
                router.ProcessTouchState(0, TouchPhase.Moved, start + new Vector2(0f, 15f * i));
                yield return null;
            }

            router.ProcessTouchState(0, TouchPhase.Ended, start + new Vector2(0f, 120f));
            yield return null;

            Assert.That(List.ScrollOffset, Is.GreaterThan(before),
                "a one-finger drag did not scroll the list");
        }

        // ------------------------------------------------------- scrolling is not selecting

        [UnityTest]
        public IEnumerator DraggingARowDoesNotSelectIt()
        {
            AddTracks(20);
            yield return LetUiCatchUp();

            TrackInfo selected = null;
            List.TrackSelected += track => selected = track;

            yield return Drag(Centre(RowFor("Track 02").GetComponent<RectTransform>()), 120f);

            Assert.That(selected, Is.Null, "scrolling selected a track");
            Assert.That(List.SelectedTrack, Is.Null);
        }

        [UnityTest]
        public IEnumerator DraggingFromALoadButtonDoesNotLoadATrack()
        {
            // The buttons cover much of the row, so a scroll very often starts on one.
            AddTracks(20);
            yield return LetUiCatchUp();

            var loads = 0;
            List.LoadRequested += (_track, _deck) => loads++;

            var before = List.ScrollOffset;
            yield return Drag(Centre(LoadButtonFor("Track 02", DeckId.A).Rect), 120f);

            Assert.That(loads, Is.EqualTo(0), "a scroll that began on A loaded a track");
            Assert.That(List.ScrollOffset, Is.GreaterThan(before),
                "a drag that began on A did not scroll either");
        }

        [UnityTest]
        public IEnumerator AShortTapStillLoadsExactlyOnce()
        {
            AddTracks(20);
            yield return LetUiCatchUp();

            var loads = 0;
            DeckId? target = null;
            List.LoadRequested += (_track, deck) => { loads++; target = deck; };

            var button = LoadButtonFor("Track 03", DeckId.B);
            var point = Centre(button.Rect);
            var router = _host.Screen.Router;
            router.PointerDown(32, point);
            yield return null;
            router.PointerUp(32, point);
            yield return LetUiCatchUp();

            Assert.That(loads, Is.EqualTo(1), "a tap on B did not load exactly one track");
            Assert.That(target, Is.EqualTo(DeckId.B));
        }

        [UnityTest]
        public IEnumerator AShortTapOnARowStillSelectsIt()
        {
            AddTracks(20);
            yield return LetUiCatchUp();

            var row = RowFor("Track 04").GetComponent<RectTransform>();
            var point = Centre(row);
            var router = _host.Screen.Router;
            router.PointerDown(33, point);
            yield return null;
            router.PointerUp(33, point);
            yield return LetUiCatchUp();

            Assert.That(List.SelectedTrack, Is.Not.Null, "a tap no longer selects a row");
            Assert.That(List.SelectedTrack.Title, Is.EqualTo("Track 04"));
        }

        // ------------------------------------------------------------------ housekeeping

        [UnityTest]
        public IEnumerator ChangingTheSearchReturnsToTheTopOfTheList()
        {
            AddTracks(20);
            yield return LetUiCatchUp();

            List.ScrollBy(100000f);
            yield return LetUiCatchUp();
            Assert.That(List.ScrollOffset, Is.GreaterThan(0f));

            List.SetQuery("Track 1");
            yield return LetUiCatchUp();

            Assert.That(List.ScrollOffset, Is.EqualTo(0f).Within(0.01f),
                "a new search opened part-way down the results");
        }

        [UnityTest]
        public IEnumerator AShortListCannotBeScrolledIntoEmptySpace()
        {
            AddTracks(2);
            yield return LetUiCatchUp();

            Assert.That(List.MaxScroll, Is.EqualTo(0f).Within(0.01f),
                "two tracks should not be scrollable at all");

            List.ScrollBy(500f);
            List.ScrollByWheel(-5f);
            yield return LetUiCatchUp();

            Assert.That(List.ScrollOffset, Is.EqualTo(0f).Within(0.01f),
                "a list that fits was scrolled into blank space");
        }

        [UnityTest]
        public IEnumerator RemovingTracksDoesNotLeaveTheListScrolledPastTheEnd()
        {
            AddTracks(20);
            yield return LetUiCatchUp();
            List.ScrollBy(100000f);
            yield return LetUiCatchUp();

            _host.Library.Clear();
            List.SetTracks(_host.Library.All);
            yield return LetUiCatchUp();

            Assert.That(List.ScrollOffset, Is.EqualTo(0f).Within(0.01f),
                "the list stayed scrolled into nothing after its tracks went away");
        }

        [UnityTest]
        public IEnumerator TheScrollIndicatorAppearsOnlyWhenThereIsMoreToSee()
        {
            AddTracks(2);
            yield return LetUiCatchUp();

            var track = List.transform.Find("Scroll/ScrollTrack");
            Assert.That(track, Is.Not.Null, "there is no scroll indicator");
            Assert.That(track.gameObject.activeSelf, Is.False,
                "a list that fits showed a scrollbar");

            AddTracks(20);
            yield return LetUiCatchUp();

            Assert.That(track.gameObject.activeSelf, Is.True,
                "a list with more below the fold showed no scrollbar");
        }
    }
}
