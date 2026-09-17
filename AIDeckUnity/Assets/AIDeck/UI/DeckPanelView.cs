using System.Globalization;
using AIDeck.Core.Deck;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// One deck's control panel (§5.3 and §5.5).
    ///
    /// Shared by the Mac and the iPad. It renders a <see cref="DeckSnapshot"/> and reports
    /// intents through <see cref="IDeckCommands"/>; it holds no transport state of its own,
    /// which is what makes the iPad a mirror of the host rather than a second opinion.
    ///
    /// Deck B is laid out as a mirror image of deck A, as §5.5 requires.
    /// </summary>
    public sealed class DeckPanelView : MonoBehaviour
    {
        private const float HeaderHeight = 26f;
        private const float RowGap = 6f;

        private DeckId _deck;
        private IDeckCommands _commands;

        private Text _badge;
        private Text _state;
        private Text _bpm;
        private JogWidget _jog;
        private ButtonWidget _play;
        private ButtonWidget _cue;
        private ButtonWidget _sync;
        private ButtonWidget _loop;
        private ButtonWidget _echo;
        private ButtonWidget _brake;
        private ButtonWidget _backspin;
        private ButtonWidget _eject;
        private FaderWidget _tempo;
        private Text _tempoValue;
        private RectTransform _controls;
        private bool _mirrored;

        public DeckId Deck => _deck;

        public static DeckPanelView Create(
            string name,
            Transform parent,
            TouchRouter router,
            DeckId deck,
            IDeckCommands commands,
            bool mirrored)
        {
            var root = UiFactory.CreatePanel(name, parent, Theme.Panel, Theme.WithAlpha(Theme.Accent(deck), 0.32f));
            var view = root.gameObject.AddComponent<DeckPanelView>();
            view._deck = deck;
            view._commands = commands;
            view._mirrored = mirrored;
            view.Build(root, router);
            return view;
        }

        private void Build(RectTransform root, TouchRouter router)
        {
            var accent = Theme.Accent(_deck);

            _badge = UiFactory.CreateText("Badge", root, _deck.ToDisplayName(),
                Theme.FontSizeLarge, TextAnchor.MiddleCenter, accent, FontStyle.Bold);
            _state = UiFactory.CreateText("State", root, "EMPTY",
                Theme.FontSizeLabel, TextAnchor.MiddleLeft, Theme.TextDim);
            _bpm = UiFactory.CreateText("Bpm", root, "— BPM",
                Theme.FontSizeLabel, TextAnchor.MiddleRight, Theme.Text);

            _jog = JogWidget.Create("Jog", root, router, _deck);
            _jog.ScratchBegan += () => _commands.ScratchBegin(_deck);
            _jog.ScratchRateChanged += rate => _commands.ScratchUpdate(_deck, rate);
            _jog.ScratchEnded += () => _commands.ScratchEnd(_deck);
            _jog.Nudged += amount => _commands.JogNudge(_deck, amount);

            _controls = UiFactory.Create("Controls", root);

            _play = ButtonWidget.Create("Play", _controls, "PLAY / PAUSE", router);
            _play.Clicked += () => _commands.TogglePlay(_deck);

            _cue = ButtonWidget.Create("Cue", _controls, "CUE", router, _deck);
            // Tap returns to the cue point; holding it down sets a new one, which is how a DJ
            // moves a cue without stopping to find a separate control.
            _cue.Clicked += () => _commands.CueReturn(_deck);
            _cue.HeldChanged += held =>
            {
                if (held)
                {
                    _cueHeldAt = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _cueHeldAt > CueSetHoldSeconds)
                {
                    _commands.CueSet(_deck);
                }
            };

            _sync = ButtonWidget.Create("Sync", _controls, "SYNC", router, _deck);
            _sync.Clicked += () => _commands.ToggleSync(_deck);

            _loop = ButtonWidget.Create("Loop", _controls, "LOOP 4", router, _deck);
            _loop.Clicked += () => _commands.ToggleLoop(_deck);

            _echo = ButtonWidget.Create("Echo", _controls, "ECHO", router, _deck);
            // ECHO is momentary: it is an effect you lean on, not a mode you forget you left on.
            _echo.HeldChanged += held => _commands.SetEcho(_deck, held);

            _brake = ButtonWidget.Create("Brake", _controls, "BRAKE", router);
            _brake.Clicked += () => _commands.Brake(_deck);

            _backspin = ButtonWidget.Create("Backspin", _controls, "BACKSPIN", router);
            _backspin.Clicked += () => _commands.Backspin(_deck);

            _eject = ButtonWidget.Create("Eject", _controls, "EJECT", router);
            _eject.Clicked += () => _commands.Eject(_deck);

            _tempo = FaderWidget.Create("Tempo", _controls, router, false, 0.5f, true, _deck);
            _tempo.ValueChanged += value => _commands.SetTempoFader(_deck, value * 2f - 1f);

            _tempoValue = UiFactory.CreateText("TempoValue", _controls, "+0.0 %",
                Theme.FontSizeLabel, TextAnchor.MiddleRight, Theme.TextDim);
        }

        private const float CueSetHoldSeconds = 0.45f;
        private float _cueHeldAt;

        private void Start() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

        private void Layout()
        {
            var rect = (RectTransform)transform;
            var width = rect.rect.width;
            var height = rect.rect.height;
            if (width <= 1f || height <= 1f)
            {
                return;
            }

            const float pad = 9f;
            var innerWidth = width - pad * 2f;
            var innerHeight = height - pad * 2f;

            // Header: badge on the outside, then the transport state, with the tempo reading
            // pushed to the far side. Deck B is the mirror image (§5.5).
            var badgeSize = HeaderHeight;
            var stateWidth = Mathf.Max(90f, innerWidth * 0.42f);
            var bpmWidth = Mathf.Max(60f, innerWidth - badgeSize - stateWidth - 16f);

            if (_mirrored)
            {
                UiFactory.Place(_badge.rectTransform, width - pad - badgeSize, pad, badgeSize, badgeSize);
                UiFactory.Place(_state.rectTransform,
                    width - pad - badgeSize - 8f - stateWidth, pad, stateWidth, badgeSize);
                UiFactory.Place(_bpm.rectTransform, pad, pad, bpmWidth, badgeSize);
                _state.alignment = TextAnchor.MiddleRight;
                _bpm.alignment = TextAnchor.MiddleLeft;
            }
            else
            {
                UiFactory.Place(_badge.rectTransform, pad, pad, badgeSize, badgeSize);
                UiFactory.Place(_state.rectTransform, pad + badgeSize + 8f, pad, stateWidth, badgeSize);
                UiFactory.Place(_bpm.rectTransform, width - pad - bpmWidth, pad, bpmWidth, badgeSize);
                _state.alignment = TextAnchor.MiddleLeft;
                _bpm.alignment = TextAnchor.MiddleRight;
            }

            // Body: jog on the outside, controls on the inside, mirrored for deck B (§5.5).
            var bodyTop = pad + badgeSize + 8f;
            var bodyHeight = height - bodyTop - pad;
            var controlsWidth = Mathf.Clamp(innerWidth * 0.46f, 150f, 230f);
            var jogWidth = innerWidth - controlsWidth - 8f;
            var jogSize = Mathf.Min(jogWidth, bodyHeight);

            var jogX = _mirrored ? width - pad - jogWidth + (jogWidth - jogSize) * 0.5f
                                 : pad + (jogWidth - jogSize) * 0.5f;
            var jogY = bodyTop + (bodyHeight - jogSize) * 0.5f;
            UiFactory.Place(_jog.Rect, jogX, jogY, jogSize, jogSize);

            var controlsX = _mirrored ? pad : width - pad - controlsWidth;
            UiFactory.Place(_controls, controlsX, bodyTop, controlsWidth, bodyHeight);

            LayoutControls(controlsWidth, bodyHeight);
        }

        private void LayoutControls(float width, float height)
        {
            // Five rows: PLAY (full width), then three pairs, then the tempo fader.
            const int rows = 5;
            var rowHeight = Mathf.Max(Theme.TouchSize, (height - RowGap * (rows - 1)) / rows);
            var half = (width - RowGap) * 0.5f;

            var y = 0f;
            UiFactory.Place(_play.Rect, 0f, y, width, rowHeight);
            y += rowHeight + RowGap;

            UiFactory.Place(_cue.Rect, 0f, y, half, rowHeight);
            UiFactory.Place(_sync.Rect, half + RowGap, y, half, rowHeight);
            y += rowHeight + RowGap;

            UiFactory.Place(_loop.Rect, 0f, y, half, rowHeight);
            UiFactory.Place(_echo.Rect, half + RowGap, y, half, rowHeight);
            y += rowHeight + RowGap;

            UiFactory.Place(_brake.Rect, 0f, y, half, rowHeight);
            UiFactory.Place(_backspin.Rect, half + RowGap, y, half, rowHeight);
            y += rowHeight + RowGap;

            var tempoWidth = width - 56f;
            UiFactory.Place(_tempo.Rect, 0f, y + rowHeight * 0.5f - 9f, tempoWidth, 18f);
            UiFactory.Place(_tempoValue.rectTransform, tempoWidth + 4f, y, 52f, rowHeight);

            // EJECT shares the tempo row on the far side; it is deliberately small and out of
            // the way, because ejecting mid-mix is not something to hit by accident.
            UiFactory.Place(_eject.Rect, tempoWidth + 4f, y + rowHeight * 0.55f, 52f, rowHeight * 0.45f);
            _eject.Rect.gameObject.SetActive(false);
        }

        /// <summary>Renders the host's view of this deck.</summary>
        public void Apply(DeckSnapshot snapshot)
        {
            _state.text = StateLabel(snapshot);
            _state.color = snapshot.State == DeckPlaybackState.Error ? Theme.Danger : Theme.TextDim;

            _bpm.text = BpmLabel(snapshot);

            var hasTrack = snapshot.HasTrack && snapshot.State != DeckPlaybackState.Error;
            var busy = snapshot.State == DeckPlaybackState.Loading;

            _play.State = busy ? ButtonVisualState.Waiting
                : !hasTrack ? ButtonVisualState.Disabled
                : snapshot.State == DeckPlaybackState.Playing ? ButtonVisualState.Active
                : ButtonVisualState.Normal;

            _cue.State = hasTrack
                ? (snapshot.CueIsSet ? ButtonVisualState.Active : ButtonVisualState.Normal)
                : ButtonVisualState.Disabled;

            _sync.State = !hasTrack || snapshot.BaseBpm <= 0d
                ? ButtonVisualState.Disabled
                : snapshot.SyncEnabled ? ButtonVisualState.Active : ButtonVisualState.Normal;

            _loop.State = hasTrack
                ? (snapshot.LoopActive ? ButtonVisualState.Active : ButtonVisualState.Normal)
                : ButtonVisualState.Disabled;

            _echo.State = hasTrack ? ButtonVisualState.Normal : ButtonVisualState.Disabled;

            var canSpin = hasTrack && snapshot.State == DeckPlaybackState.Playing;
            _brake.State = snapshot.Motion == MotionMode.Braking ? ButtonVisualState.Active
                : canSpin ? ButtonVisualState.Normal : ButtonVisualState.Disabled;
            _backspin.State = snapshot.Motion == MotionMode.Backspin ? ButtonVisualState.Active
                : canSpin ? ButtonVisualState.Normal : ButtonVisualState.Disabled;

            // The fader is left alone while a finger is on it, so an incoming snapshot cannot
            // fight the gesture in progress.
            if (!_tempo.IsBeingDragged)
            {
                _tempo.Value = (snapshot.TempoFader + 1f) * 0.5f;
            }

            var percent = (snapshot.EffectiveRate - 1f) * 100f;
            _tempoValue.text = percent.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture) + " %";

            _jog.SetPlaybackPosition(snapshot.PositionSeconds);
        }

        private static string StateLabel(DeckSnapshot snapshot)
        {
            switch (snapshot.State)
            {
                case DeckPlaybackState.Loading: return "LOADING";
                case DeckPlaybackState.Playing:
                    switch (snapshot.Motion)
                    {
                        case MotionMode.Scratching: return "SCRATCH";
                        case MotionMode.Braking: return "BRAKE";
                        case MotionMode.Backspin: return "BACKSPIN";
                        default: return "PLAYING";
                    }

                case DeckPlaybackState.Paused: return "PAUSED";
                case DeckPlaybackState.Error:
                    return string.IsNullOrEmpty(snapshot.ErrorReason)
                        ? "ERROR"
                        : "ERROR — " + snapshot.ErrorReason;
                default: return "EMPTY";
            }
        }

        private static string BpmLabel(DeckSnapshot snapshot)
        {
            if (snapshot.BaseBpm <= 0d)
            {
                return "— BPM";
            }

            return snapshot.EffectiveBpm.ToString("0.0", CultureInfo.InvariantCulture) + " BPM";
        }

        /// <summary>Releases any held control. Used on disconnect and backgrounding.</summary>
        public void ReleaseAll()
        {
            _jog.ForceRelease();
            _play.ForceRelease();
            _cue.ForceRelease();
            _sync.ForceRelease();
            _loop.ForceRelease();
            _echo.ForceRelease();
            _brake.ForceRelease();
            _backspin.ForceRelease();
            _tempo.ForceRelease();
        }
    }
}
