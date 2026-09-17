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

        /// <summary>
        /// Default recording destination: ~/Music/AI Deck on macOS, the app data folder
        /// elsewhere. The user can override it, and the override is persisted (FR-081).
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
                        return Path.Combine(music, "AI Deck");
                    }
                }
                catch (Exception)
                {
                    // Falls through to the app data folder, which always exists.
                }

                return Path.Combine(DataFolder, "Recordings");
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
