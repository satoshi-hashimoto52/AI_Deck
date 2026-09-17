using System.Collections;
using AIDeck.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// The touch router is what makes FR-071 (several controls at once) and FR-073 (a
    /// cancelled pointer releases its control) true. Both are driven here by injecting
    /// pointers directly, which is the only way to test them without a touchscreen.
    /// </summary>
    [TestFixture]
    public class TouchRouterTests
    {
        private GameObject _root;
        private Canvas _canvas;
        private TouchRouter _router;

        /// <summary>A widget that records what happened to it.</summary>
        private sealed class ProbeWidget : TouchWidget
        {
            public int Pressed;
            public int Dragged;
            public int Released;
            public int Cancelled;

            protected override void OnPressed(Vector2 screenPosition) => Pressed++;
            protected override void OnDragged(Vector2 screenPosition) => Dragged++;
            protected override void OnReleased(Vector2 screenPosition) => Released++;
            protected override void OnCancelled() => Cancelled++;
        }

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("TouchRouterTestCanvas", typeof(RectTransform));
            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _router = _root.AddComponent<TouchRouter>();

            var rect = (RectTransform)_root.transform;
            rect.sizeDelta = new Vector2(Screen.width, Screen.height);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        /// <summary>Creates a probe occupying a screen-space rectangle.</summary>
        private ProbeWidget MakeProbe(float x, float y, float width, float height)
        {
            var rect = UiFactory.Create("Probe", _root.transform);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);

            var probe = rect.gameObject.AddComponent<ProbeWidget>();
            probe.Bind(_router);
            return probe;
        }

        [UnityTest]
        public IEnumerator APointerIsRoutedToTheWidgetUnderIt()
        {
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            Assert.That(probe.Pressed, Is.EqualTo(1));
            Assert.That(probe.IsPressed, Is.True);

            _router.PointerMove(1, new Vector2(60f, 60f));
            Assert.That(probe.Dragged, Is.EqualTo(1));

            _router.PointerUp(1, new Vector2(60f, 60f));
            Assert.That(probe.Released, Is.EqualTo(1));
            Assert.That(probe.IsPressed, Is.False);
        }

        [UnityTest]
        public IEnumerator APointerOutsideEveryWidgetIsIgnored()
        {
            var probe = MakeProbe(10f, 10f, 50f, 50f);
            yield return null;

            _router.PointerDown(1, new Vector2(500f, 500f));
            Assert.That(probe.Pressed, Is.EqualTo(0));
            Assert.That(_router.ActivePointerCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TwoWidgetsAreOperatedSimultaneously()
        {
            // FR-071. A single-selection model would make the second press steal the first.
            var left = MakeProbe(10f, 10f, 100f, 100f);
            var right = MakeProbe(200f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            _router.PointerDown(2, new Vector2(250f, 50f));

            Assert.That(left.IsPressed, Is.True);
            Assert.That(right.IsPressed, Is.True);
            Assert.That(_router.ActivePointerCount, Is.EqualTo(2));

            _router.PointerMove(1, new Vector2(55f, 55f));
            Assert.That(left.Dragged, Is.EqualTo(1));
            Assert.That(right.Dragged, Is.EqualTo(0), "one finger's movement must not drive the other control");

            _router.PointerUp(1, new Vector2(55f, 55f));
            Assert.That(left.IsPressed, Is.False);
            Assert.That(right.IsPressed, Is.True, "releasing one finger must not release the other");
        }

        [UnityTest]
        public IEnumerator AWidgetKeepsItsFingerWhenTheFingerLeavesItsRectangle()
        {
            // How a real fader behaves: once grabbed, sliding off the side does not drop it.
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            _router.PointerMove(1, new Vector2(900f, 900f));

            Assert.That(probe.Dragged, Is.EqualTo(1));
            Assert.That(probe.IsPressed, Is.True);
        }

        [UnityTest]
        public IEnumerator ASecondFingerOnTheSameWidgetIsIgnored()
        {
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(30f, 30f));
            _router.PointerDown(2, new Vector2(80f, 80f));

            Assert.That(probe.Pressed, Is.EqualTo(1), "two fingers must not fight over one control");

            _router.PointerMove(2, new Vector2(85f, 85f));
            Assert.That(probe.Dragged, Is.EqualTo(0));

            _router.PointerUp(1, new Vector2(30f, 30f));
            Assert.That(probe.Released, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ACancelledPointerReleasesWithoutCompleting()
        {
            // FR-073. A cancel is the input vanishing, not a decision, so the widget must not
            // act on it.
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            _router.PointerCancel(1);

            Assert.That(probe.Cancelled, Is.EqualTo(1));
            Assert.That(probe.Released, Is.EqualTo(0), "a cancelled gesture is not a release");
            Assert.That(probe.IsPressed, Is.False);
            Assert.That(_router.ActivePointerCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator CancelAllReleasesEveryHeldControl()
        {
            // FR-074: what backgrounding does.
            var left = MakeProbe(10f, 10f, 100f, 100f);
            var right = MakeProbe(200f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            _router.PointerDown(2, new Vector2(250f, 50f));

            _router.CancelAll();

            Assert.That(left.Cancelled, Is.EqualTo(1));
            Assert.That(right.Cancelled, Is.EqualTo(1));
            Assert.That(_router.ActivePointerCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator HigherPriorityWinsWhenRectanglesOverlap()
        {
            // The load buttons sit on top of a library row's tap area.
            var background = MakeProbe(10f, 10f, 200f, 200f);
            var foreground = MakeProbe(50f, 50f, 60f, 60f);
            foreground.TouchPriority = 10;
            yield return null;

            _router.PointerDown(1, new Vector2(70f, 70f));

            Assert.That(foreground.IsPressed, Is.True);
            Assert.That(background.IsPressed, Is.False);
        }

        [UnityTest]
        public IEnumerator ADisabledWidgetTakesNoInput()
        {
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            probe.Interactable = false;
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            Assert.That(probe.Pressed, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator DeactivatingAHeldWidgetCancelsIt()
        {
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            probe.gameObject.SetActive(false);

            // The stale-capture sweep runs in Update.
            yield return null;

            Assert.That(probe.Cancelled, Is.EqualTo(1));
            Assert.That(_router.ActivePointerCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator UnregisteringAHeldWidgetCancelsIt()
        {
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            _router.Unregister(probe);

            Assert.That(probe.Cancelled, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ForceReleaseCancelsWithoutAPointerEvent()
        {
            var probe = MakeProbe(10f, 10f, 100f, 100f);
            yield return null;

            _router.PointerDown(1, new Vector2(50f, 50f));
            probe.ForceRelease();

            Assert.That(probe.Cancelled, Is.EqualTo(1));
            Assert.That(probe.IsPressed, Is.False);
        }
    }
}
