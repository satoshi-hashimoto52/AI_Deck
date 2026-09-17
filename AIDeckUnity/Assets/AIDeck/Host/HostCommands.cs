using AIDeck.Audio;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.UI;

namespace AIDeck.Host
{
    /// <summary>
    /// Applies control intents directly to the audio engine.
    ///
    /// The Mac UI and the network command router both go through this class, so a control has
    /// exactly one implementation regardless of which surface moved it. That is what stops the
    /// two paths from drifting — a rule added here (for example, refusing SYNC when the tempo
    /// is unknown) applies to both without being written twice.
    /// </summary>
    public sealed class HostCommands : IDeckCommands
    {
        private readonly AudioEngine _engine;
        private readonly TrackLibrary _library;

        public HostCommands(AudioEngine engine, TrackLibrary library)
        {
            _engine = engine;
            _library = library;
        }

        /// <summary>Raised when a command could not be carried out, with a short user-facing reason.</summary>
        public event System.Action<string> Rejected;

        // ---------------------------------------------------------------- transport

        public void LoadTrack(DeckId deck, string trackId)
        {
            var track = _library.GetById(trackId);
            if (track == null)
            {
                Reject("That track is no longer in the library.");
                return;
            }

            _engine.LoadTrack(deck, track);
        }

        public void Eject(DeckId deck) => _engine.Eject(deck);

        public void TogglePlay(DeckId deck)
        {
            if (!_engine.Deck(deck).TogglePlay())
            {
                Reject($"Deck {deck.ToDisplayName()} has no track loaded.");
            }
        }

        public void CueSet(DeckId deck) => _engine.Deck(deck).SetCueHere();

        public void CueReturn(DeckId deck) => _engine.Deck(deck).CueReturn();

        public void Seek(DeckId deck, double seconds) => _engine.Deck(deck).Seek(seconds);

        // ---------------------------------------------------------------- tempo

        public void SetTempoFader(DeckId deck, float value) => _engine.Deck(deck).Tempo.Fader = value;

        public void SetTempoRange(DeckId deck, float percent) => _engine.Deck(deck).Tempo.RangePercent = percent;

        public void ToggleSync(DeckId deck)
        {
            var model = _engine.Deck(deck);
            if (model.Tempo.SyncEnabled)
            {
                model.DisableSync();
                return;
            }

            var other = _engine.Deck(deck.Other());
            if (!model.EnableSync(other.Tempo.EffectiveBpm(other.BaseBpm)))
            {
                // Refusing is deliberate: a guessed ratio would produce an audible, unrecoverable
                // tempo jump. See TempoControl.EnableSync.
                Reject("SYNC needs a known tempo on both decks.");
            }
        }

        // ---------------------------------------------------------------- loop

        public void ToggleLoop(DeckId deck)
        {
            var model = _engine.Deck(deck);
            if (model.Loop.IsActive)
            {
                model.Loop.Disable();
                return;
            }

            // A LOOP press with no region set makes a four-beat loop from where the playhead
            // is, which is what the single LOOP button in the mock-up implies.
            if (!model.Loop.HasIn || !model.Loop.HasOut)
            {
                SetLoopBeats(deck, 4f);
                return;
            }

            if (!model.Loop.Enable())
            {
                Reject("The loop region is too short.");
            }
        }

        public void SetLoopBeats(DeckId deck, float beats)
        {
            var model = _engine.Deck(deck);
            if (!model.HasTrack)
            {
                return;
            }

            var bpm = model.BaseBpm;
            if (bpm <= 0d)
            {
                // Without a tempo, fall back to a fixed length rather than refusing: a loop of
                // a known number of seconds is still useful, it just is not beat-aligned.
                model.Loop.SetRegion(model.PositionSeconds, model.PositionSeconds + 2d);
            }
            else
            {
                model.Loop.SetBeatLoop(model.PositionSeconds, bpm, beats);
            }

            if (!model.Loop.Enable())
            {
                Reject("The loop region is too short.");
            }
        }

        // ---------------------------------------------------------------- platter

        public void JogNudge(DeckId deck, float amount) => _engine.Deck(deck).Motion.Nudge(amount);

        public void ScratchBegin(DeckId deck) => _engine.Deck(deck).Motion.BeginScratch();

        public void ScratchUpdate(DeckId deck, float rate) => _engine.Deck(deck).Motion.UpdateScratch(rate);

        public void ScratchEnd(DeckId deck) => _engine.Deck(deck).Motion.EndScratch();

        public void Brake(DeckId deck) => _engine.Deck(deck).Motion.StartBrake();

        public void Backspin(DeckId deck) => _engine.Deck(deck).Motion.StartBackspin();

        // ---------------------------------------------------------------- mixer

        public void SetChannelGain(DeckId deck, float value) => _engine.Mixer.SetChannelGain(deck, value);

        public void SetCrossfader(float value) => _engine.Mixer.Crossfader = value;

        public void SetMasterGain(float value) => _engine.Mixer.MasterGain = value;

        public void SetFilter(DeckId deck, float value) => _engine.Mixer.SetFilter(deck, value);

        public void SetMute(DeckId deck, bool muted) => _engine.Mixer.SetMute(deck, muted);

        public void SetEcho(DeckId deck, bool enabled) => _engine.Mixer.SetEcho(deck, enabled);

        public void SetCueMonitor(DeckId deck, bool enabled) => _engine.Mixer.SetCue(deck, enabled);

        // ---------------------------------------------------------------- recording

        public void StartRecording()
        {
            if (!_engine.StartRecording() && !string.IsNullOrEmpty(_engine.RecordingFailure))
            {
                Reject(_engine.RecordingFailure);
            }
        }

        public void StopRecording() => _engine.StopRecording();

        public void AllStop() => _engine.AllStop(true);

        private void Reject(string reason) => Rejected?.Invoke(reason);
    }
}
