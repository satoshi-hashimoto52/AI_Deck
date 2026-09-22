using System;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>How a button reports its state through colour (§5.1).</summary>
    public enum ButtonVisualState
    {
        Normal,
        /// <summary>Latched on, e.g. PLAY while playing or an armed LOOP.</summary>
        Active,
        /// <summary>Currently held down.</summary>
        Held,
        /// <summary>Unavailable — greyed and not touchable.</summary>
        Disabled,
        /// <summary>Command sent, waiting for the host to confirm.</summary>
        Waiting,
        /// <summary>The last attempt failed.</summary>
        Error
    }

    /// <summary>
    /// A push button, at least 44 pt on its shortest side (§5.1).
    ///
    /// Two callbacks rather than one: <see cref="Clicked"/> for momentary commands, and
    /// <see cref="HeldChanged"/> for controls like ECHO that act while the finger is down.
    /// A cancelled touch raises <c>HeldChanged(false)</c> but never <c>Clicked</c>, so a
    /// gesture interrupted by the system cannot fire a command the user did not complete.
    /// </summary>
    public sealed class ButtonWidget : TouchWidget
    {
        private Image _background;
        private Image _outline;
        private Text _label;
        private ButtonVisualState _state = ButtonVisualState.Normal;
        private DeckId? _accentDeck;

        /// <summary>Raised when a press completes normally.</summary>
        public event Action Clicked;

        /// <summary>Raised with true on press and false on release or cancel.</summary>
        public event Action<bool> HeldChanged;

        public string Label
        {
            get => _label != null ? _label.text : string.Empty;
            set
            {
                if (_label != null)
                {
                    _label.text = value ?? string.Empty;
                }
            }
        }

        public ButtonVisualState State
        {
            get => _state;
            set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;
                Interactable = value != ButtonVisualState.Disabled;
                Refresh();
            }
        }

        /// <summary>Deck whose accent colour this button uses when active. Null for neutral buttons.</summary>
        public DeckId? AccentDeck
        {
            get => _accentDeck;
            set
            {
                _accentDeck = value;
                Refresh();
            }
        }

        public static ButtonWidget Create(
            string name,
            Transform parent,
            string label,
            TouchRouter router,
            DeckId? accentDeck = null,
            int fontSize = Theme.FontSizeLabel)
        {
            var rect = UiFactory.Create(name, parent);
            var widget = rect.gameObject.AddComponent<ButtonWidget>();

            widget._outline = rect.gameObject.AddComponent<Image>();
            widget._outline.sprite = UiSprites.Rounded;
            widget._outline.type = Image.Type.Sliced;
            widget._outline.color = Theme.Line;
            widget._outline.raycastTarget = false;

            var fill = UiFactory.Create("Fill", rect);
            UiFactory.Stretch(fill, 1f, 1f, 1f, 1f);
            widget._background = fill.gameObject.AddComponent<Image>();
            widget._background.sprite = UiSprites.Rounded;
            widget._background.type = Image.Type.Sliced;
            widget._background.color = Theme.PanelRaised;
            widget._background.raycastTarget = false;

            widget._label = UiFactory.CreateText(
                "Label", rect, label, fontSize, TextAnchor.MiddleCenter, Theme.Text, FontStyle.Bold);

            widget._accentDeck = accentDeck;
            widget.Bind(router);
            widget.Refresh();
            return widget;
        }

        /// <summary>
        /// How far the finger may travel before the press stops counting as a click, or zero
        /// to never cancel.
        ///
        /// Off by default: a deck control should fire even if the finger shifts a little,
        /// because a transport button is aimed at and held, not swiped. It is switched on for
        /// buttons that sit inside a scrolling list, where a drag that starts on A or B is a
        /// scroll and must not load a track.
        /// </summary>
        public float DragCancelDistance { get; set; }

        /// <summary>Vertical movement since the last report, when <see cref="DragCancelDistance"/> is set.</summary>
        public event Action<float> DraggedBy;

        private Vector2 _pressPosition;
        private Vector2 _lastDragPosition;
        private bool _dragCancelled;

        protected override void OnPressed(Vector2 screenPosition)
        {
            _pressPosition = screenPosition;
            _lastDragPosition = screenPosition;
            _dragCancelled = false;
            Refresh();
            HeldChanged?.Invoke(true);
        }

        protected override void OnDragged(Vector2 screenPosition)
        {
            if (DragCancelDistance <= 0f)
            {
                return;
            }

            var step = screenPosition.y - _lastDragPosition.y;
            _lastDragPosition = screenPosition;

            if ((screenPosition - _pressPosition).sqrMagnitude > DragCancelDistance * DragCancelDistance)
            {
                _dragCancelled = true;
                Refresh();
            }

            if (step != 0f)
            {
                DraggedBy?.Invoke(step);
            }
        }

        protected override void OnReleased(Vector2 screenPosition)
        {
            Refresh();
            HeldChanged?.Invoke(false);

            // A drag that began on this button was a scroll. Loading a track the user never
            // asked for is the worst outcome available here, so the click is dropped.
            if (!_dragCancelled)
            {
                Clicked?.Invoke();
            }
        }

        protected override void OnCancelled()
        {
            Refresh();
            // Deliberately no Clicked: an interrupted gesture is not a command.
            HeldChanged?.Invoke(false);
        }

        private void Refresh()
        {
            if (_background == null)
            {
                return;
            }

            var accent = _accentDeck.HasValue ? Theme.Accent(_accentDeck.Value) : Theme.Ok;
            var onAccent = _accentDeck.HasValue ? Theme.OnAccent(_accentDeck.Value) : Theme.TextOnAccent;

            Color fill;
            Color edge;
            Color textColor;
            var alpha = 1f;

            if (IsPressed)
            {
                fill = Theme.Pressed;
                edge = Theme.PressedEdge;
                textColor = Theme.Text;
            }
            else
            {
                switch (_state)
                {
                    case ButtonVisualState.Active:
                        fill = accent;
                        edge = accent;
                        textColor = onAccent;
                        break;
                    case ButtonVisualState.Held:
                        fill = Theme.Pressed;
                        edge = Theme.PressedEdge;
                        textColor = Theme.Text;
                        break;
                    case ButtonVisualState.Waiting:
                        fill = Theme.PanelRaised;
                        edge = Theme.Warning;
                        textColor = Theme.Warning;
                        break;
                    case ButtonVisualState.Error:
                        fill = Theme.PanelRaised;
                        edge = Theme.Danger;
                        textColor = Theme.Danger;
                        break;
                    case ButtonVisualState.Disabled:
                        fill = Theme.PanelRaised;
                        edge = Theme.Line;
                        textColor = Theme.Text;
                        alpha = Theme.DisabledAlpha;
                        break;
                    default:
                        fill = Theme.PanelRaised;
                        edge = Theme.Line;
                        textColor = Theme.Text;
                        break;
                }
            }

            _background.color = Theme.WithAlpha(fill, alpha);
            _outline.color = Theme.WithAlpha(edge, alpha);
            _label.color = Theme.WithAlpha(textColor, alpha);
        }
    }
}
