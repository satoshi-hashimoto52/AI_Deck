using System;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// A linear fader: channel faders, the crossfader and the tempo slider.
    ///
    /// The knob follows the finger absolutely rather than by relative drag. On a touch surface
    /// with no physical cap to grab, absolute tracking is what people expect — you put your
    /// finger where you want the value and it goes there.
    ///
    /// <see cref="Value"/> is always 0…1 internally; bipolar controls map that outside.
    /// </summary>
    public sealed class FaderWidget : TouchWidget
    {
        /// <summary>Knob length along the travel axis.</summary>
        private const float KnobLength = 26f;

        private RectTransform _knob;
        private Image _knobImage;
        private Image _trackImage;
        private RectTransform _detent;
        private bool _vertical;
        private float _value;

        /// <summary>Raised as the value changes, with the new 0…1 value.</summary>
        public event Action<float> ValueChanged;

        /// <summary>Raised with true when the finger lands and false when it leaves, for any reason.</summary>
        public event Action<bool> InteractionChanged;

        /// <summary>Current position, 0…1. Setting it does not raise <see cref="ValueChanged"/>.</summary>
        public float Value
        {
            get => _value;
            set
            {
                var clamped = AudioSafety.Sanitize01(value);
                if (Mathf.Approximately(_value, clamped))
                {
                    return;
                }

                _value = clamped;
                Layout();
            }
        }

        /// <summary>
        /// True while a finger is on the fader. The host uses this to leave the control alone
        /// while it is being moved, so an incoming state snapshot does not fight the finger.
        /// </summary>
        public bool IsBeingDragged => IsPressed;

        public static FaderWidget Create(
            string name,
            Transform parent,
            TouchRouter router,
            bool vertical,
            float initialValue = 0.5f,
            bool showCentreDetent = false,
            DeckId? accentDeck = null)
        {
            var rect = UiFactory.Create(name, parent);
            var widget = rect.gameObject.AddComponent<FaderWidget>();
            widget._vertical = vertical;
            widget._value = AudioSafety.Sanitize01(initialValue);

            var edge = rect.gameObject.AddComponent<Image>();
            edge.sprite = UiSprites.Rounded;
            edge.type = Image.Type.Sliced;
            edge.color = accentDeck.HasValue
                ? Theme.WithAlpha(Theme.Accent(accentDeck.Value), 0.45f)
                : Theme.Line;
            edge.raycastTarget = false;

            var track = UiFactory.Create("Track", rect);
            UiFactory.Stretch(track, 1f, 1f, 1f, 1f);
            widget._trackImage = track.gameObject.AddComponent<Image>();
            widget._trackImage.sprite = UiSprites.Rounded;
            widget._trackImage.type = Image.Type.Sliced;
            widget._trackImage.color = Theme.PanelRaised;
            widget._trackImage.raycastTarget = false;

            if (showCentreDetent)
            {
                widget._detent = UiFactory.Create("Detent", rect);
                var detentImage = widget._detent.gameObject.AddComponent<Image>();
                detentImage.color = Theme.Line;
                detentImage.raycastTarget = false;
            }

            widget._knob = UiFactory.Create("Knob", rect);
            widget._knobImage = widget._knob.gameObject.AddComponent<Image>();
            widget._knobImage.sprite = UiSprites.Rounded;
            widget._knobImage.type = Image.Type.Sliced;
            widget._knobImage.color = Theme.Knob;
            widget._knobImage.raycastTarget = false;

            widget.Bind(router);
            return widget;
        }

        private void Start() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

        protected override void OnPressed(Vector2 screenPosition)
        {
            InteractionChanged?.Invoke(true);
            Apply(screenPosition);
        }

        protected override void OnDragged(Vector2 screenPosition) => Apply(screenPosition);

        protected override void OnReleased(Vector2 screenPosition)
        {
            Apply(screenPosition);
            _knobImage.color = Theme.Knob;
            InteractionChanged?.Invoke(false);
        }

        protected override void OnCancelled()
        {
            // The value stays where it was: a fader is a position, and abandoning the gesture
            // should not move it. What must not persist is the "being dragged" state, which
            // would otherwise block state updates from the host forever (FR-073).
            _knobImage.color = Theme.Knob;
            InteractionChanged?.Invoke(false);
        }

        private void Apply(Vector2 screenPosition)
        {
            var normalised = ToNormalised(screenPosition);
            var raw = _vertical ? normalised.y : normalised.x;

            // The knob has length, so the usable travel is shorter than the track. Rescaling
            // means the extremes are reachable without pushing the knob off the end.
            var size = _vertical ? Rect.rect.height : Rect.rect.width;
            if (size > KnobLength)
            {
                var margin = KnobLength * 0.5f / size;
                raw = Mathf.InverseLerp(margin, 1f - margin, raw);
            }

            var value = AudioSafety.Sanitize01(raw);
            _knobImage.color = Theme.PressedEdge;

            if (Mathf.Approximately(value, _value))
            {
                return;
            }

            _value = value;
            Layout();
            ValueChanged?.Invoke(_value);
        }

        private void Layout()
        {
            if (_knob == null)
            {
                return;
            }

            var rect = Rect.rect;

            if (_vertical)
            {
                var travel = Mathf.Max(0f, rect.height - KnobLength);
                _knob.anchorMin = new Vector2(0.5f, 0f);
                _knob.anchorMax = new Vector2(0.5f, 0f);
                _knob.pivot = new Vector2(0.5f, 0.5f);
                _knob.sizeDelta = new Vector2(Mathf.Max(rect.width + 6f, 20f), KnobLength);
                _knob.anchoredPosition = new Vector2(0f, KnobLength * 0.5f + travel * _value);

                if (_detent != null)
                {
                    _detent.anchorMin = new Vector2(0f, 0.5f);
                    _detent.anchorMax = new Vector2(1f, 0.5f);
                    _detent.pivot = new Vector2(0.5f, 0.5f);
                    _detent.sizeDelta = new Vector2(6f, 1f);
                    _detent.anchoredPosition = Vector2.zero;
                }
            }
            else
            {
                var travel = Mathf.Max(0f, rect.width - KnobLength);
                _knob.anchorMin = new Vector2(0f, 0.5f);
                _knob.anchorMax = new Vector2(0f, 0.5f);
                _knob.pivot = new Vector2(0.5f, 0.5f);
                _knob.sizeDelta = new Vector2(KnobLength, Mathf.Max(rect.height + 6f, 20f));
                _knob.anchoredPosition = new Vector2(KnobLength * 0.5f + travel * _value, 0f);

                if (_detent != null)
                {
                    _detent.anchorMin = new Vector2(0.5f, 0f);
                    _detent.anchorMax = new Vector2(0.5f, 1f);
                    _detent.pivot = new Vector2(0.5f, 0.5f);
                    _detent.sizeDelta = new Vector2(1f, 6f);
                    _detent.anchoredPosition = Vector2.zero;
                }
            }
        }
    }
}
