using AIDeck.Core.Analysis;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using AIDeck.Platform;
using UnityEngine;

namespace AIDeck.Controller
{
    /// <summary>
    /// The iPad application.
    ///
    /// It owns no audio state. It sends intents through <see cref="IControllerBackend"/> and
    /// renders whatever the host reports back — the division that makes a dropped packet a
    /// cosmetic problem rather than a divergence (see ARCHITECTURE.md §1).
    /// </summary>
    public sealed class ControllerApp : MonoBehaviour
    {
        /// <summary>UI refresh rate. Matches the host's snapshot rate; faster would only redraw the same state.</summary>
        private const float RefreshInterval = 0.05f;

        private DiagnosticLog _log;
        private AppSettings _settings;
        private SettingsStore _settingsStore;
        private ControllerScreen _screen;
        private IControllerBackend _backend;

        private float _refreshTimer;
        private int _renderedRevision = -1;
        private ConnectionState _renderedState = (ConnectionState)(-1);
        private readonly string[] _deckWaveformTrack = { string.Empty, string.Empty };

        public ControllerScreen Screen => _screen;
        public IControllerBackend Backend => _backend;
        public AppSettings Settings => _settings;
        public DiagnosticLog Log => _log;

        /// <summary>
        /// Builds the controller. The backend is injected so that the same screen serves the
        /// network session, an in-process host, and the tests.
        /// </summary>
        public void Initialise(
            IControllerBackend backend,
            DiagnosticLog log = null,
            AppSettings settings = null,
            SettingsStore settingsStore = null)
        {
            _log = log ?? new DiagnosticLog();

            // The store is injectable so the tests do not write into the real settings file.
            // A test that remembers an address the user never typed would leave the app trying
            // to reach a machine that does not exist.
            _settingsStore = settingsStore ?? new SettingsStore(_log);
            _settings = settings ?? _settingsStore.Load();
            _backend = backend ?? new DisconnectedBackend();

            _screen = ControllerScreen.Create(transform, _backend.Commands, _settings.LastHostAddress);
            _screen.Connect.ConnectRequested += OnConnectRequested;
            _screen.Connect.RetryRequested += OnRetryRequested;
            _screen.DisconnectRequested += () => _backend.Disconnect();
            _screen.Browser.LoadRequested += (track, deck) => _backend.Commands.LoadTrack(deck, track.Id);

            ApplySleepPolicy(false);
            Render(force: true);
        }

        private void OnConnectRequested(string address, int port)
        {
            // An explicit choice switches off the automatic one for the rest of the session.
            _userChoseHost = true;
            _settings.LastHostAddress = address;
            _settings.LastHostPort = port;
            _settingsStore.Save(_settings);
            _backend.Connect(address, port);
            Render(force: true);
        }

        private void OnRetryRequested()
        {
            // "Search again" also clears the manual choice, so discovery is free to take over.
            _userChoseHost = false;
            _attemptedAddress = string.Empty;
            _sinceAttemptStarted = 0f;
            _backend.Disconnect();
            Render(force: true);
        }

        private void Update()
        {
            if (_backend == null)
            {
                return;
            }

            _backend.Tick(Time.unscaledDeltaTime);

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer < RefreshInterval)
            {
                return;
            }

            _refreshTimer = 0f;
            Render(force: false);
            TickAutoConnect(RefreshInterval);
        }

        private bool _userChoseHost;
        private float _sinceAttemptStarted;
        private string _attemptedAddress = string.Empty;

        /// <summary>
        /// Connects to a discovered host when the stored address is not answering.
        ///
        /// Step 4 of the issue's completion definition is "the iPad finds the Mac and
        /// connects", so discovery should not merely list a host and wait to be tapped. But it
        /// only acts when the choice is unambiguous: exactly one host is being heard, the user
        /// has not picked one themselves this session, and the address currently being tried
        /// has already failed to answer. With two Macs on the network, guessing which one the
        /// DJ meant would be worse than asking.
        /// </summary>
        private void TickAutoConnect(float deltaSeconds)
        {
            if (_backend == null || _userChoseHost || _backend.State == ConnectionState.Connected)
            {
                _sinceAttemptStarted = 0f;
                return;
            }

            var hosts = _backend.DiscoveredHosts;
            if (hosts == null || hosts.Count != 1)
            {
                return;
            }

            var host = hosts[0];
            if (!host.IsValid || host.Address == _attemptedAddress)
            {
                return;
            }

            _sinceAttemptStarted += deltaSeconds;
            if (_sinceAttemptStarted < AutoConnectDelaySeconds)
            {
                return;
            }

            _sinceAttemptStarted = 0f;
            _attemptedAddress = host.Address;
            _settings.LastHostAddress = host.Address;
            _settings.LastHostPort = host.Port;
            _settingsStore.Save(_settings);
            _backend.Connect(host);
            _log.Info("Controller", "Connecting to the Mac found on this network.");
            Render(force: true);
        }

