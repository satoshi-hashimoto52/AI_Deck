namespace AIDeck.Core.Deck
{
    /// <summary>
    /// Transport state of a single deck. Wire-stable values — see NETWORK_PROTOCOL.md.
    /// The motion axis (scratch / brake / backspin) is tracked separately by
    /// <see cref="MotionMode"/> so that "playing while braking" stays representable.
    /// </summary>
    public enum DeckPlaybackState : byte
    {
        /// <summary>No track assigned.</summary>
        Empty = 0,

        /// <summary>A load is in flight; transport commands are rejected until it settles.</summary>
        Loading = 1,

        /// <summary>A track is loaded and the transport is stopped.</summary>
        Paused = 2,

        /// <summary>A track is loaded and the transport is running.</summary>
        Playing = 3,

        /// <summary>The last load failed. The deck holds no audio and reports a reason.</summary>
        Error = 4
    }

    /// <summary>How the platter is currently being driven.</summary>
    public enum MotionMode : byte
    {
        /// <summary>Free running at the tempo-controlled rate.</summary>
        Normal = 0,

        /// <summary>A finger is on the platter and is driving the rate directly (FR-032).</summary>
        Scratching = 1,

        /// <summary>Ramping down to a stop (FR-033).</summary>
        Braking = 2,

        /// <summary>Short reverse flourish, then back to the pre-spin state (FR-034).</summary>
        Backspin = 3
    }
}
