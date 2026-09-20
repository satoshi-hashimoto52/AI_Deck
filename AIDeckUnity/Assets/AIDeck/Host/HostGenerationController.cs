using System;
using System.Collections.Generic;
using System.IO;
using AIDeck.Audio;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Generation;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.Platform;
using AIDeck.UI;

namespace AIDeck.Host
{
    /// <summary>
    /// Everything that connects the generation sheet to the deck.
    ///
    /// Separate from <see cref="HostApp"/> because it is the piece with the interesting rules
    /// — when a generation may start, and which single file may reach the library — and both
    /// deserve to be readable without scrolling past the audio wiring.
    ///
    /// It owns no process and no thread. The bridge does the first and the importer the
    /// second; this decides what is allowed and what is reported.
    /// </summary>
    public sealed class HostGenerationController
    {
        private readonly IGeneratorBridge _bridge;
        private readonly GeneratePanel _panel;
        private readonly DiagnosticLog _log;
        private readonly Func<DeckActivity> _readActivity;
        private readonly Action<string> _showNotice;
        private readonly Func<IReadOnlyList<string>, bool> _importFiles;
        private readonly Action<DeckId, string> _loadTrackOnDeck;
        private readonly Func<string, TrackInfo> _findTrackByPath;
        private readonly string _generatedRoot;

        private string _pendingImportPath = string.Empty;
        private string _lastImportedPath = string.Empty;
        private DeckId? _loadWhenImported;

        public HostGenerationController(
            IGeneratorBridge bridge,
            GeneratePanel panel,
            DiagnosticLog log,
            Func<DeckActivity> readActivity,
            Action<string> showNotice,
            Func<IReadOnlyList<string>, bool> importFiles,
            Action<DeckId, string> loadTrackOnDeck,
            Func<string, TrackInfo> findTrackByPath,
            string generatedRoot)
        {
            _bridge = bridge;
            _panel = panel;
            _log = log;
            _readActivity = readActivity;
            _showNotice = showNotice;
            _importFiles = importFiles;
            _loadTrackOnDeck = loadTrackOnDeck;
            _findTrackByPath = findTrackByPath;
            _generatedRoot = generatedRoot;

            _panel.StartServerRequested += OnStartServer;
            _panel.StopServerRequested += force => _bridge.StopServer(force);
            _panel.GenerateRequested += OnGenerate;
            _panel.CancelRequested += () => _bridge.Cancel();
            _panel.CloseRequested += () => _panel.SetVisible(false);
            _panel.LoadCompletedRequested += OnLoadCompleted;

            _bridge.StatusChanged += OnStatusChanged;
        }

        /// <summary>Opens the sheet and refreshes it against the current deck state.</summary>
        public void Open()
        {
            _panel.SetVisible(true);
            _panel.ShowFieldError(string.Empty);
            Refresh();
        }

        public void Refresh() =>
            _panel.Apply(_bridge.Status, GenerationSafetyGate.Evaluate(_readActivity(), _bridge.Status.State));

        private void OnStartServer()
        {
            // Even loading the models waits for silence. It is several gigabytes of disk read
            // and page-ins, and the measured swap cost starts here rather than at GENERATE.
            var decision = GenerationSafetyGate.Evaluate(_readActivity(), GeneratorState.Stopped);
            if (!decision.IsAllowed && !decision.Reason.StartsWith("Start the AI server"))
            {
                _panel.ShowFieldError(decision.Reason);
                return;
            }

            _log?.Info("Generator", "Starting the local generator at the user's request.");
            _bridge.StartServer();
        }

        private void OnGenerate(GenerationFields fields)
        {
            var decision = GenerationSafetyGate.Evaluate(_readActivity(), _bridge.Status.State);
            if (!decision.IsAllowed)
            {
                // The refusal is shown, and what was typed is left exactly as it was.
                _panel.ShowFieldError(decision.Reason);
                _log?.Info("Generator", $"Generation refused: {decision.Reason}");
                return;
            }

            _pendingImportPath = string.Empty;
            _loadWhenImported = null;
            _log?.Info("Generator", "Generation requested.");
            _bridge.Generate(fields);
        }

