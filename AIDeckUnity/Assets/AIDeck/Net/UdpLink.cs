using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AIDeck.Core.Net;

namespace AIDeck.Net
{
    /// <summary>One datagram and where it came from.</summary>
    public readonly struct UdpDatagram
    {
        public UdpDatagram(NetMessage message, IPEndPoint source)
        {
            Message = message;
            Source = source;
        }

        public NetMessage Message { get; }
        public IPEndPoint Source { get; }
    }

    /// <summary>
    /// The fast channel: a bound UDP socket.
    ///
    /// Continuous controls and state snapshots travel here. A datagram is one frame, so there
    /// is no framing to do — anything that does not decode cleanly is counted and dropped,
    /// because on this channel a damaged packet is simply a packet that did not arrive, and
    /// the next one is along in a few milliseconds.
    ///
    /// Sends happen inline. A UDP send to a local network does not block meaningfully, and
    /// queuing it behind a thread would add the latency this channel exists to avoid.
    /// </summary>
    public sealed class UdpLink : IDisposable
    {
        private readonly Socket _socket;
        private readonly ConcurrentQueue<UdpDatagram> _inbound = new ConcurrentQueue<UdpDatagram>();
        private readonly Thread _receiveThread;
        private volatile bool _running = true;

        public UdpLink(int port, bool enableBroadcast = false)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                EnableBroadcast = enableBroadcast
            };

            // Several AI Deck processes may share a machine during development, and a port left
            // in TIME_WAIT must not stop a restart.
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            try
            {
                _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (SocketException)
            {
                // Port 0 lets the system choose: used by the controller, which only needs to be
                // reachable at whatever port it announces in the handshake.
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            }

            BoundPort = (_socket.LocalEndPoint as IPEndPoint)?.Port ?? port;

            _receiveThread = new Thread(ReceiveLoop) { Name = "AIDeck.Udp.Receive", IsBackground = true };
            _receiveThread.Start();
        }

        public int BoundPort { get; }

        /// <summary>Datagrams that would not decode. Non-zero is worth noticing but not fatal.</summary>
        public long MalformedCount { get; private set; }

        public long SentCount { get; private set; }
        public long ReceivedCount { get; private set; }

        /// <summary>Sends one frame. Failures are counted, never thrown: this channel is lossy by design.</summary>
        public bool SendTo(NetMessage message, IPEndPoint destination)
        {
            if (!_running || message == null || destination == null)
            {
                return false;
            }

            try
            {
                var bytes = MessageCodec.Encode(message);
                _socket.SendTo(bytes, destination);
                SentCount++;
                return true;
            }
            catch (Exception)
            {
                // A lost datagram on the fast channel is what the design already tolerates; the
                // heartbeat is what notices a link that has actually gone.
                return false;
            }
        }

        /// <summary>Main thread. Moves everything received since the last call into the list.</summary>
        public int DrainInto(List<UdpDatagram> destination)
        {
            if (destination == null)
            {
                return 0;
            }

            var count = 0;
            while (_inbound.TryDequeue(out var datagram))
            {
                destination.Add(datagram);
                count++;
            }

            return count;
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[ProtocolInfo.MaxFrameSize];

            while (_running)
            {
                try
                {
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    var read = _socket.ReceiveFrom(buffer, ref source);
                    if (read <= 0)
                    {
                        continue;
                    }

                    var result = MessageCodec.Decode(buffer, 0, read);
                    if (!result.Success)
                    {
                        MalformedCount++;
                        continue;
                    }

                    ReceivedCount++;
                    _inbound.Enqueue(new UdpDatagram(result.Message, source as IPEndPoint));
                }
                catch (SocketException)
                {
                    if (!_running)
                    {
                        return;
                    }

                    // A transient error (an ICMP port-unreachable from a peer that went away)
                    // must not kill the receive loop.
                    Thread.Sleep(5);
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

                    Thread.Sleep(5);
                }
            }
        }

        public void Dispose()
        {
            _running = false;

            try
            {
                _socket?.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }

            try
            {
                _receiveThread?.Join(300);
            }
            catch (Exception)
            {
                // Background thread.
            }
        }
    }
}
