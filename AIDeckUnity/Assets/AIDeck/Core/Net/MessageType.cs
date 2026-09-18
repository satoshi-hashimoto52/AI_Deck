namespace AIDeck.Core.Net
{
    /// <summary>
    /// Wire message identifiers. Values are permanent: changing one is a protocol break and
    /// requires bumping <see cref="ProtocolInfo.Version"/>. See docs/NETWORK_PROTOCOL.md.
    ///
    /// 1–9    session management
    /// 10–39  deck transport and platter
    /// 40–59  mixer, effects and recording
    /// 60–79  queries and safety
    /// 100+   host to controller
    /// </summary>
    public enum MessageType : byte
    {
        Unknown = 0,

        // --- Session (either direction) ---
        Hello = 1,
        Ping = 2,
        Bye = 3,

        // --- Deck transport (controller to host, reliable) ---
        LoadTrack = 10,
        Play = 11,
        Pause = 12,
        TogglePlay = 13,
        CueSet = 14,
        CueReturn = 15,
        Seek = 16,
        SyncToggle = 19,
        LoopSetRegion = 20,
        LoopToggle = 21,
        LoopBeats = 22,
        Eject = 23,

        // --- Platter and tempo (controller to host, low latency) ---
        TempoFader = 30,
        TempoRange = 31,
        JogNudge = 32,
        ScratchBegin = 33,
        ScratchUpdate = 34,

        /// <summary>Exact platter displacement in audio seconds; see JogWidget.ScratchMoved.</summary>
        ScratchMove = 38,
        ScratchEnd = 35,
        Brake = 36,
        Backspin = 37,

        // --- Mixer and effects (controller to host, low latency) ---
        ChannelGain = 40,
        Crossfader = 41,
        MasterGain = 42,
        Filter = 43,
        Mute = 44,
        Echo = 45,
        CueMonitor = 46,
        CrossfaderCurve = 47,

        // --- Recording (controller to host, reliable) ---
        RecordStart = 50,
        RecordStop = 51,

        // --- Queries and safety (controller to host, reliable) ---
        RequestLibrary = 60,
        RequestWaveform = 61,
        RequestSnapshot = 62,
        AllStop = 70,

        // --- Host to controller ---
        HelloAck = 100,
        Pong = 101,
        StateSnapshot = 110,
        LibraryChunk = 111,
        WaveformChunk = 112,
        Error = 120,
        Notice = 121,
        Discovery = 130
    }

    /// <summary>Which transport a message class belongs on.</summary>
    public enum MessageChannel
    {
        /// <summary>Must arrive, in order. Carried over TCP.</summary>
        Reliable = 0,

        /// <summary>Latest value wins; losses are acceptable. Carried over UDP.</summary>
        Fast = 1
    }

    public static class MessageTypeExtensions
    {
        /// <summary>
        /// Continuous controls go on the fast channel, where a superseded value may be
        /// dropped (§4.4). Everything that changes what is loaded or whether audio is
        /// running must be reliable.
        /// </summary>
        public static MessageChannel Channel(this MessageType type)
        {
            switch (type)
            {
                case MessageType.TempoFader:
                case MessageType.JogNudge:
                case MessageType.ScratchUpdate:
                case MessageType.ScratchMove:
                case MessageType.ChannelGain:
                case MessageType.Crossfader:
                case MessageType.MasterGain:
                case MessageType.Filter:
                case MessageType.StateSnapshot:
                case MessageType.Discovery:
                    return MessageChannel.Fast;

                // Ping and Pong are deliberately *reliable*. Liveness must not depend on the
                // lossy channel: on a network that drops UDP between clients — or behind a
                // firewall that blocks the controller's inbound datagrams — the session would
                // otherwise time out every three seconds and reconnect forever, even though
                // the TCP connection was perfectly healthy the whole time.
                default:
                    return MessageChannel.Reliable;
            }
        }

        /// <summary>
        /// True when a message carries a continuous value whose older instances may be
        /// discarded by the sequence gate rather than applied late.
        /// </summary>
        public static bool IsSupersedable(this MessageType type) =>
            type.Channel() == MessageChannel.Fast;

        public static bool IsHostToController(this MessageType type) => (byte)type >= 100;
    }
}
