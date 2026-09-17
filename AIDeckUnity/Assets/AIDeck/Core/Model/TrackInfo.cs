using System;
using System.Globalization;
using AIDeck.Core.Json;

namespace AIDeck.Core.Model
{
    /// <summary>Audio container formats AI Deck V1 accepts (FR-001..FR-003).</summary>
    public enum TrackFormat
    {
        Unknown = 0,
        Mp3 = 1,
        Wav = 2,
        Aiff = 3
    }

    /// <summary>
    /// Immutable metadata record for one library entry (FR-007, FR-008).
    /// The record never holds audio samples and never holds a writable handle to the
    /// source file: AI Deck treats the user's music as read-only (FR-010, NFR-008).
    /// </summary>
    public sealed class TrackInfo : IEquatable<TrackInfo>
    {
        public const string UnknownArtist = "Unknown";

        public TrackInfo(
            string id,
            string filePath,
            string title,
            string artist,
            double durationSeconds,
            TrackFormat format,
            DateTime addedUtc,
            double bpm = 0d,
            long fileSizeBytes = 0L)
        {
            Id = string.IsNullOrEmpty(id) ? MakeId(filePath) : id;
            FilePath = filePath ?? string.Empty;
            Title = string.IsNullOrWhiteSpace(title) ? DeriveTitle(FilePath) : title.Trim();
            Artist = string.IsNullOrWhiteSpace(artist) ? UnknownArtist : artist.Trim();
            DurationSeconds = AudioSafety.SanitizeDouble(durationSeconds, 0d, 24d * 60d * 60d, 0d);
            Format = format;
            AddedUtc = addedUtc.Kind == DateTimeKind.Utc ? addedUtc : addedUtc.ToUniversalTime();
            Bpm = NormalizeBpm(bpm);
            FileSizeBytes = fileSizeBytes < 0 ? 0 : fileSizeBytes;
        }

        /// <summary>Stable identifier derived from the absolute path; used on the wire.</summary>
        public string Id { get; }

        public string FilePath { get; }
        public string Title { get; }

        /// <summary>Artist, or the generation source for AI-produced material.</summary>
        public string Artist { get; }

        public double DurationSeconds { get; }
        public TrackFormat Format { get; }
        public DateTime AddedUtc { get; }

        /// <summary>Analysed tempo, or 0 when unknown (FR-008 allows "when analysable").</summary>
        public double Bpm { get; }

        public long FileSizeBytes { get; }

        public bool HasBpm => Bpm > 0d;

        /// <summary>mm:ss for UI display; hours are folded into minutes (a DJ set track never needs h).</summary>
        public string DurationDisplay => FormatDuration(DurationSeconds);

        public static string FormatDuration(double seconds)
        {
            if (!AudioSafety.IsFinite(seconds) || seconds < 0d)
            {
                seconds = 0d;
            }

            var total = (int)Math.Floor(seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", total / 60, total % 60);
        }

        /// <summary>
        /// Tempo values outside 40–250 BPM are treated as a failed analysis rather than
        /// propagated: a bogus BPM would poison SYNC (FR-030).
        /// </summary>
        public static double NormalizeBpm(double bpm)
        {
            if (!AudioSafety.IsFinite(bpm) || bpm < 40d || bpm > 250d)
            {
                return 0d;
            }

            return Math.Round(bpm, 2);
        }

        public TrackInfo WithBpm(double bpm) =>
            new TrackInfo(Id, FilePath, Title, Artist, DurationSeconds, Format, AddedUtc, bpm, FileSizeBytes);

        public TrackInfo WithDuration(double durationSeconds) =>
            new TrackInfo(Id, FilePath, Title, Artist, durationSeconds, Format, AddedUtc, Bpm, FileSizeBytes);

        /// <summary>Case-insensitive match over title, artist and file name (FR-011).</summary>
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var q = query.Trim();
            return Contains(Title, q) || Contains(Artist, q) || Contains(FileName, q);
        }

        public string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(FilePath))
                {
                    return string.Empty;
                }

