using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AIDeck.Editor
{
    /// <summary>
    /// The build pipeline for both targets.
    ///
    /// Every player setting that matters is applied here rather than left in the project
    /// asset, so a build made from a fresh clone is identical to one made on the machine that
    /// has been open in the editor for a week, and so each setting sits next to a comment
    /// saying which requirement needs it.
    /// </summary>
    public static class AIDeckBuildPipeline
    {
        public const string MacOutputFolder = "build/mac";
        public const string MacAppName = "AI Deck.app";
        public const string IosOutputFolder = "build/ios";

        public const string MacBundleId = "com.aideck.host";
        public const string IosBundleId = "com.aideck.controller";

        public const string MinimumMacOsVersion = "12.0";
        public const string MinimumIosVersion = "15.0";

        /// <summary>
        /// iOS 14 and macOS 15 and later require this before an app may reach other devices on
        /// the LAN. Without it the permission prompt never appears and discovery fails
        /// silently, which would look like a networking bug rather than a missing key
        /// (FR-060).
        /// </summary>
        public const string ControllerLocalNetworkUsageDescription =
            "AI Deck connects to the AI Deck app on your Mac over your local network to control playback.";

        /// <summary>The host's wording: it is the side being found, not the side searching.</summary>
        public const string HostLocalNetworkUsageDescription =
            "AI Deck lets your iPad find and control this Mac over your local network.";

        [MenuItem("AI Deck/Build/macOS (Apple Silicon)")]
        public static void BuildMac() => RunMacBuild(true);

        [MenuItem("AI Deck/Build/iOS (Xcode project)")]
        public static void BuildIos() => RunIosBuild(true);

        /// <summary>Batch-mode entry point for macOS.</summary>
        public static void BuildMacBatch() => RunMacBuild(false);

        /// <summary>Batch-mode entry point for iOS.</summary>
        public static void BuildIosBatch() => RunIosBuild(false);

        private static void RunMacBuild(bool interactive)
        {
            SceneBuilder.AddToBuildSettings();
            ConfigureCommon();

            var named = UnityEditor.Build.NamedBuildTarget.Standalone;
            PlayerSettings.SetApplicationIdentifier(named, MacBundleId);
            PlayerSettings.SetScriptingBackend(named, ResolveMacScriptingBackend());
            PlayerSettings.macOS.buildNumber = PlayerSettings.bundleVersion;
            PlayerSettings.macOS.applicationCategoryType = "public.app-category.music";
            SetMacMinimumVersion(MinimumMacOsVersion);
            TrySetAppleSiliconArchitecture();

            // The host has a window and a mouse; it is not a full-screen game.
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;

            var output = Path.Combine(MacOutputFolder, MacAppName);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { SceneBuilder.ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            Report(BuildPipeline.BuildPlayer(options), "macOS", output, interactive);
        }

        private static void RunIosBuild(bool interactive)
        {
            SceneBuilder.AddToBuildSettings();
            ConfigureCommon();

            var named = UnityEditor.Build.NamedBuildTarget.iOS;
            PlayerSettings.SetApplicationIdentifier(named, IosBundleId);
            PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(named, 1); // ARM64

            PlayerSettings.iOS.targetOSVersionString = MinimumIosVersion;
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPadOnly;
            PlayerSettings.iOS.appInBackgroundBehavior = iOSAppInBackgroundBehavior.Suspend;

            // FR-070: landscape only. Auto-rotation is left on so the iPad can be held either
            // way up, but the portrait orientations are disallowed.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            PlayerSettings.statusBarHidden = true;
            PlayerSettings.useAnimatedAutorotation = true;
            PlayerSettings.iOS.requiresPersistentWiFi = true; // do not power the radio down mid-set

            var options = new BuildPlayerOptions
            {
                scenes = new[] { SceneBuilder.ScenePath },
                locationPathName = IosOutputFolder,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = BuildOptions.None
            };

            Report(BuildPipeline.BuildPlayer(options), "iOS", IosOutputFolder, interactive);
        }

        private static void ConfigureCommon()
        {
            PlayerSettings.companyName = "AI Deck";
            PlayerSettings.productName = "AI Deck";
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Audio must keep running when the window loses focus; a DJ set does not stop
            // because the user clicked something else (NFR-005).
            PlayerSettings.runInBackground = true;

            WriteBuildStamp();
        }

        /// <summary>Where the build stamp resource is written, relative to the project.</summary>
        public const string BuildStampAssetPath = "Assets/Resources/aideck-build.txt";

        /// <summary>
        /// Records the commit and the time this player was built.
        ///
        /// An iPad build is generated here, then opened in Xcode, signed and installed by
        /// hand, so a stale export sits on the device looking exactly like a current one —
        /// which is how an export from before four fixes came back as an input bug. The stamp
        /// travels inside the player so the question can be answered from the running app
        /// rather than from the dates on a folder.
        /// </summary>
        private static void WriteBuildStamp()
        {
            var contents =
                "commit=" + ShortCommit() + "\n" +
                "builtUtc=" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + "\n";

            var full = Path.Combine(Directory.GetCurrentDirectory(), BuildStampAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, contents);
            AssetDatabase.ImportAsset(BuildStampAssetPath, ImportAssetOptions.ForceUpdate);

            Debug.Log($"[AI Deck] Build stamp: commit {ShortCommit()}, protocol v{Core.Net.ProtocolInfo.Version}.");
        }

        /// <summary>
        /// The short hash of HEAD, with <c>-dirty</c> appended when the tree has uncommitted
        /// changes — a build from a dirty tree is not the commit it names, and saying so is the
        /// whole point of the stamp.
        /// </summary>
        private static string ShortCommit()
        {
            var head = Git("rev-parse --short HEAD");
            if (string.IsNullOrEmpty(head))
            {
                return "unknown";
            }

            return string.IsNullOrEmpty(Git("status --porcelain")) ? head : head + "-dirty";
        }

        private static string Git(string arguments)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
                    {
                        WorkingDirectory = Directory.GetCurrentDirectory(),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return process.ExitCode == 0 ? output : string.Empty;
            }
            catch (Exception exception)
            {
                // A build without git is still a build; it just cannot name its commit.
                Debug.LogWarning($"[AI Deck] Could not read the git commit: {exception.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Picks the macOS scripting backend.
        ///
        /// IL2CPP is preferred — ahead-of-time compilation is the better fit for a real-time
        /// audio path — but the macOS IL2CPP module is a separate Unity Hub download and is not
        /// present on every machine. Rather than fail the build with "scripting backend is not
        /// installed", the pipeline falls back to Mono, which is fully supported for macOS and
        /// produces a native arm64 binary. The choice is logged so a build is never silently
        /// different from what was expected.
        /// </summary>
        private static ScriptingImplementation ResolveMacScriptingBackend()
        {
            if (IsMacIl2CppInstalled())
            {
                Debug.Log("[AI Deck] macOS scripting backend: IL2CPP.");
                return ScriptingImplementation.IL2CPP;
            }

            Debug.Log("[AI Deck] macOS IL2CPP module is not installed; building with Mono. " +
                      "Install \"macOS Build Support (IL2CPP)\" in Unity Hub to switch.");
            return ScriptingImplementation.Mono2x;
        }

        /// <summary>
        /// True when the editor ships an IL2CPP player variation for macOS. Probing the
        /// playback engine folder is the only reliable check: the editor API reports whether a
        /// <i>target</i> is supported, not whether a particular backend was installed.
        /// </summary>
        private static bool IsMacIl2CppInstalled()
        {
            try
            {
                var variations = Path.Combine(
                    EditorApplication.applicationContentsPath,
                    "PlaybackEngines", "MacStandaloneSupport", "Variations");

                if (!Directory.Exists(variations))
                {
                    return false;
                }

                foreach (var directory in Directory.GetDirectories(variations))
                {
                    if (Path.GetFileName(directory).IndexOf("il2cpp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Prefers an Apple Silicon-native macOS binary (§3 of the master issue).
        ///
        /// The setting lives in the macOS build extension rather than in
        /// <see cref="PlayerSettings"/>, and that assembly is only present when macOS Build
        /// Support is installed, so it is reached reflectively. A machine without it still
        /// builds — it just produces the editor's default architecture, and says so.
        /// </summary>
        private static void TrySetAppleSiliconArchitecture()
        {
            try
            {
                var type = Type.GetType("UnityEditor.OSXStandalone.UserBuildSettings, UnityEditor.OSXStandalone.Extensions");
                if (type == null)
                {
                    Debug.LogWarning("[AI Deck] macOS build extension not found; using the default architecture.");
                    return;
                }

                var property = type.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
                if (property == null)
                {
                    Debug.LogWarning("[AI Deck] macOS architecture setting not found; using the default.");
                    return;
                }

                var enumType = property.PropertyType;
                var value = Enum.Parse(enumType, "ARM64");
                property.SetValue(null, value);
                Debug.Log("[AI Deck] macOS architecture set to ARM64 (Apple Silicon).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AI Deck] Could not set the macOS architecture ({ex.GetType().Name}); using the default.");
            }
        }

        private static void SetMacMinimumVersion(string version)
        {
            try
            {
                var type = typeof(PlayerSettings).GetNestedType("macOS", BindingFlags.Public | BindingFlags.Static);
                var property = type?.GetProperty("targetOSVersion", BindingFlags.Public | BindingFlags.Static);
                property?.SetValue(null, version);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AI Deck] Could not set the minimum macOS version ({ex.GetType().Name}).");
            }
        }

        private static void Report(BuildReport report, string label, string output, bool interactive)
        {
            var summary = report.summary;
            var message = $"[AI Deck] {label} build {summary.result}: {output} " +
                          $"({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:0} s, " +
                          $"{summary.totalErrors} errors, {summary.totalWarnings} warnings)";

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log(message);
                return;
            }

            Debug.LogError(message);
            if (!interactive)
            {
                // Batch mode must fail loudly, or a broken build looks like a successful one.
                EditorApplication.Exit(1);
            }
        }
    }
}
