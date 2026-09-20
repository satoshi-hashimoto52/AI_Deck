using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIDeck.Core.Deck;
using AIDeck.Core.Generation;
using AIDeck.Core.Model;
using AIDeck.Host;
using AIDeck.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// The generation sheet, driven the way a person drives it: the real GENERATE button in
    /// the real top bar, the real input fields, the real router.
    ///
    /// The engine behind it is a fake. A suite that loaded ten gigabytes of weights and waited
    /// eighty-five seconds per case would not be run, and a test that is not run is not a test
    /// — so the fake stands in for ACE-Step and every state it can reach is driven directly.
    /// </summary>
    [TestFixture]
    public class GenerationPlayModeTests
    {
        /// <summary>An engine that loads nothing and does exactly what the test says.</summary>
        private sealed class FakeBridge : IGeneratorBridge
        {
            public readonly List<string> Calls = new List<string>();
            public GenerationFields LastFields;
            private GeneratorStatus _status = GeneratorStatus.Unknown("Not connected.");

            public GeneratorStatus Status => _status;
            public event Action<GeneratorStatus> StatusChanged;

            public void Connect() => Calls.Add("connect");
            public void StartServer() => Calls.Add("start");
            public void StopServer(bool force) => Calls.Add(force ? "stop:force" : "stop");
            public void Cancel() => Calls.Add("cancel");
            public void Shutdown() => Calls.Add("shutdown");

            public void Generate(GenerationFields fields)
            {
                Calls.Add("generate");
                LastFields = fields;
                Push(GeneratorState.Queued, "Queued.");
            }

            public void Push(
                GeneratorState state,
                string message,
                string fileName = "",
                string filePath = "",
                double duration = 0d,
                string errorKind = "")
            {
                _status = new GeneratorStatus(
                    state, message, 0f, errorKind, string.Empty,
                    fileName, filePath, duration, false);
                StatusChanged?.Invoke(_status);
            }
        }

        private GameObject _root;
        private HostApp _host;
        private FakeBridge _bridge;
        private string _settingsPath;
        private string _libraryPath;
        private string _generatedFolder;
        private string _generatedFile;

        [SetUp]
        public void SetUp()
        {
            _bridge = new FakeBridge();

            var unique = Guid.NewGuid().ToString("N");
            _settingsPath = Path.Combine(Path.GetTempPath(), $"aideck-gen-settings-{unique}.json");
            _libraryPath = Path.Combine(Path.GetTempPath(), $"aideck-gen-library-{unique}.json");

            // Created inactive so every override lands before Awake runs. Without this the
            // real settings and library are used and the live ports are bound — and the
            // generator bridge would be the real one, which is the whole thing being avoided.
            _root = new GameObject("GenerationTestHost");
            _root.SetActive(false);
            _host = _root.AddComponent<HostApp>();
            _host.SettingsPathOverride = _settingsPath;
            _host.LibraryPathOverride = _libraryPath;
            _host.NetworkingEnabled = false;
            _host.GeneratorBridgeOverride = _bridge;
            _root.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
            }

            foreach (var path in new[] { _settingsPath, _libraryPath, _generatedFile })
            {
                TryDelete(path);
            }

            if (!string.IsNullOrEmpty(_generatedFile))
            {
                TryDelete(Path.ChangeExtension(_generatedFile, ".json"));
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A leftover file in the temp folder is harmless.
            }
        }

        /// <summary>The tail of the diagnostic log, so a refusal names itself in the failure.</summary>
        private string LastLog()
        {
            var text = string.Empty;
            foreach (var entry in _host.Log.Recent(40))
            {
                text += entry.Category + ": " + entry.Message + " | ";
            }

            return text;
        }

        private IEnumerator LetUiCatchUp()
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;
        }

        private ButtonWidget FindButton(string name)
        {
            foreach (var button in _host.GetComponentsInChildren<ButtonWidget>(true))
            {
                if (button.gameObject.name == name)
                {
                    return button;
                }
            }

            return null;
        }

        private void Click(ButtonWidget button)
        {
            Assert.That(button, Is.Not.Null, "the button does not exist");
            Assert.That(((AIDeck.UI.ITouchTarget)button).TouchEnabled, Is.True,
                $"{button.gameObject.name} is disabled, so the press would be ignored");

            var corners = new Vector3[4];
            button.Rect.GetWorldCorners(corners);
            var centre = (corners[0] + corners[2]) * 0.5f;
            var point = new Vector2(centre.x, centre.y);

            _host.Screen.Router.PointerDown(1, point);

            // The router hands a press to the topmost enabled widget at that point. If the
            // sheet has not been laid out, every control is still full-rect and the press
            // lands on whichever was registered last — which is the failure this catches, and
            // names, instead of leaving an empty call list to puzzle over.
            Assert.That(button.IsPressed, Is.True,
                $"the press at {point} did not reach {button.gameObject.name}; "
                + $"its rect is {button.Rect.rect} at {button.Rect.position}");

            _host.Screen.Router.PointerUp(1, point);
        }

        private InputField FindField(string name)
        {
            foreach (var field in _host.Screen.Generate.GetComponentsInChildren<InputField>(true))
            {
                if (field.gameObject.name == name)
                {
                    return field;
                }
            }

            return null;
        }

        /// <summary>A real WAV in the real generated folder, so the import path is the real one.</summary>
        private string WriteGeneratedTrack(string title)
        {
            _generatedFolder = Path.Combine(
                AIDeck.Platform.MusicFolderScanner.DefaultMusicFolder, HostApp.GeneratedFolderName);
            Directory.CreateDirectory(_generatedFolder);
            var path = Path.Combine(_generatedFolder, title + ".wav");
            var source = TestAudioFile.CreateTone(2d);
            File.Copy(source, path, overwrite: true);
            TestAudioFile.Delete(source);
            _generatedFile = path;
            return path;
        }

        // ------------------------------------------------------------- the sheet

        [UnityTest]
        public IEnumerator TheGenerateButtonOpensTheSheet()
        {
            yield return LetUiCatchUp();

            Assert.That(_host.Screen.Generate.IsVisible, Is.False, "the sheet starts hidden");

            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            Assert.That(_host.Screen.Generate.IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator NothingIsStartedAndNoModelIsLoadedUntilAButtonIsPressed()
        {
            // The rule that matters most on a 16 GB machine: launching AI Deck must never
            // load ten gigabytes of weights.
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Contain("connect"));
            Assert.That(_bridge.Calls, Does.Not.Contain("start"),
                "the engine was started without anyone asking");
        }

        [UnityTest]
        public IEnumerator PressingStartAiServerAsksTheBridgeToLoad()
        {
            yield return LetUiCatchUp();
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            Click(FindButton("StartServer"));
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Contain("start"));
        }

        [UnityTest]
        public IEnumerator TypingIntoTheFieldsAndPressingGenerateSendsThoseValues()
        {
            yield return LetUiCatchUp();
            _bridge.Push(GeneratorState.Ready, "Ready.");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            FindField("TitleField").text = "Midnight Test";
            FindField("PromptField").text = "slow synthwave";
            FindField("DurationField").text = "45";
            FindField("BpmField").text = "124";
            FindField("SeedField").text = "99";
            yield return LetUiCatchUp();

            Click(FindButton("GenerateNow"));
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Contain("generate"), "refused: " + LastLog());
            Assert.That(_bridge.LastFields.Title, Is.EqualTo("Midnight Test"));
            Assert.That(_bridge.LastFields.Duration, Is.EqualTo("45"));
            Assert.That(_bridge.LastFields.Bpm, Is.EqualTo("124"));
            Assert.That(_bridge.LastFields.Seed, Is.EqualTo("99"));
        }

        [UnityTest]
        public IEnumerator AnInvalidFieldIsRefusedWithoutLosingWhatWasTyped()
        {
            yield return LetUiCatchUp();
            _bridge.Push(GeneratorState.Ready, "Ready.");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            FindField("TitleField").text = "Kept";
            FindField("PromptField").text = "kept as well";
            FindField("DurationField").text = "5";      // below the ten-second floor
            yield return LetUiCatchUp();

            Click(FindButton("GenerateNow"));
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Not.Contain("generate"),
                "an invalid request was sent anyway");
            Assert.That(FindField("TitleField").text, Is.EqualTo("Kept"));
            Assert.That(FindField("PromptField").text, Is.EqualTo("kept as well"));
            Assert.That(FindField("DurationField").text, Is.EqualTo("5"));
        }

        // ------------------------------------------------------------ the states

        [UnityTest]
        public IEnumerator TheSheetFollowsQueuedThenGeneratingThenCompleted()
        {
            yield return LetUiCatchUp();
            _bridge.Push(GeneratorState.Ready, "Ready.");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            FindField("TitleField").text = "Progress Test";
            Click(FindButton("GenerateNow"));
            yield return LetUiCatchUp();

            Assert.That(_bridge.Status.State, Is.EqualTo(GeneratorState.Queued));

            _bridge.Push(GeneratorState.Generating, "Generating…");
            yield return LetUiCatchUp();
            Assert.That(_bridge.Status.State, Is.EqualTo(GeneratorState.Generating));

            var path = WriteGeneratedTrack("Progress Test");
            _bridge.Push(GeneratorState.Completed, "Done.", "Progress Test.wav", path, 2d);
            yield return LetUiCatchUp();

            Assert.That(_bridge.Status.State, Is.EqualTo(GeneratorState.Completed));
        }

        [UnityTest]
        public IEnumerator AFinishedTrackIsAddedToTheLibraryExactlyOnce()
        {
            yield return LetUiCatchUp();
            var before = _host.Library.Count;

            var path = WriteGeneratedTrack("Library Test");
            _bridge.Push(GeneratorState.Completed, "Done.", "Library Test.wav", path, 2d);

            var deadline = Time.realtimeSinceStartup + 30f;
            while (_host.Library.GetByPath(path) == null && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(_host.Library.GetByPath(path), Is.Not.Null,
                "the generated track did not reach the library");
            Assert.That(_host.Library.Count, Is.EqualTo(before + 1),
                "adding one generated track changed the library by more than one row");

            // Pushing the same completion again must not add a second copy.
            _bridge.Push(GeneratorState.Completed, "Done.", "Library Test.wav", path, 2d);
            yield return LetUiCatchUp();
            yield return LetUiCatchUp();

            Assert.That(_host.Library.Count, Is.EqualTo(before + 1),
                "the same file was added twice");
        }

        [UnityTest]
        public IEnumerator TheSidecarJsonIsNeverOfferedToTheLibrary()
        {
            yield return LetUiCatchUp();
            var before = _host.Library.Count;

            var path = WriteGeneratedTrack("Sidecar Test");
            File.WriteAllText(Path.ChangeExtension(path, ".json"), "{\"schema_version\":2}");

            _bridge.Push(GeneratorState.Completed, "Done.", "Sidecar Test.wav", path, 2d);
            var deadline = Time.realtimeSinceStartup + 30f;
            while (_host.Library.GetByPath(path) == null && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(_host.Library.GetByPath(path), Is.Not.Null,
                "the WAV was not added; log: " + LastLog());
            Assert.That(_host.Library.GetByPath(Path.ChangeExtension(path, ".json")), Is.Null,
                "the metadata sidecar was offered to the library as if it were a track");
        }

        [UnityTest]
        public IEnumerator AFailedGenerationAddsNothingToTheLibrary()
        {
            yield return LetUiCatchUp();
            var before = _host.Library.Count;

            _bridge.Push(GeneratorState.Failed, "The generator ran out of memory.",
                errorKind: "task-failed");
            yield return LetUiCatchUp();
            yield return LetUiCatchUp();

            Assert.That(_host.Library.Count, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator ACancelledGenerationAddsNothingToTheLibrary()
        {
            yield return LetUiCatchUp();
            var before = _host.Library.Count;

            _bridge.Push(GeneratorState.Cancelling, "Cancelling…");
            yield return LetUiCatchUp();
            _bridge.Push(GeneratorState.Cancelled, "Generation cancelled.");
            yield return LetUiCatchUp();
            yield return LetUiCatchUp();

            Assert.That(_host.Library.Count, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator PressingCancelAsksTheBridgeToCancel()
        {
            yield return LetUiCatchUp();
            _bridge.Push(GeneratorState.Generating, "Generating…");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            Click(FindButton("Cancel"));
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Contain("cancel"));
        }

        [UnityTest]
        public IEnumerator PressingStopAiServerForcesTheStopBecauseTheUserAsked()
        {
            yield return LetUiCatchUp();
            _bridge.Push(GeneratorState.Ready, "Ready.");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            Click(FindButton("StopServer"));
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Contain("stop:force"));
        }

        // -------------------------------------------------------------- the gate

        [UnityTest]
        public IEnumerator GeneratingIsRefusedWhileADeckIsPlaying()
        {
            yield return LetUiCatchUp();
            AddTrack("Gate Tone", 220f);
            yield return LetUiCatchUp();
            yield return LoadAndPlayDeckA();

            _bridge.Push(GeneratorState.Ready, "Ready.");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            // The button is greyed rather than merely ignoring the press, so the refusal is
            // visible before anyone tries. Pressing it anyway must still do nothing.
            var generate = FindButton("GenerateNow");
            Assert.That(((AIDeck.UI.ITouchTarget)generate).TouchEnabled, Is.False,
                "GENERATE was offered while a deck was playing");

            FindField("TitleField").text = "Should Not Run";
            _host.Generation.Open();
            yield return LetUiCatchUp();

            Assert.That(_bridge.Calls, Does.Not.Contain("generate"),
                "a generation was started while a deck was playing");
        }

        [UnityTest]
        public IEnumerator GeneratingIsRefusedWhileRecording()
        {
            yield return LetUiCatchUp();
            _host.Engine.StartRecording();
            yield return LetUiCatchUp();

            _bridge.Push(GeneratorState.Ready, "Ready.");
            Click(FindButton("Generate"));
            yield return LetUiCatchUp();

            var generate = FindButton("GenerateNow");
            var wasOffered = ((AIDeck.UI.ITouchTarget)generate).TouchEnabled;

            FindField("TitleField").text = "Should Not Run";
            _host.Generation.Open();
            yield return LetUiCatchUp();

            _host.Engine.StopRecording();

            Assert.That(wasOffered, Is.False,
                "GENERATE was offered while the master output was being recorded");
            Assert.That(_bridge.Calls, Does.Not.Contain("generate"),
                "a generation was started while the master output was being recorded");
        }

        // ------------------------------------------------------------- afterwards

        [UnityTest]
        public IEnumerator ClosingTheSheetLeavesTheDeckControlsWorking()
        {
            // The sheet is modal, so the thing to prove is that closing it gives everything
            // back rather than leaving a control captured behind a panel that has gone.
            yield return LetUiCatchUp();
            AddTrack("After Close", 220f);
            yield return LetUiCatchUp();

            Click(FindButton("Generate"));
            yield return LetUiCatchUp();
            Assert.That(_host.Screen.Generate.IsVisible, Is.True);

            Click(FindButton("Close"));
            yield return LetUiCatchUp();
            Assert.That(_host.Screen.Generate.IsVisible, Is.False);

            yield return LoadAndPlayDeckA();
            Assert.That(_host.Engine.DeckA.IsPlaying, Is.True,
                "the deck stopped responding after the generation sheet was closed");

            _host.Engine.DeckA.TogglePlay();
        }

        // ---------------------------------------------------------------- helpers

        private void AddTrack(string title, float frequency)
        {
            var path = TestAudioFile.CreateTone(2d, frequency);
            var info = new TrackInfo(null, path, title, "Generator", 2d,
                TrackFormat.Wav, DateTime.UtcNow, 0d, new FileInfo(path).Length);
            _host.Library.Add(info);
        }

        private IEnumerator LoadAndPlayDeckA()
        {
            var tracks = _host.Library.All;
            var first = tracks.Count > 0 ? tracks[0] : null;
            Assert.That(first, Is.Not.Null, "no track to play");

            _host.Engine.LoadTrack(DeckId.A, first);
            var deadline = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < deadline
                   && _host.Engine.DeckA.Transport.State != DeckPlaybackState.Paused)
            {
                yield return null;
            }

            _host.Engine.DeckA.TogglePlay();
            yield return null;
            yield return null;
        }
    }
}
