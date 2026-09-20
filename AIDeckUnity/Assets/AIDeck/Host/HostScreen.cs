using System;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.Host
{
    /// <summary>
    /// The Mac window (§5.6).
    ///
    /// Requirement §5.6 is that the Mac can do everything without the iPad, so it composes the
    /// same <see cref="DeckPanelView"/>, <see cref="MixerPanelView"/> and
    /// <see cref="BrowserView"/> the controller uses, and adds the things only the host has:
    /// the library import field, the Mac's own address for manual connection, the iPad's
    /// connection state, and a one-line log summary.
    /// </summary>
    public sealed class HostScreen : MonoBehaviour
    {
        /// <summary>Design resolution. The canvas scales from here.</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1440f, 900f);

        private const float TopBarHeight = 38f;
        private const float LogHeight = 22f;
        private const float BrowserFraction = 0.4f;

        private RectTransform _content;
        private InputField _pathField;
        private Text _addressLabel;
        private Text _logLabel;
        private Text _importLabel;

        public BrowserView Browser { get; private set; }
        public DeckPanelView DeckA { get; private set; }
        public DeckPanelView DeckB { get; private set; }
        public MixerPanelView Mixer { get; private set; }
        public TouchRouter Router { get; private set; }

        /// <summary>Raised with the path the user typed into the import field.</summary>
        public event Action<string> AddPathRequested;

        /// <summary>Raised by the panic button (§9).</summary>
        public event Action AllStopRequested;

        /// <summary>Raised when the user asks to remove the selected track from the library (§5.6).</summary>
        public event Action RemoveSelectedRequested;

        /// <summary>Raised when the user opens the generation sheet from the top bar.</summary>
        public event Action GenerateRequested;

        /// <summary>The generation sheet. Built with the screen, shown only on request.</summary>
        public GeneratePanel Generate { get; private set; }

        public static HostScreen Create(Transform parent, IDeckCommands commands)
        {
            var canvas = UiFactory.CreateCanvas("Host Canvas", parent, ReferenceResolution);
            var screen = canvas.gameObject.AddComponent<HostScreen>();
            screen.Router = canvas.gameObject.AddComponent<TouchRouter>();
            screen.Build((RectTransform)canvas.transform, commands);
            return screen;
        }

        private void Build(RectTransform canvasRect, IDeckCommands commands)
        {
            UiFactory.CreateImage("Background", canvasRect, Theme.Background);

            _content = UiFactory.Create("Content", canvasRect);
            UiFactory.Stretch(_content, 12f, 10f, 12f, 10f);

            BuildTopBar(commands);

            Browser = BrowserView.Create("Browser", _content, Router);
            Browser.SeekRequested += commands.Seek;

            var decks = UiFactory.Create("Decks", _content);
            DeckA = DeckPanelView.Create("DeckA", decks, Router, DeckId.A, commands, false);
            Mixer = MixerPanelView.Create("Mixer", decks, Router, commands);
            DeckB = DeckPanelView.Create("DeckB", decks, Router, DeckId.B, commands, true);
            _decks = decks;

            // On the canvas rather than inside the content rect, so the sheet covers the deck
            // completely and no transport control can be reached behind it.
            Generate = GeneratePanel.Create("GeneratePanel", canvasRect, Router);

            _logLabel = UiFactory.CreateText("Log", _content, string.Empty,
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.TextDim);
        }

        private RectTransform _decks;

        private void BuildTopBar(IDeckCommands commands)
        {
            var bar = UiFactory.Create("TopBar", _content);
            _topBar = bar;

            var title = UiFactory.CreateText("Title", bar, "AI DECK",
                Theme.FontSizeBody, TextAnchor.MiddleLeft, Theme.Text, FontStyle.Bold);
            _title = title;

            _addressLabel = UiFactory.CreateText("Address", bar, string.Empty,
                Theme.FontSizeLabel, TextAnchor.MiddleLeft, Theme.TextDim);

            var field = UiFactory.Create("PathField", bar);
            var fieldBackground = field.gameObject.AddComponent<Image>();
            fieldBackground.color = Theme.PanelRaised;
            var fieldOutline = field.gameObject.AddComponent<Outline>();
            fieldOutline.effectColor = Theme.Line;
            fieldOutline.effectDistance = new Vector2(1f, 1f);
            fieldOutline.useGraphicAlpha = false;

            var fieldText = UiFactory.CreateText("Text", field, string.Empty, Theme.FontSizeLabel);
            UiFactory.Stretch(fieldText.rectTransform, 8f, 0f, 8f, 0f);
            var placeholder = UiFactory.CreateText("Placeholder", field,
                "Path of a music file or folder to add…", Theme.FontSizeLabel,
                TextAnchor.MiddleLeft, Theme.TextDim);
            UiFactory.Stretch(placeholder.rectTransform, 8f, 0f, 8f, 0f);

            _pathField = field.gameObject.AddComponent<InputField>();
            _pathField.textComponent = fieldText;
            _pathField.placeholder = placeholder;
            _pathField.lineType = InputField.LineType.SingleLine;
            _pathField.onSubmit.AddListener(_ => SubmitPath());

            _addButton = ButtonWidget.Create("Add", bar, "ADD FILES", Router, null, Theme.FontSizeSmall);
            _addButton.Clicked += SubmitPath;

            _removeButton = ButtonWidget.Create("Remove", bar, "REMOVE", Router, null, Theme.FontSizeSmall);
            _removeButton.Clicked += () => RemoveSelectedRequested?.Invoke();

            _allStopButton = ButtonWidget.Create("AllStop", bar, "ALL STOP", Router, null, Theme.FontSizeSmall);
            _allStopButton.Clicked += () => AllStopRequested?.Invoke();

            _generateButton = ButtonWidget.Create("Generate", bar, "GENERATE", Router, null, Theme.FontSizeSmall);
            _generateButton.Clicked += () => GenerateRequested?.Invoke();

            _importLabel = UiFactory.CreateText("Import", bar, string.Empty,
                Theme.FontSizeSmall, TextAnchor.MiddleRight, Theme.TextDim);
        }

        private RectTransform _topBar;
        private Text _title;
        private ButtonWidget _addButton;
        private ButtonWidget _removeButton;
        private ButtonWidget _allStopButton;
        private ButtonWidget _generateButton;

        private void SubmitPath()
        {
            var path = _pathField.text;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            AddPathRequested?.Invoke(path);
        }

        private void Start() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

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

            UiFactory.Place(_topBar, 0f, 0f, width, TopBarHeight);

            var x = 0f;
            UiFactory.Place(_title.rectTransform, x, 0f, 90f, TopBarHeight);
            x += 94f;
            UiFactory.Place(_addressLabel.rectTransform, x, 0f, 230f, TopBarHeight);
            x += 236f;

            const float buttonWidth = 96f;
            const float buttonStride = buttonWidth + 6f;
            var buttons = buttonStride * 4f;
            var fieldWidth = Mathf.Max(160f, width - x - buttons - 220f);
            UiFactory.Place((RectTransform)_pathField.transform, x, 6f, fieldWidth, TopBarHeight - 12f);
            x += fieldWidth + 6f;
            UiFactory.Place(_addButton.Rect, x, 5f, buttonWidth, TopBarHeight - 10f);
            x += buttonStride;
            UiFactory.Place(_removeButton.Rect, x, 5f, buttonWidth, TopBarHeight - 10f);
            x += buttonStride;
            UiFactory.Place(_allStopButton.Rect, x, 5f, buttonWidth, TopBarHeight - 10f);
            x += buttonStride;
            UiFactory.Place(_generateButton.Rect, x, 5f, buttonWidth, TopBarHeight - 10f);
            x += buttonStride;
            UiFactory.Place(_importLabel.rectTransform, x, 0f, Mathf.Max(0f, width - x), TopBarHeight);

            var bodyTop = TopBarHeight + Theme.Gap;
            var bodyHeight = height - bodyTop - LogHeight - Theme.Gap;
            var browserHeight = bodyHeight * BrowserFraction;

            UiFactory.Place((RectTransform)Browser.transform, 0f, bodyTop, width, browserHeight);

            var decksTop = bodyTop + browserHeight + Theme.Gap;
            var decksHeight = bodyHeight - browserHeight - Theme.Gap;
            UiFactory.Place(_decks, 0f, decksTop, width, decksHeight);

            var mixerWidth = 236f;
            var deckWidth = (width - mixerWidth - Theme.Gap * 2f) * 0.5f;
            UiFactory.Place((RectTransform)DeckA.transform, 0f, 0f, deckWidth, decksHeight);
            UiFactory.Place((RectTransform)Mixer.transform, deckWidth + Theme.Gap, 0f, mixerWidth, decksHeight);
            UiFactory.Place((RectTransform)DeckB.transform,
                deckWidth + mixerWidth + Theme.Gap * 2f, 0f, deckWidth, decksHeight);

            UiFactory.Place(_logLabel.rectTransform, 0f, height - LogHeight, width, LogHeight);
        }

        /// <summary>Renders the host's own state into every panel.</summary>
        public void Apply(StateSnapshot snapshot)
        {
            Browser.Apply(snapshot);
            DeckA.Apply(snapshot.DeckA);
            DeckB.Apply(snapshot.DeckB);
            Mixer.Apply(snapshot);
        }

        /// <summary>Shows the Mac's LAN address, so it can be typed into the iPad (§5.6, FR-061).</summary>
        public void SetAddress(string address, int port)
        {
            _addressLabel.text = string.IsNullOrEmpty(address)
                ? "Address unavailable"
                : $"{address}:{port}";
        }

        /// <summary>The one-line log summary of §5.6.</summary>
        public void SetLog(string message, bool isError = false)
        {
            _logLabel.text = message ?? string.Empty;
            _logLabel.color = isError ? Theme.Danger : Theme.TextDim;
        }

        public void SetImportStatus(string message)
        {
            _importLabel.text = message ?? string.Empty;
        }

        public void ClearPathField() => _pathField.text = string.Empty;

        /// <summary>
        /// Hides the Mac window without stopping the host.
        ///
        /// Used when a controller runs against an in-process host on one machine: both would
        /// otherwise draw their canvases on top of each other.
        /// </summary>
        public void SetVisible(bool visible) => gameObject.SetActive(visible);

        public void SetPathField(string path) => _pathField.text = path ?? string.Empty;

        /// <summary>Greys the remove button out when nothing is selected.</summary>
        public void SetRemoveEnabled(bool enabled) =>
            _removeButton.State = enabled ? ButtonVisualState.Normal : ButtonVisualState.Disabled;
    }
}
