using AIDeck.Core.Model;

namespace AIDeck.Core.Deck
{
    /// <summary>
    /// The single cue point of a deck (FR-024). V1 has one cue per deck, matching the
    /// mock-up's single CUE button; hot cues are a V2 item.
    /// </summary>
    public sealed class CuePoint
    {
        private double _trackLengthSeconds;

        public CuePoint(double trackLengthSeconds = 0d)
        {
            TrackLengthSeconds = trackLengthSeconds;
        }

        /// <summary>Cue position in seconds. Always inside [0, track length].</summary>
        public double PositionSeconds { get; private set; }

        /// <summary>True once the user has explicitly placed a cue; before that the cue is the track start.</summary>
        public bool IsSet { get; private set; }

        public double TrackLengthSeconds
        {
            get => _trackLengthSeconds;
            set
            {
                _trackLengthSeconds = AudioSafety.SanitizeDouble(value, 0d, 24d * 60d * 60d, 0d);
                PositionSeconds = Clamp(PositionSeconds);
            }
        }

        /// <summary>Places the cue. Out-of-range and non-finite requests clamp instead of being rejected.</summary>
        public void Set(double positionSeconds)
        {
            PositionSeconds = Clamp(positionSeconds);
            IsSet = true;
        }

        /// <summary>Clears the cue back to the track start — used when a new track is loaded.</summary>
        public void Reset()
        {
            PositionSeconds = 0d;
            IsSet = false;
        }

        /// <summary>The position the transport jumps to on CUE: the stored cue, or 0 when unset.</summary>
        public double ReturnPosition => IsSet ? PositionSeconds : 0d;

        private double Clamp(double value) =>
            AudioSafety.SanitizeDouble(value, 0d, _trackLengthSeconds, 0d);
    }
}
