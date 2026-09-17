using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Platform;

namespace AIDeck.Net
{
    /// <summary>What the controller's side of the link is doing (FR-062).</summary>
    public enum ControllerSessionState
    {
        Idle,
        Searching,
        Connecting,
        Connected,
        Reconnecting,
        Failed
    }

    /// <summary>
    /// The controller's network endpoint: discovery, handshake, heartbeat and reconnection.
    ///
    /// On reconnection it re-runs the handshake and takes the host's state wholesale. Buffered
    /// intents are deliberately dropped: a command composed before the link dropped was formed
    /// against a state that no longer applies, and firing it on reconnect is the last thing a
    /// DJ wants (§5 of NETWORK_PROTOCOL.md).
    /// </summary>
    public sealed class ControllerSession : IDisposable
    {
        /// <summary>Delay between reconnection attempts. Short enough to feel automatic.</summary>
        public const float ReconnectIntervalSeconds = 2f;

        /// <summary>How long a connection attempt may take before it is abandoned.</summary>
        public const int ConnectTimeoutMilliseconds = 4000;

        private readonly SequenceSource _sequence = new SequenceSource();
        private readonly SequenceGate _gate = new SequenceGate();
        private readonly List<NetMessage> _tcpScratch = new List<NetMessage>();
        private readonly List<UdpDatagram> _udpScratch = new List<UdpDatagram>();

        private UdpLink _udp;
        private DiscoveryListener _discovery;
        private TcpLink _link;
        private IPEndPoint _hostFastEndpoint;
        private Thread _connectThread;
        private volatile bool _connectInFlight;
        private volatile TcpClient _connected;
        private volatile string _connectError;

        private string _address = string.Empty;
        private int _port = ProtocolInfo.DefaultTcpPort;
        private bool _wantConnection;
        private float _sinceHeartbeatSent;
        private float _sinceHeartbeatHeard;
        private float _sinceReconnect;
        private float _sinceSnapshot;
        private bool _reportedConnected;

        /// <summary>How long without a state snapshot before the user is told updates have stopped.</summary>
        public const float StaleSnapshotSeconds = 3f;

        public ControllerSessionState State { get; private set; } = ControllerSessionState.Idle;

        /// <summary>Short user-facing status (FR-062).</summary>
        public string StatusText { get; private set; } = "Not connected";

        public string HostName { get; private set; } = string.Empty;

        public string HostAddress => _address;

        public bool IsConnected => State == ControllerSessionState.Connected;

        /// <summary>Raised on the main thread once the handshake completes.</summary>
        public event Action Connected;

        /// <summary>Raised on the main thread when the link goes, with the reason.</summary>
        public event Action<string> Disconnected;

        /// <summary>Raised for each state snapshot (FR-064).</summary>
        public event Action<StateSnapshot> SnapshotReceived;

        public event Action<LibraryChunkPayload> LibraryChunkReceived;

        public event Action<WaveformChunkPayload> WaveformChunkReceived;

        /// <summary>Raised for a host error message; the text is meant for the user.</summary>
        public event Action<string> ErrorReceived;

        public event Action<string> NoticeReceived;

        // ---------------------------------------------------------------- lifecycle

        /// <summary>Starts listening for host beacons (FR-060).</summary>
        public void StartDiscovery()
        {
            if (_discovery != null)
            {
                return;
            }

            try
            {
                _discovery = new DiscoveryListener();
                if (State == ControllerSessionState.Idle)
                {
                    State = ControllerSessionState.Searching;
                    StatusText = "Looking for a Mac on this network…";
                }
            }
            catch (Exception ex)
            {
                // Discovery is a convenience; manual entry still works, so this is reported
                // rather than treated as fatal.
                StatusText = "Automatic discovery is unavailable: " + NetDiagnostics.Describe(ex);
            }
        }

        public int DiscoveredHostCount => _discovery?.HostCount ?? 0;

        public void CopyDiscoveredHosts<T>(List<T> destination, Func<string, string, int, T> factory) =>
            _discovery?.CopyHostsInto(destination, factory);

