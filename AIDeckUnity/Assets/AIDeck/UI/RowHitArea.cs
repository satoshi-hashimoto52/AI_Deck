using System;
using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>
    /// An invisible tap target covering a whole library row.
    ///
    /// It exists so that tapping anywhere on a row selects it, while the A and B load buttons
    /// that overlap it keep working — they register at a higher <see cref="TouchWidget.TouchPriority"/>,
    /// so the router hands them the finger first.
    ///
    /// A drag that leaves the row is not a tap: the list scrolls, and a scroll must not select.
    /// </summary>
    public sealed class RowHitArea : TouchWidget
    {
        /// <summary>Movement beyond this many pixels means the user was scrolling, not tapping.</summary>
        private const float DragTolerance = 10f;

        private Vector2 _pressPosition;
        private bool _moved;

        public event Action Tapped;

        protected override void OnPressed(Vector2 screenPosition)
        {
            _pressPosition = screenPosition;
            _moved = false;
        }

        protected override void OnDragged(Vector2 screenPosition)
        {
            if ((screenPosition - _pressPosition).sqrMagnitude > DragTolerance * DragTolerance)
            {
                _moved = true;
            }
        }

        protected override void OnReleased(Vector2 screenPosition)
        {
            if (!_moved)
            {
                Tapped?.Invoke();
            }
        }

        protected override void OnCancelled() => _moved = true;
    }
}
