using AIDeck.Core.Net;
using UnityEngine;

namespace AIDeck.Platform
{
    /// <summary>
    /// Which source this player was built from.
    ///
    /// An iPad build is generated, opened in Xcode, signed and installed by hand, so a stale
    /// export can sit on the device looking exactly like a current one. That happened: an
    /// export made before four fixes was still on the iPad, and the missing behaviour read as
    /// a touch-input bug. The commit and the build time are therefore baked in at build time
    /// and said out loud at startup, so "which build is this?" is a question with an answer
    /// rather than an inference from file dates.
    ///
    /// The stamp is written by <c>AIDeckBuildPipeline</c> into <c>Resources</c>. Running from
    /// the editor there is no build, and the values read <c>unknown</c>.
    /// </summary>
    public static class BuildStamp
    {
        /// <summary>Name of the resource the build pipeline writes. No extension.</summary>
        public const string ResourceName = "aideck-build";

        public const string UnknownValue = "unknown";

        private static bool _loaded;
        private static string _commit = UnknownValue;
        private static string _builtUtc = UnknownValue;

        /// <summary>Short git hash the player was built from, or <c>unknown</c>.</summary>
        public static string Commit
        {
            get
            {
                Load();
                return _commit;
            }
        }

        /// <summary>UTC build time as <c>yyyy-MM-dd HH:mm</c>, or <c>unknown</c>.</summary>
        public static string BuiltUtc
        {
            get
            {
                Load();
                return _builtUtc;
            }
        }

        /// <summary>The player version from the build settings.</summary>
        public static string Version => Application.version;

        /// <summary>Wire protocol this build speaks.</summary>
        public static int ProtocolVersion => ProtocolInfo.Version;

        /// <summary>One line naming everything needed to identify a build.</summary>
        public static string Summary =>
            $"v{Version} · commit {Commit} · built {BuiltUtc} UTC · protocol v{ProtocolVersion}";

        /// <summary>The same line, wrapped for a narrow panel.</summary>
        public static string ShortSummary => $"v{Version} · {Commit} · protocol v{ProtocolVersion}";

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null || string.IsNullOrEmpty(asset.text))
            {
                return;
            }

            foreach (var line in asset.text.Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (key == "commit")
                {
                    _commit = value;
                }
                else if (key == "builtUtc")
                {
                    _builtUtc = value;
                }
            }
        }
    }
}
