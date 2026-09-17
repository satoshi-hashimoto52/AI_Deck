using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace AIDeck.Editor
{
    /// <summary>
    /// Adds the Info.plist keys neither platform can work without.
    ///
    /// Unity has no player setting for the local network usage description, and both iOS 14+
    /// and recent macOS refuse LAN access without it — silently, with no error the app can
    /// see. Discovery would simply never find anything (FR-060, FR-061), so the key is added
    /// here as part of every build rather than left as a manual step someone forgets.
    /// </summary>
    public static class AIDeckPostProcessBuild
    {
        [PostProcessBuild(100)]
        public static void OnPostProcessBuild(BuildTarget target, string path)
        {
            switch (target)
            {
                case BuildTarget.iOS:
                    PatchPlist(Path.Combine(path, "Info.plist"), true);
                    break;
                case BuildTarget.StandaloneOSX:
                    PatchPlist(Path.Combine(path, "Contents", "Info.plist"), false);
                    break;
            }
        }

        private static void PatchPlist(string plistPath, bool ios)
        {
            if (!File.Exists(plistPath))
            {
                Debug.LogWarning($"[AI Deck] Info.plist not found at {plistPath}; local network access may be refused.");
                return;
            }

            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            var root = plist.root;

            root.SetString("NSLocalNetworkUsageDescription", ios
                ? AIDeckBuildPipeline.ControllerLocalNetworkUsageDescription
                : AIDeckBuildPipeline.HostLocalNetworkUsageDescription);

            if (ios)
            {
                // Keeps the Wi-Fi radio up: a controller whose link is powered down mid-set
                // would look like a dropped connection.
                root.SetBoolean("UIRequiresPersistentWiFi", true);

                // The controller is a fixed landscape surface (FR-070). Declaring it here as
                // well as in the player settings means the generated Xcode project is correct
                // even if someone rebuilds it by hand.
                var orientations = root.CreateArray("UISupportedInterfaceOrientations~ipad");
                orientations.AddString("UIInterfaceOrientationLandscapeLeft");
                orientations.AddString("UIInterfaceOrientationLandscapeRight");
            }

            plist.WriteToFile(plistPath);
            Debug.Log($"[AI Deck] Patched {plistPath}.");
        }
    }
}
