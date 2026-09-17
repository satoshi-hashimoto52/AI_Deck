using System.Collections;
using System.Collections.Generic;
using AIDeck.Audio;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using AIDeck.Platform;
using AIDeck.UI;
using UnityEngine;

namespace AIDeck.Host
{
    /// <summary>
    /// The Mac application.
    ///
    /// Owns the library, the audio engine and the window, and holds the authoritative state
    /// that the controller mirrors. Everything that changes state goes through
    /// <see cref="HostCommands"/>, so the Mac UI and the network layer cannot diverge.
    /// </summary>
    public sealed class HostApp : MonoBehaviour
    {
        /// <summary>How often the UI is refreshed from the engine. 20 Hz matches the snapshot rate.</summary>
        private const float RefreshInterval = 0.05f;

        private DiagnosticLog _log;
        private AppSettings _settings;
        private SettingsStore _settingsStore;
        private LibraryStore _libraryStore;
        private TrackLibrary _library;
        private AudioEngine _engine;
        private TrackImporter _importer;
        private HostScreen _screen;
        private HostCommands _commands;
        private HostNetworkBridge _bridge;

        private float _refreshTimer;
        private int _renderedRevision = -1;
        private string _notice = string.Empty;
        private float _noticeTimer;
        private readonly string[] _deckWaveformTrack = { string.Empty, string.Empty };

        public TrackLibrary Library => _library;

        /// <summary>The Mac window. Exposed so an in-process controller can hide it.</summary>
        public HostScreen Screen => _screen;
        public AudioEngine Engine => _engine;
        public HostCommands Commands => _commands;
        public DiagnosticLog Log => _log;
        public AppSettings Settings => _settings;

        /// <summary>The network endpoint, or null when networking could not start.</summary>
        public HostNetworkBridge Bridge => _bridge;

        /// <summary>The connection status shown in the status line (FR-062).</summary>
        public string ConnectionStatus =>
            _bridge != null ? _bridge.StatusText : "Networking is not running";

        private void Awake()
        {
            _log = new DiagnosticLog();
            _settingsStore = new SettingsStore(_log);
            _settings = _settingsStore.Load();

            _libraryStore = new LibraryStore(_log);
            _library = new TrackLibrary();
            var report = _libraryStore.Load(_library);
            if (report.Success && report.Loaded > 0)
            {
                _log.Info("Library", $"Restored {report.Loaded} tracks.");
            }

            _engine = gameObject.AddComponent<AudioEngine>();
            _engine.Initialise(_log, _settings);
            _engine.Notice += ShowNotice;
            _engine.DeckLoadCompleted += OnDeckLoadCompleted;

            _importer = gameObject.AddComponent<TrackImporter>();
            _importer.Initialise(_log, _library);
            _importer.Progress += OnImportProgress;
            _importer.Completed += OnImportCompleted;

            _commands = new HostCommands(_engine, _library);
            _commands.Rejected += ShowNotice;

            _screen = HostScreen.Create(transform, _commands);
            _screen.AddPathRequested += OnAddPath;
            _screen.AllStopRequested += () =>
            {
                _commands.AllStop();
                ShowNotice("All decks stopped.");
            };
            _screen.RemoveSelectedRequested += RemoveSelectedTrack;
            _screen.SetRemoveEnabled(false);

            _bridge = gameObject.AddComponent<HostNetworkBridge>();
            _bridge.Notice += ShowNotice;
            _bridge.Initialise(_engine, _commands, _library, _settings, _log);

            _screen.SetAddress(DeviceInfo.LocalIPv4(), _bridge.Session?.TcpPort ?? ProtocolInfo.DefaultTcpPort);
            _screen.SetPathField(MusicFolderScanner.DefaultMusicFolder);

            _library.Changed += OnLibraryChanged;
            _log.EntryAdded += OnLogEntry;

            // The default drop folder is created on first run so there is somewhere obvious to
            // put music, and so the pre-filled path in the import field actually exists.
            AppPaths.EnsureFolder(MusicFolderScanner.DefaultMusicFolder);

            if (_settings.WasRepaired)
            {
                ShowNotice(_settings.RepairReason);
            }

            // §5.6 asks for the master volume to survive a restart (FR-082).
            _engine.Mixer.MasterGain = _settings.MasterVolume;

            RefreshLibraryView();
            ReportMissingFiles();
            _log.Info("Host", "AI Deck host started.");

            _startupOptions = HostStartupOptions.FromEnvironment();
            if (_startupOptions.HasWork)
            {
                StartCoroutine(RunStartupOptions());
            }
        }

