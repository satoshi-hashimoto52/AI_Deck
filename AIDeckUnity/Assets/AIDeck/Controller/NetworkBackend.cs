using System;
using System.Collections.Generic;
using AIDeck.Core.Analysis;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Net;
using AIDeck.UI;

namespace AIDeck.Controller
{
    /// <summary>
    /// The controller talking to a real host over the LAN.
    ///
    /// Implements the same <see cref="IControllerBackend"/> the UI was written against in
    /// Phase 2, so nothing in the screen changed when the network arrived. It owns the library
    /// and waveform caches the host streams over, because the controller has no disk copy of
    /// either and must be able to draw them without asking again every frame.
    /// </summary>
    public sealed class NetworkBackend : IControllerBackend, IDeckCommands, IDisposable
    {
        private readonly ControllerSession _session = new ControllerSession();
        private readonly DiagnosticLog _log;
        private readonly List<TrackInfo> _library = new List<TrackInfo>();
        private readonly List<DiscoveredHost> _hosts = new List<DiscoveredHost>();
        private readonly Dictionary<string, WaveformData> _waveforms = new Dictionary<string, WaveformData>(StringComparer.Ordinal);
        private readonly Dictionary<string, WaveformAssembly> _pendingWaveforms = new Dictionary<string, WaveformAssembly>(StringComparer.Ordinal);
        private readonly HashSet<string> _requestedWaveforms = new HashSet<string>(StringComparer.Ordinal);

        private StateSnapshot _snapshot = StateSnapshot.Empty;
        private int _libraryRevision = -1;
        private readonly List<TrackInfo> _incomingLibrary = new List<TrackInfo>();
        private int _incomingRevision = -1;
        private int _incomingChunks;

        /// <summary>Collects a track's waveform chunks until the set is complete.</summary>
        private sealed class WaveformAssembly
        {
            public int ChunkCount;
            public int Received;
            public int BucketsPerSecond;
            public double DurationSeconds;
            public readonly List<byte> Peaks = new List<byte>();
            public readonly List<byte> Rms = new List<byte>();
        }

        public NetworkBackend(DiagnosticLog log = null)
        {
            _log = log;

            _session.SnapshotReceived += OnSnapshot;
            _session.LibraryChunkReceived += OnLibraryChunk;
            _session.WaveformChunkReceived += OnWaveformChunk;
            _session.ErrorReceived += message => _log?.Warning("Net", message);
            _session.NoticeReceived += message => _log?.Info("Net", message);
            _session.Connected += OnConnected;
            _session.Disconnected += OnDisconnected;

            _session.StartDiscovery();
        }

        public ControllerSession Session => _session;

        public IDeckCommands Commands => this;

        public StateSnapshot Snapshot => _snapshot;

        public IReadOnlyList<TrackInfo> Library => _library;

        public ConnectionState State
        {
            get
            {
                switch (_session.State)
                {
                    case ControllerSessionState.Searching: return ConnectionState.Searching;
                    case ControllerSessionState.Connecting: return ConnectionState.Connecting;
                    case ControllerSessionState.Connected: return ConnectionState.Connected;
                    case ControllerSessionState.Reconnecting: return ConnectionState.Reconnecting;
                    case ControllerSessionState.Failed: return ConnectionState.Failed;
                    default: return ConnectionState.Idle;
                }
            }
        }

        public string StatusText => _session.StatusText;

        public IReadOnlyList<DiscoveredHost> DiscoveredHosts => _hosts;

        public WaveformData WaveformFor(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
            {
                return null;
            }

            if (_waveforms.TryGetValue(trackId, out var waveform))
            {
                return waveform;
            }

            // Ask once. The host streams it over; asking every frame would flood the reliable
            // channel with requests for something already on its way.
            if (_requestedWaveforms.Add(trackId))
            {
                _session.Send(MessageType.RequestWaveform, null, Messages.Text(trackId));
            }

            return null;
        }

