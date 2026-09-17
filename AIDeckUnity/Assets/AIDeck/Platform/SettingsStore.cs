using AIDeck.Core.Diagnostics;
using AIDeck.Core.Settings;

namespace AIDeck.Platform
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> (FR-080…FR-084).
    ///
    /// A save failure is reported and logged but never thrown: losing a preference is an
    /// annoyance, interrupting a set is not acceptable.
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly DiagnosticLog _log;
        private readonly string _path;

        public SettingsStore(DiagnosticLog log, string path = null)
        {
            _log = log;
            _path = string.IsNullOrEmpty(path) ? AppPaths.SettingsFile : path;
        }

        public string Path => _path;

        /// <summary>Never returns null. A missing or corrupt file yields repaired defaults.</summary>
        public AppSettings Load()
        {
            var text = AtomicFile.TryReadAllText(_path, out var error);
            if (error != null)
            {
                _log?.Warning("Settings", "Could not read the settings file; defaults were used.");
            }

            var settings = AppSettings.Deserialize(text);
            if (settings.WasRepaired)
            {
                _log?.Warning("Settings", settings.RepairReason);
            }

            return settings;
        }

        public bool Save(AppSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            if (AtomicFile.TryWriteAllText(_path, settings.Serialize(), out var error))
            {
                return true;
            }

            _log?.Warning("Settings", "Settings could not be saved: " + error);
            return false;
        }
    }
}