        /// <summary>Connects to a host (FR-061). Replaces any connection in progress.</summary>
        public void Connect(string address, int port)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                State = ControllerSessionState.Failed;
                StatusText = "Enter the address shown on the Mac.";
                return;
            }

            Close("Connecting to another Mac.", notify: false);

            _address = address.Trim();
            _port = port > 0 && port <= 65535 ? port : ProtocolInfo.DefaultTcpPort;
            _wantConnection = true;
            _sinceReconnect = 0f;

            BeginConnect();
        }

        /// <summary>Drops the link and stops trying to reconnect.</summary>
        public void Disconnect()
        {
            _wantConnection = false;

            var link = _link;
            if (link != null && link.IsConnected)
            {
                link.Send(new NetMessage(MessageType.Bye, null,
                    _sequence.Next(MessageType.Bye, null), NetMessage.NowMs()));
            }

            Close("Disconnected.", notify: true);
            State = _discovery != null ? ControllerSessionState.Searching : ControllerSessionState.Idle;
            StatusText = _discovery != null ? "Looking for a Mac on this network…" : "Not connected";
        }

        private void BeginConnect()
        {
            if (_connectInFlight)
            {
                return;
            }

            State = State == ControllerSessionState.Reconnecting
                ? ControllerSessionState.Reconnecting
                : ControllerSessionState.Connecting;
            StatusText = State == ControllerSessionState.Reconnecting
                ? $"Reconnecting to {_address}…"
                : $"Connecting to {_address}…";

            _connectInFlight = true;
            _connected = null;
            _connectError = null;

            var address = _address;
            var port = _port;

            _connectThread = new Thread(() => ConnectWorker(address, port))
            {
                Name = "AIDeck.Controller.Connect",
                IsBackground = true
            };
            _connectThread.Start();
        }

        /// <summary>
        /// Connects off the main thread. <see cref="TcpClient.Connect(string,int)"/> blocks for
        /// the system timeout — tens of seconds on an unreachable address — which would freeze
        /// the control surface (NFR-002).
        /// </summary>
        private void ConnectWorker(string address, int port)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                var async = client.BeginConnect(address, port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds))
                {
                    client.Close();
                    _connectError = "The Mac did not answer. Check the address and that AI Deck is running.";
                    return;
                }

                client.EndConnect(async);
                _connected = client;
            }
            catch (Exception ex)
            {
                try
                {
                    client?.Close();
                }
                catch (Exception)
                {
                    // Already gone.
                }

                _connectError = NetDiagnostics.Describe(ex);
            }
            finally
            {
                _connectInFlight = false;
            }
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Main thread. Call once per frame.</summary>
        public void Poll(float deltaSeconds)
        {
            _discovery?.Tick(deltaSeconds);

            CompletePendingConnect();
            PumpTcp();
            PumpUdp();
            PumpHeartbeat(deltaSeconds);
            PumpReconnect(deltaSeconds);
        }

        private void CompletePendingConnect()
        {
            var client = _connected;
            if (client != null)
            {
                _connected = null;
                StartHandshake(client);
                return;
            }

            var error = _connectError;
            if (error == null || _connectInFlight)
            {
                return;
            }

            _connectError = null;

            if (_wantConnection)
            {
                State = ControllerSessionState.Reconnecting;
                StatusText = error;
            }
            else
            {
                State = ControllerSessionState.Failed;
                StatusText = error;
            }
        }

        private void StartHandshake(TcpClient client)
        {
            try
            {
                _udp ??= new UdpLink(0);
                _link = new TcpLink(client);

                var hello = new HelloPayload
                {
                    DeviceName = DeviceInfo.FriendlyName,
                    AppVersion = DeviceInfo.AppVersion,
                    FastPort = _udp.BoundPort
                }.Serialize();

                _link.Send(new NetMessage(MessageType.Hello, null,
                    _sequence.Next(MessageType.Hello, null), NetMessage.NowMs(), hello));

                StatusText = $"Connected to {_address}, waiting for the Mac…";
                _sinceHeartbeatHeard = 0f;
            }
            catch (Exception ex)
            {
                State = ControllerSessionState.Reconnecting;
                StatusText = NetDiagnostics.Describe(ex);
            }
        }

        private void PumpTcp()
        {
            var link = _link;
            if (link == null)
            {
                return;
            }

            _tcpScratch.Clear();
            link.DrainInto(_tcpScratch);

            foreach (var message in _tcpScratch)
            {
                HandleMessage(message);
            }

            if (!link.IsConnected)
            {
                var reason = link.CloseReason ?? "The connection to the Mac was lost.";
                Close(reason, notify: true);

                if (_wantConnection)
                {
                    State = ControllerSessionState.Reconnecting;
                    StatusText = reason;
                    _sinceReconnect = 0f;
                }
            }
        }

        private void PumpUdp()
        {
            if (_udp == null)
            {
                return;
            }

            _udpScratch.Clear();
            _udp.DrainInto(_udpScratch);

            foreach (var datagram in _udpScratch)
            {
                if (_hostFastEndpoint != null && datagram.Source != null &&
                    !datagram.Source.Address.Equals(_hostFastEndpoint.Address))
                {
                    continue;
                }

                HandleMessage(datagram.Message);
            }
        }

        private void HandleMessage(NetMessage message)
        {
            if (message == null)
            {
                return;
            }

            switch (message.Type)
            {
                case MessageType.HelloAck:
                    CompleteHandshake(message);
                    return;

                case MessageType.Ping:
                    _sinceHeartbeatHeard = 0f;
                    SendReliable(MessageType.Pong);
                    return;

                case MessageType.Pong:
                    _sinceHeartbeatHeard = 0f;
                    return;

                case MessageType.StateSnapshot:
                    _sinceHeartbeatHeard = 0f;
                    _sinceSnapshot = 0f;
                    // Snapshots supersede one another, so an out-of-order datagram is dropped.
                    if (_gate.Accept(message))
                    {
                        SnapshotReceived?.Invoke(StateSnapshot.Deserialize(message.Payload));
                    }

                    return;

                case MessageType.LibraryChunk:
                    LibraryChunkReceived?.Invoke(LibraryChunkPayload.Deserialize(message.Payload));
                    return;

                case MessageType.WaveformChunk:
                    WaveformChunkReceived?.Invoke(WaveformChunkPayload.Deserialize(message.Payload));
                    return;

                case MessageType.Error:
                {
                    var text = Messages.ReadText(message, "The Mac reported an error.");
                    ErrorReceived?.Invoke(text);

                    // An error on the reliable channel before the handshake completes means the
                    // host refused us — another controller, or a protocol mismatch. Retrying
                    // would just be refused again.
                    if (State != ControllerSessionState.Connected)
                    {
                        _wantConnection = false;
                        Close(text, notify: false);
                        State = ControllerSessionState.Failed;
                        StatusText = text;
                    }

                    return;
                }

                case MessageType.Notice:
                    NoticeReceived?.Invoke(Messages.ReadText(message));
                    return;

                case MessageType.Bye:
                    Close("The Mac closed the connection.", notify: true);
                    State = ControllerSessionState.Failed;
                    StatusText = "The Mac closed the connection.";
                    _wantConnection = false;
                    return;
            }
        }

        private void CompleteHandshake(NetMessage message)
        {
            var ack = HelloPayload.Deserialize(message.Payload);
            HostName = string.IsNullOrWhiteSpace(ack.DeviceName) ? "Mac" : ack.DeviceName;

            if (IPAddress.TryParse(_address, out var parsed) && ack.FastPort > 0 && ack.FastPort <= 65535)
            {
                _hostFastEndpoint = new IPEndPoint(parsed, ack.FastPort);
            }

            _gate.Reset();
            State = ControllerSessionState.Connected;
            StatusText = $"Connected to {HostName}";
            _sinceHeartbeatHeard = 0f;
            _sinceSnapshot = 0f;
            _reportedConnected = true;

            // Take the host's world wholesale. Nothing from before the connection is replayed.
            SendReliable(MessageType.RequestLibrary);
            SendReliable(MessageType.RequestSnapshot);

            Connected?.Invoke();
        }

        private void PumpHeartbeat(float deltaSeconds)
        {
            if (State != ControllerSessionState.Connected)
            {
                return;
            }

            _sinceHeartbeatSent += deltaSeconds;
            if (_sinceHeartbeatSent >= ProtocolInfo.HeartbeatIntervalSeconds)
            {
                _sinceHeartbeatSent = 0f;
                SendReliable(MessageType.Ping);
            }

            // The link is alive (heartbeats are reliable) but no state is arriving, which means
            // the fast channel is being dropped somewhere. Saying so is far better than
            // silently showing state that stopped updating.
            _sinceSnapshot += deltaSeconds;
            if (_sinceSnapshot > StaleSnapshotSeconds)
            {
                StatusText = $"Connected to {HostName}, but not receiving updates. " +
                             "Something on this network is blocking them.";
            }
            else if (!_reportedConnected)
            {
                _reportedConnected = true;
                StatusText = $"Connected to {HostName}";
            }

            _sinceHeartbeatHeard += deltaSeconds;
            if (_sinceHeartbeatHeard > ProtocolInfo.HeartbeatTimeoutSeconds)
            {
                Close("The Mac stopped responding.", notify: true);
                State = ControllerSessionState.Reconnecting;
                StatusText = "The Mac stopped responding. Reconnecting…";
                _sinceReconnect = 0f;
            }
        }

        private void PumpReconnect(float deltaSeconds)
        {
            if (!_wantConnection || State == ControllerSessionState.Connected ||
                State == ControllerSessionState.Connecting || _connectInFlight)
            {
                return;
            }

            if (State != ControllerSessionState.Reconnecting)
            {
                return;
            }

            _sinceReconnect += deltaSeconds;
            if (_sinceReconnect < ReconnectIntervalSeconds)
            {
                return;
            }

            _sinceReconnect = 0f;
            BeginConnect();
        }

        // ---------------------------------------------------------------- sending

        /// <summary>
        /// Sends a control intent on whichever channel its type belongs to. Anything sent while
        /// disconnected is dropped, not queued.
        /// </summary>
        public bool Send(MessageType type, DeckId? deck = null, byte[] payload = null)
        {
            if (State != ControllerSessionState.Connected)
            {
                return false;
            }

            var message = new NetMessage(type, deck, _sequence.Next(type, deck), NetMessage.NowMs(), payload);
            return type.Channel() == MessageChannel.Fast ? SendFast(message) : SendReliableMessage(message);
        }

        private bool SendReliable(MessageType type, byte[] payload = null) =>
            SendReliableMessage(new NetMessage(type, null, _sequence.Next(type, null), NetMessage.NowMs(), payload));

        private bool SendReliableMessage(NetMessage message)
        {
            var link = _link;
            return link != null && link.IsConnected && link.Send(message);
        }

        private bool SendFast(NetMessage message)
        {
            if (_udp == null || _hostFastEndpoint == null)
            {
                // Before the handshake names the host's fast port there is nowhere to send;
                // the reliable channel carries anything that matters at that point.
                return SendReliableMessage(message);
            }

            return _udp.SendTo(message, _hostFastEndpoint);
        }

        // ---------------------------------------------------------------- teardown

        private void Close(string reason, bool notify)
        {
            var link = _link;
            _link = null;
            _hostFastEndpoint = null;
            _reportedConnected = false;

            if (link != null)
            {
                link.Dispose();
                if (notify)
                {
                    Disconnected?.Invoke(reason);
                }
            }

            _gate.Reset();
        }

        public void Dispose()
        {
            _wantConnection = false;
            Close("The controller shut down.", notify: false);

            _discovery?.Dispose();
            _discovery = null;
            _udp?.Dispose();
            _udp = null;

            State = ControllerSessionState.Idle;
        }

        // ---------------------------------------------------------------- diagnostics

        public long StaleRejected => _gate.RejectedCount;
        public int PendingSendCount => _link?.PendingSendCount ?? 0;
        public long CoalescedCount => _link?.CoalescedCount ?? 0L;
    }
}
