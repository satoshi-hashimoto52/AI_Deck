using AIDeck.Core.Model;

namespace AIDeck.UI
{
    /// <summary>
    /// Everything the control surface can ask for.
    ///
    /// The Mac and the iPad present the same controls, so they share the same view classes;
    /// what differs is where the intent goes. The Mac implementation calls the audio engine
    /// directly, the iPad implementation puts a message on the wire. Neither view knows which
    /// it is talking to, which is what keeps the two interfaces from drifting apart.
    /// </summary>
    public interface IDeckCommands
    {
        // --- transport ---
        void LoadTrack(DeckId deck, string trackId);
        void Eject(DeckId deck);
        void TogglePlay(DeckId deck);
        void CueSet(DeckId deck);
        void CueReturn(DeckId deck);
        void Seek(DeckId deck, double seconds);

        // --- tempo and sync ---
        void SetTempoFader(DeckId deck, float value);
        void SetTempoRange(DeckId deck, float percent);
        void ToggleSync(DeckId deck);

        // --- loop ---
        void ToggleLoop(DeckId deck);
        void SetLoopBeats(DeckId deck, float beats);

        // --- platter ---
        void JogNudge(DeckId deck, float amount);
        void ScratchBegin(DeckId deck);
        void ScratchUpdate(DeckId deck, float rate);
        void ScratchEnd(DeckId deck);
        void Brake(DeckId deck);
        void Backspin(DeckId deck);

        // --- mixer ---
        void SetChannelGain(DeckId deck, float value);
        void SetCrossfader(float value);
        void SetMasterGain(float value);
        void SetFilter(DeckId deck, float value);
        void SetMute(DeckId deck, bool muted);
        void SetEcho(DeckId deck, bool enabled);
        void SetCueMonitor(DeckId deck, bool enabled);

        // --- recording and safety ---
        void StartRecording();
        void StopRecording();

        /// <summary>Release every continuous control and stop both decks (§9).</summary>
        void AllStop();
    }
}
