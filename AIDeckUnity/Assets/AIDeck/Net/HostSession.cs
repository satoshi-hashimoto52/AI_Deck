using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Platform;

namespace AIDeck.Net
{
    /// <summary>What the host's side of the link is doing.</summary>
    public enum HostSessionState
    {
        Stopped,

        /// <summary>Listening, broadcasting a beacon, no controller yet.</summary>
        Listening,

        /// <summary>A controller is connected.</summary>
        Connected,

        /// <summary>The listener could not start; see <see cref="HostSession.StatusText"/>.</summary>
        Failed
    }

    /// <summary>
    /// The host's network endpoint.
    ///
    /// Accepts **one** controller. A second is refused with an explanation rather than
    /// silently queued: two controllers driving one deck would need an arbitration rule that
    /// V1 has no reason to define (see KNOWN_LIMITATIONS.md §1.5).
    ///
    /// Threading follows the same rule as the rest of the audio path — sockets run on their
    /// own threads and nothing they receive is applied until the main thread calls
    /// <see cref="Poll"/>.
    /// </summary>
    public sealed class HostSession : IDisposable
    {
        private readonly SequenceGate _gate = new SequenceGate();
        private readonly SequenceSource _sequence = new SequenceSource();
        private readonly ConcurrentQueue<TcpClient> _pending = new ConcurrentQueue<TcpClient>();
        private readonly List<NetMessage> _tcpScratch = new List<NetMessage>();
        private readonly List<UdpDatagram> _udpScratch = new List<UdpDatagram>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private UdpLink _udp;
        private DiscoveryBroadcaster _beacon;
        private TcpLink _controller;
        private IPEndPoint _controllerFastEndpoint;

        private volatile bool _running;
        private float _sinceHeartbeatSent;
        private float _sinceHeartbeatHeard;
        private float _sinceSnapshot;

        public HostSessionState State { get; private set; } = HostSessionState.Stopped;

        /// <summary>Short user-facing status, shown in the Mac window (§5.6, FR-062).</summary>
        public string StatusText { get; private set; } = "Not started";

        public int TcpPort { get; private set; }
        public int UdpPort { get; private set; }

        /// <summary>Address of the connected controller, for display. Empty when none.</summary>
        public string ControllerAddress { get; private set; } = string.Empty;

        /// <summary>Friendly name the controller announced in its handshake.</summary>
        public string ControllerName { get; private set; } = string.Empty;

        public bool IsControllerConnected => State == HostSessionState.Connected && _controller != null;

        /// <summary>Raised on the main thread when a controller connects.</summary>
        public event Action<string> ControllerConnected;

        /// <summary>Raised on the main thread when the controller goes away, with the reason.</summary>
        public event Action<string> ControllerDisconnected;

        /// <summary>Raised on the main thread for each accepted command. Stale messages never reach it.</summary>
        public event Action<NetMessage> CommandReceived;

        /// <summary>Raised when something happened the user should be told.</summary>
        public event Action<string> Notice;

        // ---------------------------------------------------------------- lifecycle

        public bool Start(int tcpPort = ProtocolInfo.DefaultTcpPort, int udpPort = ProtocolInfo.DefaultUdpPort)
        {
            Stop();

            try
            {
                _listener = new TcpListener(IPAddress.Any, tcpPort);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
                TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _udp = new UdpLink(udpPort);
                UdpPort = _udp.BoundPort;

                _beacon = new DiscoveryBroadcaster(TcpPort, UdpPort);

                _running = true;
                _acceptThread = new Thread(AcceptLoop) { Name = "AIDeck.Host.Accept", IsBackground = true };
                _acceptThread.Start();

                State = HostSessionState.Listening;
                StatusText = "Waiting for a controller";
                return true;
            }
            catch (Exception ex)
            {
                State = HostSessionState.Failed;
                StatusText = NetDiagnostics.Describe(ex);
                Stop();
                State = HostSessionState.Failed;
                return false;
            }
        }

