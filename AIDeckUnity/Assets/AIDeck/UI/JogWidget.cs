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
    /// **The whole visible disc grabs the audio.** An earlier version reserved the outer ring
    /// for a tempo nudge and only let the inner 62 % of the radius scratch — which is 62 % of
    /// the *area* doing nothing perceptible, so most of the wheel felt dead. A platter you can
    /// only use in the middle is not a platter.
    ///
    /// Rotation is measured as an angle around the centre, so the gesture works from anywhere
    /// on the wheel and keeps working after the finger leaves it. One full revolution moves
    /// <see cref="SecondsPerRevolution"/> of audio, matching a 33⅓ RPM platter, which is what
    /// makes the control feel like a turntable rather than an abstract slider.
    ///
    /// Holding the platter still stops it: if a frame passes with no movement the widget
    /// reports a rate of zero, exactly as a hand on a record does.
    /// </summary>
    public sealed class JogWidget : TouchWidget
    {
        /// <summary>Audio seconds per full revolution — a 33⅓ RPM platter.</summary>
        public const float SecondsPerRevolution = 1.8f;

        /// <summary>Fraction of the radius drawn as the inner label area. Purely cosmetic.</summary>
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
        private int _lastMoveFrame = -1;

        /// <summary>Raised when a finger takes hold of the record surface.</summary>
        public event Action ScratchBegan;

        /// <summary>Raised with the signed playback rate the gesture is commanding.</summary>
        public event Action<float> ScratchRateChanged;

        /// <summary>
        /// Raised with the audio seconds this step of the gesture moved the platter.
        ///
        /// Displacement is the primitive a jog wheel actually produces; rate is derived from it
        /// by dividing by the frame time. Rate alone is not enough, because it is clamped for
        /// safety before it reaches the audio — and a clamped rate multiplied back by the frame
        /// time is no longer the distance the finger travelled. A quick flick would move the
        /// track a fraction of what the hand did. The host therefore positions a stopped deck
        /// from this value and takes the rate only for the sound of a moving one.
        /// </summary>
        public event Action<float> ScratchMoved;

        /// <summary>Raised when the record surface is released, for any reason.</summary>
        public event Action ScratchEnded;

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
            _lastAngle = AngleAt(screenPosition);
            _smoothedRate = 0f;
            _lastMoveFrame = Time.frameCount;

            // Anywhere on the disc takes hold of the audio.
            _scratching = true;
            ScratchBegan?.Invoke();
            ScratchRateChanged?.Invoke(0f);

            Highlight(true);
        }

        protected override void OnDragged(Vector2 screenPosition)
        {
            if (!_scratching)
            {
                return;
            }

            var angle = AngleAt(screenPosition);

            // DeltaAngle takes the shorter way round, so crossing 359° to 0° is one small step
            // forward rather than a jump most of the way backwards.
            var delta = Mathf.DeltaAngle(_lastAngle, angle);
            _lastAngle = angle;
            _lastMoveFrame = Time.frameCount;

            var dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
            {
                return;
            }

            // Degrees of platter rotation to audio seconds, then to a playback rate. Clockwise
            // is a negative screen-space delta, and must move the track forwards.
            var audioSeconds = -delta / 360f * SecondsPerRevolution;
            var rate = audioSeconds / dt;

            _smoothedRate = Mathf.Lerp(_smoothedRate, rate, VelocitySmoothing);
            var value = Mathf.Abs(_smoothedRate) < StillThreshold ? 0f : _smoothedRate;
            ScratchRateChanged?.Invoke(
                AudioSafety.Sanitize(value, -PlatterMotion.MaxScratchRate, PlatterMotion.MaxScratchRate, 0f));

            // One revolution is SecondsPerRevolution of audio, so a step of the gesture is
            // exactly this much of the track, whatever the frame rate happened to be.
            ScratchMoved?.Invoke(AudioSafety.Sanitize(audioSeconds, -MaxStepSeconds, MaxStepSeconds, 0f));
        }

        /// <summary>
        /// Largest jump one gesture step may make. A whole revolution in a single frame is a
        /// glitch, not a hand.
        /// </summary>
        public const float MaxStepSeconds = SecondsPerRevolution;

        private void Update()
        {
            if (!_scratching || _lastMoveFrame < 0 || Time.frameCount <= _lastMoveFrame + 1)
            {
                return;
            }

            // A finger resting on the platter is holding it still. Without this the last
            // reported rate would persist and the record would keep spinning under the hand.
            if (_smoothedRate != 0f)
            {
                _smoothedRate = 0f;
                ScratchRateChanged?.Invoke(0f);
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
            _lastMoveFrame = -1;
            ScratchEnded?.Invoke();
        }

        /// <summary>
        /// Angle of a screen point about the centre of the disc.
        ///
        /// <see cref="TouchWidget.ToLocal"/> returns a point relative to the rect's *pivot*, and
        /// the layout places these panels with a top-left pivot. Measuring the angle straight
        /// from that value swings it about the corner of the wheel instead of its middle, which
        /// made a steady drag produce deltas that were wrong in size and sometimes in sign —
        /// the wheel turned, but the track moved erratically or not at all.
        /// </summary>
        private float AngleAt(Vector2 screenPosition)
        {
            var fromCentre = ToLocal(screenPosition) - Rect.rect.center;
            return Mathf.Atan2(fromCentre.y, fromCentre.x) * Mathf.Rad2Deg;
        }

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
