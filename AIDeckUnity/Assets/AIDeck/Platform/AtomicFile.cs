using System;
using System.IO;
using System.Text;

namespace AIDeck.Platform
{
    /// <summary>
    /// Write-then-rename file helper.
    ///
    /// The library and the settings are rewritten whenever they change. Writing in place
    /// means a crash or a power loss mid-write leaves a truncated document, and the next
    /// launch loses the whole library. Writing to a temporary file and renaming makes the
    /// replacement atomic on every file system AI Deck runs on, so the worst case is losing
    /// the most recent change rather than the file.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/>.
        /// Returns false with a reason instead of throwing — a failed settings save must not
        /// interrupt a DJ set.
        /// </summary>
        public static bool TryWriteAllText(string path, string contents, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No path was supplied.";
                return false;
            }

            var temporary = path + ".tmp";
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(temporary, contents ?? string.Empty, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                TryDelete(temporary);
                return false;
            }
        }

        /// <summary>Reads a file. Returns null when it is absent or unreadable.</summary>
        public static string TryReadAllText(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return null;
            }
        }

        public static bool TryWriteAllBytes(string path, byte[] contents, out string error)
        {
            error = null;
            var temporary = path + ".tmp";
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllBytes(temporary, contents ?? Array.Empty<byte>());

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                TryDelete(temporary);
                return false;
            }
        }

        public static byte[] TryReadAllBytes(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover .tmp is harmless; it is overwritten on the next save.
            }
        }

        private static string Describe(Exception ex)
        {
            switch (ex)
            {
                case UnauthorizedAccessException _:
                    return "The folder is not writable.";
                case DirectoryNotFoundException _:
                    return "The folder no longer exists.";
                case IOException _:
                    return "The file could not be written (disk full or file in use).";
                default:
                    return ex.GetType().Name;
            }
        }
    }
}
