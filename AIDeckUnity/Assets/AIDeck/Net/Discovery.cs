using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AIDeck.Core.Net;
using AIDeck.Platform;

namespace AIDeck.Net
{
    /// <summary>
    /// Announces the host on the LAN once a second (FR-060).
    ///
    /// Broadcast rather than multicast or Bonjour: broadcast needs no registration, no
    /// service daemon and no extra entitlement, and on a home or studio network it reaches
    /// every device. Where it does not — guest Wi-Fi, client isolation — nothing would, which
    /// is exactly why manual address entry is a first-class path rather than a fallback.
    ///
    /// The beacon is sent to the broadcast address of every up interface, not only to
    /// 255.255.255.255, because a Mac on both Wi-Fi and Ethernet otherwise announces itself on
    /// only one of them.
    /// </summary>
    public sealed class DiscoveryBroadcaster : IDisposable
    {
        private readonly UdpLink _link;
        private readonly SequenceSource _sequence = new SequenceSource();
        private readonly List<IPEndPoint> _targets = new List<IPEndPoint>();
        private readonly int _tcpPort;
        private readonly int _udpPort;
        private float _timer;
        private float _refreshTimer;

        /// <summary>How often the interface list is re-examined; Wi-Fi comes and goes.</summary>
        private const float InterfaceRefreshSeconds = 10f;

        public DiscoveryBroadcaster(int tcpPort, int udpPort, int discoveryPort = ProtocolInfo.DiscoveryPort)
        {
            _tcpPort = tcpPort;
            _udpPort = udpPort;
            DiscoveryPort = discoveryPort;

            // Bound to port 0: the broadcaster only sends.
            _link = new UdpLink(0, enableBroadcast: true);
            RefreshTargets();
        }

        public int DiscoveryPort { get; }

        public long BeaconsSent { get; private set; }

        /// <summary>Advances the beacon timer. Call once per frame.</summary>
        public void Tick(float deltaSeconds)
        {
            _refreshTimer += deltaSeconds;
            if (_refreshTimer >= InterfaceRefreshSeconds)
            {
                _refreshTimer = 0f;
                RefreshTargets();
            }

            _timer += deltaSeconds;
            if (_timer < ProtocolInfo.DiscoveryIntervalSeconds)
            {
                return;
            }

            _timer = 0f;
            Broadcast();
        }

        public void Broadcast()
        {
            var payload = new DiscoveryPayload
            {
                ServiceName = ProtocolInfo.ServiceName,
                HostName = DeviceInfo.FriendlyName,
                TcpPort = _tcpPort,
                UdpPort = _udpPort
            }.Serialize();

            var message = new NetMessage(
                MessageType.Discovery, null,
                _sequence.Next(MessageType.Discovery, null),
                NetMessage.NowMs(), payload);

            foreach (var target in _targets)
            {
                _link.SendTo(message, target);
            }

            BeaconsSent++;
        }

        private void RefreshTargets()
        {
            _targets.Clear();
            _targets.Add(new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork ||
                            info.IPv4Mask == null)
                        {
                            continue;
                        }

                        var broadcast = DirectedBroadcast(info.Address, info.IPv4Mask);
                        if (broadcast != null)
                        {
                            _targets.Add(new IPEndPoint(broadcast, DiscoveryPort));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // The global broadcast address is already in the list, so a failure here only
                // costs multi-homed coverage.
            }
        }

        /// <summary>The directed broadcast address for an interface, e.g. 192.168.1.255.</summary>
        private static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
        {
            try
            {
                var addressBytes = address.GetAddressBytes();
                var maskBytes = mask.GetAddressBytes();
                if (addressBytes.Length != 4 || maskBytes.Length != 4)
                {
                    return null;
                }

                var result = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    result[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
                }

                return new IPAddress(result);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose() => _link.Dispose();
    }

    /// <summary>
    /// Listens for host beacons (FR-060).
    ///
    /// A host is forgotten after <see cref="ForgetAfterSeconds"/> without a beacon, so the list
    /// shows what is reachable now rather than everything ever seen. Beacons whose service name
    /// is not AI Deck's are ignored, so an unrelated broadcast on the same port cannot appear
    /// as a host.
    /// </summary>
    public sealed class DiscoveryListener : IDisposable
    {
        /// <summary>Three missed beacons, matching the heartbeat's tolerance.</summary>
        public const float ForgetAfterSeconds = 4f;

        private readonly UdpLink _link;
        private readonly List<UdpDatagram> _scratch = new List<UdpDatagram>();
        private readonly Dictionary<string, Entry> _hosts = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private struct Entry
        {
            public string Name;
            public int TcpPort;
            public float Age;
        }

        public DiscoveryListener(int discoveryPort = ProtocolInfo.DiscoveryPort)
        {
            _link = new UdpLink(discoveryPort);
        }

        public long BeaconsHeard { get; private set; }

        /// <summary>Advances ageing and consumes any beacons received. Call once per frame.</summary>
        public void Tick(float deltaSeconds)
        {
            _scratch.Clear();
            _link.DrainInto(_scratch);

            foreach (var datagram in _scratch)
            {
                if (datagram.Message.Type != MessageType.Discovery || datagram.Source == null)
                {
                    continue;
                }

                var payload = DiscoveryPayload.Deserialize(datagram.Message.Payload);
                if (!payload.IsAiDeck)
                {
                    continue;
                }

                var address = datagram.Source.Address.ToString();
                _hosts[address] = new Entry
                {
                    Name = payload.HostName,
                    TcpPort = payload.TcpPort > 0 ? payload.TcpPort : ProtocolInfo.DefaultTcpPort,
                    Age = 0f
                };

                BeaconsHeard++;
            }

            if (_hosts.Count == 0)
            {
                return;
            }

            _expired.Clear();
            var keys = new List<string>(_hosts.Keys);
            foreach (var key in keys)
            {
                var entry = _hosts[key];
                entry.Age += deltaSeconds;
                if (entry.Age > ForgetAfterSeconds)
                {
                    _expired.Add(key);
                }
                else
                {
                    _hosts[key] = entry;
                }
            }

            foreach (var key in _expired)
            {
                _hosts.Remove(key);
            }
        }

        private readonly List<string> _expired = new List<string>();

        /// <summary>Fills <paramref name="destination"/> with the hosts currently being heard.</summary>
        public void CopyHostsInto<T>(List<T> destination, Func<string, string, int, T> factory)
        {
            if (destination == null || factory == null)
            {
                return;
            }

            destination.Clear();
            foreach (var pair in _hosts)
            {
                destination.Add(factory(pair.Value.Name, pair.Key, pair.Value.TcpPort));
            }
        }

        public int HostCount => _hosts.Count;

        public void Clear() => _hosts.Clear();

        public void Dispose() => _link.Dispose();
    }
}
