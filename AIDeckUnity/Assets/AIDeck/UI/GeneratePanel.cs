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

        /// <summary>
        /// The sheet's own type scale, in reference-resolution points (1440×900, match height).
        ///
        /// Deliberately not <see cref="Theme"/>'s sizes. Those are tuned for a dense deck
        /// surface that is read at a glance and mostly recognised by shape and colour; this is
        /// a form, read word by word, and at Theme's 10–13 pt it was reported as unreadable on
        /// the actual machine. Changing Theme would have moved every label on the deck, which
        /// is not what was wrong.
        /// </summary>
        private const int TitleSize = 23;
        private const int StateSize = 18;
        private const int BodySize = 16;
        private const int WarningSize = 15;
        private const int FieldLabelSize = 14;
        private const int FieldTextSize = 16;
        private const int ButtonSize = 15;

        /// <summary>Smallest size anywhere on this sheet. Asserted by a test.</summary>
        public const int MinimumFontSize = 14;

        /// <summary>
        /// Whether GENERATE is offered: the generator can start work *and* the room is quiet.
        /// Exposed so a test can assert the rule rather than infer it from a colour.
        /// </summary>
        public bool CanGenerateNow { get; private set; }

        /// <summary>How many times the layout has actually run. For the no-churn test.</summary>
        public int LayoutCount { get; private set; }

        private RectTransform _card;
        private RectTransform _viewport;
        private RectTransform _content;
        private ScrollRect _scroll;
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

            // The card scrolls. At readable type the form is taller than a 900-point window
            // once the warning wraps, and a control the user cannot reach is worse than a
            // small one. Clamped, vertical only: this is a form, not a map.
            _viewport = UiFactory.Create("Viewport", _card);
            var viewportImage = _viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0f);
            viewportImage.raycastTarget = true;   // the scroll wheel needs something to hit
            _viewport.gameObject.AddComponent<RectMask2D>();

            _content = UiFactory.Create("Content", _viewport);

            _scroll = _card.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 24f;
            _scroll.inertia = false;

            _title = UiFactory.CreateText("Title", _content, "Generate a track on this Mac",
                TitleSize, TextAnchor.MiddleLeft, Theme.Text, FontStyle.Bold);

            _stateLine = UiFactory.CreateText("State", _content, "Not connected",
                StateSize, TextAnchor.MiddleLeft, Theme.TextDim);

            _messageLine = UiFactory.CreateText("Message", _content, string.Empty,
                BodySize, TextAnchor.UpperLeft, Theme.TextDim);
            _messageLine.horizontalOverflow = HorizontalWrapMode.Wrap;
            _messageLine.verticalOverflow = VerticalWrapMode.Overflow;

            // Said before anything is pressed, not after it hurts. Both numbers are measured
            // on this machine and are in docs/GENERATOR_PHASE1.md.
            _warningLine = UiFactory.CreateText("Warning", _content, StandingWarning,
                WarningSize, TextAnchor.UpperLeft, Theme.Warning);
            _warningLine.horizontalOverflow = HorizontalWrapMode.Wrap;
            // Overflow rather than Truncate: the layout measures this label and gives it the
            // height it asks for, so clipping it would only ever hide the warning.
            _warningLine.verticalOverflow = VerticalWrapMode.Overflow;

            _titleField = BuildField("TitleField", "Track title", "Neon Highway Phase 2");
            _promptField = BuildField("PromptField", "Style / description", DefaultPrompt);
            _lyricsField = BuildField("LyricsField", "Lyrics (optional)", string.Empty, multiline: true);
            _durationField = BuildField("DurationField", "Length (seconds)", "30");
            _bpmField = BuildField("BpmField", "BPM", "118");
            _keyField = BuildField("KeyField", "Key", "A minor");
            _languageField = BuildField("LanguageField", "Vocal language", "ja");
            _seedField = BuildField("SeedField", "Seed (optional)", string.Empty);

            _fieldError = UiFactory.CreateText("FieldError", _content, string.Empty,
                BodySize, TextAnchor.UpperLeft, Theme.Danger);
            _fieldError.horizontalOverflow = HorizontalWrapMode.Wrap;
            _fieldError.verticalOverflow = VerticalWrapMode.Overflow;

            // A latched button rather than a new widget type: MUTE and ECHO already mean
            // "on" by sitting in Active, so this reads the same way to a user and to a test.
            _stopAfterToggle = MakeButton("StopAfter", StopAfterLabel(true), ToggleStopAfter);
            _stopAfterToggle.State = ButtonVisualState.Active;

            _resultLine = UiFactory.CreateText("Result", _content, string.Empty,
                BodySize, TextAnchor.MiddleLeft, Theme.Ok);

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

        /// <summary>Shown whenever the machine is not already in trouble.</summary>
        private const string StandingWarning =
            "The first start loads about 10 GB and takes a few minutes. On 16 GB this swaps "
            + "heavily — around 19 GB during a 30-second track — so stop the decks before "
            + "generating.";

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
            var button = ButtonWidget.Create(name, _content, label, _router, null, ButtonSize);
            button.Clicked += clicked;
            _buttons.Add(button);
            return button;
        }

        private InputField BuildField(string name, string label, string initial, bool multiline = false)
        {
            var labelText = UiFactory.CreateText(name + "Label", _content, label,
                FieldLabelSize, TextAnchor.MiddleLeft, Theme.TextDim);
            labelText.name = name + "Label";

            var field = UiFactory.Create(name, _content);
            var background = field.gameObject.AddComponent<Image>();
            background.color = Theme.PanelRaised;
            var outline = field.gameObject.AddComponent<Outline>();
            outline.effectColor = Theme.Line;
            outline.effectDistance = new Vector2(1f, 1f);
            outline.useGraphicAlpha = false;

            var text = UiFactory.CreateText("Text", field, string.Empty, FieldTextSize);
            UiFactory.Stretch(text.rectTransform, 8f, 4f, 8f, 4f);
            text.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;

            var placeholder = UiFactory.CreateText("Placeholder", field, label,
                FieldTextSize, multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft,
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
            // so it wins the one line there is room for. When nothing is refused the message
            // must never contradict the state above it — a `ready` carrying "Loading models"
            // is what made the sheet look stuck, and the bridge now keeps the two together.
            _messageLine.text = gate.IsAllowed ? status.Message : gate.Reason;
            _messageLine.color = gate.IsAllowed ? Theme.TextDim : Theme.Warning;

            // The machine's own numbers, only when they are bad enough to act on. A generation
            // started at 25 GB of swap does not fail; it makes the whole Mac unusable, and
            // that is worth saying before the button is pressed rather than after.
            _warningLine.text = status.Memory.UnderPressure && !string.IsNullOrEmpty(status.Memory.Advice)
                ? status.Memory.Advice
                : StandingWarning;
            _warningLine.color = status.Memory.UnderPressure ? Theme.Danger : Theme.Warning;

            if (status.State == GeneratorState.Failed && !string.IsNullOrEmpty(status.Message))
            {
                _messageLine.color = Theme.Danger;
            }

            // Tied to "there is a finished track", not to the Completed state itself. With
            // "stop the AI server after generating" on — the default — the state moves
            // Completed → Stopped within a second or two, and keying the buttons off the state
            // made LOAD TO A vanish before it could be pressed. The bridge keeps reporting the
            // result until the next generation starts, so that is what to ask.
            var completed = !string.IsNullOrEmpty(status.CompletedFileName)
                            && !status.State.IsBusy();
            _resultLine.text = completed
                ? $"Added: {status.CompletedFileName} ({status.CompletedDurationSeconds:0.0} s)"
                : string.Empty;

            _loadAButton.gameObject.SetActive(completed);
            _loadBButton.gameObject.SetActive(completed);

            var busy = status.State.IsBusy();

            // Stated in one place, in the terms the requirement uses: the generator must be in
            // a state that can start work, and the room must be quiet. Either one alone is not
            // enough, and whichever fails has already put its reason on the message line.
            CanGenerateNow = status.State.CanGenerate() && gate.IsAllowed;
            _generateButton.State = CanGenerateNow ? ButtonVisualState.Normal : ButtonVisualState.Disabled;
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

        /// <summary>Guards against re-entering the layout from inside itself.</summary>
        private bool _laying;

        private void Layout()
        {
            // Measuring a wrapping label reads Text.preferredHeight, which asks Unity to
            // rebuild the canvas, which can call OnRectTransformDimensionsChange straight back
            // into here. Before this guard that recursion had no bottom: it blew the stack and
            // took the player down with a hard crash and no managed exception to explain it.
            if (_laying)
            {
                return;
            }

            _laying = true;
            try
            {
                LayoutInner();
            }
            finally
            {
                _laying = false;
            }
        }

        private void LayoutInner()
        {
            LayoutCount++;
            var rect = (RectTransform)transform;
            var width = rect.rect.width;
            var height = rect.rect.height;
            if (width <= 1f || height <= 1f || _card == null)
            {
                return;
            }

            const float pad = 22f;
            const float gap = 10f;
            const float rowHeight = 34f;
            const float labelHeight = 19f;

            var cardWidth = Mathf.Min(820f, width - 60f);
            var cardHeight = Mathf.Min(860f, height - 40f);
            _card.anchorMin = new Vector2(0.5f, 0.5f);
            _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            _card.sizeDelta = new Vector2(cardWidth, cardHeight);
            _card.anchoredPosition = Vector2.zero;

            UiFactory.Stretch(_viewport, 0f, 0f, 0f, 0f);

            var inner = cardWidth - pad * 2f;
            var y = pad;

            UiFactory.Place(_title.rectTransform, pad, y, inner, TitleSize + 10f);
            y += TitleSize + 14f;
            UiFactory.Place(_stateLine.rectTransform, pad, y, inner, StateSize + 8f);
            y += StateSize + 12f;

            // Measured rather than guessed: the warning is three lines at one width and two at
            // another, and a fixed height either clips it or leaves a hole above the fields.
            var messageHeight = WrappedHeight(_messageLine, inner, BodySize);
            UiFactory.Place(_messageLine.rectTransform, pad, y, inner, messageHeight);
            y += messageHeight + 6f;

            var warningHeight = WrappedHeight(_warningLine, inner, WarningSize);
            UiFactory.Place(_warningLine.rectTransform, pad, y, inner, warningHeight);
            y += warningHeight + gap;

            y = PlaceField(_titleField, "TitleField", pad, y, inner, rowHeight, labelHeight, gap);
            y = PlaceField(_promptField, "PromptField", pad, y, inner, rowHeight * 2f, labelHeight, gap);
            y = PlaceField(_lyricsField, "LyricsField", pad, y, inner, 84f, labelHeight, gap);

            // Four short boxes on one row.
            var quarter = (inner - gap * 3f) / 4f;
            PlaceLabel("DurationFieldLabel", pad, y, quarter, labelHeight);
            PlaceLabel("BpmFieldLabel", pad + quarter + gap, y, quarter, labelHeight);
            PlaceLabel("KeyFieldLabel", pad + (quarter + gap) * 2f, y, quarter, labelHeight);
            PlaceLabel("SeedFieldLabel", pad + (quarter + gap) * 3f, y, quarter, labelHeight);
            y += labelHeight + 3f;
            UiFactory.Place((RectTransform)_durationField.transform, pad, y, quarter, rowHeight);
            UiFactory.Place((RectTransform)_bpmField.transform, pad + quarter + gap, y, quarter, rowHeight);
            UiFactory.Place((RectTransform)_keyField.transform, pad + (quarter + gap) * 2f, y, quarter, rowHeight);
            UiFactory.Place((RectTransform)_seedField.transform, pad + (quarter + gap) * 3f, y, quarter, rowHeight);
            y += rowHeight + gap;

            y = PlaceField(_languageField, "LanguageFieldLabel", pad, y, quarter * 2f,
                rowHeight, labelHeight, gap);

            var errorHeight = WrappedHeight(_fieldError, inner, BodySize);
            UiFactory.Place(_fieldError.rectTransform, pad, y, inner, errorHeight);
            y += errorHeight + 6f;

            UiFactory.Place(_stopAfterToggle.Rect, pad, y, inner, Theme.TouchSize);
            y += Theme.TouchSize + gap;

            UiFactory.Place(_resultLine.rectTransform, pad, y, inner, BodySize + 8f);
            y += BodySize + 14f;

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
            y += Theme.TouchSize + pad;

            ContentHeight = y;

            // Anchored to the top so growing downwards scrolls rather than re-centres.
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = new Vector2(0f, 0f);
            _content.offsetMax = new Vector2(0f, 0f);
            _content.sizeDelta = new Vector2(0f, y);

            NeedsScrolling = y > cardHeight + 0.5f;
            if (_scroll != null)
            {
                _scroll.enabled = NeedsScrolling;
                if (!NeedsScrolling)
                {
                    _content.anchoredPosition = Vector2.zero;
                }
            }
        }

        /// <summary>Total height of the laid-out form. Larger than the card means it scrolls.</summary>
        public float ContentHeight { get; private set; }

        /// <summary>Whether the form is taller than the card and the user must scroll.</summary>
        public bool NeedsScrolling { get; private set; }

        /// <summary>
        /// Height a wrapping label needs at this width.
        ///
        /// <c>Text.preferredHeight</c> is only meaningful once the rect is the width the text
        /// will wrap at, so the width is applied first and the answer read back.
        /// </summary>
        private static float WrappedHeight(Text label, float width, int fontSize)
        {
            if (label == null)
            {
                return 0f;
            }

            if (string.IsNullOrEmpty(label.text))
            {
                return 0f;
            }

            var rect = label.rectTransform;
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
            var preferred = label.preferredHeight;
            return Mathf.Max(fontSize + 6f, preferred + 4f);
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
            var label = _content.Find(name) as RectTransform;
            if (label != null)
            {
                UiFactory.Place(label, x, y, width, height);
            }
        }
    }
}
