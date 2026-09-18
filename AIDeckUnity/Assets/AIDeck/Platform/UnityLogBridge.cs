using System;
using AIDeck.Core.Diagnostics;
using UnityEngine;

namespace AIDeck.Platform
{
    /// <summary>
    /// Mirrors <see cref="DiagnosticLog"/> entries into Unity's log.
    ///
    /// The diagnostic log is an in-memory ring, which is fine for the status line but useless
    /// when the thing being diagnosed is a shipped `.app` on someone else's desk. Unity writes
    /// its log to a known file — `~/Library/Logs/AI Deck/AI Deck/Player.log` on macOS — so
    /// forwarding there is what makes a field report possible.
    ///
    /// Only entries the diagnostic log already accepted are forwarded, so its level filter
    /// still governs volume: at the default `Info` level a session produces a handful of lines
    /// per user action and nothing at all while merely playing.
    /// </summary>
    public sealed class UnityLogBridge : IDisposable
    {
        private readonly DiagnosticLog _log;

        public UnityLogBridge(DiagnosticLog log)
        {
            _log = log;
            if (_log != null)
            {
                _log.EntryAdded += OnEntry;
            }
        }

        private static void OnEntry(LogEntry entry)
        {
            var line = $"[AI Deck] {entry.Category}: {entry.Message}";
            switch (entry.Level)
            {
                case LogLevel.Error:
                    Debug.LogError(line);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }
        }

        public void Dispose()
        {
            if (_log != null)
            {
                _log.EntryAdded -= OnEntry;
            }
        }
    }
}