        /// <summary>
        /// How long a stored address is given to answer before a discovered host is tried
        /// instead. Long enough that a Mac which is simply slow to accept is not abandoned.
        /// </summary>
        private const float AutoConnectDelaySeconds = 3f;

        private void Render(bool force)
        {
            if (_screen == null || _backend == null)
            {
                return;
            }

            var snapshot = _backend.Snapshot;
            var state = _backend.State;

            if (force || state != _renderedState)
            {
                _renderedState = state;
                _screen.SetConnectionState(state, _backend.StatusText);

                // A link that has just gone means every control the finger was on is now
                // driving nothing (FR-066).
                if (state != ConnectionState.Connected)
                {
                    _screen.ReleaseAll();
                    _backend.ReleaseAll();
                }
            }

            _screen.Connect.Apply(state, _backend.StatusText, _backend.DiscoveredHosts);

            if (state != ConnectionState.Connected)
            {
                _screen.Browser.SetStatus(_backend.StatusText, UI.Theme.TextDim);
                return;
            }

            _screen.Apply(snapshot);
            UpdateBrowserStatus(snapshot);
            ApplySleepPolicy(snapshot.DeckA.State == Core.Deck.DeckPlaybackState.Playing ||
                             snapshot.DeckB.State == Core.Deck.DeckPlaybackState.Playing);

            if (force || _renderedRevision != snapshot.LibraryRevision)
            {
                _renderedRevision = snapshot.LibraryRevision;
                _screen.Browser.SetTracks(_backend.Library);
            }

            UpdateWaveform(DeckId.A, snapshot.DeckA.TrackId);
            UpdateWaveform(DeckId.B, snapshot.DeckB.TrackId);
        }

        /// <summary>
        /// The browse area's own status line. The connection state lives in the strip above,
        /// so this one carries what is useful while browsing: how much is in the library and
        /// whatever the host most recently had to say.
        /// </summary>
        private void UpdateBrowserStatus(StateSnapshot snapshot)
        {
            var count = _backend.Library.Count;
            var text = count == 1 ? "1 track" : count + " tracks";

            if (!string.IsNullOrEmpty(snapshot.Notice))
            {
                _screen.Browser.SetStatus(text + "  ·  " + snapshot.Notice, UI.Theme.Warning);
                return;
            }

            if (snapshot.MasterClipping)
            {
                _screen.Browser.SetStatus(text + "  ·  output clipping", UI.Theme.Danger);
                return;
            }

            _screen.Browser.SetStatus(text);
        }

        private void UpdateWaveform(DeckId deck, string trackId)
        {
            var index = (int)deck;
            var id = trackId ?? string.Empty;

            // The waveform can arrive after the track does, so an empty result is retried
            // rather than latched.
            var known = _deckWaveformTrack[index] == id;
            if (known && (id.Length == 0 || _screen.Browser.HasWaveform(deck)))
            {
                return;
            }

            _deckWaveformTrack[index] = id;

            if (id.Length == 0)
            {
                _screen.Browser.SetWaveform(deck, WaveformData.Empty);
                _screen.Browser.SetDeckTitle(deck, string.Empty);
                return;
            }

            var waveform = _backend.WaveformFor(id);
            if (waveform != null)
            {
                _screen.Browser.SetWaveform(deck, waveform);
            }

            var title = FindTitle(id);
            _screen.Browser.SetDeckTitle(deck, title);
        }

        private string FindTitle(string trackId)
        {
            var library = _backend.Library;
            for (var i = 0; i < library.Count; i++)
            {
                if (library[i].Id == trackId)
                {
                    return library[i].Title;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Keeps the screen awake while a deck is playing (FR-076), subject to the user
        /// setting. Left to the system otherwise, because holding the screen on through a
        /// whole idle session would drain the iPad for nothing.
        /// </summary>
        private void ApplySleepPolicy(bool playing)
        {
            UnityEngine.Screen.sleepTimeout = _settings.PreventSleepWhilePlaying && playing
                ? SleepTimeout.NeverSleep
                : SleepTimeout.SystemSetting;
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused)
            {
                return;
            }

            // FR-074: backgrounding is not a release. Every held control is dropped, and the
            // continuous state on the host is released too, so a finger that was on the
            // platter when the phone rang does not leave the deck scratching.
            _screen?.ReleaseAll();
            _backend?.ReleaseAll();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                _screen?.ReleaseAll();
                _backend?.ReleaseAll();
            }
        }

        private void OnApplicationQuit()
        {
            _backend?.ReleaseAll();
            _backend?.Disconnect();
        }
    }
}
