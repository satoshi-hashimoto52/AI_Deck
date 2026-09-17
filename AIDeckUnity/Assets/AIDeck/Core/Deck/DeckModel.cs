using System;
using AIDeck.Core.Model;
using AIDeck.Core.Net;

namespace AIDeck.Core.Deck
{
    /// <summary>
    /// The complete logical state of one deck: transport, cue, loop, tempo and platter.
    ///
    /// This is the layer the host's audio engine drives and the layer the tests exercise.
    /// It owns no Unity objects and no audio buffers, so every rule the requirements state
    /// about deck behaviour — loop wrapping, cue return, sync refusal, motion cancellation —
    /// is verifiable without an audio device.
    /// </summary>
    public sealed class DeckModel
    {
        /// <summary>Fade applied on play, pause and load so no transition steps the gain (§9).</summary>
        public const float TransitionFadeSeconds = 0.012f;

        private double _positionSeconds;
        private double _trackLengthSeconds;

        public DeckModel(DeckId deck)
        {
            Deck = deck;
            Transport = new DeckStateMachine();
            Cue = new CuePoint();
            Loop = new LoopRegion();
            Tempo = new TempoControl();
            Motion = new PlatterMotion();
            TrackId = string.Empty;
        }

        public DeckId Deck { get; }
        public DeckStateMachine Transport { get; }
        public CuePoint Cue { get; }
        public LoopRegion Loop { get; }
        public TempoControl Tempo { get; }
        public PlatterMotion Motion { get; }

        public string TrackId { get; private set; }

        /// <summary>Analysed tempo of the loaded track, 0 when unknown.</summary>
        public double BaseBpm { get; private set; }

        public double TrackLengthSeconds
        {
            get => _trackLengthSeconds;
            private set
            {
                _trackLengthSeconds = AudioSafety.SanitizeDouble(value, 0d, 24d * 60d * 60d, 0d);
                Cue.TrackLengthSeconds = _trackLengthSeconds;
                Loop.TrackLengthSeconds = _trackLengthSeconds;
            }
        }

        /// <summary>Playhead in seconds. Always inside [0, track length].</summary>
        public double PositionSeconds
        {
            get => _positionSeconds;
            private set => _positionSeconds = AudioSafety.SanitizeDouble(value, 0d, _trackLengthSeconds, 0d);
        }

        public bool IsPlaying => Transport.IsPlaying;
        public bool HasTrack => Transport.HasTrack && !string.IsNullOrEmpty(TrackId);

        /// <summary>
        /// Final rate handed to the audio layer: tempo fader × SYNC × platter motion.
        /// Clamped to the scratch range, which is wider than the tempo range because a
        /// scratch legitimately exceeds normal playback speed and may run backwards.
        /// </summary>
        public float EffectiveRate => AudioSafety.Sanitize(
            Tempo.EffectiveRate * Motion.Rate,
            -PlatterMotion.MaxScratchRate,
            PlatterMotion.MaxScratchRate,
            1f);

        /// <summary>Raised when the audio layer must jump the playhead (cue, loop wrap, seek).</summary>
        public event Action<double> SeekRequested;

        /// <summary>Raised when the deck reaches the end of the track and stops.</summary>
        public event Action ReachedEnd;

        // ---------------------------------------------------------------- loading

        /// <summary>Marks a load as started. Transport commands are refused until it settles.</summary>
        public void BeginLoad()
        {
            Transport.TryApply(DeckCommand.BeginLoad);
            Motion.Cancel();
        }

        /// <summary>
        /// Completes a load. Resets cue, loop and platter so no state leaks from the
        /// previous track — a loop left over from another song would be silently wrong.
        /// </summary>
        public bool CompleteLoad(string trackId, double lengthSeconds, double baseBpm)
        {
            if (!Transport.TryApply(DeckCommand.LoadSucceeded))
            {
                return false;
            }

            TrackId = trackId ?? string.Empty;
            TrackLengthSeconds = lengthSeconds;
            BaseBpm = TrackInfo.NormalizeBpm(baseBpm);
            Cue.Reset();
            Loop.Clear();
            Motion.Cancel();
            Tempo.DisableSync();
            PositionSeconds = 0d;
            return true;
        }

        public bool FailLoad(string reason)
        {
            if (!Transport.TryApply(DeckCommand.LoadFailed, reason))
            {
                return false;
            }

            TrackId = string.Empty;
            TrackLengthSeconds = 0d;
            BaseBpm = 0d;
            Cue.Reset();
            Loop.Clear();
            Motion.Cancel();
            PositionSeconds = 0d;
            return true;
        }

        /// <summary>Unloads the deck. Always succeeds; this is the recovery path out of Error.</summary>
        public void Eject()
        {
            Transport.TryApply(DeckCommand.Eject);
            TrackId = string.Empty;
            TrackLengthSeconds = 0d;
            BaseBpm = 0d;
            Cue.Reset();
            Loop.Clear();
            Motion.Cancel();
            Tempo.DisableSync();
            Tempo.ResetFader();
            PositionSeconds = 0d;
        }

        /// <summary>Sets the tempo once background analysis produces it (FR-029).</summary>
        public void SetAnalysedBpm(double bpm) => BaseBpm = TrackInfo.NormalizeBpm(bpm);

        // ---------------------------------------------------------------- transport

        public bool Play() => Transport.TryApply(DeckCommand.Play);

        public bool Pause()
        {
            if (!Transport.TryApply(DeckCommand.Pause))
            {
                return false;
            }

            // A pause while a gesture is in flight must not leave the platter held at a
            // scratch rate: the next play would start at the wrong speed (FR-066).
            Motion.Cancel();
            return true;
        }