                var slash = FilePath.LastIndexOfAny(new[] { '/', '\\' });
                return slash >= 0 ? FilePath.Substring(slash + 1) : FilePath;
            }
        }

        private static bool Contains(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Title fallback when no tag is available: the file name without extension.</summary>
        public static string DeriveTitle(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return "Untitled";
            }

            var slash = filePath.LastIndexOfAny(new[] { '/', '\\' });
            var name = slash >= 0 ? filePath.Substring(slash + 1) : filePath;
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                name = name.Substring(0, dot);
            }

            return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
        }

        /// <summary>
        /// Deterministic 64-bit FNV-1a of the path, rendered as hex. Deterministic ids keep
        /// deck assignments valid across restarts without storing a separate id table.
        /// </summary>
        public static string MakeId(string filePath)
        {
            var text = filePath ?? string.Empty;
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                var hash = offset;
                foreach (var c in text)
                {
                    hash ^= c;
                    hash *= prime;
                }

                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        public static TrackFormat FormatFromPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return TrackFormat.Unknown;
            }

            var dot = filePath.LastIndexOf('.');
            if (dot < 0 || dot == filePath.Length - 1)
            {
                return TrackFormat.Unknown;
            }

            var ext = filePath.Substring(dot + 1).ToLowerInvariant();
            switch (ext)
            {
                case "mp3": return TrackFormat.Mp3;
                case "wav":
                case "wave": return TrackFormat.Wav;
                case "aif":
                case "aiff":
                case "aifc": return TrackFormat.Aiff;
                default: return TrackFormat.Unknown;
            }
        }

        public static bool IsSupported(string filePath) => FormatFromPath(filePath) != TrackFormat.Unknown;

        public JsonValue ToJson() =>
            JsonValue.NewObject()
                .Set("id", Id)
                .Set("path", FilePath)
                .Set("title", Title)
                .Set("artist", Artist)
                .Set("duration", DurationSeconds)
                .Set("format", (double)(int)Format)
                .Set("addedUtc", FormatTimestamp(AddedUtc))
                .Set("bpm", Bpm)
                .Set("size", FileSizeBytes);

        /// <summary>
        /// Rebuilds a record from JSON. Returns null when the entry has no usable path,
        /// so a partially corrupt library file loses only the damaged rows (FR-084 spirit).
        /// </summary>
        public static TrackInfo FromJson(JsonValue json)
        {
            if (json == null || !json.IsObject)
            {
                return null;
            }

            var path = json["path"].AsString(string.Empty);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var added = ParseTimestamp(json["addedUtc"]);

            var format = (TrackFormat)json["format"].AsInt((int)TrackFormat.Unknown);
            if (!Enum.IsDefined(typeof(TrackFormat), format))
            {
                format = FormatFromPath(path);
            }

            return new TrackInfo(
                json["id"].AsString(null),
                path,
                json["title"].AsString(null),
                json["artist"].AsString(null),
                json["duration"].AsDouble(0d),
                format,
                added,
                json["bpm"].AsDouble(0d),
                json["size"].AsLong(0L));
        }

        /// <summary>
        /// Timestamps are stored as ISO-8601 text, not as tick counts. A .NET tick count for
        /// any modern date needs more than the 53 bits a JSON number carries exactly, so
        /// writing it as a number would silently shift the stored time by a fraction of a
        /// millisecond on every save/load round trip.
        /// </summary>
        internal static string FormatTimestamp(DateTime value) =>
            value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        /// <summary>
        /// Reads a timestamp written by <see cref="FormatTimestamp"/>. A value that cannot be
        /// read falls back to "now" rather than failing the whole library entry.
        /// </summary>
        internal static DateTime ParseTimestamp(JsonValue value)
        {
            var text = value?.AsString(null);
            if (!string.IsNullOrEmpty(text) &&
                DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return DateTime.UtcNow;
        }

        public bool Equals(TrackInfo other) => other != null && string.Equals(Id, other.Id, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as TrackInfo);

        public override int GetHashCode() => Id?.GetHashCode() ?? 0;

        public override string ToString() => $"{Title} — {Artist} ({DurationDisplay})";
    }
}
