using System;
using System.Collections.Generic;
using AIDeck.Core.Generation;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// The generation sheet: a modal over the deck, never beside it.
    ///
    /// It covers the whole screen behind a scrim rather than taking a corner, for the same
    /// reason the controller's connect sheet does — a half-visible deck invites a click on a
    /// transport control while a model is loading, and this is exactly the moment when
    /// nothing should be started. Closing it puts every deck control back within reach
    /// untouched.
    /// </summary>
    public sealed class GeneratePanel : MonoBehaviour
    {
        /// <summary>Raised when the user asks to load the models.</summary>
        public event Action StartServerRequested;

        /// <summary>Raised when the user asks to unload them. True means "even if adopted".</summary>
        public event Action<bool> StopServerRequested;

        /// <summary>Raised with the typed fields when the user asks to generate.</summary>
        public event Action<GenerationFields> GenerateRequested;

        public event Action CancelRequested;
        public event Action CloseRequested;

        /// <summary>Raised when the finished track should go onto a deck.</summary>
        public event Action<DeckId> LoadCompletedRequested;

        private RectTransform _card;
        private Text _title;
        private Text _stateLine;
        private Text _messageLine;
        private Text _warningLine;
        private Text _fieldError;
        private Text _resultLine;

        private InputField _titleField;
        private InputField _promptField;
        private InputField _lyricsField;
        private InputField _durationField;
        private InputField _bpmField;
        private InputField _keyField;
        private InputField _languageField;
        private InputField _seedField;

        private ButtonWidget _startServerButton;
        private ButtonWidget _generateButton;
        private ButtonWidget _cancelButton;
        private ButtonWidget _stopServerButton;
        private ButtonWidget _closeButton;
        private ButtonWidget _loadAButton;
        private ButtonWidget _loadBButton;
        private ButtonWidget _stopAfterToggle;
        private bool _stopAfterGenerating = true;

        private TouchRouter _router;
        private readonly List<ButtonWidget> _buttons = new List<ButtonWidget>();

        /// <summary>
        /// Whether the engine should be unloaded as soon as a track is finished.
        ///
        /// On by default. The models hold several gigabytes and this machine has sixteen; the
        /// common case is "make a track, then play", and leaving a model resident through a
        /// set is the expensive mistake to make by accident.
        /// </summary>
        public bool StopServerAfterGenerating => _stopAfterGenerating;

        public static GeneratePanel Create(string name, Transform parent, TouchRouter router)
        {
            var root = UiFactory.Create(name, parent);
            var panel = root.gameObject.AddComponent<GeneratePanel>();
            panel._router = router;
            panel.Build(root);
            panel.SetVisible(false);
            return panel;
        }

        private void Build(RectTransform root)
        {
            var scrim = UiFactory.CreateImage("Scrim", root, new Color(0.03f, 0.035f, 0.05f, 0.96f));
            scrim.raycastTarget = false;

            _card = UiFactory.CreatePanel("Card", root, Theme.Panel, Theme.Line);

            _title = UiFactory.CreateText("Title", _card, "Generate a track on this Mac",
                Theme.FontSizeLarge, TextAnchor.MiddleLeft, Theme.Text, FontStyle.Bold);

            _stateLine = UiFactory.CreateText("State", _card, "Not connected",
                Theme.FontSizeBody, TextAnchor.MiddleLeft, Theme.TextDim);

            _messageLine = UiFactory.CreateText("Message", _card, string.Empty,
                Theme.FontSizeSmall, TextAnchor.UpperLeft, Theme.TextDim);
            _messageLine.horizontalOverflow = HorizontalWrapMode.Wrap;
            _messageLine.verticalOverflow = VerticalWrapMode.Truncate;

            // Said before anything is pressed, not after it hurts. Both numbers are measured
            // on this machine and are in docs/GENERATOR_PHASE1.md.
            _warningLine = UiFactory.CreateText("Warning", _card,
                "The first start loads about 10 GB and takes a few minutes. On 16 GB this "
                + "swaps heavily — around 19 GB during a 30-second track — so stop the decks "
                + "before generating.",
                Theme.FontSizeSmall, TextAnchor.UpperLeft, Theme.Warning);
            _warningLine.horizontalOverflow = HorizontalWrapMode.Wrap;
            _warningLine.verticalOverflow = VerticalWrapMode.Truncate;

            _titleField = BuildField("TitleField", "Track title", "Neon Highway Phase 2");
            _promptField = BuildField("PromptField", "Style / description", DefaultPrompt);
            _lyricsField = BuildField("LyricsField", "Lyrics (optional)", string.Empty, multiline: true);
            _durationField = BuildField("DurationField", "Length (seconds)", "30");
            _bpmField = BuildField("BpmField", "BPM", "118");
            _keyField = BuildField("KeyField", "Key", "A minor");
            _languageField = BuildField("LanguageField", "Vocal language", "ja");
            _seedField = BuildField("SeedField", "Seed (optional)", string.Empty);

            _fieldError = UiFactory.CreateText("FieldError", _card, string.Empty,
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.Danger);

            // A latched button rather than a new widget type: MUTE and ECHO already mean
            // "on" by sitting in Active, so this reads the same way to a user and to a test.
            _stopAfterToggle = MakeButton("StopAfter", StopAfterLabel(true), ToggleStopAfter);
            _stopAfterToggle.State = ButtonVisualState.Active;

            _resultLine = UiFactory.CreateText("Result", _card, string.Empty,
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.Ok);

            _startServerButton = MakeButton("StartServer", "START AI SERVER",
                () => StartServerRequested?.Invoke());
            _generateButton = MakeButton("GenerateNow", "GENERATE", RaiseGenerate);
            _cancelButton = MakeButton("Cancel", "CANCEL", () => CancelRequested?.Invoke());
            _stopServerButton = MakeButton("StopServer", "STOP AI SERVER",
                () => StopServerRequested?.Invoke(true));
            _closeButton = MakeButton("Close", "CLOSE", () => CloseRequested?.Invoke());
            _loadAButton = MakeButton("LoadA", "LOAD TO A", () => LoadCompletedRequested?.Invoke(DeckId.A));
            _loadBButton = MakeButton("LoadB", "LOAD TO B", () => LoadCompletedRequested?.Invoke(DeckId.B));

            // Above everything else on the canvas, like the controller's connect sheet.
            foreach (var button in _buttons)
            {
                button.TouchPriority = 100;
            }

        }

        private static string StopAfterLabel(bool on) =>
            on
                ? "STOP THE AI SERVER AFTER GENERATING:  ON"
                : "STOP THE AI SERVER AFTER GENERATING:  OFF";

        private void ToggleStopAfter()
        {
            _stopAfterGenerating = !_stopAfterGenerating;
            _stopAfterToggle.Label = StopAfterLabel(_stopAfterGenerating);
            _stopAfterToggle.State = _stopAfterGenerating
                ? ButtonVisualState.Active
                : ButtonVisualState.Normal;
        }

        private const string DefaultPrompt =
            "Japanese female vocal, sophisticated night-drive melodic deep house and synthwave, "
            + "steady four-on-the-floor beat, deep warm bass, shimmering synth arpeggios, "
            + "DJ-friendly instrumental intro and outro";

        private ButtonWidget MakeButton(string name, string label, Action clicked)
        {
            var button = ButtonWidget.Create(name, _card, label, _router, null, Theme.FontSizeSmall);
            button.Clicked += clicked;
            _buttons.Add(button);
            return button;
        }

        private InputField BuildField(string name, string label, string initial, bool multiline = false)
        {
            var labelText = UiFactory.CreateText(name + "Label", _card, label,
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.TextDim);
            labelText.name = name + "Label";

            var field = UiFactory.Create(name, _card);
            var background = field.gameObject.AddComponent<Image>();
            background.color = Theme.PanelRaised;
            var outline = field.gameObject.AddComponent<Outline>();
            outline.effectColor = Theme.Line;
            outline.effectDistance = new Vector2(1f, 1f);
            outline.useGraphicAlpha = false;

            var text = UiFactory.CreateText("Text", field, string.Empty, Theme.FontSizeLabel);
            UiFactory.Stretch(text.rectTransform, 8f, 4f, 8f, 4f);
            text.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;

            var placeholder = UiFactory.CreateText("Placeholder", field, label,
                Theme.FontSizeLabel, multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft,
                Theme.TextDim);
            UiFactory.Stretch(placeholder.rectTransform, 8f, 4f, 8f, 4f);

            var input = field.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
            input.text = initial ?? string.Empty;
            return input;
        }

        // ------------------------------------------------------------------ state

        /// <summary>Reads the boxes. Never mutates them, so a refusal costs the user nothing.</summary>
        public GenerationFields ReadFields() => new GenerationFields
        {
            Title = _titleField.text,
            Prompt = _promptField.text,
            Lyrics = _lyricsField.text,
            Duration = _durationField.text,
            Bpm = _bpmField.text,
            Key = _keyField.text,
            TimeSignature = "4",
            VocalLanguage = _languageField.text,
            Seed = _seedField.text
        };

        private void RaiseGenerate()
        {
            var fields = ReadFields();
            var validation = GenerationValidator.Validate(fields);
            if (!validation.IsValid)
            {
                // The boxes keep everything that was typed; only the error line changes.
                ShowFieldError(validation.Message);
                return;
            }

            ShowFieldError(string.Empty);
            GenerateRequested?.Invoke(fields);
        }

        public void ShowFieldError(string message)
        {
            if (_fieldError != null)
            {
                _fieldError.text = message ?? string.Empty;
            }
        }

        /// <summary>Renders one status, plus whatever the safety gate has to say about it.</summary>
        public void Apply(GeneratorStatus status, GateDecision gate)
        {
            _stateLine.text = status.State.ToDisplayText()
                              + (status.State.IsBusy() && status.ElapsedSeconds > 0f
                                  ? $" — {status.ElapsedSeconds:0} s"
                                  : string.Empty);
            _stateLine.color = ColourFor(status.State);

            // A refusal from the gate is more useful than the generator's own idle message,
            // so it wins the one line there is room for.
            _messageLine.text = gate.IsAllowed ? status.Message : gate.Reason;
            _messageLine.color = gate.IsAllowed ? Theme.TextDim : Theme.Warning;

            if (status.State == GeneratorState.Failed && !string.IsNullOrEmpty(status.Message))
            {
                _messageLine.color = Theme.Danger;
            }

            var completed = status.State == GeneratorState.Completed
                            && !string.IsNullOrEmpty(status.CompletedFileName);
            _resultLine.text = completed
                ? $"Added: {status.CompletedFileName} ({status.CompletedDurationSeconds:0.0} s)"
                : string.Empty;

            _loadAButton.gameObject.SetActive(completed);
            _loadBButton.gameObject.SetActive(completed);

            var busy = status.State.IsBusy();
            _generateButton.State = gate.IsAllowed ? ButtonVisualState.Normal : ButtonVisualState.Disabled;
            _cancelButton.State = busy ? ButtonVisualState.Normal : ButtonVisualState.Disabled;
            _startServerButton.State =
                status.State == GeneratorState.Stopped || status.State == GeneratorState.Unknown
                    ? ButtonVisualState.Normal
                    : ButtonVisualState.Disabled;
            _stopServerButton.State =
                status.State == GeneratorState.Stopped
                || status.State == GeneratorState.NotInstalled
                || busy
                    ? ButtonVisualState.Disabled
                    : ButtonVisualState.Normal;

            Layout();
        }

        private static Color ColourFor(GeneratorState state)
        {
            switch (state)
            {
                case GeneratorState.Ready:
                case GeneratorState.Completed:
                    return Theme.Ok;
                case GeneratorState.Failed:
                    return Theme.Danger;
                case GeneratorState.Starting:
                case GeneratorState.Queued:
                case GeneratorState.Generating:
                case GeneratorState.Cancelling:
                    return Theme.Warning;
                default:
                    return Theme.TextDim;
            }
        }

        public bool IsVisible => gameObject.activeSelf;

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);

            if (visible)
            {
                // Unity does not deliver OnRectTransformDimensionsChange to a disabled object,
                // so a sheet that has been hidden since it was built has never been laid out
                // and every control still sits at its default full-rect size — which makes
                // them overlap and the wrong one take the press.
                Layout();
            }

            if (!visible && _router != null)
            {
                // Anything held when the sheet closes is released, so a control cannot be left
                // captured behind a panel that is no longer there (FR-073).
                _router.CancelAll();
            }
        }

        // ----------------------------------------------------------------- layout

        private void OnEnable() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

        private void Layout()
        {
            var rect = (RectTransform)transform;
            var width = rect.rect.width;
            var height = rect.rect.height;
            if (width <= 1f || height <= 1f || _card == null)
            {
                return;
            }

            const float pad = 20f;
            const float gap = 8f;
            const float rowHeight = 30f;
            const float labelHeight = 15f;

            var cardWidth = Mathf.Min(760f, width - 80f);
            var cardHeight = Mathf.Min(760f, height - 40f);
            _card.anchorMin = new Vector2(0.5f, 0.5f);
            _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            _card.sizeDelta = new Vector2(cardWidth, cardHeight);
            _card.anchoredPosition = Vector2.zero;

            var inner = cardWidth - pad * 2f;
            var y = pad;

            UiFactory.Place(_title.rectTransform, pad, y, inner, 26f);
            y += 28f;
            UiFactory.Place(_stateLine.rectTransform, pad, y, inner, 20f);
            y += 22f;
            UiFactory.Place(_messageLine.rectTransform, pad, y, inner, 34f);
            y += 36f;
            UiFactory.Place(_warningLine.rectTransform, pad, y, inner, 44f);
            y += 48f;

            y = PlaceField(_titleField, "TitleField", pad, y, inner, rowHeight, labelHeight, gap);
            y = PlaceField(_promptField, "PromptField", pad, y, inner, rowHeight, labelHeight, gap);
            y = PlaceField(_lyricsField, "LyricsField", pad, y, inner, 66f, labelHeight, gap);

            // Four short numeric boxes on one row.
            var quarter = (inner - gap * 3f) / 4f;
            PlaceLabel("DurationFieldLabel", pad, y, quarter, labelHeight);
            PlaceLabel("BpmFieldLabel", pad + quarter + gap, y, quarter, labelHeight);
            PlaceLabel("KeyFieldLabel", pad + (quarter + gap) * 2f, y, quarter, labelHeight);
            PlaceLabel("SeedFieldLabel", pad + (quarter + gap) * 3f, y, quarter, labelHeight);
            y += labelHeight + 2f;
            UiFactory.Place((RectTransform)_durationField.transform, pad, y, quarter, rowHeight);
            UiFactory.Place((RectTransform)_bpmField.transform, pad + quarter + gap, y, quarter, rowHeight);
            UiFactory.Place((RectTransform)_keyField.transform, pad + (quarter + gap) * 2f, y, quarter, rowHeight);
            UiFactory.Place((RectTransform)_seedField.transform, pad + (quarter + gap) * 3f, y, quarter, rowHeight);
            y += rowHeight + gap;

            y = PlaceField(_languageField, "LanguageFieldLabel", pad, y, quarter, rowHeight, labelHeight, gap);

            UiFactory.Place(_fieldError.rectTransform, pad, y, inner, 18f);
            y += 20f;

            UiFactory.Place(_stopAfterToggle.Rect, pad, y, inner, 26f);
            y += 30f;

            UiFactory.Place(_resultLine.rectTransform, pad, y, inner, 18f);
            y += 22f;

            var buttonWidth = (inner - gap * 2f) / 3f;
            UiFactory.Place(_startServerButton.Rect, pad, y, buttonWidth, Theme.TouchSize);
            UiFactory.Place(_generateButton.Rect, pad + buttonWidth + gap, y, buttonWidth, Theme.TouchSize);
            UiFactory.Place(_cancelButton.Rect, pad + (buttonWidth + gap) * 2f, y, buttonWidth, Theme.TouchSize);
            y += Theme.TouchSize + gap;

            UiFactory.Place(_stopServerButton.Rect, pad, y, buttonWidth, Theme.TouchSize);
            UiFactory.Place(_loadAButton.Rect, pad + buttonWidth + gap, y, buttonWidth * 0.48f, Theme.TouchSize);
            UiFactory.Place(_loadBButton.Rect, pad + buttonWidth + gap + buttonWidth * 0.52f, y,
                buttonWidth * 0.48f, Theme.TouchSize);
            UiFactory.Place(_closeButton.Rect, pad + (buttonWidth + gap) * 2f, y, buttonWidth, Theme.TouchSize);
        }

        private float PlaceField(
            InputField field, string labelName, float x, float y, float width,
            float height, float labelHeight, float gap)
        {
            PlaceLabel(labelName.EndsWith("Label") ? labelName : labelName + "Label",
                x, y, width, labelHeight);
            y += labelHeight + 2f;
            UiFactory.Place((RectTransform)field.transform, x, y, width, height);
            return y + height + gap;
        }

        private void PlaceLabel(string name, float x, float y, float width, float height)
        {
            var label = _card.Find(name) as RectTransform;
            if (label != null)
            {
                UiFactory.Place(label, x, y, width, height);
            }
        }
    }
}