        public void Stop()
        {
            _running = false;

            DropController("The host stopped.", notify: false);

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            _listener = null;

            try
            {
                _acceptThread?.Join(300);
            }
            catch (Exception)
            {
                // Background thread.
            }

            _acceptThread = null;

            _beacon?.Dispose();
            _beacon = null;
            _udp?.Dispose();
            _udp = null;

            _gate.Reset();
            State = HostSessionState.Stopped;
            StatusText = "Not started";
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    _pending.Enqueue(client);
                }
                catch (SocketException)
                {
                    if (_running)
                    {
                        Thread.Sleep(20);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    if (!_running)
                    {
                        return;
                    }

                    Thread.Sleep(20);
                }
            }
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Main thread. Accepts, routes and times out. Call once per frame.</summary>
        public void Poll(float deltaSeconds)
        {
            if (!_running)
            {
                return;
            }

            _beacon?.Tick(deltaSeconds);

            AcceptPending();
            PumpTcp();
            PumpUdp();
            PumpHeartbeat(deltaSeconds);
        }

        private void AcceptPending()
        {
            while (_pending.TryDequeue(out var client))
            {
                if (_controller != null && _controller.IsConnected)
                {
                    RefuseSecondController(client);
                    continue;
                }

                _controller = new TcpLink(client);
                _controllerFastEndpoint = null;
                _sinceHeartbeatHeard = 0f;
                _gate.Reset();
                ControllerAddress = _controller.RemoteAddress;
                ControllerName = string.Empty;
                StatusText = "Controller connecting…";
            }
        }

        /// <summary>
        /// Tells a second controller why it is being turned away, then closes it. Sending the
        /// explanation is worth the extra round trip: a connection that just drops looks like a
        /// network fault, and the user would go looking in the wrong place.
        /// </summary>
        private void RefuseSecondController(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var message = new NetMessage(
                    MessageType.Error, null, _sequence.NextGlobal(), NetMessage.NowMs(),
                    Messages.Text("Another controller is already connected to this Mac."));
                var bytes = MessageCodec.Encode(message);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                // The refusal is best-effort; the close below is what matters.
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }

            Notice?.Invoke("A second controller tried to connect and was refused.");
        }

        private void PumpTcp()
        {
            var link = _controller;
            if (link == null)
            {
                return;
            }

            _tcpScratch.Clear();
            link.DrainInto(_tcpScratch);

            foreach (var message in _tcpScratch)
            {
                HandleMessage(message, reliable: true);
            }

            if (!link.IsConnected)
            {
                DropController(link.CloseReason ?? "The controller disconnected.", notify: true);
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
                // Only the connected controller may drive the decks. Without this check any
                // device on the LAN could send a crossfader packet.
                if (_controllerFastEndpoint == null ||
                    datagram.Source == null ||
                    !datagram.Source.Address.Equals(_controllerFastEndpoint.Address))
                {
                    continue;
                }

                HandleMessage(datagram.Message, reliable: false);
            }
        }

        private void HandleMessage(NetMessage message, bool reliable)
        {
            if (message == null)
            {
                return;
            }

            switch (message.Type)
            {
                case MessageType.Hello:
                    CompleteHandshake(message);
                    return;

                case MessageType.Ping:
                    _sinceHeartbeatHeard = 0f;
                    SendReliable(MessageType.Pong);
                    return;

                case MessageType.Pong:
                    _sinceHeartbeatHeard = 0f;
                    return;

                case MessageType.Bye:
                    DropController("The controller disconnected.", notify: true);
                    return;
            }

            if (State != HostSessionState.Connected)
            {
                // Anything before the handshake completes is ignored rather than acted on.
                return;
            }

            _sinceHeartbeatHeard = 0f;

            // FR-067: a superseded continuous control must not be applied late.
            if (!_gate.Accept(message))
            {
                return;
            }

            CommandReceived?.Invoke(message);
        }

        private void CompleteHandshake(NetMessage message)
        {
            var link = _controller;
            if (link == null)
            {
                return;
            }

            var hello = HelloPayload.Deserialize(message.Payload);
            ControllerName = string.IsNullOrWhiteSpace(hello.DeviceName) ? "Controller" : hello.DeviceName;

            var address = link.RemoteAddress;
            if (!string.IsNullOrEmpty(address) && hello.FastPort > 0 && hello.FastPort <= 65535 &&
                IPAddress.TryParse(address, out var parsed))
            {
                _controllerFastEndpoint = new IPEndPoint(parsed, hello.FastPort);
            }

            var ack = new HelloPayload
            {
                DeviceName = DeviceInfo.FriendlyName,
                AppVersion = DeviceInfo.AppVersion,
                FastPort = UdpPort
            }.Serialize();

            link.Send(new NetMessage(MessageType.HelloAck, null,
                _sequence.Next(MessageType.HelloAck, null), NetMessage.NowMs(), ack));

            State = HostSessionState.Connected;
            StatusText = $"Connected to {ControllerName}";
            _sinceHeartbeatHeard = 0f;
            _sinceSnapshot = ProtocolInfo.SnapshotIntervalSeconds; // send one immediately

            ControllerConnected?.Invoke(ControllerName);
        }

