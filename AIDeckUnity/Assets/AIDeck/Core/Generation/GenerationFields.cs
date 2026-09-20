using System;
using System.Globalization;

namespace AIDeck.Core.Generation
{
    /// <summary>What the user typed, before it is known to be usable.</summary>
    public sealed class GenerationFields
    {
        public string Title = string.Empty;
        public string Prompt = string.Empty;
        public string Lyrics = string.Empty;
        public string Duration = "30";
        public string Bpm = "118";
        public string Key = "A minor";
        public string TimeSignature = "4";
        public string VocalLanguage = "ja";
        public string Seed = string.Empty;

        public GenerationFields Clone() => new GenerationFields
        {
            Title = Title,
            Prompt = Prompt,
            Lyrics = Lyrics,
            Duration = Duration,
            Bpm = Bpm,
            Key = Key,
            TimeSignature = TimeSignature,
            VocalLanguage = VocalLanguage,
            Seed = Seed
        };
    }

    /// <summary>The outcome of checking one set of fields.</summary>
    public readonly struct ValidationResult
    {
        private ValidationResult(bool ok, string field, string message)
        {
            IsValid = ok;
            Field = field;
            Message = message;
        }

        public bool IsValid { get; }

        /// <summary>Which field is wrong, so the panel can point at it. Empty when valid.</summary>
        public string Field { get; }

        public string Message { get; }

        public static ValidationResult Ok() => new ValidationResult(true, string.Empty, string.Empty);

        public static ValidationResult Invalid(string field, string message) =>
            new ValidationResult(false, field, message);
    }

    /// <summary>
    /// Checks a generation request before anything is sent.
    ///
    /// The Python bridge validates the same rules again, and that duplication is deliberate:
    /// the UI's job is to stop a bad request being *sent* — and to say which box is wrong
    /// while the user still has what they typed — while the bridge's job is to stop a bad
    /// request being *run*, whatever sent it.
    ///
    /// The ranges match <c>GenerationRequest.validate</c> in the Python client. If one moves,
    /// the other must; a test asserts the boundaries on both sides.
    /// </summary>
    public static class GenerationValidator
    {
        public const float MinimumDuration = 10f;
        public const float MaximumDuration = 600f;
        public const int MinimumBpm = 30;
        public const int MaximumBpm = 300;

        private static readonly string[] AllowedTimeSignatures = { "2", "3", "4", "6" };

        public static ValidationResult Validate(GenerationFields fields)
        {
            if (fields == null)
            {
                return ValidationResult.Invalid("title", "Nothing to generate.");
            }

            if (string.IsNullOrWhiteSpace(fields.Title))
            {
                return ValidationResult.Invalid("title", "A title is required.");
            }

            if (string.IsNullOrWhiteSpace(fields.Prompt))
            {
                return ValidationResult.Invalid("prompt", "A style description is required.");
            }

            if (!TryParseFinite(fields.Duration, out var duration))
            {
                return ValidationResult.Invalid("duration", "Length must be a number of seconds.");
            }

            if (duration < MinimumDuration || duration > MaximumDuration)
            {
                return ValidationResult.Invalid(
                    "duration",
                    $"Length must be between {MinimumDuration:0} and {MaximumDuration:0} seconds.");
            }

            if (!TryParseFinite(fields.Bpm, out var bpm))
            {
                return ValidationResult.Invalid("bpm", "BPM must be a number.");
            }

            if (bpm < MinimumBpm || bpm > MaximumBpm)
            {
                return ValidationResult.Invalid(
                    "bpm", $"BPM must be between {MinimumBpm} and {MaximumBpm}.");
            }

            if (Array.IndexOf(AllowedTimeSignatures, (fields.TimeSignature ?? string.Empty).Trim()) < 0)
            {
                return ValidationResult.Invalid(
                    "timeSignature", "Time signature must be 2, 3, 4 or 6.");
            }

            var seed = (fields.Seed ?? string.Empty).Trim();
            if (seed.Length > 0 && !long.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return ValidationResult.Invalid("seed", "Seed must be a whole number, or left empty.");
            }

            return ValidationResult.Ok();
        }

        /// <summary>
        /// Parses a number and refuses NaN and Infinity.
        ///
        /// <c>float.TryParse</c> happily returns true for "NaN" and "Infinity", and a NaN
        /// duration would travel all the way to the engine before anything noticed.
        /// </summary>
        private static bool TryParseFinite(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }
    }
}
