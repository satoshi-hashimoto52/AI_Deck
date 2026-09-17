using System.Collections.Generic;
using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>A widget that can take a finger.</summary>
    public interface ITouchTarget
    {
        /// <summary>The rectangle to hit-test. Null or inactive targets are skipped.</summary>
        RectTransform TouchRect { get; }

        /// <summary>Higher wins when rectangles overlap.</summary>
        int TouchPriority { get; }

        bool TouchEnabled { get; }

        void OnTouchDown(int pointerId, Vector2 screenPosition);
        void OnTouchMove(int pointerId, Vector2 screenPosition);
        void OnTouchUp(int pointerId, Vector2 screenPosition);

        /// <summary>The finger went away without a proper release: cancelled, backgrounded, lost.</summary>
        void OnTouchCancel(int pointerId);
    }

    /// <summary>
    /// Routes raw touches to widgets.
    ///
    /// AI Deck does not use <c>EventSystem</c> for its control surface. Two requirements make
    /// that the wrong tool here: FR-071 needs several controls driven at once with no notion
    /// of a single "selected" object, and FR-073 needs <see cref="TouchPhase.Canceled"/> to
    /// release whatever the finger was holding. Reading <c>Input.touches</c> directly gives
    /// both, and it also gives one obvious place to implement FR-074 — when the app is
    /// backgrounded, every captured finger is cancelled.
    ///
    /// A widget keeps its finger until that finger lifts, even if it moves outside the
    /// widget's rectangle. That is how a real fader behaves: once you have grabbed it, sliding
    /// off the side does not drop it.
    /// </summary>
    public sealed class TouchRouter : MonoBehaviour
    {
        /// <summary>Pointer id used for the mouse, so it cannot collide with a real finger id.</summary>
        public const int MousePointerId = -100;

        private readonly List<ITouchTarget> _targets = new List<ITouchTarget>();
        private readonly Dictionary<int, ITouchTarget> _captured = new Dictionary<int, ITouchTarget>();
        private readonly List<int> _scratchIds = new List<int>();

        /// <summary>The camera the canvas renders with, or null for Screen Space - Overlay.</summary>
        public Camera UiCamera { get; set; }

        /// <summary>Number of fingers currently holding a widget. Shown in diagnostics.</summary>
        public int ActivePointerCount => _captured.Count;

        public void Register(ITouchTarget target)
        {
            if (target != null && !_targets.Contains(target))
            {
                _targets.Add(target);
            }
        }

        public void Unregister(ITouchTarget target)
        {
            if (target == null)
            {
                return;
            }

            _targets.Remove(target);
            CancelCapturesFor(target);
        }

        private void Update()
        {
            ProcessTouches();
            ProcessMouse();
            DropStaleCaptures();
        }

        private void ProcessTouches()
        {
            var count = Input.touchCount;
            for (var i = 0; i < count; i++)
            {
                var touch = Input.GetTouch(i);
                var id = touch.fingerId;

                switch (touch.phase)
                {
                    case TouchPhase.Began:
                        Begin(id, touch.position);
                        break;

                    case TouchPhase.Moved:
                    case TouchPhase.Stationary:
                        Move(id, touch.position);
                        break;

                    case TouchPhase.Ended:
                        End(id, touch.position);
                        break;

                    case TouchPhase.Canceled:
                        // FR-073: a cancelled pointer is not a release. The widget must drop
                        // whatever it was doing rather than act on the last known position.
                        Cancel(id);
                        break;
                }
            }
        }

        private void ProcessMouse()
        {
            // The Mac host is driven with a mouse; the same widgets serve both, so the mouse
            // is fed through as one extra pointer rather than duplicated in a second path.
            if (Input.touchCount > 0)
            {
                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                Begin(MousePointerId, Input.mousePosition);
            }
            else if (Input.GetMouseButton(0))
            {
                Move(MousePointerId, Input.mousePosition);
            }
            else if (Input.GetMouseButtonUp(0))
            {
                End(MousePointerId, Input.mousePosition);
            }
        }

        private void Begin(int id, Vector2 position)
        {
            if (_captured.ContainsKey(id))
            {
                Cancel(id);
            }

            var target = Pick(position);
            if (target == null)
            {
                return;
            }

            _captured[id] = target;
            target.OnTouchDown(id, position);
        }

        private void Move(int id, Vector2 position)
        {
            if (_captured.TryGetValue(id, out var target))
            {
                target.OnTouchMove(id, position);
            }
        }

        private void End(int id, Vector2 position)
        {
            if (!_captured.TryGetValue(id, out var target))
            {
                return;
            }

            _captured.Remove(id);
            target.OnTouchUp(id, position);
        }

        private void Cancel(int id)
        {
            if (!_captured.TryGetValue(id, out var target))
            {
                return;
            }

            _captured.Remove(id);
            target.OnTouchCancel(id);
        }

        /// <summary>Topmost enabled target whose rectangle contains the point.</summary>
        private ITouchTarget Pick(Vector2 position)
        {
            ITouchTarget best = null;
            var bestPriority = int.MinValue;

            for (var i = 0; i < _targets.Count; i++)
            {
                var target = _targets[i];
                if (target == null || !target.TouchEnabled)
                {
                    continue;
                }

                var rect = target.TouchRect;
                if (rect == null || !rect.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, position, UiCamera))
                {
                    continue;
                }

                if (target.TouchPriority >= bestPriority)
                {
                    best = target;
                    bestPriority = target.TouchPriority;
                }
            }

            return best;
        }

        /// <summary>A captured widget that was destroyed or disabled must not keep its finger.</summary>
        private void DropStaleCaptures()
        {
            if (_captured.Count == 0)
            {
                return;
            }

            _scratchIds.Clear();
            foreach (var pair in _captured)
            {
                var rect = pair.Value?.TouchRect;
                if (pair.Value == null || rect == null || !rect.gameObject.activeInHierarchy)
                {
                    _scratchIds.Add(pair.Key);
                }
            }

            foreach (var id in _scratchIds)
            {
                if (_captured.TryGetValue(id, out var target))
                {
                    _captured.Remove(id);
                    target?.OnTouchCancel(id);
                }
            }
        }

        private void CancelCapturesFor(ITouchTarget target)
        {
            _scratchIds.Clear();
            foreach (var pair in _captured)
            {
                if (ReferenceEquals(pair.Value, target))
                {
                    _scratchIds.Add(pair.Key);
                }
            }

            foreach (var id in _scratchIds)
            {
                _captured.Remove(id);
                target.OnTouchCancel(id);
            }
        }

        /// <summary>
        /// Cancels every held control. Called when the application loses focus or is
        /// backgrounded (FR-074), and by the session layer when the link drops (FR-066).
        /// </summary>
        public void CancelAll()
        {
            if (_captured.Count == 0)
            {
                return;
            }

            _scratchIds.Clear();
            _scratchIds.AddRange(_captured.Keys);
            foreach (var id in _scratchIds)
            {
                if (_captured.TryGetValue(id, out var target))
                {
                    _captured.Remove(id);
                    target?.OnTouchCancel(id);
                }
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                CancelAll();
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                CancelAll();
            }
        }

        private void OnDisable() => CancelAll();
    }
}