        public void Connect(string address, int port) => _session.Connect(address, port);

        public void Connect(DiscoveredHost host) => _session.Connect(host.Address, host.Port);

        public void Disconnect() => _session.Disconnect();

        public void Tick(float deltaSeconds)
        {
            _session.Poll(deltaSeconds);
            _session.CopyDiscoveredHosts(_hosts, (name, address, port) => new DiscoveredHost(name, address, port));
        }

        /// <summary>
        /// Tells the host to release every continuous control (FR-066, FR-074).
        ///
        /// Sent as explicit gesture ends rather than as a blanket stop, because the controller
        /// going into the background is not a request to stop the music — the host decides that
        /// from its own disconnect policy.
        /// </summary>
        public void ReleaseAll()
        {
            if (_session.State != ControllerSessionState.Connected)
            {
                return;
            }

            foreach (var deck in new[] { DeckId.A, DeckId.B })
            {
                _session.Send(MessageType.ScratchEnd, deck);
            }
        }

        // ---------------------------------------------------------------- incoming

        private void OnConnected()
        {
            // A reconnection may land on a host whose library has changed; nothing is assumed.
            _library.Clear();
            _libraryRevision = -1;
            _requestedRevision = -1;
            _waveforms.Clear();
            _requestedWaveforms.Clear();
            _pendingWaveforms.Clear();
            _log?.Info("Net", "Connected to " + _session.HostName);
        }

        private void OnDisconnected(string reason)
        {
            _snapshot = StateSnapshot.Empty;
            _log?.Info("Net", "Disconnected: " + reason);
        }

        private int _requestedRevision = -1;

        private void OnSnapshot(StateSnapshot snapshot)
        {
            _snapshot = snapshot;

            // The host pushes the library on change, so this is a safety net for a push that
            // was lost or that happened while the link was down. Asking once per revision keeps
            // it from becoming a request every 50 ms while a transfer is already in flight.
            if (snapshot.LibraryRevision != _libraryRevision &&
                snapshot.LibraryRevision != _requestedRevision)
            {
                _requestedRevision = snapshot.LibraryRevision;
                RequestLibrary();
            }
        }

        private void OnLibraryChunk(LibraryChunkPayload chunk)
        {
            // Chunks from a superseded revision are discarded rather than merged, so a library
            // that changed mid-transfer never produces a half-and-half list.
            if (chunk.Revision != _incomingRevision)
            {
                _incomingRevision = chunk.Revision;
                _incomingChunks = 0;
                _incomingLibrary.Clear();
            }

            if (chunk.Tracks != null)
            {
                _incomingLibrary.AddRange(chunk.Tracks);
            }

            _incomingChunks++;

            if (_incomingChunks < chunk.ChunkCount)
            {
                return;
            }

            _library.Clear();
            _library.AddRange(_incomingLibrary);
            _libraryRevision = chunk.Revision;
            _requestedRevision = chunk.Revision;
            _incomingLibrary.Clear();
            _incomingChunks = 0;
            _incomingRevision = -1;
        }

        private void OnWaveformChunk(WaveformChunkPayload chunk)
        {
            if (string.IsNullOrEmpty(chunk.TrackId))
            {
                return;
            }

            if (!_pendingWaveforms.TryGetValue(chunk.TrackId, out var assembly) ||
                assembly.ChunkCount != chunk.ChunkCount)
            {
                assembly = new WaveformAssembly
                {
                    ChunkCount = chunk.ChunkCount,
                    BucketsPerSecond = chunk.BucketsPerSecond,
                    DurationSeconds = chunk.DurationSeconds
                };

                _pendingWaveforms[chunk.TrackId] = assembly;
            }

            if (chunk.Peaks != null)
            {
                assembly.Peaks.AddRange(chunk.Peaks);
            }

            if (chunk.Rms != null)
            {
                assembly.Rms.AddRange(chunk.Rms);
            }

            assembly.Received++;
            if (assembly.Received < assembly.ChunkCount)
            {
                return;
            }

            _waveforms[chunk.TrackId] = new WaveformData(
                assembly.Peaks.ToArray(), assembly.Rms.ToArray(),
                assembly.DurationSeconds, assembly.BucketsPerSecond);

            _pendingWaveforms.Remove(chunk.TrackId);
        }