        private HostStartupOptions _startupOptions = new HostStartupOptions();

        /// <summary>
        /// Applies the command-line startup options (see <see cref="HostStartupOptions"/>).
        /// Runs as a coroutine because the import and the deck loads are both asynchronous.
        /// </summary>
        private IEnumerator RunStartupOptions()
        {
            if (!string.IsNullOrEmpty(_startupOptions.ImportPath))
            {
                OnAddPath(_startupOptions.ImportPath);
                while (_importer.IsImporting)
                {
                    yield return null;
                }
            }

            if (!_startupOptions.AutoLoad)
            {
                yield break;
            }

            var tracks = _library.All;
            if (tracks.Count == 0)
            {
                ShowNotice("Nothing to load: the library is empty.");
                yield break;
            }

            _engine.LoadTrack(DeckId.A, tracks[0]);
            _engine.LoadTrack(DeckId.B, tracks[tracks.Count > 1 ? 1 : 0]);

            var deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline &&
                   (_engine.DeckA.Transport.State == Core.Deck.DeckPlaybackState.Loading ||
                    _engine.DeckB.Transport.State == Core.Deck.DeckPlaybackState.Loading))
            {
                yield return null;
            }

            if (_startupOptions.AutoPlay)
            {
                _engine.DeckA.Play();
                _engine.DeckB.Play();
            }
        }

        /// <summary>
        /// Tells the user how many library entries point at a file that is no longer there.
        ///
        /// They are reported, not removed. A missing file is usually an unmounted drive rather
        /// than a deletion, and silently dropping those entries would lose a curated library
        /// the moment an external disk was unplugged. Loading such a track already fails with a
        /// clear reason, and REMOVE takes the entry out when the user decides it is gone.
        /// </summary>
        private void ReportMissingFiles()
        {
            var missing = 0;
            foreach (var track in _library.All)
            {
                try
                {
                    if (!System.IO.File.Exists(track.FilePath))
                    {
                        missing++;
                    }
                }
                catch (System.Exception)
                {
                    // An unreadable path counts as missing for reporting purposes.
                    missing++;
                }
            }

            if (missing > 0)
            {
                var message = missing == 1
                    ? "1 track in the library is no longer on disk."
                    : $"{missing} tracks in the library are no longer on disk.";
                ShowNotice(message);
                _log.Warning("Library", message);
            }
        }

        private void Update()
        {
            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer < RefreshInterval)
            {
                return;
            }

            _refreshTimer = 0f;

            if (_noticeTimer > 0f)
            {
                _noticeTimer -= RefreshInterval;
                if (_noticeTimer <= 0f)
                {
                    _notice = string.Empty;
                }
            }

            var snapshot = _engine.BuildSnapshot(_library.Revision, _notice);
            _screen.Apply(snapshot);
            UpdateWaveforms(snapshot);
            UpdateStatusLine(snapshot);

            if (_renderedRevision != _library.Revision)
            {
                RefreshLibraryView();
            }

