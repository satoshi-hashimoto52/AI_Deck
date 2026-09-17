using System;
using System.Collections.Generic;
using AIDeck.Core.Analysis;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.UI;

namespace AIDeck.Controller
{
    /// <summary>What the controller is doing about its link to the host (FR-062).</summary>
    public enum ConnectionState
    {
        /// <summary>Not connected and not trying.</summary>
        Idle,

        /// <summary>Listening for a host beacon (FR-060).</summary>
        Searching,

        /// <summary>Handshake in progress.</summary>
        Connecting,

        Connected,

        /// <summary>The link dropped and the controller is retrying (FR-065).</summary>
        Reconnecting,

        /// <summary>Refused or failed, with a reason the user can act on.</summary>
        Failed
    }

    /// <summary>A host the controller has heard from (FR-060).</summary>
    public readonly struct DiscoveredHost
    {
        public DiscoveredHost(string name, string address, int port)
        {
            Name = name ?? string.Empty;
            Address = address ?? string.Empty;
            Port = port;
        }

        public string Name { get; }
        public string Address { get; }
        public int Port { get; }

        public bool IsValid => !string.IsNullOrEmpty(Address);
    }

    /// <summary>
    /// Everything the controller UI needs from whatever it is talking to.
    ///
    /// The screen is written against this interface rather than against a socket, for two
    /// reasons. The network layer lands in Phase 3 and the UI should not have to change when it
    /// does; and a controller bound directly to an in-process host — what
    /// <c>-aideck-role controller-local</c> starts — exercises the same screen without a
    /// second device, which is how the layout is verified before there is any hardware.
    /// </summary>
    public interface IControllerBackend
    {
        /// <summary>Where control intents go.</summary>
        IDeckCommands Commands { get; }

        /// <summary>The host's authoritative state, refreshed continuously.</summary>
        StateSnapshot Snapshot { get; }

        /// <summary>The host's library, or empty when not connected.</summary>
        IReadOnlyList<TrackInfo> Library { get; }

        ConnectionState State { get; }

        /// <summary>Short user-facing explanation of <see cref="State"/>.</summary>
        string StatusText { get; }

        /// <summary>Hosts heard from on the LAN, newest first.</summary>
        IReadOnlyList<DiscoveredHost> DiscoveredHosts { get; }

        /// <summary>Waveform envelope for a track, or null when it has not arrived yet.</summary>
        WaveformData WaveformFor(string trackId);

        /// <summary>Connects to an address the user typed (FR-061).</summary>
        void Connect(string address, int port);

        /// <summary>Connects to a host found by discovery (FR-060).</summary>
        void Connect(DiscoveredHost host);

        void Disconnect();

        /// <summary>Called once per frame from the controller's Update.</summary>
        void Tick(float deltaSeconds);

        /// <summary>
        /// The app was backgrounded or the link dropped: drop every in-flight gesture so no
        /// control is left held (FR-066, FR-074).
        /// </summary>
        void ReleaseAll();
    }
}
