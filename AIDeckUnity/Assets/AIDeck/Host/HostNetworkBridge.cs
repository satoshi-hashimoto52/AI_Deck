using System;
using AIDeck.Audio;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using AIDeck.Net;
using AIDeck.Platform;
using AIDeck.UI;
using UnityEngine;

namespace AIDeck.Host
{
    /// <summary>
    /// Connects the host's network endpoint to the audio engine.
    ///
    /// Every decoded command is turned into a call on the **same** <see cref="HostCommands"/>
    /// the Mac window uses. Nothing here reimplements a control, so a rule added in
    /// `HostCommands` — SYNC refusing when the tempo is unknown, a loop refusing when it is too
    /// short — applies identically whether the control was moved on the Mac or on the iPad.
    ///
    /// The other half of its job is §9: when the controller goes, every continuous control is
    /// released and the configured disconnect policy is applied, whose default is the safe stop.
    /// </summary>
    public sealed class HostNetworkBridge : MonoBehaviour
    {
        private HostSession _session;
        private HostCommands _commands;
        private AudioEngine _engine;
        private TrackLibrary _library;
        private AppSettings _settings;
        private DiagnosticLog _log;

        private int _sentLibraryRevision = -1;

        public HostSession Session => _session;

        /// <summary>Human-readable connection state for the Mac window (§5.6, FR-062).</summary>
        public string StatusText => _session?.StatusText ?? "Networking is not running";

        public bool IsControllerConnected => _session != null && _session.IsControllerConnected;

        /// <summary>Raised when something happened the user should be told.</summary>
        public event Action<string> Notice;

        public bool Initialise(
            AudioEngine engine,
            HostCommands commands,
            TrackLibrary library,
            AppSettings settings,
            DiagnosticLog log)
        {
            _engine = engine;
            _commands = commands;
            _library = library;
            _settings = settings;
            _log = log;

            _session = new HostSession();
            _session.CommandReceived += OnCommand;
            _session.ControllerConnected += OnControllerConnected;
            _session.ControllerDisconnected += OnControllerDisconnected;
            _session.Notice += message => Notice?.Invoke(message);

            if (!_session.Start())
            {
                // Reported, not hidden: the Mac still works standalone, but the user needs to
                // know why the iPad cannot find it.
                _log?.Error("Net", _session.StatusText);
                Notice?.Invoke("Networking could not start: " + _session.StatusText);
                return false;
            }

            _log?.Info("Net", $"Listening on TCP {_session.TcpPort}, UDP {_session.UdpPort}.");
            return true;
        }

        private void Update()
        {
            if (_session == null)
            {
                return;
            }

            var delta = Time.unscaledDeltaTime;
            _session.Poll(delta);
            _session.TickSnapshot(delta, () => _engine.BuildSnapshot(_library.Revision));

            // The controller re-fetches the library only when the revision it sees moves on,
            // so the large payload goes out on change rather than on a timer.
            if (_session.IsControllerConnected && _sentLibraryRevision != _library.Revision)
            {
                _sentLibraryRevision = _library.Revision;
                _session.SendLibrary(_library.All, _library.Revision);
            }
        }

        private void OnControllerConnected(string name)
        {
            _sentLibraryRevision = _library.Revision;
            _session.SendLibrary(_library.All, _library.Revision);
            _log?.Info("Net", $"{name} connected.");
            Notice?.Invoke($"{name} connected.");
        }

        private void OnControllerDisconnected(string reason)
        {
            _sentLibraryRevision = -1;
            _log?.Info("Net", "Controller disconnected: " + reason);
            Notice?.Invoke("Controller disconnected. " + reason);

            // §9 / FR-066. Release first, unconditionally: a platter left under a gesture that
            // no longer has a finger behind it would keep scratching. Then apply the policy,
            // whose default is to stop.
            _engine.ApplyDisconnectPolicy();
        }

