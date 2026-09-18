using System;
using System.IO;
using UnityEngine;

namespace AIDeck.Platform
{
    /// <summary>
    /// Every path AI Deck writes to. Centralised so that the "we never write outside our own
    /// data folder" rule is checkable in one place, and so the recording default is somewhere
    /// the user can actually find (FR-054, FR-081).
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Per-user application data. Unity guarantees this exists and is writable.</summary>
        public static string DataFolder => Application.persistentDataPath;

        public static string SettingsFile =>
            Path.Combine(DataFolder, Core.Settings.AppSettings.FileName);

        public static string LibraryFile => Path.Combine(DataFolder, "aideck-library.json");

        /// <summary>Waveform envelopes, cached so a re-loaded track does not re-analyse (FR-027).</summary>
        public static string WaveformCacheFolder => Path.Combine(DataFolder, "waveforms");

        /// <summary>Name of the recordings subfolder. Excluded from library scans by name.</summary>
        public const string RecordingsFolderName = "Recordings";

        /// <summary>
        /// A path with the home directory folded back to <c>~</c>, for showing and logging.
        ///
        /// The data folder is worth saying out loud — a library that appears empty is nearly
        /// always a library being read from somewhere else — but the account name in it is
        /// not, and NFR-012 keeps personal detail out of the log.
        /// </summary>
        public static string ForDisplay(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) || !path.StartsWith(home, StringComparison.Ordinal)
                ? path
                : "~" + path.Substring(home.Length);
        }

        /// <summary>
        /// Default recording destination: <c>~/Music/AI Deck/Recordings</c> on macOS, a
        /// subfolder of the app data folder elsewhere. The user can override it, and the
        /// override is persisted (FR-081).
        ///
        /// It is deliberately a *subfolder* of the music folder rather than the music folder
        /// itself. When the two were the same, every recording was picked up by the next
        /// library scan and appeared as a track called <c>AIDeck_20260918_085224</c>.
        /// </summary>
        public static string DefaultRecordingFolder
        {
            get
            {
                try
                {
                    var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    if (!string.IsNullOrEmpty(music) && Directory.Exists(music))
                    {
                        return Path.Combine(music, "AI Deck", RecordingsFolderName);
                    }
                }
                catch (Exception)
                {
                    // Falls through to the app data folder, which always exists.
                }

                return Path.Combine(DataFolder, RecordingsFolderName);
            }
        }

        /// <summary>Creates a folder if it is missing. Returns false rather than throwing.</summary>
        public static bool EnsureFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The cache file for one track's waveform envelope.</summary>
        public static string WaveformCacheFile(string trackId) =>
            Path.Combine(WaveformCacheFolder, (trackId ?? "unknown") + ".wfm");
    }
}
