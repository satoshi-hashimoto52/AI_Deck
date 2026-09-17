using System;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.Controller
{
    /// <summary>
    /// The iPad control surface (§5.1–§5.5).
    ///
    /// Landscape only, split 40 / 60 between browsing and control, with deck A on the left,
    /// the mixer in the middle and deck B mirrored on the right. It composes the same
    /// <see cref="BrowserView"/>, <see cref="DeckPanelView"/> and <see cref="MixerPanelView"/>
    /// the Mac window uses, so the two surfaces cannot drift apart.
    ///
    /// The content root is inset by the device safe area (FR-075), so every control inside it
    /// is automatically clear of the rounded corners and the home indicator without each
    /// widget having to know they exist.
    /// </summary>
    public sealed class ControllerScreen : MonoBehaviour
    {
        /// <summary>
        /// Design resolution: iPad mini in landscape, in points. The canvas matches on height,
        /// so a taller-aspect device gives the rows more room rather than shrinking everything.
        /// </summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1133f, 744f);

        private const float StatusHeight = 26f;
        private const float BrowserFraction = 0.4f;
        private const float MixerWidth = 232f;

        private RectTransform _content;
        private RectTransform _decks;
        private Text _connectionLabel;
        private Text _recordingLabel;
        private ButtonWidget _disconnectButton;

        public BrowserView Browser { get; private set; }
        public DeckPanelView DeckA { get; private set; }
        public DeckPanelView DeckB { get; private set; }
        public MixerPanelView Mixer { get; private set; }
        public ConnectPanel Connect { get; private set; }
        public TouchRouter Router { get; private set; }

        /// <summary>Raised when the user asks to drop the link.</summary>
        public event Action DisconnectRequested;

        public static ControllerScreen Create(Transform parent, IDeckCommands commands, string lastAddress)
        {
            var canvas = UiFactory.CreateCanvas("Controller Canvas", parent, ReferenceResolution);
            var screen = canvas.gameObject.AddComponent<ControllerScreen>();
            screen.Router = canvas.gameObject.AddComponent<TouchRouter>();
            screen.Build((RectTransform)canvas.transform, commands, lastAddress);
            return screen;
        }

        private void Build(RectTransform canvasRect, IDeckCommands commands, string lastAddress)
        {
            UiFactory.CreateImage("Background", canvasRect, Theme.Background);

            // Safe area first: everything below lives inside it (FR-075).
            var safe = UiFactory.Create("SafeArea", canvasRect);
            UiFactory.ApplySafeArea(safe);
            _safeArea = safe;

            _content = UiFactory.Create("Content", safe);
            UiFactory.Stretch(_content, 10f, 6f, 10f, 6f);

            _connectionLabel = UiFactory.CreateText("Connection", _content, "Not connected",
                Theme.FontSizeLabel, TextAnchor.MiddleLeft, Theme.TextDim);
            _recordingLabel = UiFactory.CreateText("Recording", _content, string.Empty,
                Theme.FontSizeLabel, TextAnchor.MiddleRight, Theme.TextDim);

            _disconnectButton = ButtonWidget.Create("Disconnect", _content, "DISCONNECT",
                Router, null, Theme.FontSizeSmall);
            _disconnectButton.Clicked += () => DisconnectRequested?.Invoke();

            Browser = BrowserView.Create("Browser", _content, Router);
            Browser.SeekRequested += commands.Seek;

            _decks = UiFactory.Create("Decks", _content);
            DeckA = DeckPanelView.Create("DeckA", _decks, Router, DeckId.A, commands, false);
            Mixer = MixerPanelView.Create("Mixer", _decks, Router, commands);
            DeckB = DeckPanelView.Create("DeckB", _decks, Router, DeckId.B, commands, true);

            Connect = ConnectPanel.Create("ConnectPanel", canvasRect, Router, lastAddress);
        }

        private RectTransform _safeArea;

        private void Start() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

        private void Update()
        {
            // The safe area changes when the iPad is rotated between the two landscape
            // orientations, and Unity does not raise an event for it.
            var safe = Screen.safeArea;
            if (safe != _lastSafeArea)
            {
                _lastSafeArea = safe;
                UiFactory.ApplySafeArea(_safeArea);
                Layout();
            }
        }

        private Rect _lastSafeArea;

        private void Layout()
        {
            if (_content == null)
            {
                return;
            }

            var width = _content.rect.width;
            var height = _content.rect.height;
            if (width <= 1f || height <= 1f)
            {
                return;
            }

            // Status strip.
            var disconnectWidth = 110f;
            UiFactory.Place(_connectionLabel.rectTransform, 0f, 0f, width * 0.5f, StatusHeight);
            UiFactory.Place(_recordingLabel.rectTransform, width * 0.5f, 0f,
                width * 0.5f - disconnectWidth - 8f, StatusHeight);
            UiFactory.Place(_disconnectButton.Rect, width - disconnectWidth, 1f, disconnectWidth, StatusHeight - 2f);

            var bodyTop = StatusHeight + 4f;
            var bodyHeight = height - bodyTop;
            var browserHeight = bodyHeight * BrowserFraction;

            UiFactory.Place((RectTransform)Browser.transform, 0f, bodyTop, width, browserHeight);

            var decksTop = bodyTop + browserHeight + Theme.Gap;
            var decksHeight = bodyHeight - browserHeight - Theme.Gap;
            UiFactory.Place(_decks, 0f, decksTop, width, decksHeight);

            var mixerWidth = Mathf.Min(MixerWidth, width * 0.24f);
            var deckWidth = (width - mixerWidth - Theme.Gap * 2f) * 0.5f;
            UiFactory.Place((RectTransform)DeckA.transform, 0f, 0f, deckWidth, decksHeight);
            UiFactory.Place((RectTransform)Mixer.transform, deckWidth + Theme.Gap, 0f, mixerWidth, decksHeight);
            UiFactory.Place((RectTransform)DeckB.transform,
                deckWidth + mixerWidth + Theme.Gap * 2f, 0f, deckWidth, decksHeight);
        }

        /// <summary>Renders the host state into every panel.</summary>
        public void Apply(StateSnapshot snapshot)
        {
            Browser.Apply(snapshot);
            DeckA.Apply(snapshot.DeckA);
            DeckB.Apply(snapshot.DeckB);
            Mixer.Apply(snapshot);

            _recordingLabel.text = snapshot.IsRecording
                ? "● REC " + TrackInfo.FormatDuration(snapshot.RecordingSeconds)
                : string.Empty;
            _recordingLabel.color = Theme.Danger;
        }

        /// <summary>Shows or hides the connection sheet and updates the status strip (FR-062).</summary>
        public void SetConnectionState(ConnectionState state, string statusText)
        {
            var connected = state == ConnectionState.Connected;
            Connect.gameObject.SetActive(!connected);
            _disconnectButton.gameObject.SetActive(connected);

            _connectionLabel.text = statusText ?? string.Empty;
            _connectionLabel.color = connected ? Theme.Ok
                : state == ConnectionState.Failed ? Theme.Danger
                : Theme.Warning;
        }

        /// <summary>
        /// Releases every held control. Called when the app is backgrounded (FR-074) and when
        /// the link drops (FR-066) — in both cases the finger that was driving a control is
        /// gone, and leaving it held would freeze a fader or a platter.
        /// </summary>
        public void ReleaseAll()
        {
            Router.CancelAll();
            Browser.ReleaseAll();
            DeckA.ReleaseAll();
            DeckB.ReleaseAll();
            Mixer.ReleaseAll();
            Connect.ReleaseAll();
        }
    }
}