        private void PumpHeartbeat(float deltaSeconds)
        {
            if (State != HostSessionState.Connected)
            {
                return;
            }

            _sinceHeartbeatSent += deltaSeconds;
            if (_sinceHeartbeatSent >= ProtocolInfo.HeartbeatIntervalSeconds)
            {
                _sinceHeartbeatSent = 0f;
                SendReliable(MessageType.Ping);
            }

            _sinceHeartbeatHeard += deltaSeconds;
            if (_sinceHeartbeatHeard > ProtocolInfo.HeartbeatTimeoutSeconds)
            {
                DropController("The controller stopped responding.", notify: true);
            }
        }

        private void DropController(string reason, bool notify)
        {
            var link = _controller;
            _controller = null;
            _controllerFastEndpoint = null;

            if (link != null)
            {
                link.Dispose();
            }

            ControllerAddress = string.Empty;
            ControllerName = string.Empty;
            _gate.Reset();

            if (State == HostSessionState.Connected || link != null)
            {
                State = _running ? HostSessionState.Listening : HostSessionState.Stopped;
                StatusText = _running ? "Waiting for a controller" : "Not started";

                if (notify)
                {
                    ControllerDisconnected?.Invoke(reason);
                }
            }
        }

        // ---------------------------------------------------------------- sending

        /// <summary>Sends on the reliable channel. No-op when no controller is connected.</summary>
        public bool SendReliable(MessageType type, byte[] payload = null, DeckId? deck = null)
        {
            var link = _controller;
            if (link == null || !link.IsConnected)
            {
                return false;
            }

            return link.Send(new NetMessage(type, deck, _sequence.Next(type, deck), NetMessage.NowMs(), payload));
        }

        private bool SendFast(NetMessage message)
        {
            var endpoint = _controllerFastEndpoint;
            if (_udp == null || endpoint == null)
            {
                return false;
            }

            return _udp.SendTo(message, endpoint);
        }

        /// <summary>
        /// Broadcasts the full state on the fast channel at the interval of §4.4. A full
        /// snapshot rather than a delta: a controller that missed packets or has just
        /// reconnected is corrected by the next one, with no catch-up protocol (FR-064).
        /// </summary>
        public void TickSnapshot(float deltaSeconds, Func<StateSnapshot> snapshotFactory)
        {
            if (State != HostSessionState.Connected || snapshotFactory == null)
            {
                return;
            }

            _sinceSnapshot += deltaSeconds;
            if (_sinceSnapshot < ProtocolInfo.SnapshotIntervalSeconds)
            {
                return;
            }

            _sinceSnapshot = 0f;

            var snapshot = snapshotFactory();
            SendFast(new NetMessage(MessageType.StateSnapshot, null,
                _sequence.Next(MessageType.StateSnapshot, null), NetMessage.NowMs(), snapshot.Serialize()));
        }

        /// <summary>Sends the library in frame-sized chunks on the reliable channel.</summary>
        public void SendLibrary(IReadOnlyList<TrackInfo> tracks, int revision)
        {
            foreach (var chunk in Messages.ChunkLibrary(tracks, revision))
            {
                if (!SendReliable(MessageType.LibraryChunk, chunk.Serialize()))
                {
                    return;
                }
            }
        }

        /// <summary>Sends one track's waveform envelope in frame-sized chunks.</summary>
        public void SendWaveform(string trackId, Core.Analysis.WaveformData waveform)
        {
            foreach (var chunk in Messages.ChunkWaveform(trackId, waveform))
            {
                if (!SendReliable(MessageType.WaveformChunk, chunk.Serialize()))
                {
                    return;
                }
            }
        }

        public void SendError(string text) => SendReliable(MessageType.Error, Messages.Text(text));

        public void SendNotice(string text) => SendReliable(MessageType.Notice, Messages.Text(text));

        // ---------------------------------------------------------------- diagnostics

        public long StaleRejected => _gate.RejectedCount;
        public long Accepted => _gate.AcceptedCount;
        public long BeaconsSent => _beacon?.BeaconsSent ?? 0L;
        public int PendingSendCount => _controller?.PendingSendCount ?? 0;
        public long CoalescedCount => _controller?.CoalescedCount ?? 0L;

        public void Dispose() => Stop();
    }
}
