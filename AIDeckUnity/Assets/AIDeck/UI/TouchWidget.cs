using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>
    /// Base for every control the finger can touch.
    ///
    /// Owns the single-finger capture rule that makes multi-touch behave (FR-071): a widget
    /// accepts the first finger that lands on it and ignores any others until that one leaves.
    /// Without it, a second finger landing on an already-held fader would fight the first.
    ///
    /// <see cref="OnCancelled"/> is separate from <see cref="OnReleased"/> on purpose. A
    /// release is a decision; a cancel is the input vanishing, and §9 requires the widget to
    /// return to a defined state rather than act on the last position it saw.
    /// </summary>
    public abstract class TouchWidget : MonoBehaviour, ITouchTarget
    {
        private RectTransform _rect;
        private int _activePointer = int.MinValue;

        protected TouchRouter Router { get; private set; }

        public RectTransform Rect => _rect != null ? _rect : _rect = (RectTransform)transform;

        RectTransform ITouchTarget.TouchRect => Rect;

        public int TouchPriority { get; set; }

        /// <summary>A disabled control shows dimmed and takes no input (§5.1).</summary>
        public bool Interactable { get; set; } = true;

        bool ITouchTarget.TouchEnabled => Interactable && isActiveAndEnabled;

        public bool IsPressed => _activePointer != int.MinValue;

        /// <summary>Attaches the widget to a router. Call once, after construction.</summary>
        public void Bind(TouchRouter router)
        {
            Router = router;
            router?.Register(this);
        }

        protected virtual void OnDestroy() => Router?.Unregister(this);

        void ITouchTarget.OnTouchDown(int pointerId, Vector2 screenPosition)
        {
            if (_activePointer != int.MinValue)
            {
                // Already held. A second finger on the same control is ignored rather than
                // allowed to fight the first.
                return;
            }

            _activePointer = pointerId;
            OnPressed(screenPosition);
        }

        void ITouchTarget.OnTouchMove(int pointerId, Vector2 screenPosition)
        {
            if (pointerId != _activePointer)
            {
                return;
            }

            OnDragged(screenPosition);
        }

        void ITouchTarget.OnTouchUp(int pointerId, Vector2 screenPosition)
        {
            if (pointerId != _activePointer)
            {
                return;
            }

            _activePointer = int.MinValue;
            OnReleased(screenPosition);
        }

        void ITouchTarget.OnTouchCancel(int pointerId)
        {
            if (pointerId != _activePointer)
            {
                return;
            }

            _activePointer = int.MinValue;
            OnCancelled();
        }

        /// <summary>Releases the control as if the finger had been cancelled. Safe to call at any time.</summary>
        public void ForceRelease()
        {
            if (_activePointer == int.MinValue)
            {
                return;
            }

            _activePointer = int.MinValue;
            OnCancelled();
        }

        /// <summary>Converts a screen point to this widget's local coordinates.</summary>
        protected Vector2 ToLocal(Vector2 screenPosition)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                Rect, screenPosition, Router != null ? Router.UiCamera : null, out var local);
            return local;
        }

        /// <summary>0 at the left/bottom edge, 1 at the right/top edge.</summary>
        protected Vector2 ToNormalised(Vector2 screenPosition)
        {
            var local = ToLocal(screenPosition);
            var rect = Rect.rect;
            var x = rect.width > 0f ? (local.x - rect.xMin) / rect.width : 0f;
            var y = rect.height > 0f ? (local.y - rect.yMin) / rect.height : 0f;
            return new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
        }

        protected abstract void OnPressed(Vector2 screenPosition);

        protected virtual void OnDragged(Vector2 screenPosition)
        {
        }

        protected virtual void OnReleased(Vector2 screenPosition)
        {
        }

        /// <summary>The finger went away without releasing. Return to a defined state (FR-073).</summary>
        protected abstract void OnCancelled();
    }
}
