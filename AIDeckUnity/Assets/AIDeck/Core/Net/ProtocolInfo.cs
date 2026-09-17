namespace AIDeck.Core.Net
{
    /// <summary>Protocol-wide constants shared by both ends.</summary>
    public static class ProtocolInfo
    {
        /// <summary>
        /// Wire protocol version. Bump on any incompatible change to the header, to a
        /// message's payload layout, or to an enum value. FR-068 requires that a mismatched
        /// peer is rejected explicitly rather than allowed to misinterpret packets.
        /// </summary>
        public const ushort Version = 1;

        /// <summary>The oldest version this build can still talk to.</summary>
        public const ushort MinimumSupportedVersion = 1;

        /// <summary>First byte of every frame: 'A'.</summary>
        public const byte Magic0 = 0x41;

        /// <summary>Second byte of every frame: 'D'.</summary>
        public const byte Magic1 = 0x44;

        /// <summary>Fixed header size in bytes.</summary>
        public const int HeaderSize = 22;

        /// <summary>Largest payload a single frame may carry.</summary>
        public const int MaxPayloadSize = 60000;

        /// <summary>Largest complete frame.</summary>
        public const int MaxFrameSize = HeaderSize + MaxPayloadSize;

        /// <summary>Deck byte used by messages that are not deck-scoped.</summary>
        public const byte NoDeck = 0xFF;

        /// <summary>TCP port the host listens on for the reliable channel.</summary>
        public const int DefaultTcpPort = 47810;

        /// <summary>UDP port the host listens on for the fast channel.</summary>
        public const int DefaultUdpPort = 47811;

        /// <summary>UDP port used for the discovery beacon (FR-060).</summary>
        public const int DiscoveryPort = 47812;

        /// <summary>Interval between discovery beacons.</summary>
        public const float DiscoveryIntervalSeconds = 1f;

        /// <summary>Interval between heartbeats on an established session.</summary>
        public const float HeartbeatIntervalSeconds = 1f;

        /// <summary>
        /// Silence after which a peer is considered gone. Three missed heartbeats — long
        /// enough to ride out a Wi-Fi hiccup, short enough that the safety stop still feels
        /// immediate.
        /// </summary>
        public const float HeartbeatTimeoutSeconds = 3f;

        /// <summary>Rate at which the host broadcasts state snapshots (§4.4).</summary>
        public const float SnapshotIntervalSeconds = 0.05f;

        /// <summary>Human-readable name of the service, used in the discovery beacon.</summary>
        public const string ServiceName = "AIDeck";

        public static bool IsCompatible(ushort version) =>
            version >= MinimumSupportedVersion && version <= Version;
    }
}
