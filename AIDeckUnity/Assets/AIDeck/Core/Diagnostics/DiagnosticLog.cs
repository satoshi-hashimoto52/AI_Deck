using System;
using System.Collections.Generic;

namespace AIDeck.Core.Diagnostics
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>One log entry. Held in memory only; nothing here is written to disk by Core.</summary>
    public readonly struct LogEntry
    {
        public LogEntry(DateTime timestampUtc, LogLevel level, string category, string message)
        {
            TimestampUtc = timestampUtc;
            Level = level;
            Category = category ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public DateTime TimestampUtc { get; }
        public LogLevel Level { get; }
        public string Category { get; }
        public string Message { get; }

        public override string ToString() =>
            $"{TimestampUtc:HH:mm:ss} [{Level}] {Category}: {Message}";
    }

    /// <summary>
    /// Bounded in-memory ring of diagnostic entries, backing the Mac UI's log summary (§5.6).
    ///
    /// Two requirements shape it. NFR-006 forbids personal data, account information and
    /// file contents in the log, so callers pass a category and a short message and the log
    /// itself refuses anything longer than <see cref="MaxMessageLength"/>. NFR-007 forbids
    /// swallowing exceptions, so the log distinguishes the user-facing notice from the
    /// diagnostic detail rather than merging them.
    /// </summary>
    public sealed class DiagnosticLog
    {
        public const int MaxMessageLength = 512;

        private readonly Queue<LogEntry> _entries = new Queue<LogEntry>();
        private readonly object _lock = new object();
        private readonly int _capacity;

        public DiagnosticLog(int capacity = 500)
        {
            _capacity = capacity < 16 ? 16 : capacity;
        }

        /// <summary>Entries at or below this level are discarded.</summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        /// <summary>Raised for every accepted entry, on the calling thread.</summary>
        public event Action<LogEntry> EntryAdded;

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        public long ErrorCount { get; private set; }
        public long WarningCount { get; private set; }

        /// <summary>The most recent error message, for the status line. Empty when there is none.</summary>
        public string LastError { get; private set; } = string.Empty;

        public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
        public void Info(string category, string message) => Write(LogLevel.Info, category, message);
        public void Warning(string category, string message) => Write(LogLevel.Warning, category, message);
        public void Error(string category, string message) => Write(LogLevel.Error, category, message);

        /// <summary>
        /// Records an exception without hiding it: the type and message go to the log while
        /// the caller decides separately what the user is shown (NFR-007).
        /// </summary>
        public void Exception(string category, Exception exception, string context = null)
        {
            if (exception == null)
            {
                return;
            }

            var prefix = string.IsNullOrEmpty(context) ? string.Empty : context + " — ";
            Write(LogLevel.Error, category, $"{prefix}{exception.GetType().Name}: {exception.Message}");
        }

        public void Write(LogLevel level, string category, string message)
        {
            if (level < MinimumLevel)
            {
                return;
            }

            var entry = new LogEntry(DateTime.UtcNow, level, Truncate(category, 32), Truncate(message, MaxMessageLength));

            lock (_lock)
            {
                _entries.Enqueue(entry);
                while (_entries.Count > _capacity)
                {
                    _entries.Dequeue();
                }

                if (level == LogLevel.Error)
                {
                    ErrorCount++;
                    LastError = entry.Message;
                }
                else if (level == LogLevel.Warning)
                {
                    WarningCount++;
                }
            }

            EntryAdded?.Invoke(entry);
        }

        /// <summary>Most recent entries, newest last.</summary>
        public List<LogEntry> Recent(int count = 50)
        {
            lock (_lock)
            {
                var result = new List<LogEntry>(Math.Min(count, _entries.Count));
                var skip = Math.Max(0, _entries.Count - count);
                var index = 0;
                foreach (var entry in _entries)
                {
                    if (index++ < skip)
                    {
                        continue;
                    }

                    result.Add(entry);
                }

                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                ErrorCount = 0;
                WarningCount = 0;
                LastError = string.Empty;
            }
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var cleaned = text.Replace('\n', ' ').Replace('\r', ' ');
            return cleaned.Length > maxLength ? cleaned.Substring(0, maxLength) + "…" : cleaned;
        }
    }
}
