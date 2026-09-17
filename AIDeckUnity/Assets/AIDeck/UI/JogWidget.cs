using System;
using AIDeck.Core.Deck;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// The touch platter (§5.3, FR-072, FR-032).
    ///
    /// Two zones, like a real jog wheel:
    /// * the **inner disc** is the record surface — touching it grabs the audio and the finger
    ///   drives playback directly, including backwards;
    /// * the **outer ring** is the rim — dragging it nudges the track ahead or behind without
    ///   taking hold of it.
    ///
    /// Rotation is measured as an angle around the centre, so the gesture works from anywhere
    /// on the wheel. One full revolution moves <see cref="SecondsPerRevolution"/> of audio,
    /// matching a 33⅓ RPM platter, which is what makes the control feel like a turntable
    /// rather than an abstract slider.
    /// </summary>
    public sealed class JogWidget : TouchWidget
    {
        /// <summary>Audio seconds per full revolution — a 33⅓ RPM platter.</summary>
        public const float SecondsPerRevolution = 1.8f;

        /// <summary>Fraction of the radius that counts as the record surface.</summary>
        public const float InnerRadiusFraction = 0.62f;

        /// <summary>Smoothing on the measured angular velocity, to ride out one-frame jitter.</summary>
        private const float VelocitySmoothing = 0.45f;

        /// <summary>Angular velocity below which the platter is treated as held still.</summary>
        private const float StillThreshold = 0.0005f;

        private RectTransform _mark;
        private Image _face;
        private Image _outline;

        private bool _scratching;
        private float _lastAngle;
        private float _smoothedRate;
        private float _markAngle;
        private DeckId _deck;

        /// <summary>Raised when a finger takes hold of the record surface.</summary>
        public event Action ScratchBegan;

        /// <summary>Raised with the signed playback rate the gesture is commanding.</summary>
        public event Action<float> ScratchRateChanged;

        /// <summary>Raised when the record surface is released, for any reason.</summary>
        public event Action ScratchEnded;

        /// <summary>Raised with a rate offset when the rim is dragged.</summary>
        public event Action<float> Nudged;

        /// <summary>True while the record surface is held.</summary>
        public bool IsScratching => _scratching;

        public static JogWidget Create(string name, Transform parent, TouchRouter router, DeckId deck)
        {
            var rect = UiFactory.Create(name, parent);
            var widget = rect.gameObject.AddComponent<JogWidget>();
            widget._deck = deck;

            // The platter is a disc: a rim ring in the deck accent with the record surface
            // drawn inside it.
            widget._outline = rect.gameObject.AddComponent<Image>();
            widget._outline.sprite = UiSprites.Circle;
            widget._outline.color = Theme.WithAlpha(Theme.Accent(deck), 0.5f);
            widget._outline.raycastTarget = false;

            var face = UiFactory.Create("Face", rect);
            UiFactory.Stretch(face, 2f, 2f, 2f, 2f);
            widget._face = face.gameObject.AddComponent<Image>();
            widget._face.sprite = UiSprites.Circle;
            widget._face.color = Theme.PanelRaised;
            widget._face.raycastTarget = false;

            var inner = UiFactory.Create("Inner", rect);
            inner.anchorMin = new Vector2(0.5f, 0.5f);
            inner.anchorMax = new Vector2(0.5f, 0.5f);
            inner.pivot = new Vector2(0.5f, 0.5f);
            inner.sizeDelta = Vector2.zero;
            var innerImage = inner.gameObject.AddComponent<Image>();
            innerImage.sprite = UiSprites.Circle;
            innerImage.color = Theme.Panel;
            innerImage.raycastTarget = false;
            widget._inner = inner;

            widget._mark = UiFactory.Create("Mark", rect);
            var markImage = widget._mark.gameObject.AddComponent<Image>();
            markImage.color = Theme.Accent(deck);
            markImage.raycastTarget = false;

            widget.Bind(router);
            return widget;
        }

        private RectTransform _inner;

        private void Start() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

        /// <summary>
        /// Spins the index mark so the platter visibly turns with playback. Driven by the
        /// host's reported position, so what the mark shows is what is actually playing.
        /// </summary>
        public void SetPlaybackPosition(double seconds)
        {
            if (_mark == null)
            {
                return;
            }

            var revolutions = seconds / SecondsPerRevolution;
            _markAngle = (float)(-revolutions * 360d % 360d);
            _mark.localRotation = Quaternion.Euler(0f, 0f, _markAngle);
        }

        protected override void OnPressed(Vector2 screenPosition)
        {
            var local = ToLocal(screenPosition);
            _lastAngle = Angle(local);
            _smoothedRate = 0f;

            if (IsInner(local))
            {
                _scratching = true;
                ScratchBegan?.Invoke();
                ScratchRateChanged?.Invoke(0f);
            }

            Highlight(true);
        }

        protected override void OnDragged(Vector2 screenPosition)
        {
            var local = ToLocal(screenPosition);
            var angle = Angle(local);
            var delta = Mathf.DeltaAngle(_lastAngle, angle);
            _lastAngle = angle;

            var dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
            {
                return;
            }

            // Degrees of platter rotation to audio seconds, then to a playback rate.
            var audioSeconds = -delta / 360f * SecondsPerRevolution;
            var rate = audioSeconds / dt;

            if (_scratching)
            {
                _smoothedRate = Mathf.Lerp(_smoothedRate, rate, VelocitySmoothing);
                var value = Mathf.Abs(_smoothedRate) < StillThreshold ? 0f : _smoothedRate;
                ScratchRateChanged?.Invoke(
                    AudioSafety.Sanitize(value, -PlatterMotion.MaxScratchRate, PlatterMotion.MaxScratchRate, 0f));
            }
            else
            {
                // The rim bends the tempo briefly rather than taking hold of the audio.
                Nudged?.Invoke(AudioSafety.Sanitize(
                    audioSeconds * 4f, -PlatterMotion.MaxNudge, PlatterMotion.MaxNudge, 0f));
            }
        }

        protected override void OnReleased(Vector2 screenPosition) => Finish();

        protected override void OnCancelled() => Finish();

        private void Finish()
        {
            Highlight(false);
            _smoothedRate = 0f;

            if (!_scratching)
            {
                return;
            }

            _scratching = false;
            ScratchEnded?.Invoke();
        }

        private bool IsInner(Vector2 local)
        {
            var radius = Mathf.Min(Rect.rect.width, Rect.rect.height) * 0.5f;
            return radius > 0f && local.magnitude <= radius * InnerRadiusFraction;
        }

        private static float Angle(Vector2 local) => Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg;

        private void Highlight(bool on)
        {
            if (_outline == null)
            {
                return;
            }

            _outline.color = on
                ? Theme.Accent(_deck)
                : Theme.WithAlpha(Theme.Accent(_deck), 0.5f);
        }

        private void Layout()
        {
            var size = Mathf.Min(Rect.rect.width, Rect.rect.height);
            if (size <= 0f)
            {
                return;
            }

            if (_inner != null)
            {
                _inner.sizeDelta = new Vector2(size * InnerRadiusFraction, size * InnerRadiusFraction);
            }

            if (_mark != null)
            {
                _mark.anchorMin = new Vector2(0.5f, 0.5f);
                _mark.anchorMax = new Vector2(0.5f, 0.5f);
                _mark.pivot = new Vector2(0.5f, 0f);
                _mark.sizeDelta = new Vector2(4f, size * 0.46f);
                _mark.anchoredPosition = Vector2.zero;
            }
        }
    }
}
