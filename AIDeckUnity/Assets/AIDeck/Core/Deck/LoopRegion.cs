using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Deck
{
    /// <summary>
    /// Loop in/out region for one deck (FR-031).
    /// The region is only <see cref="IsActive"/> when both ends are set, the region is at
    /// least <see cref="MinLengthSeconds"/> long, and looping has been enabled — a zero
    /// length loop would pin the transport and stall the audio thread.
    /// </summary>
    public sealed class LoopRegion
    {
        /// <summary>Shortest loop V1 allows. Below ~50 ms a loop is a buzz, not a loop.</summary>
        public const double MinLengthSeconds = 0.05d;

        private double _trackLengthSeconds;
        private bool _enabled;

        public LoopRegion(double trackLengthSeconds = 0d)
        {
            TrackLengthSeconds = trackLengthSeconds;
        }

        public double InSeconds { get; private set; }
        public double OutSeconds { get; private set; }
        public bool HasIn { get; private set; }
        public bool HasOut { get; private set; }

        public double TrackLengthSeconds
        {
            get => _trackLengthSeconds;
            set
            {
                _trackLengthSeconds = AudioSafety.SanitizeDouble(value, 0d, 24d * 60d * 60d, 0d);
                InSeconds = Clamp(InSeconds);
                OutSeconds = Clamp(OutSeconds);
                if (!IsValidRegion)
                {
                    _enabled = false;
                }
            }
        }

        public double LengthSeconds => IsValidRegion ? OutSeconds - InSeconds : 0d;

        private bool IsValidRegion =>
            HasIn && HasOut && OutSeconds - InSeconds >= MinLengthSeconds;

        /// <summary>True when the transport must wrap at <see cref="OutSeconds"/>.</summary>
        public bool IsActive => _enabled && IsValidRegion;

        public void SetIn(double seconds)
        {
            InSeconds = Clamp(seconds);
            HasIn = true;
            if (!IsValidRegion)
            {
                _enabled = false;
            }
        }

        public void SetOut(double seconds)
        {
            OutSeconds = Clamp(seconds);
            HasOut = true;
            if (!IsValidRegion)
            {
                _enabled = false;
            }
        }

        /// <summary>Sets both ends at once; the shorter value becomes IN regardless of argument order.</summary>
        public void SetRegion(double a, double b)
        {
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            InSeconds = Clamp(lo);
            OutSeconds = Clamp(hi);
            HasIn = true;
            HasOut = true;
            if (!IsValidRegion)
            {
                _enabled = false;
            }
        }

        /// <summary>
        /// Convenience for the LOOP button: set OUT a musical length after IN.
        /// </summary>
        public void SetBeatLoop(double inSeconds, double bpm, double beats)
        {
            if (!AudioSafety.IsFinite(bpm) || bpm <= 0d || !AudioSafety.IsFinite(beats) || beats <= 0d)
            {
                return;
            }

            var length = beats * 60d / bpm;
            SetRegion(inSeconds, inSeconds + length);
        }

        /// <summary>Enabling only takes effect when the region is valid; an invalid enable is a no-op.</summary>
        public bool Enable()
        {
            if (!IsValidRegion)
            {
                return false;
            }

            _enabled = true;
            return true;
        }

        public void Disable() => _enabled = false;

        public bool Toggle()
        {
            if (_enabled)
            {
                _enabled = false;
                return false;
            }

            return Enable();
        }

        public void Clear()
        {
            InSeconds = 0d;
            OutSeconds = 0d;
            HasIn = false;
            HasOut = false;
            _enabled = false;
        }

        /// <summary>
        /// Wraps a playhead position into the loop. Positions before IN are left alone so
        /// that arming a loop ahead of the playhead does not yank the transport forward.
        /// Handles a playhead that has overshot by more than one loop length.
        /// </summary>
        public double Wrap(double positionSeconds)
        {
            if (!IsActive || !AudioSafety.IsFinite(positionSeconds))
            {
                return positionSeconds;
            }

            if (positionSeconds < OutSeconds)
            {
                return positionSeconds;
            }

            var length = LengthSeconds;
            var over = (positionSeconds - InSeconds) % length;
            if (over < 0d)
            {
                over += length;
            }

            return InSeconds + over;
        }

        private double Clamp(double value) =>
            AudioSafety.SanitizeDouble(value, 0d, _trackLengthSeconds, 0d);
    }
}