        public bool TogglePlay() => Transport.TryApply(DeckCommand.TogglePlay);

        /// <summary>Stores the current playhead as the cue point (FR-024).</summary>
        public bool SetCueHere()
        {
            if (!HasTrack)
            {
                return false;
            }

            Cue.Set(PositionSeconds);
            return true;
        }

        /// <summary>Stops and returns to the cue point (FR-023, FR-024).</summary>
        public bool CueReturn()
        {
            if (!Transport.TryApply(DeckCommand.CueReturn))
            {
                return false;
            }

            Motion.Cancel();
            Seek(Cue.ReturnPosition);
            return true;
        }

        /// <summary>Moves the playhead (FR-025). Out-of-range requests clamp.</summary>
        public bool Seek(double seconds)
        {
            if (!HasTrack)
            {
                return false;
            }

            PositionSeconds = seconds;
            SeekRequested?.Invoke(PositionSeconds);
            return true;
        }

        // ---------------------------------------------------------------- position

        /// <summary>
        /// Advances the playhead by <paramref name="deltaSeconds"/> of wall-clock time at the
        /// current effective rate, applying loop wrapping and the end-of-track stop.
        ///
        /// Used directly by the tests and by the host when it needs a predicted position
        /// between audio callbacks. When Unity's AudioSource is the position authority the
        /// host calls <see cref="ReportPosition"/> instead.
        /// </summary>
        public void Advance(double deltaSeconds)
        {
            if (!IsPlaying || !AudioSafety.IsFinite(deltaSeconds) || deltaSeconds <= 0d)
            {
                return;
            }

            var next = _positionSeconds + deltaSeconds * EffectiveRate;
            ApplyPosition(next, true);
        }

        /// <summary>
        /// Accepts the position the audio device actually reached and returns the position
        /// the device should be moved to, or null when it may continue undisturbed.
        /// </summary>
        public double? ReportPosition(double deviceSeconds)
        {
            var before = _positionSeconds;
            ApplyPosition(deviceSeconds, false);

            if (Math.Abs(_positionSeconds - deviceSeconds) < 1e-6d)
            {
                return null;
            }

            // ApplyPosition moved the playhead (loop wrap or end stop); the device must follow.
            return _positionSeconds;
        }

        private void ApplyPosition(double candidate, bool raiseSeek)
        {
            if (!AudioSafety.IsFinite(candidate))
            {
                return;
            }

            if (candidate < 0d)
            {
                // Reverse motion ran off the front of the track. Park at the start rather
                // than wrapping to the end, which would be a jarring surprise mid-scratch.
                PositionSeconds = 0d;
                if (raiseSeek)
                {
                    SeekRequested?.Invoke(0d);
                }

                return;
            }

            if (Loop.IsActive)
            {
                var wrapped = Loop.Wrap(candidate);
                if (Math.Abs(wrapped - candidate) > 1e-9d)
                {
                    PositionSeconds = wrapped;
                    SeekRequested?.Invoke(PositionSeconds);
                    return;
                }

                PositionSeconds = wrapped;
                return;
            }

            if (_trackLengthSeconds > 0d && candidate >= _trackLengthSeconds)
            {
                PositionSeconds = _trackLengthSeconds;
                Transport.TryApply(DeckCommand.Pause);
                Motion.Cancel();
                ReachedEnd?.Invoke();
                return;
            }

            PositionSeconds = candidate;
        }

        // ---------------------------------------------------------------- platter

        /// <summary>
        /// Advances the platter model and applies whatever it asks for.
        /// Called once per frame by the host; also drives BRAKE and BACKSPIN to completion.
        /// </summary>
        public void TickMotion(float deltaSeconds)
        {
            var outcome = Motion.Tick(deltaSeconds);
            switch (outcome)
            {
                case MotionOutcome.Stop:
                    Pause();
                    break;
                case MotionOutcome.Release:
                    // Nothing to do: the rate is already back under tempo control.
                    break;
            }
        }

        /// <summary>
        /// Releases every continuous control and returns the deck to a defined state.
        /// Called on touch cancel, app backgrounding and network loss (FR-066, FR-073, FR-074).
        /// </summary>
        public void ReleaseContinuousControls() => Motion.Cancel();

        // ---------------------------------------------------------------- sync

        /// <summary>
        /// Matches this deck's tempo to <paramref name="otherBpm"/> (FR-030).
        /// Returns false when either tempo is unknown or the match would need an unsafe rate,
        /// in which case SYNC stays off rather than applying a wrong ratio.
        /// </summary>
        public bool EnableSync(double otherBpm) => Tempo.EnableSync(BaseBpm, otherBpm);

        public void DisableSync() => Tempo.DisableSync();

        // ---------------------------------------------------------------- snapshot

        public DeckSnapshot ToSnapshot(float peakLevel = 0f) => new DeckSnapshot
        {
            Deck = Deck,
            State = Transport.State,
            Motion = Motion.Mode,
            TrackId = TrackId ?? string.Empty,
            PositionSeconds = PositionSeconds,
            LengthSeconds = TrackLengthSeconds,
            BaseBpm = BaseBpm,
            TempoFader = Tempo.Fader,
            TempoRangePercent = Tempo.RangePercent,
            SyncEnabled = Tempo.SyncEnabled,
            EffectiveRate = EffectiveRate,
            CueSeconds = Cue.PositionSeconds,
            CueIsSet = Cue.IsSet,
            LoopInSeconds = Loop.InSeconds,
            LoopOutSeconds = Loop.OutSeconds,
            LoopActive = Loop.IsActive,
            PeakLevel = AudioSafety.Sanitize01(peakLevel),
            ErrorReason = Transport.ErrorReason
        };
    }
}
