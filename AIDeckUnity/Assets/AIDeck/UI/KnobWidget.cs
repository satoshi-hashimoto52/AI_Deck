using System;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// A bipolar rotary control, used for the FILTER knobs (§5.4).
    ///
    /// Driven by vertical drag rather than by rotation around the centre. Circular tracking is
    /// unreliable on a small touch target — near the middle a tiny movement swings the angle
    /// wildly — whereas vertical drag is predictable and lets the knob stay at the 44 pt
    /// minimum without becoming twitchy.
    ///
    /// A double tap returns the knob to centre, which is the only quick way back to a true
    /// bypass mid-mix.
    /// </summary>
    public sealed class KnobWidget : TouchWidget
    {
        /// <summary>Screen pixels of vertical drag for the full −1…+1 sweep.</summary>
        private const float DragRange = 220f;

        private const float DoubleTapSeconds = 0.3f;
        private const float TapMovementTolerance = 8f;

        /// <summary>Sweep angle either side of centre.</summary>
        private const float MaxAngle = 140f;

        private RectTransform _pointer;
        private Image _face;
        private Image _outline;
        private float _value;
        private float _valueAtPress;
        private Vector2 _pressPosition;
        private float _lastTapTime = -10f;
        private bool _movedSincePress;

        /// <summary>Raised as the value changes, with the new −1…+1 value.</summary>
        public event Action<float> ValueChanged;

        public event Action<bool> InteractionChanged;

        /// <summary>Position, −1…+1. Setting it does not raise <see cref="ValueChanged"/>.</summary>
        public float Value
        {
            get => _value;
            set
            {
                var clamped = AudioSafety.SanitizeBipolar(value);
                if (Mathf.Approximately(_value, clamped))
                {
                    return;
                }

                _value = clamped;
                Layout();
            }
        }

        public bool IsBeingDragged => IsPressed;

        public static KnobWidget Create(string name, Transform parent, TouchRouter router, DeckId? accentDeck = null)
        {
            var rect = UiFactory.Create(name, parent);
            var widget = rect.gameObject.AddComponent<KnobWidget>();

            widget._outline = rect.gameObject.AddComponent<Image>();
            widget._outline.sprite = UiSprites.Circle;
            widget._outline.color = accentDeck.HasValue
                ? Theme.WithAlpha(Theme.Accent(accentDeck.Value), 0.45f)
                : Theme.Line;
            widget._outline.raycastTarget = false;

            var face = UiFactory.Create("Face", rect);
            UiFactory.Stretch(face, 1.5f, 1.5f, 1.5f, 1.5f);
            widget._face = face.gameObject.AddComponent<Image>();
            widget._face.sprite = UiSprites.Circle;
            widget._face.color = Theme.PanelRaised;
            widget._face.raycastTarget = false;

            widget._pointer = UiFactory.Create("Pointer", rect);
            var pointerImage = widget._pointer.gameObject.AddComponent<Image>();
            pointerImage.color = Theme.Text;
            pointerImage.raycastTarget = false;

            widget.Bind(router);
            return widget;
        }

        private void Start() => Layout();

        protected override void OnPressed(Vector2 screenPosition)
        {
            _valueAtPress = _value;
            _pressPosition = screenPosition;
            _movedSincePress = false;
            InteractionChanged?.Invoke(true);
        }

        protected override void OnDragged(Vector2 screenPosition)
        {
            var delta = screenPosition.y - _pressPosition.y;
            if (Mathf.Abs(delta) > TapMovementTolerance)
            {
                _movedSincePress = true;
            }

            var value = AudioSafety.SanitizeBipolar(_valueAtPress + delta / DragRange * 2f);
            if (Mathf.Approximately(value, _value))
            {
                return;
            }

            _value = value;
            Layout();
            ValueChanged?.Invoke(_value);
        }

        protected override void OnReleased(Vector2 screenPosition)
        {
            InteractionChanged?.Invoke(false);

            if (_movedSincePress)
            {
                _lastTapTime = -10f;
                return;
            }

            if (Time.unscaledTime - _lastTapTime <= DoubleTapSeconds)
            {
                _lastTapTime = -10f;
                if (!Mathf.Approximately(_value, 0f))
                {
                    _value = 0f;
                    Layout();
                    ValueChanged?.Invoke(_value);
                }

                return;
            }

            _lastTapTime = Time.unscaledTime;
        }

        protected override void OnCancelled()
        {
            // Restore the value the knob had when the finger landed. A cancelled gesture is
            // not a decision, and leaving a filter half-swept after a system interruption
            // would be an audible surprise (§9).
            _lastTapTime = -10f;
            if (!Mathf.Approximately(_value, _valueAtPress))
            {
                _value = _valueAtPress;
                Layout();
                ValueChanged?.Invoke(_value);
            }

            InteractionChanged?.Invoke(false);
        }

        private void Layout()
        {
            if (_pointer == null)
            {
                return;
            }

            var size = Mathf.Min(Rect.rect.width, Rect.rect.height);
            var length = Mathf.Max(8f, size * 0.32f);

            _pointer.anchorMin = new Vector2(0.5f, 0.5f);
            _pointer.anchorMax = new Vector2(0.5f, 0.5f);
            _pointer.pivot = new Vector2(0.5f, 0f);
            _pointer.sizeDelta = new Vector2(3f, length);
            _pointer.anchoredPosition = Vector2.zero;
            _pointer.localRotation = Quaternion.Euler(0f, 0f, -_value * MaxAngle);
        }
    }
}