        /// <summary>Routes one accepted command. Stale messages never reach here (FR-067).</summary>
        private void OnCommand(NetMessage message)
        {
            var deck = message.Deck ?? DeckId.A;

            switch (message.Type)
            {
                // ---- transport ----
                case MessageType.LoadTrack:
                    _commands.LoadTrack(deck, Messages.ReadText(message));
                    break;
                case MessageType.Eject:
                    _commands.Eject(deck);
                    break;
                case MessageType.Play:
                    _engine.Deck(deck).Play();
                    break;
                case MessageType.Pause:
                    _engine.Deck(deck).Pause();
                    break;
                case MessageType.TogglePlay:
                    _commands.TogglePlay(deck);
                    break;
                case MessageType.CueSet:
                    _commands.CueSet(deck);
                    break;
                case MessageType.CueReturn:
                    _commands.CueReturn(deck);
                    break;
                case MessageType.Seek:
                    _commands.Seek(deck, Messages.ReadDouble(message));
                    break;

                // ---- tempo and sync ----
                case MessageType.TempoFader:
                    _commands.SetTempoFader(deck, Messages.ReadFloat(message));
                    break;
                case MessageType.TempoRange:
                    _commands.SetTempoRange(deck, Messages.ReadFloat(message, 8f));
                    break;
                case MessageType.SyncToggle:
                    _commands.ToggleSync(deck);
                    break;

                // ---- loop ----
                case MessageType.LoopToggle:
                    _commands.ToggleLoop(deck);
                    break;
                case MessageType.LoopBeats:
                    _commands.SetLoopBeats(deck, Messages.ReadFloat(message, 4f));
                    break;
                case MessageType.LoopSetRegion:
                {
                    Messages.ReadDoublePair(message, out var from, out var to);
                    _engine.Deck(deck).Loop.SetRegion(from, to);
                    _engine.Deck(deck).Loop.Enable();
                    break;
                }

                // ---- platter ----
                case MessageType.JogNudge:
                    _commands.JogNudge(deck, Messages.ReadFloat(message));
                    break;
                case MessageType.ScratchBegin:
                    _commands.ScratchBegin(deck);
                    break;
                case MessageType.ScratchUpdate:
                    _commands.ScratchUpdate(deck, Messages.ReadFloat(message));
                    break;
                case MessageType.ScratchMove:
                    _commands.ScratchMove(deck, Messages.ReadFloat(message));
                    break;
                case MessageType.ScratchEnd:
                    _commands.ScratchEnd(deck);
                    break;
                case MessageType.Brake:
                    _commands.Brake(deck);
                    break;
                case MessageType.Backspin:
                    _commands.Backspin(deck);
                    break;

                // ---- mixer ----
                case MessageType.ChannelGain:
                    _commands.SetChannelGain(deck, Messages.ReadFloat(message));
                    break;
                case MessageType.Crossfader:
                    _commands.SetCrossfader(Messages.ReadFloat(message));
                    break;
                case MessageType.MasterGain:
                    _commands.SetMasterGain(Messages.ReadFloat(message));
                    break;
                case MessageType.Filter:
                    _commands.SetFilter(deck, Messages.ReadFloat(message));
                    break;
                case MessageType.Mute:
                    _commands.SetMute(deck, Messages.ReadBool(message));
                    break;
                case MessageType.Echo:
                    _commands.SetEcho(deck, Messages.ReadBool(message));
                    break;
                case MessageType.CueMonitor:
                    _commands.SetCueMonitor(deck, Messages.ReadBool(message));
                    break;
                case MessageType.CrossfaderCurve:
                    _engine.Mixer.CrossfaderCurveType = ToCurve(Messages.ReadByte(message));
                    break;

                // ---- recording ----
                case MessageType.RecordStart:
                    _commands.StartRecording();
                    break;
                case MessageType.RecordStop:
                    _commands.StopRecording();
                    break;

                // ---- queries and safety ----
                case MessageType.RequestLibrary:
                    _sentLibraryRevision = _library.Revision;
                    _session.SendLibrary(_library.All, _library.Revision);
                    break;
                case MessageType.RequestWaveform:
                {
                    var trackId = Messages.ReadText(message);
                    var waveform = WaveformCache.TryLoad(trackId);
                    _session.SendWaveform(trackId, waveform ?? Core.Analysis.WaveformData.Empty);
                    break;
                }

                case MessageType.RequestSnapshot:
                    // The periodic broadcast will carry it within 50 ms; nothing special to do.
                    break;

                case MessageType.AllStop:
                    _commands.AllStop();
                    Notice?.Invoke("All decks stopped from the controller.");
                    break;

                case MessageType.Unknown:
                    // A newer controller sent something this build does not know. Ignoring it is
                    // the compatible behaviour: unknown types are additive (NETWORK_PROTOCOL §3).
                    break;
            }
        }

        private static Core.Mixer.CrossfaderCurveType ToCurve(byte value) =>
            value <= (byte)Core.Mixer.CrossfaderCurveType.Sharp
                ? (Core.Mixer.CrossfaderCurveType)value
                : Core.Mixer.CrossfaderCurveType.Smooth;

        /// <summary>Sends a short message for the controller to show.</summary>
        public void SendNotice(string text) => _session?.SendNotice(text);

        private void OnDestroy() => Shutdown();

        private void OnApplicationQuit() => Shutdown();

        public void Shutdown()
        {
            if (_session == null)
            {
                return;
            }

            _session.Dispose();
            _session = null;
        }
    }
}