            _screen.SetRemoveEnabled(_screen.Browser.Library.SelectedTrack != null);
        }

        private void UpdateStatusLine(StateSnapshot snapshot)
        {
            var address = DeviceInfo.LocalIPv4();
            var recording = snapshot.IsRecording
                ? $"  ·  ● REC {TrackInfo.FormatDuration(snapshot.RecordingSeconds)}"
                : string.Empty;

            _screen.Browser.SetStatus(
                $"{ConnectionStatus}  ·  {(_library.Count == 1 ? "1 track" : _library.Count + " tracks")}{recording}",
                snapshot.IsRecording ? Theme.Danger : Theme.TextDim);

            _screen.SetAddress(address, _bridge?.Session?.TcpPort ?? ProtocolInfo.DefaultTcpPort);
        }

        /// <summary>
        /// Loads each deck's waveform from the cache when the loaded track changes. The
        /// envelope was produced at import, so this is a file read rather than an analysis.
        /// </summary>
        private void UpdateWaveforms(StateSnapshot snapshot)
        {
            ApplyWaveform(DeckId.A, snapshot.DeckA.TrackId);
            ApplyWaveform(DeckId.B, snapshot.DeckB.TrackId);
        }

        private void ApplyWaveform(DeckId deck, string trackId)
        {
            var index = (int)deck;
            if (_deckWaveformTrack[index] == (trackId ?? string.Empty))
            {
                return;
            }

            _deckWaveformTrack[index] = trackId ?? string.Empty;

            if (string.IsNullOrEmpty(trackId))
            {
                _screen.Browser.SetWaveform(deck, Core.Analysis.WaveformData.Empty);
                _screen.Browser.SetDeckTitle(deck, string.Empty);
                return;
            }

            var waveform = WaveformCache.TryLoad(trackId);
            _screen.Browser.SetWaveform(deck, waveform ?? Core.Analysis.WaveformData.Empty);

            var track = _library.GetById(trackId);
            _screen.Browser.SetDeckTitle(deck, track?.Title ?? string.Empty);

            if (waveform == null)
            {
                _log.Debug("Waveform", $"No cached waveform for deck {deck.ToDisplayName()}.");
            }
        }

        /// <summary>
        /// Removes the selected track from the catalogue (§5.6). The file on disk is never
        /// touched — FR-010 and NFR-008 make the library a catalogue, not a file manager, and
        /// there is no code path here that could delete the user's music.
        /// </summary>
        private void RemoveSelectedTrack()
        {
            var track = _screen.Browser.Library.SelectedTrack;
            if (track == null)
            {
                ShowNotice("Select a track first.");
                return;
            }

            // A track that is on a deck is ejected first, so the deck is not left holding a
            // reference to something the library no longer knows about.
            if (_engine.DeckA.TrackId == track.Id)
            {
                _engine.Eject(DeckId.A);
            }

            if (_engine.DeckB.TrackId == track.Id)
            {
                _engine.Eject(DeckId.B);
            }

            if (_library.Remove(track.Id))
            {
                _screen.Browser.Library.ClearSelection();
                ShowNotice($"Removed \"{track.Title}\" from the library. The file was not deleted.");
                RefreshLibraryView();
            }
        }

        private void RefreshLibraryView()
        {
            _renderedRevision = _library.Revision;
            _screen.Browser.SetTracks(_library.All);
        }

        private void OnLibraryChanged() => _libraryStore.Save(_library);

        private void OnAddPath(string path)
        {
            if (_importer.IsImporting)
            {
                ShowNotice("An import is already running.");
                return;
            }

            var files = MusicFolderScanner.Scan(path, out var error);
            if (files.Count == 0)
            {
                ShowNotice(error ?? "Nothing to add.");
                return;
            }

            if (!string.IsNullOrEmpty(error))
            {
                ShowNotice(error);
            }

            _screen.SetImportStatus($"Adding {files.Count} files…");
            _importer.Import(files);
        }

        private void OnImportProgress(ImportProgress progress)
        {
            _screen.SetImportStatus(progress.Total <= 0
                ? string.Empty
                : $"Adding {progress.Completed}/{progress.Total}  {progress.CurrentFile}");
        }

        private void OnImportCompleted(List<AddReport> reports)
        {
            var added = 0;
            var skipped = 0;
            string firstReason = null;

            foreach (var report in reports)
            {
                if (report.Succeeded)
                {
                    added++;
                }
                else
                {
                    skipped++;
                    firstReason ??= report.Reason;
                }
            }

            // Skipped files are reported, never silently dropped (FR-005).
            var summary = skipped == 0
                ? $"Added {added} tracks."
                : $"Added {added} tracks, skipped {skipped}. {firstReason}";

            _screen.SetImportStatus(string.Empty);
            ShowNotice(summary);
            _log.Info("Library", summary);
            _libraryStore.Save(_library);
            RefreshLibraryView();
        }

        private void OnDeckLoadCompleted(DeckId deck, bool success, string error)
        {
            if (!success)
            {
                ShowNotice($"Deck {deck.ToDisplayName()}: {error}");
            }
        }

        private void OnLogEntry(LogEntry entry)
        {
            if (entry.Level >= LogLevel.Warning)
            {
                _screen.SetLog($"{entry.Category}: {entry.Message}", entry.Level == LogLevel.Error);
            }
        }

        private void ShowNotice(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _notice = message;
            _noticeTimer = 5f;
            _screen.SetLog(message);
        }

        private void OnApplicationQuit() => Persist();

        private void OnDestroy()
        {
            if (_library != null)
            {
                _library.Changed -= OnLibraryChanged;
            }

            if (_log != null)
            {
                _log.EntryAdded -= OnLogEntry;
            }
        }

        /// <summary>Saves settings and the library. Called on quit and after a library change.</summary>
        public void Persist()
        {
            if (_settings == null)
            {
                return;
            }

            _settings.MasterVolume = _engine != null ? _engine.Mixer.MasterGain : _settings.MasterVolume;
            _settings.CrossfaderCurve = _engine != null ? _engine.Mixer.CrossfaderCurveType : _settings.CrossfaderCurve;
            _settingsStore.Save(_settings);
            _libraryStore.Save(_library);
        }
    }
}
