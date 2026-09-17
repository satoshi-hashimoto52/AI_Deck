using AIDeck.Core.Diagnostics;
using AIDeck.Core.Library;

namespace AIDeck.Platform
{
    /// <summary>Persists the track catalogue (FR-009).</summary>
    public sealed class LibraryStore
    {
        private readonly DiagnosticLog _log;
        private readonly string _path;

        public LibraryStore(DiagnosticLog log, string path = null)
        {
            _log = log;
            _path = string.IsNullOrEmpty(path) ? AppPaths.LibraryFile : path;
        }

        public string Path => _path;

        /// <summary>
        /// Fills <paramref name="library"/> from disk. Reports how many entries were restored
        /// and how many were damaged — a partially readable file loses only the damaged rows.
        /// </summary>
        public LibraryLoadReport Load(TrackLibrary library)
        {
            if (library == null)
            {
                return new LibraryLoadReport(false, 0, 0, "No library was supplied.");
            }

            var text = AtomicFile.TryReadAllText(_path, out var error);
            if (text == null)
            {
                if (error != null)
                {
                    _log?.Warning("Library", "The library file could not be read: " + error);
                    return new LibraryLoadReport(false, 0, 0, error);
                }

                // No file yet: a first launch, not a failure.
                return new LibraryLoadReport(true, 0, 0, string.Empty);
            }

            var report = library.LoadJson(text);
            if (!report.Success)
            {
                _log?.Error("Library", report.Reason);
            }
            else if (report.Skipped > 0)
            {
                _log?.Warning("Library", $"{report.Skipped} damaged library entries were skipped.");
            }

            return report;
        }

        public bool Save(TrackLibrary library)
        {
            if (library == null)
            {
                return false;
            }

            if (AtomicFile.TryWriteAllText(_path, library.ToJson(), out var error))
            {
                return true;
            }

            _log?.Error("Library", "The library could not be saved: " + error);
            return false;
        }
    }
}