        private void OnStatusChanged(GeneratorStatus status)
        {
            if (status.State == GeneratorState.Completed
                && !string.IsNullOrEmpty(status.CompletedFilePath)
                && status.CompletedFilePath != _lastImportedPath)
            {
                ImportCompleted(status.CompletedFilePath);

                // The sheet's "stop the AI server after generating" switch is on by default
                // because the models hold several gigabytes on a machine with sixteen, and the
                // common case is make a track, then play. Only on success: after a failure the
                // user will usually want another go, and a three-minute reload between attempts
                // would be its own punishment.
                //
                // Not forced, so an engine the bridge merely adopted is left running — it was
                // not ours to start.
                if (_panel.StopServerAfterGenerating)
                {
                    _log?.Info("Generator", "Stopping the generator: asked to after generating.");
                    _bridge.StopServer(force: false);
                }
            }

            if (_panel.IsVisible)
            {
                Refresh();
            }
        }

        /// <summary>
        /// Put one finished file into the library.
        ///
        /// One file, by its path, through the same importer <b>ADD FILES</b> uses — so the
        /// duplicate check, the BPM analysis and the waveform cache are the ones that are
        /// already tested, not a second copy of them. Re-scanning the whole folder would work
        /// too and is what the obvious implementation does, but it re-reads every track the
        /// user owns to add one, and it reports "skipped 12" for a successful generation.
        /// </summary>
        private void ImportCompleted(string path)
        {
            if (!IsInsideGeneratedFolder(path))
            {
                // The bridge is local and trusted, but it is still a separate process handing
                // this one a path. Only the generated-music folder is accepted.
                _log?.Warning("Generator",
                    "A completed track was reported outside the generated folder and was ignored.");
                return;
            }

            if (!File.Exists(path))
            {
                _log?.Warning("Generator", "A completed track was reported but is not on disk.");
                return;
            }

            _lastImportedPath = path;
            _pendingImportPath = path;

            // The sidecar is never offered: it sits next to the track and is not audio.
            var ok = _importFiles(new List<string> { path });
            if (!ok)
            {
                _showNotice?.Invoke("The library is busy; the generated track was not added yet.");
                _pendingImportPath = string.Empty;
            }
        }

        /// <summary>Called by the host once an import has finished, so a deck load can follow.</summary>
        public void OnImportCompleted()
        {
            if (string.IsNullOrEmpty(_pendingImportPath))
            {
                return;
            }

            var path = _pendingImportPath;
            _pendingImportPath = string.Empty;

            var track = _findTrackByPath(path);
            if (track == null)
            {
                return;
            }

            _showNotice?.Invoke($"Added the generated track \"{track.Title}\" to the library.");

            if (_loadWhenImported.HasValue)
            {
                _loadTrackOnDeck(_loadWhenImported.Value, track.Id);
                _loadWhenImported = null;
            }
        }

        private void OnLoadCompleted(DeckId deck)
        {
            var path = _bridge.Status.CompletedFilePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var track = _findTrackByPath(path);
            if (track != null)
            {
                _loadTrackOnDeck(deck, track.Id);
                _panel.SetVisible(false);
                return;
            }

            // Still being analysed; remember the intent and act when the import lands.
            _loadWhenImported = deck;
        }

        /// <summary>
        /// Whether a reported path really is inside the generated-music folder.
        ///
        /// Compared after full resolution so that <c>…/Generated/../../etc/passwd</c> cannot
        /// pass a prefix test.
        /// </summary>
        private bool IsInsideGeneratedFolder(string path)
        {
            if (string.IsNullOrEmpty(_generatedRoot) || string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                var root = Path.GetFullPath(_generatedRoot).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.Ordinal)
                       && full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                _log?.Exception("Generator", exception, "Checking the generated path");
                return false;
            }
        }
    }
}
