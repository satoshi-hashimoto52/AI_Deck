using System;
using AIDeck.Core.Json;
using AIDeck.Core.Mixer;
using AIDeck.Core.Model;

namespace AIDeck.Core.Settings
{
    /// <summary>What the host does with its audio when the controller disappears (§9).</summary>
    public enum DisconnectPolicy : byte
    {
        /// <summary>Fade both decks out and stop. The safe default the issue mandates.</summary>
        StopPlayback = 0,

        /// <summary>Keep playing, but release every held control. For unattended playback.</summary>
        ContinuePlayback = 1
    }

    /// <summary>
    /// Persisted user settings (FR-080 to FR-084).
    ///
    /// Every property sanitises on assignment and <see cref="FromJson"/> repairs anything it
    /// cannot read, so a truncated or hand-edited settings file degrades to defaults instead
    /// of producing an unusable app — that is FR-084 stated as code.
    /// </summary>
    public sealed class AppSettings
    {
        public const int FileVersion = 1;
        public const string FileName = "aideck-settings.json";

        private float _masterVolume = 0.8f;
        private string _lastHostAddress = string.Empty;
        private string _recordingFolder = string.Empty;
        private float _tempoRangePercent = Deck.TempoControl.DefaultRangePercent;
        private float _uiScale = 1f;

        /// <summary>Most recent host address the controller connected to (FR-080).</summary>
        public string LastHostAddress
        {
            get => _lastHostAddress;
            set => _lastHostAddress = Trim(value, 128);
        }

        /// <summary>Port that goes with <see cref="LastHostAddress"/>.</summary>
        public int LastHostPort { get; set; } = Net.ProtocolInfo.DefaultTcpPort;

        /// <summary>Whether to try discovery before falling back to the stored address (FR-060, FR-061).</summary>
        public bool AutoDiscoverHost { get; set; } = true;

        /// <summary>Folder recordings are written to (FR-081). Empty means "use the platform default".</summary>
        public string RecordingFolder
        {
            get => _recordingFolder;
            set => _recordingFolder = Trim(value, 1024);
        }

        /// <summary>Master output level, restored at launch (FR-082).</summary>
        public float MasterVolume
        {
            get => _masterVolume;
            set => _masterVolume = AudioSafety.Sanitize01(value);
        }

        /// <summary>Crossfader shape (FR-083).</summary>
        public CrossfaderCurveType CrossfaderCurve { get; set; } = CrossfaderCurveType.Smooth;

        /// <summary>Default tempo fader range for both decks (FR-083).</summary>
        public float TempoRangePercent
        {
            get => _tempoRangePercent;
            set
            {
                var control = new Deck.TempoControl { RangePercent = value };
                _tempoRangePercent = control.RangePercent;
            }
        }

        /// <summary>Controller UI scale (FR-083). Clamped to a range that keeps 44 pt targets legal.</summary>
        public float UiScale
        {
            get => _uiScale;
            set => _uiScale = AudioSafety.Sanitize(value, 0.8f, 1.4f, 1f);
        }

        /// <summary>What the host does when the controller drops (§9). Default is the safe stop.</summary>
        public DisconnectPolicy OnDisconnect { get; set; } = DisconnectPolicy.StopPlayback;

        /// <summary>Keep the screen awake while a deck is playing (FR-076).</summary>
        public bool PreventSleepWhilePlaying { get; set; } = true;

        /// <summary>Show the diagnostic overlay. Off by default.</summary>
        public bool ShowDiagnostics { get; set; }

        /// <summary>True when the last load had to repair or discard the stored document (FR-084).</summary>
        public bool WasRepaired { get; private set; }

        /// <summary>Why the settings were repaired, for the status area. Empty when clean.</summary>
        public string RepairReason { get; private set; } = string.Empty;

        public JsonValue ToJson() =>
            JsonValue.NewObject()
                .Set("version", FileVersion)
                .Set("lastHostAddress", LastHostAddress)
                .Set("lastHostPort", LastHostPort)
                .Set("autoDiscoverHost", AutoDiscoverHost)
                .Set("recordingFolder", RecordingFolder)
                .Set("masterVolume", MasterVolume)
                .Set("crossfaderCurve", (double)(int)CrossfaderCurve)
                .Set("tempoRangePercent", TempoRangePercent)
                .Set("uiScale", UiScale)
                .Set("onDisconnect", (double)(int)OnDisconnect)
                .Set("preventSleepWhilePlaying", PreventSleepWhilePlaying)
                .Set("showDiagnostics", ShowDiagnostics);

