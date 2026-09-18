using System.Collections;
using System.Collections.Generic;
using AIDeck.Controller;
using AIDeck.Core.Analysis;
using AIDeck.Core.Deck;
using AIDeck.Core.Mixer;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using AIDeck.Platform;
using AIDeck.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// The controller renders host state and sends intents; it holds no audio state of its
    /// own. These tests drive it with a stub backend so the screen, the safe-area layout and
    /// the release-on-background rule are exercised without a host or a network.
    /// </summary>
    [TestFixture]
    public class ControllerPlayModeTests
    {
        /// <summary>A backend whose state the test sets directly, recording what it is told.</summary>
        private sealed class StubBackend : IControllerBackend, IDeckCommands
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<TrackInfo> Tracks = new List<TrackInfo>();
            public StateSnapshot Current = StateSnapshot.Empty;
            public int ReleaseAllCount;

            public IDeckCommands Commands => this;
            public StateSnapshot Snapshot => Current;
            public IReadOnlyList<TrackInfo> Library => Tracks;
            public ConnectionState State { get; set; } = ConnectionState.Connected;
            public string StatusText { get; set; } = "Connected";
            public IReadOnlyList<DiscoveredHost> DiscoveredHosts { get; set; } = new List<DiscoveredHost>();
            public WaveformData WaveformFor(string trackId) => null;

            public string ConnectedTo { get; private set; }

            public void Connect(string address, int port) => ConnectedTo = $"{address}:{port}";
            public void Connect(DiscoveredHost host) => Connect(host.Address, host.Port);
            public void Disconnect() => State = ConnectionState.Idle;
            public void Tick(float deltaSeconds) { }
            public void ReleaseAll() => ReleaseAllCount++;

            public void LoadTrack(DeckId deck, string trackId) => Calls.Add($"Load:{deck}:{trackId}");
            public void Eject(DeckId deck) => Calls.Add($"Eject:{deck}");
            public void TogglePlay(DeckId deck) => Calls.Add($"TogglePlay:{deck}");
            public void CueSet(DeckId deck) => Calls.Add($"CueSet:{deck}");
            public void CueReturn(DeckId deck) => Calls.Add($"CueReturn:{deck}");
            public void Seek(DeckId deck, double seconds) => Calls.Add($"Seek:{deck}");
            public void SetTempoFader(DeckId deck, float value) => Calls.Add($"Tempo:{deck}");
            public void SetTempoRange(DeckId deck, float percent) { }
            public void ToggleSync(DeckId deck) => Calls.Add($"Sync:{deck}");
            public void ToggleLoop(DeckId deck) => Calls.Add($"Loop:{deck}");
            public void SetLoopBeats(DeckId deck, float beats) { }
            public void JogNudge(DeckId deck, float amount) => Calls.Add($"Nudge:{deck}");
            public void ScratchBegin(DeckId deck) => Calls.Add($"ScratchBegin:{deck}");
            public void ScratchUpdate(DeckId deck, float rate) => Calls.Add($"ScratchUpdate:{deck}");
            public void ScratchMove(DeckId deck, float seconds) => Calls.Add($"ScratchMove:{deck}");
            public void ScratchEnd(DeckId deck) => Calls.Add($"ScratchEnd:{deck}");
            public void Brake(DeckId deck) => Calls.Add($"Brake:{deck}");
            public void Backspin(DeckId deck) => Calls.Add($"Backspin:{deck}");
            public void SetChannelGain(DeckId deck, float value) => Calls.Add($"Gain:{deck}");
            public void SetCrossfader(float value) => Calls.Add("Crossfader");
            public void SetMasterGain(float value) => Calls.Add("Master");
            public void SetFilter(DeckId deck, float value) => Calls.Add($"Filter:{deck}");
            public void SetMute(DeckId deck, bool muted) => Calls.Add($"Mute:{deck}:{muted}");
            public void SetEcho(DeckId deck, bool enabled) => Calls.Add($"Echo:{deck}:{enabled}");
            public void SetCueMonitor(DeckId deck, bool enabled) => Calls.Add($"CueMon:{deck}:{enabled}");
            public void StartRecording() => Calls.Add("RecordStart");
            public void StopRecording() => Calls.Add("RecordStop");
            public void AllStop() => Calls.Add("AllStop");
        }

        private GameObject _root;
        private ControllerApp _app;
        private StubBackend _backend;
        private string _settingsPath;

        [SetUp]
        public void SetUp()
        {
            _backend = new StubBackend();
            _root = new GameObject("ControllerTestHost");
            _app = _root.AddComponent<ControllerApp>();

            // A temporary settings file: the tests must not write a host address into the real
            // one, or the installed app would start trying to reach a machine that never existed.
            _settingsPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "aideck-test-settings-" + System.Guid.NewGuid().ToString("N") + ".json");

            _app.Initialise(_backend, null, new AppSettings(), new SettingsStore(null, _settingsPath));
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }

            try
            {
                if (System.IO.File.Exists(_settingsPath))
                {
                    System.IO.File.Delete(_settingsPath);
                }
            }
            catch (System.Exception)
            {
                // A leftover file in the temp folder is harmless.
            }
        }

        private static StateSnapshot PopulatedSnapshot()
        {
            var snapshot = StateSnapshot.Empty;
            snapshot.LibraryRevision = 1;
            snapshot.DeckA = new DeckSnapshot
            {
                Deck = DeckId.A,
                State = DeckPlaybackState.Playing,
                TrackId = "track-a",
                PositionSeconds = 30d,
                LengthSeconds = 180d,
                BaseBpm = 128d,
                EffectiveRate = 1f,
                TempoRangePercent = 8f,
                ErrorReason = string.Empty
            };
            snapshot.DeckB = DeckSnapshot.Empty(DeckId.B);
            snapshot.ChannelGainA = 0.7f;
            snapshot.ChannelGainB = 0.3f;
            snapshot.MasterGain = 0.9f;
            snapshot.Crossfader = -0.4f;
            snapshot.CrossfaderCurve = CrossfaderCurveType.Smooth;
            snapshot.MuteB = true;
            snapshot.IsRecording = true;
            snapshot.RecordingSeconds = 75d;
            snapshot.RecordingFileName = "AIDeck_20260918_010203.wav";
            return snapshot;
        }

        [UnityTest]
        public IEnumerator TheScreenIsBuiltWithEveryPanel()
        {
            yield return null;

            Assert.That(_app.Screen, Is.Not.Null);
            Assert.That(_app.Screen.Browser, Is.Not.Null);
            Assert.That(_app.Screen.DeckA, Is.Not.Null);
            Assert.That(_app.Screen.DeckB, Is.Not.Null);
            Assert.That(_app.Screen.Mixer, Is.Not.Null);
            Assert.That(_app.Screen.Connect, Is.Not.Null);
            Assert.That(_app.Screen.DeckA.Deck, Is.EqualTo(DeckId.A));
            Assert.That(_app.Screen.DeckB.Deck, Is.EqualTo(DeckId.B));
        }

        [UnityTest]
        public IEnumerator TheConnectSheetIsHiddenOnlyWhileConnected()
        {
            yield return null;
            Assert.That(_app.Screen.Connect.gameObject.activeSelf, Is.False, "connected: the sheet is out of the way");

            _backend.State = ConnectionState.Reconnecting;
            _backend.StatusText = "Reconnecting…";
            yield return new WaitForSeconds(0.2f);

            Assert.That(_app.Screen.Connect.gameObject.activeSelf, Is.True,
                "a controller that is not connected must say so rather than present dead controls");
        }

        [UnityTest]
        public IEnumerator LosingTheLinkReleasesEveryHeldControl()
        {
            yield return null;
            var before = _backend.ReleaseAllCount;

            _backend.State = ConnectionState.Reconnecting;
            yield return new WaitForSeconds(0.2f);

            // FR-066: the finger that was driving a control is now driving nothing.
            Assert.That(_backend.ReleaseAllCount, Is.GreaterThan(before));
        }

        [UnityTest]
        public IEnumerator HostStateIsRenderedIntoThePanels()
        {
            _backend.Tracks.Add(new TrackInfo("track-a", "/m/a.mp3", "Alpha", "Engine", 180d,
                TrackFormat.Mp3, System.DateTime.UtcNow, 128d));
            _backend.Current = PopulatedSnapshot();

            yield return new WaitForSeconds(0.2f);

            Assert.That(_app.Screen.Browser.Library.VisibleCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ControlsReportIntentsThroughTheBackend()
        {
            _backend.Current = PopulatedSnapshot();
            yield return new WaitForSeconds(0.2f);

            // Driving the widget directly rather than through screen coordinates: the routing
            // itself is covered by TouchRouterTests, and this asserts the wiring.
            var router = _app.Screen.Router;
            var play = FindButton(_app.Screen.DeckA, "Play");
            Assert.That(play, Is.Not.Null, "the deck panel should have a PLAY button");

            router.PointerDown(1, ScreenCentre(play.Rect));
            router.PointerUp(1, ScreenCentre(play.Rect));

            Assert.That(_backend.Calls, Does.Contain("TogglePlay:A"));
        }

        [UnityTest]
        public IEnumerator BackgroundingReleasesHeldControls()
        {
            _backend.Current = PopulatedSnapshot();
            yield return new WaitForSeconds(0.2f);

            var router = _app.Screen.Router;
            var play = FindButton(_app.Screen.DeckA, "Play");
            router.PointerDown(1, ScreenCentre(play.Rect));
            Assert.That(play.IsPressed, Is.True);

            // FR-074. Unity raises OnApplicationPause on the component; calling it directly is
            // how the behaviour is reachable in a test.
            _app.SendMessage("OnApplicationPause", true, SendMessageOptions.DontRequireReceiver);
            yield return null;

            Assert.That(play.IsPressed, Is.False, "a backgrounded app must not leave a control held");
            Assert.That(_backend.Calls, Does.Not.Contain("TogglePlay:A"),
                "the interrupted press must not fire the command");
        }

        [UnityTest]
        public IEnumerator ConnectingFromTheSheetUsesTheTypedAddress()
        {
            _backend.State = ConnectionState.Idle;
            yield return new WaitForSeconds(0.2f);

            _app.Screen.Connect.GetType();
            var field = _app.Screen.Connect.GetComponentInChildren<UnityEngine.UI.InputField>(true);
            Assert.That(field, Is.Not.Null);
            field.text = "192.168.1.55";

            var connect = FindButton(_app.Screen.Connect, "Connect");
            Assert.That(connect, Is.Not.Null);

            var router = _app.Screen.Router;
            router.PointerDown(1, ScreenCentre(connect.Rect));
            router.PointerUp(1, ScreenCentre(connect.Rect));

            Assert.That(_backend.ConnectedTo, Is.EqualTo("192.168.1.55:" + ProtocolInfo.DefaultTcpPort));
            Assert.That(_app.Settings.LastHostAddress, Is.EqualTo("192.168.1.55"),
                "the address is remembered for next launch (FR-080)");
        }

        [UnityTest]
        public IEnumerator AnAddressWithAPortIsParsed()
        {
            _backend.State = ConnectionState.Idle;
            yield return new WaitForSeconds(0.2f);

            var field = _app.Screen.Connect.GetComponentInChildren<UnityEngine.UI.InputField>(true);
            field.text = "10.0.0.9:50000";

            var connect = FindButton(_app.Screen.Connect, "Connect");
            var router = _app.Screen.Router;
            router.PointerDown(1, ScreenCentre(connect.Rect));
            router.PointerUp(1, ScreenCentre(connect.Rect));

            Assert.That(_backend.ConnectedTo, Is.EqualTo("10.0.0.9:50000"));
        }

        [UnityTest]
        public IEnumerator EveryTouchTargetMeetsTheMinimumSize()
        {
            // §5.1 requires 44 pt on the primary controls. The canvas reference resolution is
            // in points, so a reference pixel is a point.
            _backend.Current = PopulatedSnapshot();
            yield return new WaitForSeconds(0.2f);

            var names = new[] { "Play", "Cue", "Sync", "Loop", "Echo", "Brake", "Backspin" };
            foreach (var name in names)
            {
                var button = FindButton(_app.Screen.DeckA, name);
                Assert.That(button, Is.Not.Null, name + " is missing");

                var size = button.Rect.rect;
                Assert.That(Mathf.Min(size.width, size.height), Is.GreaterThanOrEqualTo(Theme.TouchSize - 0.5f),
                    $"{name} is {size.width}x{size.height}, smaller than the 44 pt minimum");
            }
        }

        [UnityTest]
        public IEnumerator ScrubbingTheWaveformSeeksTheDeck()
        {
            // FR-025. The waveform scrolls past a fixed playhead, so dragging it is the same
            // gesture as moving a record under the needle.
            var snapshot = PopulatedSnapshot();
            snapshot.DeckA.PositionSeconds = 60d;
            _backend.Current = snapshot;
            yield return new WaitForSeconds(0.2f);

            var scrubber = FindScrubber(_app.Screen.Browser, "ScrubA");
            Assert.That(scrubber, Is.Not.Null, "deck A should have a scrub area over its waveform");

            var centre = ScreenCentre(scrubber.Rect);
            var router = _app.Screen.Router;

            router.PointerDown(1, centre);
            // Drag right by a quarter of the strip: the track moves right, so time goes back.
            router.PointerMove(1, centre + new Vector2(scrubber.Rect.rect.width * 0.25f, 0f));

            Assert.That(scrubber.IsScrubbing, Is.True);
            Assert.That(_backend.Calls, Does.Contain("Seek:A"));

            router.PointerUp(1, centre);
            Assert.That(scrubber.IsScrubbing, Is.False);
        }

        [UnityTest]
        public IEnumerator ATapOnTheWaveformDoesNotSeek()
        {
            // A tap that moves a pixel or two is not a scrub; seeking on it would make the
            // waveform impossible to touch without moving the track.
            _backend.Current = PopulatedSnapshot();
            yield return new WaitForSeconds(0.2f);

            var scrubber = FindScrubber(_app.Screen.Browser, "ScrubA");
            var centre = ScreenCentre(scrubber.Rect);
            var router = _app.Screen.Router;

            router.PointerDown(1, centre);
            router.PointerMove(1, centre + new Vector2(2f, 0f));
            router.PointerUp(1, centre + new Vector2(2f, 0f));

            Assert.That(_backend.Calls, Does.Not.Contain("Seek:A"));
        }

        [UnityTest]
        public IEnumerator ScrubbingAnEmptyDeckDoesNothing()
        {
            _backend.Current = StateSnapshot.Empty;
            yield return new WaitForSeconds(0.2f);

            var scrubber = FindScrubber(_app.Screen.Browser, "ScrubA");
            var centre = ScreenCentre(scrubber.Rect);
            var router = _app.Screen.Router;

            router.PointerDown(1, centre);
            router.PointerMove(1, centre + new Vector2(120f, 0f));
            router.PointerUp(1, centre);

            Assert.That(_backend.Calls, Does.Not.Contain("Seek:A"));
        }

        [UnityTest]
        public IEnumerator TheRecordButtonStartsAndStopsRecording()
        {
            // FR-050 and FR-051 from the controller's side: the button reflects the host's
            // recording state and toggles against it.
            _backend.Current = StateSnapshot.Empty;
            yield return new WaitForSeconds(0.2f);

            var record = FindButton(_app.Screen.Mixer, "Record");
            Assert.That(record, Is.Not.Null);

            var router = _app.Screen.Router;
            var centre = ScreenCentre(record.Rect);

            router.PointerDown(1, centre);
            router.PointerUp(1, centre);
            Assert.That(_backend.Calls, Does.Contain("RecordStart"));

            // The host now reports that it is recording; the same button must stop it.
            var recording = StateSnapshot.Empty;
            recording.IsRecording = true;
            recording.RecordingSeconds = 12d;
            recording.RecordingFileName = "AIDeck_20260918_010203.wav";
            _backend.Current = recording;
            yield return new WaitForSeconds(0.2f);

            router.PointerDown(1, centre);
            router.PointerUp(1, centre);
            Assert.That(_backend.Calls, Does.Contain("RecordStop"));
        }

        private static WaveformScrubber FindScrubber(Component root, string name)
        {
            foreach (var scrubber in root.GetComponentsInChildren<WaveformScrubber>(true))
            {
                if (scrubber.gameObject.name == name)
                {
                    return scrubber;
                }
            }

            return null;
        }

        private static ButtonWidget FindButton(Component root, string name)
        {
            foreach (var button in root.GetComponentsInChildren<ButtonWidget>(true))
            {
                if (button.gameObject.name == name)
                {
                    return button;
                }
            }

            return null;
        }

        /// <summary>Screen-space centre of a rect, for injecting a pointer at it.</summary>
        private static Vector2 ScreenCentre(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var centre = (corners[0] + corners[2]) * 0.5f;
            return new Vector2(centre.x, centre.y);
        }

        // ------------------------------------------------------------- touch input (FR-071)
        //
        // These go through TouchRouter.ProcessTouchState, which is the same conversion
        // Update() feeds from Input.touches: a phase, a finger id and a screen position in,
        // a captured widget out. Calling PointerDown directly — as the older tests do —
        // skips exactly the step an iPad-only failure would live in.

        private JogWidget Jog(DeckId deck)
        {
            var panel = deck == DeckId.A ? _app.Screen.DeckA : _app.Screen.DeckB;
            return panel.GetComponentInChildren<JogWidget>(true);
        }

        /// <summary>Screen point on the jog's circle at <paramref name="degrees"/>, 0° = right.</summary>
        private static Vector2 PointOnJog(JogWidget jog, float degrees, float radiusFraction = 0.8f)
        {
            var corners = new Vector3[4];
            jog.Rect.GetWorldCorners(corners);
            var centre = (Vector2)((corners[0] + corners[2]) * 0.5f);
            var radius = Mathf.Min(corners[2].x - corners[0].x, corners[2].y - corners[0].y) * 0.5f;
            return centre + new Vector2(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad))
                   * radius * radiusFraction;
        }

        private int Count(string call)
        {
            var n = 0;
            foreach (var entry in _backend.Calls)
            {
                if (entry == call)
                {
                    n++;
                }
            }

            return n;
        }

        [UnityTest]
        public IEnumerator ATouchThatBeginsOnTheJogDrivesTheDeck()
        {
            _backend.Current = PopulatedSnapshot();
            yield return null;

            var router = _app.Screen.Router;
            var jog = Jog(DeckId.A);
            Assert.That(jog, Is.Not.Null, "deck A has no jog wheel");

            router.ProcessTouchState(0, TouchPhase.Began, PointOnJog(jog, 90f));
            yield return null;

            Assert.That(jog.IsScratching, Is.True, "a real touch did not take hold of the platter");
            Assert.That(_backend.Calls, Does.Contain("ScratchBegin:A"));

            for (var i = 1; i <= 6; i++)
            {
                router.ProcessTouchState(0, TouchPhase.Moved, PointOnJog(jog, 90f - 30f * i));
                yield return null;
            }

            Assert.That(Count("ScratchMove:A"), Is.GreaterThan(0),
                "turning the platter sent no displacement to the host");

            router.ProcessTouchState(0, TouchPhase.Ended, PointOnJog(jog, -90f));
            yield return null;

            Assert.That(jog.IsScratching, Is.False);
            Assert.That(_backend.Calls, Does.Contain("ScratchEnd:A"));
        }

        [UnityTest]
        public IEnumerator TheGestureKeepsItsFingerIdThroughEveryPhase()
        {
            _backend.Current = PopulatedSnapshot();
            yield return null;

            var router = _app.Screen.Router;
            var jog = Jog(DeckId.A);

            router.ProcessTouchState(7, TouchPhase.Began, PointOnJog(jog, 0f));
            yield return null;
            Assert.That(router.IsCaptured(7), Is.True);

            // A finger resting still is still holding the platter.
            router.ProcessTouchState(7, TouchPhase.Stationary, PointOnJog(jog, 0f));
            yield return null;
            Assert.That(jog.IsScratching, Is.True, "a stationary finger dropped the platter");

            // A different finger lifting elsewhere must not end this gesture.
            router.ProcessTouchState(8, TouchPhase.Ended, PointOnJog(jog, 180f));
            yield return null;
            Assert.That(jog.IsScratching, Is.True, "another finger's release ended this gesture");

            router.ProcessTouchState(7, TouchPhase.Moved, PointOnJog(jog, -60f));
            yield return null;
            router.ProcessTouchState(7, TouchPhase.Ended, PointOnJog(jog, -60f));
            yield return null;

            Assert.That(jog.IsScratching, Is.False);
            Assert.That(router.IsCaptured(7), Is.False);
        }

        [UnityTest]
        public IEnumerator TheTwoJogWheelsTakeSeparateFingersAtTheSameTime()
        {
            _backend.Current = PopulatedSnapshot();
            yield return null;

            var router = _app.Screen.Router;
            var a = Jog(DeckId.A);
            var b = Jog(DeckId.B);

            router.ProcessTouchState(0, TouchPhase.Began, PointOnJog(a, 90f));
            router.ProcessTouchState(1, TouchPhase.Began, PointOnJog(b, 90f));
            yield return null;

            Assert.That(a.IsScratching, Is.True);
            Assert.That(b.IsScratching, Is.True);

            router.ProcessTouchState(0, TouchPhase.Ended, PointOnJog(a, 90f));
            yield return null;

            Assert.That(a.IsScratching, Is.False);
            Assert.That(b.IsScratching, Is.True, "releasing one jog released the other");

            router.ProcessTouchState(1, TouchPhase.Ended, PointOnJog(b, 90f));
            yield return null;
            Assert.That(b.IsScratching, Is.False);
        }

        [UnityTest]
        public IEnumerator FingerZeroDoesNotCollideWithTheMousePointer()
        {
            // The mouse is fed through the same router as an extra pointer. If it shared an id
            // with the first finger, a touch would cancel the mouse's gesture and vice versa.
            _backend.Current = PopulatedSnapshot();
            yield return null;

            Assert.That(TouchRouter.MousePointerId, Is.LessThan(0),
                "the mouse pointer id must be outside the range iOS gives fingers");

            var router = _app.Screen.Router;
            var jog = Jog(DeckId.A);

            router.ProcessTouchState(0, TouchPhase.Began, PointOnJog(jog, 90f));
            yield return null;

            Assert.That(router.IsCaptured(0), Is.True);
            Assert.That(router.IsCaptured(TouchRouter.MousePointerId), Is.False);

            router.ProcessTouchState(0, TouchPhase.Ended, PointOnJog(jog, 90f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ACancelledTouchDropsThePlatterWithoutActingOnIt()
        {
            _backend.Current = PopulatedSnapshot();
            yield return null;

            var router = _app.Screen.Router;
            var jog = Jog(DeckId.A);

            router.ProcessTouchState(2, TouchPhase.Began, PointOnJog(jog, 90f));
            yield return null;
            router.ProcessTouchState(2, TouchPhase.Canceled, PointOnJog(jog, 90f));
            yield return null;

            Assert.That(jog.IsScratching, Is.False, "a cancelled touch kept the platter");
            Assert.That(router.IsCaptured(2), Is.False);
            Assert.That(_backend.Calls, Does.Contain("ScratchEnd:A"),
                "a cancelled gesture must still release the deck on the host");
        }

        [UnityTest]
        public IEnumerator TheJogIsHitAtTheSamePlaceWhenTheCanvasIsScaled()
        {
            // An iPad mini renders at 2x and the canvas scales to its reference resolution, so
            // every screen point is converted twice before it reaches a widget. The conversion
            // is by world corners, and this is what proves it stays correct under a scale
            // factor rather than only at 1:1.
            _backend.Current = PopulatedSnapshot();
            yield return null;

            var canvas = _app.Screen.GetComponentInChildren<Canvas>(true);
            Assert.That(canvas, Is.Not.Null);

            var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
            Assert.That(scaler, Is.Not.Null, "the control surface must scale with the screen");

            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 2f;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var router = _app.Screen.Router;
            var jog = Jog(DeckId.A);

            router.ProcessTouchState(0, TouchPhase.Began, PointOnJog(jog, 90f));
            yield return null;

            Assert.That(jog.IsScratching, Is.True, "the jog could not be hit at a 2x canvas scale");

            for (var i = 1; i <= 6; i++)
            {
                router.ProcessTouchState(0, TouchPhase.Moved, PointOnJog(jog, 90f - 30f * i));
                yield return null;
            }

            Assert.That(Count("ScratchMove:A"), Is.GreaterThan(0),
                "a scaled canvas produced no platter movement");

            router.ProcessTouchState(0, TouchPhase.Ended, PointOnJog(jog, -90f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator NothingTransparentCoversTheJogWheels()
        {
            // A stray full-screen graphic with ray casting left on would swallow every touch
            // and look exactly like a dead jog.
            _backend.Current = PopulatedSnapshot();
            yield return null;

            var router = _app.Screen.Router;

            foreach (var deck in new[] { DeckId.A, DeckId.B })
            {
                var jog = Jog(deck);
                router.ProcessTouchState(4, TouchPhase.Began, PointOnJog(jog, 45f, 0.95f));
                yield return null;

                Assert.That(jog.IsScratching, Is.True,
                    $"something is above deck {deck}'s jog at the edge of the disc");

                router.ProcessTouchState(4, TouchPhase.Ended, PointOnJog(jog, 45f, 0.95f));
                yield return null;
            }
        }

        [Test]
        public void TheSafeAreaInsetMatchesAnIPadMiniInLandscape()
        {
            // FR-075. An iPad mini in landscape keeps the home indicator at the bottom and the
            // rounded corners at both ends; the content rect is inset by exactly that. Getting
            // it wrong moves every control away from where the finger lands, which reads as an
            // input bug rather than a layout one.
            const int width = 2266;
            const int height = 1488;
            var safe = new Rect(59f, 0f, width - 118f, height - 42f);

            var go = new GameObject("SafeAreaProbe", typeof(RectTransform));
            try
            {
                var rect = (RectTransform)go.transform;
                UiFactory.ApplySafeArea(rect, safe, width, height);

                Assert.That(rect.anchorMin.x, Is.EqualTo(59f / width).Within(1e-5f));
                Assert.That(rect.anchorMin.y, Is.EqualTo(0f).Within(1e-5f));
                Assert.That(rect.anchorMax.x, Is.EqualTo((width - 59f) / width).Within(1e-5f));
                Assert.That(rect.anchorMax.y, Is.EqualTo((height - 42f) / (float)height).Within(1e-5f));
                Assert.That(rect.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(rect.offsetMax, Is.EqualTo(Vector2.zero));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ASafeAreaOfTheWholeScreenInsetsNothing()
        {
            // A device with no notch reports the full screen, and the layout must be untouched
            // rather than shifted by a rounding error.
            var go = new GameObject("SafeAreaProbe", typeof(RectTransform));
            try
            {
                var rect = (RectTransform)go.transform;
                UiFactory.ApplySafeArea(rect, new Rect(0f, 0f, 1024f, 768f), 1024, 768);

                Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
