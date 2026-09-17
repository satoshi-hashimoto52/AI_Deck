using System;
using System.Collections.Generic;
using System.IO;
using AIDeck.Core.Model;

namespace AIDeck.Platform
{
    /// <summary>
    /// Finds audio files under a path the user names (FR-004).
    ///
    /// A standalone Unity player has no native file picker, and adding one would mean a
    /// plug-in of unclear provenance, which §14 rules out. So the Mac UI takes a typed or
    /// pasted path — a file or a folder — and this walks it.
    ///
    /// The walk is depth-limited and count-limited. Pointing it at a home directory by
    /// accident should return a useful set quickly rather than stat every file on the disk,
    /// and an unreadable subfolder is skipped rather than aborting the scan.
    /// </summary>
    public static class MusicFolderScanner
    {
        public const int MaxDepth = 6;
        public const int MaxFiles = 5000;

        /// <summary>
        /// Returns the supported audio files at <paramref name="path"/>. A file path returns
        /// itself when supported; a folder is walked recursively.
        /// </summary>
        public static List<string> Scan(string path, out string error)
        {
            error = null;
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Type the path of a file or folder to add.";
                return results;
            }

            var trimmed = Expand(path.Trim().Trim('"', '\''));

            try
            {
                if (File.Exists(trimmed))
                {
                    if (TrackInfo.IsSupported(trimmed))
                    {
                        results.Add(trimmed);
                    }
                    else
                    {
                        error = "That file is not an MP3, WAV or AIFF.";
                    }

                    return results;
                }

                if (!Directory.Exists(trimmed))
                {
                    error = "That path does not exist.";
                    return results;
                }

                Walk(trimmed, 0, results);

                if (results.Count == 0)
                {
                    error = "No MP3, WAV or AIFF files were found there.";
                }
                else if (results.Count >= MaxFiles)
                {
                    error = $"Stopped after {MaxFiles} files. Add a more specific folder for the rest.";
                }
            }
            catch (UnauthorizedAccessException)
            {
                error = "That folder is not readable.";
            }
            catch (Exception ex)
            {
                error = "The folder could not be read: " + ex.GetType().Name;
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        private static void Walk(string folder, int depth, List<string> results)
        {
            if (depth > MaxDepth || results.Count >= MaxFiles)
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception)
            {
                // One unreadable folder must not abort the whole scan.
                return;
            }

            foreach (var file in files)
            {
                if (results.Count >= MaxFiles)
                {
                    return;
                }

                // Skip macOS resource forks and hidden files; they are never real media.
                var name = Path.GetFileName(file);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TrackInfo.IsSupported(file))
                {
                    results.Add(file);
                }
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var child in folders)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                Walk(child, depth + 1, results);
            }
        }

        /// <summary>Expands a leading <c>~</c> to the user's home folder.</summary>
        public static string Expand(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '~')
            {
                return path;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                return path;
            }

            return path.Length == 1 ? home : Path.Combine(home, path.Substring(2));
        }

        /// <summary>
        /// The folder AI Deck offers by default: <c>~/Music/AI Deck</c>. Created on first run
        /// so there is somewhere obvious to drop files.
        /// </summary>
        public static string DefaultMusicFolder
        {
            get
            {
                try
                {
                    var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    if (!string.IsNullOrEmpty(music))
                    {
                        return Path.Combine(music, "AI Deck");
                    }
                }
                catch (Exception)
                {
                    // Falls through.
                }

                return AppPaths.DataFolder;
            }
        }
    }
}