        public string Serialize() => JsonWriter.Write(ToJson());

        /// <summary>
        /// Restores settings from text. Never throws and never returns null: an unreadable
        /// document produces a default instance flagged with <see cref="WasRepaired"/>.
        /// </summary>
        public static AppSettings Deserialize(string text)
        {
            var settings = new AppSettings();

            if (string.IsNullOrWhiteSpace(text))
            {
                settings.MarkRepaired("No settings file was found; defaults were used.");
                return settings;
            }

            if (!JsonParser.TryParse(text, out var root, out var error))
            {
                settings.MarkRepaired($"The settings file was unreadable ({error}); defaults were restored.");
                return settings;
            }

            if (!root.IsObject)
            {
                settings.MarkRepaired("The settings file had an unexpected shape; defaults were restored.");
                return settings;
            }

            var version = root["version"].AsInt(0);
            if (version > FileVersion)
            {
                settings.MarkRepaired(
                    $"The settings file was written by a newer version of AI Deck (v{version}); defaults were used.");
                return settings;
            }

            settings.LastHostAddress = root["lastHostAddress"].AsString(string.Empty);
            settings.LastHostPort = ClampPort(root["lastHostPort"].AsInt(Net.ProtocolInfo.DefaultTcpPort));
            settings.AutoDiscoverHost = root["autoDiscoverHost"].AsBool(true);
            settings.RecordingFolder = root["recordingFolder"].AsString(string.Empty);
            settings.MasterVolume = root["masterVolume"].AsFloat(0.8f);
            settings.CrossfaderCurve = ToCurve(root["crossfaderCurve"].AsInt((int)CrossfaderCurveType.Smooth));
            settings.TempoRangePercent = root["tempoRangePercent"].AsFloat(Deck.TempoControl.DefaultRangePercent);
            settings.UiScale = root["uiScale"].AsFloat(1f);
            settings.OnDisconnect = ToPolicy(root["onDisconnect"].AsInt((int)DisconnectPolicy.StopPlayback));
            settings.PreventSleepWhilePlaying = root["preventSleepWhilePlaying"].AsBool(true);
            settings.ShowDiagnostics = root["showDiagnostics"].AsBool(false);

            // A file that parses but is missing the core keys is more likely truncated than
            // intentionally minimal, so say so rather than pretending it loaded cleanly.
            if (!root.Has("masterVolume") && !root.Has("lastHostAddress"))
            {
                settings.MarkRepaired("The settings file was incomplete; missing values were filled with defaults.");
            }

            return settings;
        }

        public AppSettings Clone()
        {
            var clone = Deserialize(Serialize());
            clone.WasRepaired = false;
            clone.RepairReason = string.Empty;
            return clone;
        }

        public void ClearRepairFlag()
        {
            WasRepaired = false;
            RepairReason = string.Empty;
        }

        private void MarkRepaired(string reason)
        {
            WasRepaired = true;
            RepairReason = reason;
        }

        private static int ClampPort(int port) =>
            port < 1 || port > 65535 ? Net.ProtocolInfo.DefaultTcpPort : port;

        // Both enums are byte-backed for the wire format, so the stored JSON number is
        // range-checked directly rather than through Enum.IsDefined, which would need the
        // value boxed as the exact underlying type.
        private static CrossfaderCurveType ToCurve(int value) =>
            value >= 0 && value <= (int)CrossfaderCurveType.Sharp
                ? (CrossfaderCurveType)value
                : CrossfaderCurveType.Smooth;

        private static DisconnectPolicy ToPolicy(int value) =>
            value >= 0 && value <= (int)DisconnectPolicy.ContinuePlayback
                ? (DisconnectPolicy)value
                : DisconnectPolicy.StopPlayback;

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            return trimmed.Length > maxLength ? trimmed.Substring(0, maxLength) : trimmed;
        }
    }
}
