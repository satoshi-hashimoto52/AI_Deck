using System;

namespace AIDeck.Core.Deck
{
    /// <summary>Commands the transport accepts. One entry point keeps every guard in one place.</summary>
    public enum DeckCommand
    {
        BeginLoad,
        LoadSucceeded,
        LoadFailed,
        Play,
        Pause,
        TogglePlay,
        /// <summary>Stop and return to the cue point (FR-023, FR-024).</summary>
        CueReturn,
        Eject,
        /// <summary>A runtime fault (device loss, decode failure mid-play).</summary>
        Fault
    }

    /// <summary>
    /// Pure transport state machine for one deck.
    /// Invalid transitions are rejected and reported rather than silently applied, which
    /// is what lets the network layer accept commands from a controller that has a stale
    /// view of the deck without the host ever entering an impossible state.
    /// </summary>
    public sealed class DeckStateMachine
    {
        private string _errorReason = string.Empty;

        public DeckPlaybackState State { get; private set; } = DeckPlaybackState.Empty;

        /// <summary>User-facing reason for <see cref="DeckPlaybackState.Error"/>; never contains file contents (NFR-006).</summary>
        public string ErrorReason => _errorReason;

        /// <summary>Raised after every accepted transition, with (previous, next).</summary>
        public event Action<DeckPlaybackState, DeckPlaybackState> StateChanged;

        public bool HasTrack => State == DeckPlaybackState.Paused || State == DeckPlaybackState.Playing;

        public bool IsPlaying => State == DeckPlaybackState.Playing;

        /// <summary>True while a transport command would be rejected because a load is in flight.</summary>
        public bool IsBusy => State == DeckPlaybackState.Loading;

        /// <summary>
        /// Applies a command. Returns true when the state machine accepted it.
        /// A rejected command is a no-op: the caller must not act on it either.
        /// </summary>
        public bool TryApply(DeckCommand command, string reason = null)
        {
            var next = Resolve(command);
            if (!next.HasValue)
            {
                return false;
            }

            var target = next.Value;
            _errorReason = target == DeckPlaybackState.Error
                ? Sanitize(reason)
                : string.Empty;

            if (target == State)
            {
                // Idempotent commands (Play while playing, Pause while paused) are accepted
                // so a retried network message does not surface as an error, but they raise
                // no change event.
                return true;
            }

            var previous = State;
            State = target;
            StateChanged?.Invoke(previous, target);
            return true;
        }

        private DeckPlaybackState? Resolve(DeckCommand command)
        {
            switch (command)
            {
                case DeckCommand.BeginLoad:
                    // A load may start from any state — loading over a playing deck is legal
                    // and is the normal way a DJ swaps a track in.
                    return DeckPlaybackState.Loading;

                case DeckCommand.LoadSucceeded:
                    return State == DeckPlaybackState.Loading ? DeckPlaybackState.Paused : (DeckPlaybackState?)null;

                case DeckCommand.LoadFailed:
                    return State == DeckPlaybackState.Loading ? DeckPlaybackState.Error : (DeckPlaybackState?)null;

                case DeckCommand.Play:
                    return HasTrack ? DeckPlaybackState.Playing : (DeckPlaybackState?)null;

                case DeckCommand.Pause:
                    return HasTrack ? DeckPlaybackState.Paused : (DeckPlaybackState?)null;

                case DeckCommand.TogglePlay:
                    if (!HasTrack)
                    {
                        return null;
                    }

                    return State == DeckPlaybackState.Playing
                        ? DeckPlaybackState.Paused
                        : DeckPlaybackState.Playing;

                case DeckCommand.CueReturn:
                    return HasTrack ? DeckPlaybackState.Paused : (DeckPlaybackState?)null;

                case DeckCommand.Eject:
                    // Always allowed: ejecting is the recovery path out of Error and Loading.
                    return DeckPlaybackState.Empty;

                case DeckCommand.Fault:
                    return State == DeckPlaybackState.Empty ? (DeckPlaybackState?)null : DeckPlaybackState.Error;

                default:
                    return null;
            }
        }

        /// <summary>Keeps reasons short and free of newlines so they fit the status line and the wire format.</summary>
        private static string Sanitize(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "Unknown error";
            }

            var cleaned = reason.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return cleaned.Length > 160 ? cleaned.Substring(0, 160) : cleaned;
        }
    }
}
