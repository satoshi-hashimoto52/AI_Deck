using System;
using System.Collections.Generic;
using AIDeck.Core.Analysis;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.UI;

namespace AIDeck.Controller
{
    /// <summary>
    /// The backend used before a connection exists.
    ///
    /// Every command is discarded rather than queued. Queuing would mean that pressing PLAY
    /// while disconnected fires the moment the link comes up, which is the last thing a DJ
    /// wants — the intent was formed against a state that no longer applies (the same
    /// reasoning that makes the session drop buffered intents on reconnect, see
    /// NETWORK_PROTOCOL.md §5).
    /// </summary>
    public sealed class DisconnectedBackend : IControllerBackend, IDeckCommands
    {
        private static readonly TrackInfo[] NoTracks = Array.Empty<TrackInfo>();
        private static readonly DiscoveredHost[] NoHosts = Array.Empty<DiscoveredHost>();

        public DisconnectedBackend(string statusText = "Not connected")
        {
            StatusText = statusText;
        }

        public IDeckCommands Commands => this;

        public StateSnapshot Snapshot => StateSnapshot.Empty;

        public IReadOnlyList<TrackInfo> Library => NoTracks;

        public ConnectionState State { get; set; } = ConnectionState.Idle;

        public string StatusText { get; set; }

        public IReadOnlyList<DiscoveredHost> DiscoveredHosts => NoHosts;

        public WaveformData WaveformFor(string trackId) => null;

        public void Connect(string address, int port)
        {
            State = ConnectionState.Failed;
            StatusText = "Connecting is not available in this build yet.";
        }

        public void Connect(DiscoveredHost host) => Connect(host.Address, host.Port);

        public void Disconnect()
        {
            State = ConnectionState.Idle;
            StatusText = "Not connected";
        }

        public void Tick(float deltaSeconds)
        {
        }

        public void ReleaseAll()
        {
        }

        // ---- IDeckCommands: every intent is deliberately dropped ----

        public void LoadTrack(DeckId deck, string trackId) { }
        public void Eject(DeckId deck) { }
        public void TogglePlay(DeckId deck) { }
        public void CueSet(DeckId deck) { }
        public void CueReturn(DeckId deck) { }
        public void Seek(DeckId deck, double seconds) { }
        public void SetTempoFader(DeckId deck, float value) { }
        public void SetTempoRange(DeckId deck, float percent) { }
        public void ToggleSync(DeckId deck) { }
        public void ToggleLoop(DeckId deck) { }
        public void SetLoopBeats(DeckId deck, float beats) { }
        public void JogNudge(DeckId deck, float amount) { }
        public void ScratchBegin(DeckId deck) { }
        public void ScratchUpdate(DeckId deck, float rate) { }
        public void ScratchMove(DeckId deck, float seconds) { }
        public void ScratchEnd(DeckId deck) { }
        public void Brake(DeckId deck) { }
        public void Backspin(DeckId deck) { }
        public void SetChannelGain(DeckId deck, float value) { }
        public void SetCrossfader(float value) { }
        public void SetMasterGain(float value) { }
        public void SetFilter(DeckId deck, float value) { }
        public void SetMute(DeckId deck, bool muted) { }
        public void SetEcho(DeckId deck, bool enabled) { }
        public void SetCueMonitor(DeckId deck, bool enabled) { }
        public void StartRecording() { }
        public void StopRecording() { }
        public void AllStop() { }
    }
}
