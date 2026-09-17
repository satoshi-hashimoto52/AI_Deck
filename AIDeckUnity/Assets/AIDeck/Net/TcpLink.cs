using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using AIDeck.Core.Net;

namespace AIDeck.Net
{
    /// <summary>
    /// One connected TCP peer: the reliable channel.
    ///
    /// Receive and send each run on their own thread; nothing here touches Unity. Decoded
    /// frames land in a queue that the main thread drains with <see cref="DrainInto"/>, so
    /// application state is only ever mutated on the main thread. Raising events straight from
    /// the socket thread would put every handler in the app on a background thread, which for
    /// a Unity project is a rule that gets broken silently and found much later.
    /// </summary>
    public sealed class TcpLink : IDisposable
    {
        /// <summary>Receive buffer. One read comfortably holds several frames.</summary>
        private const int ReceiveBufferSize = 16 * 1024;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly FrameReader _reader = new FrameReader();
        private readonly ConcurrentQueue<NetMessage> _inbound = new ConcurrentQueue<NetMessage>();
        private readonly OutboundQueue _outbound;
        private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);
        private readonly Thread _receiveThread;
        private readonly Thread _sendThread;

        private volatile bool _running = true;
        private int _closedReported;

        public TcpLink(TcpClient client, int outboundCapacity = 512)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _client.NoDelay = true; // a DJ command must not wait for Nagle to fill a segment
            _stream = _client.GetStream();
            _outbound = new OutboundQueue(outboundCapacity);

            _receiveThread = new Thread(ReceiveLoop) { Name = "AIDeck.Tcp.Receive", IsBackground = true };
            _sendThread = new Thread(SendLoop) { Name = "AIDeck.Tcp.Send", IsBackground = true };
            _receiveThread.Start();
            _sendThread.Start();
        }

        public bool IsConnected => _running && _client.Connected;

        /// <summary>Set once the link has closed, with a short user-facing reason.</summary>
        public string CloseReason { get; private set; }

        /// <summary>The peer's remote address, for display. Never logged with a port or a name.</summary>
        public string RemoteAddress
        {
            get
            {
                try
                {
                    return _client.Client?.RemoteEndPoint is System.Net.IPEndPoint endpoint
                        ? endpoint.Address.ToString()
                        : string.Empty;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        public long CoalescedCount => _outbound.CoalescedCount;
        public long DroppedCount => _outbound.DroppedCount;
        public int PendingSendCount => _outbound.Count;
        public long ResyncCount => _reader.ResyncCount;
        public long RejectedCount => _reader.RejectedCount;
        public ushort LastUnsupportedVersion => _reader.LastUnsupportedVersion;

        /// <summary>
        /// Queues a message. Returns false when the reliable backlog is full, which the caller
        /// must treat as a dead link rather than ignore (NFR-004).
        /// </summary>
        public bool Send(NetMessage message)
        {
            if (!_running)
            {
                return false;
            }

            if (!_outbound.Enqueue(message))
            {
                Close("The connection could not keep up and was closed.");
                return false;
            }

            _sendSignal.Set();
            return true;
        }

        /// <summary>Main thread. Moves everything received since the last call into the list.</summary>
        public int DrainInto(List<NetMessage> destination)
        {
            if (destination == null)
            {
                return 0;
            }

            var count = 0;
            while (_inbound.TryDequeue(out var message))
            {
                destination.Add(message);
                count++;
            }

            return count;
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[ReceiveBufferSize];
            var frames = new List<NetMessage>();

            try
            {
                while (_running)
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        Close("The other device closed the connection.");
                        return;
                    }

                    frames.Clear();
                    if (!_reader.Append(buffer, 0, read, frames))
                    {
                        Close(_reader.FatalError ?? "The connection sent unreadable data.");
                        return;
                    }

                    foreach (var frame in frames)
                    {
                        _inbound.Enqueue(frame);
                    }

                    if (_reader.LastUnsupportedVersion != 0)
                    {
                        // FR-068: an incompatible peer is refused outright rather than allowed
                        // to half-work.
                        Close($"The other device speaks AI Deck protocol v{_reader.LastUnsupportedVersion}, " +
                              $"this one speaks v{ProtocolInfo.Version}.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Close(NetDiagnostics.Describe(ex));
            }
        }

        private void SendLoop()
        {
            var batch = new List<NetMessage>(32);

            try
            {
                while (_running)
                {
                    // Waking on a signal rather than polling keeps an idle link at zero cost,
                    // and the timeout means a shutdown is never missed.
                    _sendSignal.WaitOne(50);

                    batch.Clear();
                    _outbound.DrainTo(batch, 64);

                    foreach (var message in batch)
                    {
                        if (!_running)
                        {
                            return;
                        }

                        var bytes = MessageCodec.Encode(message);
                        _stream.Write(bytes, 0, bytes.Length);
                    }

                    if (batch.Count > 0)
                    {
                        _stream.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Close(NetDiagnostics.Describe(ex));
            }
        }

        /// <summary>Closes the link once, keeping the first reason.</summary>
        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closedReported, 1) != 0)
            {
                return;
            }

            CloseReason = reason ?? "The connection closed.";
            _running = false;
            _sendSignal.Set();

            try
            {
                _stream?.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }

            try
            {
                _client?.Close();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            Close("The connection was closed.");

            try
            {
                _receiveThread?.Join(500);
                _sendThread?.Join(500);
            }
            catch (Exception)
            {
                // Background threads; the process can exit regardless.
            }

            _sendSignal.Dispose();
        }
    }
}
