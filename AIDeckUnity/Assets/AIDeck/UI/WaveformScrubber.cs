using System;
using AIDeck.Core.Model;
using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>
    /// Needle-drop seeking on a waveform strip (FR-025).
    ///
    /// The waveform scrolls past a fixed playhead, so a point on screen already corresponds to
    /// a point in the track: dragging left or right moves the playhead by however much audio
    /// lies between them. That is the same gesture as dragging a record under the needle, and
    /// it is why the seek is expressed relative to the current position rather than as an
    /// absolute position in the file.
    ///
    /// The seek is sent continuously while the finger moves, not only on release. A DJ finding
    /// a cue point needs to hear where they are.
    /// </summary>
    public sealed class WaveformScrubber : TouchWidget
    {
        /// <summary>Movement below this many pixels is a tap, not a scrub.</summary>
        private const float DragThreshold = 4f;

        private DeckId _deck;
        private Vector2 _pressPosition;
        private double _positionAtPress;
        private bool _moved;

        /// <summary>Seconds of audio visible across the strip. Must match the waveform view.</summary>
        public float WindowSeconds { get; set; } = 12f;

        /// <summary>The deck's current playhead, set every frame from the host state.</summary>
        public double CurrentPosition { get; set; }

        /// <summary>True while the strip has a track; a scrub on an empty deck does nothing.</summary>
        public bool HasTrack { get; set; }

        /// <summary>Raised with the deck and the position to seek to, in seconds.</summary>
        public event Action<DeckId, double> SeekRequested;

        /// <summary>Raised with true while a scrub is in progress, so the caller can stop fighting it.</summary>
        public event Action<bool> ScrubbingChanged;

        public bool IsScrubbing { get; private set; }

        public static WaveformScrubber Create(string name, Transform parent, TouchRouter router, DeckId deck)
        {
            var rect = UiFactory.Create(name, parent);
            var scrubber = rect.gameObject.AddComponent<WaveformScrubber>();
            scrubber._deck = deck;
            scrubber.Bind(router);
            return scrubber;
        }

        protected override void OnPressed(Vector2 screenPosition)
        {
            _pressPosition = screenPosition;
            _positionAtPress = CurrentPosition;
            _moved = false;
        }

        protected override void OnDragged(Vector2 screenPosition)
        {
            if (!HasTrack)
            {
                return;
            }

            var dx = screenPosition.x - _pressPosition.x;
            if (!_moved)
            {
                if (Mathf.Abs(dx) < DragThreshold)
                {
                    return;
                }

                _moved = true;
                IsScrubbing = true;
                ScrubbingChanged?.Invoke(true);
            }

            var width = Rect.rect.width;
            if (width <= 0f)
            {
                return;
            }

            // Screen pixels to seconds, using the same window the waveform is drawn with.
            // Dragging right moves the waveform right, which means going *back* in the track.
            var seconds = _positionAtPress - dx / width * WindowSeconds;
            SeekRequested?.Invoke(_deck, Math.Max(0d, seconds));
        }

        protected override void OnReleased(Vector2 screenPosition)
        {
            if (_moved)
            {
                OnDragged(screenPosition);
            }

            Finish();
        }

        /// <summary>
        /// A cancelled scrub stops where it is rather than snapping back. The playhead has
        /// already moved and the audio has already been heard there; undoing it would be a
        /// second unexpected jump (§9 is about avoiding surprises, not about reverting them).
        /// </summary>
        protected override void OnCancelled() => Finish();

        private void Finish()
        {
            if (!IsScrubbing)
            {
                return;
            }

            IsScrubbing = false;
            _moved = false;
            ScrubbingChanged?.Invoke(false);
        }
    }
}