        /// <summary>The library revision the controller currently holds; -1 before the first transfer.</summary>
        public int LibraryRevision => _libraryRevision;

        /// <summary>Asks the host to resend the library. Called when the snapshot's revision moves ahead.</summary>
        public void RequestLibrary() => _session.Send(MessageType.RequestLibrary);

        // ---------------------------------------------------------------- IDeckCommands

        public void LoadTrack(DeckId deck, string trackId) =>
            _session.Send(MessageType.LoadTrack, deck, Messages.Text(trackId));

        public void Eject(DeckId deck) => _session.Send(MessageType.Eject, deck);
        public void TogglePlay(DeckId deck) => _session.Send(MessageType.TogglePlay, deck);
        public void CueSet(DeckId deck) => _session.Send(MessageType.CueSet, deck);
        public void CueReturn(DeckId deck) => _session.Send(MessageType.CueReturn, deck);
        public void Seek(DeckId deck, double seconds) => _session.Send(MessageType.Seek, deck, Messages.Double(seconds));

        public void SetTempoFader(DeckId deck, float value) =>
            _session.Send(MessageType.TempoFader, deck, Messages.Float(value));

        public void SetTempoRange(DeckId deck, float percent) =>
            _session.Send(MessageType.TempoRange, deck, Messages.Float(percent));

        public void ToggleSync(DeckId deck) => _session.Send(MessageType.SyncToggle, deck, Messages.Bool(true));
        public void ToggleLoop(DeckId deck) => _session.Send(MessageType.LoopToggle, deck);

        public void SetLoopBeats(DeckId deck, float beats) =>
            _session.Send(MessageType.LoopBeats, deck, Messages.Float(beats));

        public void JogNudge(DeckId deck, float amount) =>
            _session.Send(MessageType.JogNudge, deck, Messages.Float(amount));

        public void ScratchBegin(DeckId deck) => _session.Send(MessageType.ScratchBegin, deck);

        public void ScratchUpdate(DeckId deck, float rate) =>
            _session.Send(MessageType.ScratchUpdate, deck, Messages.Float(rate));

        public void ScratchEnd(DeckId deck) => _session.Send(MessageType.ScratchEnd, deck);
        public void Brake(DeckId deck) => _session.Send(MessageType.Brake, deck);
        public void Backspin(DeckId deck) => _session.Send(MessageType.Backspin, deck);

        public void SetChannelGain(DeckId deck, float value) =>
            _session.Send(MessageType.ChannelGain, deck, Messages.Float(value));

        public void SetCrossfader(float value) =>
            _session.Send(MessageType.Crossfader, null, Messages.Float(value));

        public void SetMasterGain(float value) =>
            _session.Send(MessageType.MasterGain, null, Messages.Float(value));

        public void SetFilter(DeckId deck, float value) =>
            _session.Send(MessageType.Filter, deck, Messages.Float(value));

        public void SetMute(DeckId deck, bool muted) =>
            _session.Send(MessageType.Mute, deck, Messages.Bool(muted));

        public void SetEcho(DeckId deck, bool enabled) =>
            _session.Send(MessageType.Echo, deck, Messages.Bool(enabled));

        public void SetCueMonitor(DeckId deck, bool enabled) =>
            _session.Send(MessageType.CueMonitor, deck, Messages.Bool(enabled));

        public void StartRecording() => _session.Send(MessageType.RecordStart);
        public void StopRecording() => _session.Send(MessageType.RecordStop);
        public void AllStop() => _session.Send(MessageType.AllStop);

        public void Dispose() => _session.Dispose();
    }
}
