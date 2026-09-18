using System.Collections.Generic;
using AIDeck.Core.Diagnostics;
using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>
    /// Where a pointer event came from.
    ///
    /// Recorded so that an unexplained control movement can be attributed rather than guessed
    /// at. A load that reports <see cref="Injected"/> in a shipped build would mean something
    /// inside the process is driving the UI, which is a different problem from a stray click.
    /// </summary>
    public enum PointerSource
    {
        /// <summary>The operating system's mouse, read from <c>Input</c> by this router.</summary>
        Mouse = 0,

        /// <summary>A touchscreen contact, read from <c>Input.touches</c> by this router.</summary>
        Touch = 1,

        /// <summary>
        /// Delivered by code calling <see cref="TouchRouter.PointerDown"/> and friends.
        /// Nothing in the shipping application does this: the only callers are the PlayMode
        /// tests, which run in the editor. There is no network, IPC or scripting surface that
        /// reaches these methods.
        /// </summary>
        Injected = 2
    }

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

        /// <summary>
        /// Whether the application currently has keyboard/mouse focus.
        ///
        /// The host runs with <c>runInBackground</c> on, because a DJ set must not stop when
        /// the window loses focus. The side effect is that Unity keeps calling Update — and
        /// keeps reporting the operating system's mouse state — while the user is clicking in
        /// some *other* application. Acting on that would let a click meant for a text editor
        /// move a crossfader, which is exactly what was observed: controls changing on their
        /// own while the window sat in the background.
        ///
        /// Touches are not gated: an unfocused iOS app does not receive any, and backgrounding
        /// is handled by <see cref="OnApplicationPause"/>.
        /// </summary>
        public bool HasFocus { get; private set; } = true;

        /// <summary>
        /// Where the most recent pointer event came from. Logged alongside user actions so the
        /// three input paths can be told apart after the fact.
        /// </summary>
        public PointerSource LastPointerSource { get; private set; } = PointerSource.Mouse;

        /// <summary>
        /// Where every pointer that starts or is cancelled is recorded, or null to record
        /// nothing.
        ///
        /// A control that moved on its own is only diagnosable if there is a line saying which
        /// input path moved it, where, and onto what. Moves are deliberately not logged: a
        /// single drag is thousands of them, and the question being answered is always "what
        /// started this?".
        /// </summary>
        public DiagnosticLog Log { get; set; }

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

        /// <summary>
        /// True while a mouse button that was already down when focus arrived must be ignored.
        ///
        /// Clicking a background window is how macOS raises it, and that same click must not
        /// also operate whatever control happens to be underneath — a click aimed at the title
        /// bar of a window would otherwise load a track. The same flag covers a focus blip in
        /// the middle of a drag: the operating system reports the button going down again when
        /// focus returns, which would start a second gesture the user never began.
        /// </summary>
        private bool _mouseHeldFromBeforeFocus;

        private void OnEnable()
        {
            HasFocus = Application.isFocused;
            _mouseHeldFromBeforeFocus = Input.GetMouseButton(0);
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
                        Begin(id, touch.position, PointerSource.Touch);
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

            ProcessMouseState(
                Input.GetMouseButtonDown(0),
                Input.GetMouseButton(0),
                Input.GetMouseButtonUp(0),
                Input.mousePosition,
                PointerSource.Mouse);
        }

        /// <summary>
        /// Applies one frame of mouse-button state.
        ///
        /// Public because the rules below exist entirely because of what the operating system
        /// reports around a focus change, and <c>Input</c> cannot be driven from a test. A test
        /// leaves <paramref name="source"/> at its default so its frames are logged as
        /// <see cref="PointerSource.Injected"/> and can never be mistaken for a real mouse.
        /// </summary>
        public void ProcessMouseState(
            bool pressedThisFrame,
            bool held,
            bool releasedThisFrame,
            Vector2 position,
            PointerSource source = PointerSource.Injected)
        {
            // A click aimed at another application is not a click on this one.
            if (!HasFocus)
            {
                return;
            }

            // The press that brought the window forward belongs to the window manager.
            if (_mouseHeldFromBeforeFocus)
            {
                if (held)
                {
                    return;
                }

                _mouseHeldFromBeforeFocus = false;
            }

            if (pressedThisFrame)
            {
                Begin(MousePointerId, position, source);
            }
            else if (held)
            {
                Move(MousePointerId, position);
            }
            else if (releasedThisFrame)
            {
                End(MousePointerId, position);
            }
        }

        /// <summary>
        /// Delivers a pointer-down. Public because the router is deliberately independent of
        /// where pointers come from: <see cref="Update"/> feeds it from <c>Input</c>, and the
        /// PlayMode tests feed it directly, which is the only way to test simultaneous
        /// multi-touch (FR-071) and cancellation (FR-073) without a touchscreen.
        /// </summary>
        public void PointerDown(int id, Vector2 position, PointerSource source = PointerSource.Injected) =>
            Begin(id, position, source);

        /// <summary>Delivers a pointer move.</summary>
        public void PointerMove(int id, Vector2 position) => Move(id, position);

        /// <summary>Delivers a pointer release.</summary>
        public void PointerUp(int id, Vector2 position) => End(id, position);

        /// <summary>Delivers a pointer cancellation (FR-073).</summary>
        public void PointerCancel(int id) => Cancel(id);

        /// <summary>True when this pointer currently holds a widget.</summary>
        public bool IsCaptured(int pointerId) => _captured.ContainsKey(pointerId);

        private void Begin(int id, Vector2 position, PointerSource source)
        {
            LastPointerSource = source;

            if (_captured.ContainsKey(id))
            {
                Cancel(id);
            }

            var target = Pick(position);
            Log?.Info(
                "Pointer",
                $"Down [{source}] id {id} at ({position.x:F0}, {position.y:F0}) → " +
                (target == null ? "nothing" : Describe(target)));

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
            Log?.Info("Pointer", $"Cancelled id {id} holding {Describe(target)}.");
            target.OnTouchCancel(id);
        }

        /// <summary>Name of the widget's object, for the pointer trail. Never a file path.</summary>
        private static string Describe(ITouchTarget target)
        {
            var rect = target?.TouchRect;
            return rect == null ? target?.GetType().Name ?? "nothing" : rect.name;
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

        private void OnApplicationFocus(bool focused) => SetFocus(focused, Input.GetMouseButton(0));

        /// <summary>
        /// Applies a focus change. Separate from the Unity message because the test has to be
        /// able to say what the mouse button was doing at the moment focus arrived.
        /// </summary>
        public void SetFocus(bool focused, bool mouseButtonHeld)
        {
            HasFocus = focused;
            Log?.Info("Pointer", focused ? "Window focused." : "Window unfocused; held controls released.");

            if (focused)
            {
                _mouseHeldFromBeforeFocus = mouseButtonHeld;
            }
            else
            {
                CancelAll();
            }
        }

        private void OnDisable() => CancelAll();
    }
}
