using System.Collections.Generic;
using AIDeck.Audio;
using AIDeck.Controller;
using AIDeck.Core.Analysis;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Host;
using AIDeck.Platform;
using AIDeck.UI;

namespace AIDeck.App
{
    /// <summary>
    /// Binds the controller UI directly to a host running in the same process.
    ///
    /// This is the development and verification path: it drives the real screen, the real
    /// command router and the real audio engine, with the network replaced by a method call.
    /// It is how the iPad layout and every control are checked before there is a second
    /// device to check them on, and it is the shape the §10.2 integration tests use with the
    /// network put back in.
    ///
    /// It is not a shortcut around Phase 3. The controller still sends intents and renders
    /// only what the host reports — the same discipline as over the wire.
    /// </summary>
    public sealed class LocalHostBackend : IControllerBackend
    {
        private readonly HostApp _host;

        public LocalHostBackend(HostApp host)
        {
            _host = host;
        }

        public IDeckCommands Commands => _host.Commands;

        public StateSnapshot Snapshot => _host.Engine.BuildSnapshot(_host.Library.Revision);

        public IReadOnlyList<TrackInfo> Library => _host.Library.All;

        public ConnectionState State => ConnectionState.Connected;

        public string StatusText => "Connected to this Mac (in-process)";

        public IReadOnlyList<DiscoveredHost> DiscoveredHosts { get; } = new List<DiscoveredHost>();

        public WaveformData WaveformFor(string trackId) => WaveformCache.TryLoad(trackId);

        public void Connect(string address, int port)
        {
        }

        public void Connect(DiscoveredHost host)
        {
        }

        public void Disconnect()
        {
        }

        public void Tick(float deltaSeconds)
        {
        }

        /// <summary>
        /// Releases the host's continuous controls, exactly as the disconnect path will in
        /// Phase 3 (FR-066). The playback policy is left alone: the controller going away is
        /// not the same as the link dropping when they are the same process.
        /// </summary>
        public void ReleaseAll() => _host.Engine.AllStop(false);
    }
}
