using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Generation;
using AIDeck.Core.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AIDeck.Platform
{
    /// <summary>
    /// Talks to the Python bridge over loopback HTTP, and owns the bridge process.
    ///
    /// Three processes, and the ownership between them is one-directional and explicit:
    ///
    /// <code>
    ///   AI Deck  --owns-->  bridge (python)  --owns-->  ACE-Step server
    /// </code>
    ///
    /// Each link is only an ownership if that side *started* the other. AI Deck stops the
    /// bridge it launched when it quits; a bridge the user was already running is used and
    /// left alone. The bridge applies the same rule to the engine. Nothing here matches on a
    /// process name or calls <c>pkill</c> — stopping goes through the shell scripts that
    /// already validate a PID against its own command line.
    ///
    /// Everything is a coroutine over <c>UnityWebRequest</c>. Nothing blocks the main thread,
    /// and nothing here is reachable from the audio thread: <c>OnAudioFilterRead</c> never
    /// calls into this type, directly or otherwise.
    /// </summary>
    public sealed class LocalGeneratorBridge : MonoBehaviour, IGeneratorBridge
    {
        public const string BridgeHost = "127.0.0.1";
        public const int BridgePort = 8765;

        /// <summary>How often the panel's state is refreshed while something is happening.</summary>
        private const float BusyPollSeconds = 1.0f;

        /// <summary>And while it is not. A stopped generator does not need watching closely.</summary>
        private const float IdlePollSeconds = 3.0f;

        private DiagnosticLog _log;
        private string _baseUrl;
        private string _repositoryRoot;
        private Process _bridgeProcess;
        private bool _startedBridge;
        private bool _connected;
        private bool _pollInFlight;
        private float _nextPollTime;
        private GeneratorStatus _status = GeneratorStatus.Unknown("Not connected.");

        public GeneratorStatus Status => _status;

        public event Action<GeneratorStatus> StatusChanged;

        /// <summary>Where the repository is, so the bridge can be launched from it.</summary>
        public string RepositoryRootOverride { get; set; }

        public void Initialise(DiagnosticLog log)
        {
            _log = log;
            _baseUrl = $"http://{BridgeHost}:{BridgePort}";
            _repositoryRoot = RepositoryRootOverride ?? ResolveRepositoryRoot();
        }

        public void Connect()
        {
            if (_connected)
            {
                return;
            }

            _connected = true;
            _log?.Info("Generator", $"Watching the bridge at {_baseUrl}.");
            _nextPollTime = 0f;
        }

        // ------------------------------------------------------------- commands

        public void StartServer() => StartCoroutine(Post("/v1/server/start", "{}"));

        public void StopServer(bool force) =>
            StartCoroutine(Post("/v1/server/stop", force ? "{\"force\":true}" : "{}"));

        public void Generate(GenerationFields fields)
        {
            var validation = GenerationValidator.Validate(fields);
            if (!validation.IsValid)
            {
                // Refused here rather than sent and bounced, so the panel can point at the
                // offending box while the user still has everything they typed.
                Apply(new GeneratorStatus(
                    _status.State, validation.Message, 0f, "invalid-input", validation.Field,
                    string.Empty, string.Empty, 0d, _status.OwnsServer));
                return;
            }

            StartCoroutine(Post("/v1/generate", BuildRequestJson(fields)));
        }

        public void Cancel() => StartCoroutine(Post("/v1/cancel", "{}"));

        /// <summary>
        /// The request body, built with the project's own JSON writer.
        ///
        /// Numbers are sent as numbers rather than as the strings the user typed, so the
        /// bridge validates a value rather than re-parsing a locale-formatted one.
        /// </summary>
        private static string BuildRequestJson(GenerationFields fields)
        {
            var body = JsonValue.NewObject()
                .Set("title", fields.Title.Trim())
                .Set("prompt", fields.Prompt.Trim())
                .Set("lyrics", fields.Lyrics ?? string.Empty)
                .Set("duration_seconds", ParseNumber(fields.Duration, 30d))
                .Set("bpm", ParseNumber(fields.Bpm, 118d))
                .Set("key_scale", (fields.Key ?? string.Empty).Trim())
                .Set("time_signature", (fields.TimeSignature ?? "4").Trim())
                .Set("vocal_language", (fields.VocalLanguage ?? "ja").Trim());

            var seed = (fields.Seed ?? string.Empty).Trim();
            body.Set("seed", seed.Length == 0 ? JsonValue.Null : JsonValue.String(seed));
            return JsonWriter.Write(body, indented: false);
        }

        private static double ParseNumber(string text, double fallback) =>
            double.TryParse(
                (text ?? string.Empty).Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                ? value
                : fallback;

        public void Shutdown()
        {
            // Only the bridge this application launched. A bridge someone else started keeps
            // running, and so does whatever engine it owns.
            if (!_startedBridge || _bridgeProcess == null)
            {
                return;
            }

            try
            {
                if (!_bridgeProcess.HasExited)
                {
                    _log?.Info("Generator", "Stopping the generator bridge AI Deck started.");

                    // Asked over the contract rather than signalled, so the bridge runs its
                    // own shutdown and stops the ACE-Step server it owns. Process.Kill is
                    // SIGKILL: it would skip that entirely and leave several gigabytes of
                    // model resident with nothing left that knows how to stop it.
                    if (!RequestBridgeShutdown() || !_bridgeProcess.WaitForExit(8000))
                    {
                        _log?.Warning("Generator",
                            "The bridge did not stop when asked; ending the process.");
                        _bridgeProcess.Kill();
                        _bridgeProcess.WaitForExit(3000);
                    }
                }
            }
            catch (Exception exception)
            {
                // The process is going away either way; a failure to signal it must not stop
                // AI Deck from quitting.
                _log?.Exception("Generator", exception, "Stopping the bridge");
            }
            finally
            {
                _bridgeProcess.Dispose();
                _bridgeProcess = null;
                _startedBridge = false;
            }
        }

        private void OnApplicationQuit() => Shutdown();

        /// <summary>
        /// Asks the bridge to shut itself down, synchronously.
        ///
        /// Quitting is the one moment a blocking call is the right shape: there is no next
        /// frame to deliver a coroutine's result to. The timeout is short because a bridge
        /// that cannot answer gets ended the hard way a moment later anyway.
        /// </summary>
        private bool RequestBridgeShutdown()
        {
            try
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    _baseUrl + "/v1/shutdown");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;

                var body = System.Text.Encoding.UTF8.GetBytes("{}");
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == System.Net.HttpStatusCode.OK;
                }
            }
            catch (Exception exception)
            {
                _log?.Info("Generator", $"The bridge did not accept a shutdown request: {exception.GetType().Name}");
                return false;
            }
        }

        // ---------------------------------------------------------------- polling

        /// <summary>
        /// Drives the poll from <c>Update</c> rather than from a long-lived coroutine.
        ///
        /// The first version was a <c>while</c> loop that yielded a nested coroutine and then
        /// a <c>WaitForSecondsRealtime</c>. It stopped after exactly one pass — no exception,
        /// no timeout, nothing in the log — and a finished track was therefore never noticed.
        /// A timer and one short-lived request per tick has no state to get stuck in, and the
        /// failure it replaces was invisible, which is the worst kind.
        /// </summary>
        private void Update()
        {
            if (!_connected || _pollInFlight)
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextPollTime)
            {
                return;
            }

            _pollInFlight = true;
            StartCoroutine(PollOnce());
        }

        private IEnumerator PollOnce()
        {
            // try/finally, not an early reset. Clearing the in-flight flag up front looked
            // like exception-safety and was the opposite: Update then started a fresh poll
            // coroutine every frame, and the nested coroutines blew the stack within seconds.
            // The finally still runs when a subscriber throws, which is what the flag needed
            // protecting against in the first place.
            try
            {
                yield return Get("/v1/state");

                if (_status.State == GeneratorState.Unknown && !_startedBridge)
                {
                    // Nothing is listening. Launching the bridge is cheap — it imports no
                    // model and holds no weights — so it is started on demand rather than
                    // asking the user to run a second command. The *engine* still needs an
                    // explicit press.
                    TryStartBridgeProcess();
                }
            }
            finally
            {
                // A generator that is loading is watched more closely than one that is stopped.
                _nextPollTime = Time.realtimeSinceStartup
                                + (_status.State.IsBusy() || _status.State == GeneratorState.Starting
                                    ? BusyPollSeconds
                                    : IdlePollSeconds);
                _pollInFlight = false;
            }
        }

        private IEnumerator Get(string path)
        {
            using (var request = UnityWebRequest.Get(_baseUrl + path))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();
                HandleResponse(request, path);
            }
        }

        private IEnumerator Post(string path, string body)
        {
            using (var request = new UnityWebRequest(_baseUrl + path, "POST"))
            {
                var payload = System.Text.Encoding.UTF8.GetBytes(body ?? "{}");
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 60;
                yield return request.SendWebRequest();
                HandleResponse(request, path);
            }
        }

        private void HandleResponse(UnityWebRequest request, string path)
        {
            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                Apply(GeneratorStatus.Unknown("The generator bridge is not running."));
                return;
            }

            var text = request.downloadHandler?.text;
            JsonValue json = null;
            var parseError = "an empty response";
            if (string.IsNullOrEmpty(text) || !JsonParser.TryParse(text, out json, out parseError))
            {
                _log?.Warning("Generator", $"The bridge returned something unreadable from {path}: {parseError}");
                Apply(GeneratorStatus.Unknown("The generator bridge answered with nonsense."));
                return;
            }

            if (!json["ok"].AsBool())
            {
                var error = json["error"];
                var kind = error["kind"].AsString(string.Empty) ?? string.Empty;
                var message = error["message"].AsString("The generator refused that.");
                var detail = error["detail"].AsString(string.Empty) ?? string.Empty;

                // The short sentence goes on screen; the long one goes to the log (NFR-007).
                if (!string.IsNullOrEmpty(detail))
                {
                    _log?.Warning("Generator", $"{kind}: {detail}");
                }

                Apply(new GeneratorStatus(
                    _status.State, message, _status.ElapsedSeconds, kind, detail,
                    string.Empty, string.Empty, 0d, _status.OwnsServer));
                return;
            }

            Apply(ReadStatus(json["data"]));
        }

        private static GeneratorStatus ReadStatus(JsonValue data)
        {
            var result = data["result"];
            var hasResult = result != null && result.Kind == JsonKind.Object;

            return new GeneratorStatus(
                GeneratorStateExtensions.Parse(data["state"].AsString(string.Empty)),
                data["message"].AsString(string.Empty),
                data["elapsed_seconds"].AsFloat(),
                data["error_kind"].AsString(string.Empty),
                data["error_detail"].AsString(string.Empty),
                hasResult ? result["audio_file"].AsString(string.Empty) : string.Empty,
                hasResult ? result["audio_path"].AsString(string.Empty) : string.Empty,
                hasResult ? result["duration_seconds"].AsDouble() : 0d,
                data["owns_server"].AsBool(),
                ReadMemory(data["memory"]));
        }

        private static MemoryPressure ReadMemory(JsonValue memory)
        {
            if (memory == null || memory.Kind != JsonKind.Object)
            {
                return MemoryPressure.None;
            }

            return new MemoryPressure(
                memory["swap_used_gb"].AsFloat(),
                memory["compressed_gb"].AsFloat(),
                memory["free_gb"].AsFloat(),
                memory["under_pressure"].AsBool(),
                memory["advice"].AsString(string.Empty));
        }

        private void Apply(GeneratorStatus status)
        {
            var changed = status.DisplaySignature != _status.DisplaySignature
                          || status.CompletedFilePath != _status.CompletedFilePath;

            var stateChanged = status.State != _status.State;
            _status = status;

            if (stateChanged)
            {
                // One line per transition, not per poll. Without it, "the track never reached
                // the library" and "the generator never said it had one" look identical.
                _log?.Info(
                    "Generator",
                    $"State {status.State}"
                    + (string.IsNullOrEmpty(status.CompletedFileName)
                        ? " (no file)"
                        : $" with \"{status.CompletedFileName}\""));
            }

            if (changed)
            {
                StatusChanged?.Invoke(status);
            }
        }

        // ---------------------------------------------------------------- process

        /// <summary>
        /// Launch the Python bridge, once.
        ///
        /// Failure here is not fatal and is not shouted about: a machine without the generator
        /// installed is a perfectly good DJ deck, and the panel already says "not installed".
        /// </summary>
        private void TryStartBridgeProcess()
        {
            if (_bridgeProcess != null || string.IsNullOrEmpty(_repositoryRoot))
            {
                return;
            }

            if (!Directory.Exists(Path.Combine(_repositoryRoot, "generator")))
            {
                return;
            }

            try
            {
                // Standard output and error are deliberately NOT redirected.
                //
                // They were, and nothing ever read them. That gave two failures: the 16 KB
                // pipe would eventually fill and block the bridge mid-write, and — the one
                // that was actually hit — when AI Deck went away the read ends closed, so the
                // orphaned bridge's next log line raised BrokenPipeError. Since the first
                // thing START AI SERVER did was log, the engine was never started and the
                // panel sat at Stopped reporting "internal: BrokenPipeError".
                //
                // Reading them asynchronously would also work, but the callbacks arrive on a
                // thread pool thread where no Unity API may be touched, and the bridge already
                // keeps its own log at .aideck-generator/logs/bridge.log. No pipe is the
                // simplest thing that cannot break.
                var info = new ProcessStartInfo
                {
                    FileName = "/usr/bin/env",
                    Arguments = "python3 -m generator.aideck_generator.bridge_server",
                    WorkingDirectory = _repositoryRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                _bridgeProcess = Process.Start(info);
                _startedBridge = _bridgeProcess != null;
                if (_startedBridge)
                {
                    _log?.Info("Generator", $"Started the generator bridge (PID {_bridgeProcess.Id}).");
                }
            }
            catch (Exception exception)
            {
                _log?.Exception("Generator", exception, "Starting the generator bridge");
                _bridgeProcess = null;
                _startedBridge = false;
            }
        }

        /// <summary>
        /// Find the repository from the running player.
        ///
        /// In the editor that is two levels above <c>Assets</c>. In a built <c>.app</c> the
        /// generator is not inside the bundle — it is a development tool that lives in the
        /// checkout — so the build looks beside the application and gives up quietly if it is
        /// not there.
        /// </summary>
        private static string ResolveRepositoryRoot()
        {
            // Eight levels, because a built player sits deeper than an editor one:
            // …/AIDeckUnity/build/mac/AI Deck.app/Contents/Resources/Data is six directories
            // below the repository root before the check can even start.
            var candidate = Application.dataPath;
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(candidate); depth++)
            {
                var parent = Directory.GetParent(candidate)?.FullName;
                if (string.IsNullOrEmpty(parent))
                {
                    break;
                }

                if (Directory.Exists(Path.Combine(parent, "generator"))
                    && Directory.Exists(Path.Combine(parent, "AIDeckUnity")))
                {
                    return parent;
                }

                candidate = parent;
            }

            return string.Empty;
        }
    }
}
