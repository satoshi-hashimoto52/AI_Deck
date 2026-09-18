using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AIDeck.Core.Analysis;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.Platform;
using UnityEngine;

namespace AIDeck.Audio
{
    /// <summary>Progress of a running import, for the Mac UI.</summary>
    public readonly struct ImportProgress
    {
        public ImportProgress(int completed, int total, string currentFile)
        {
            Completed = completed;
            Total = total;
            CurrentFile = currentFile ?? string.Empty;
        }

        public int Completed { get; }
        public int Total { get; }

        /// <summary>File name only, never the full path (NFR-006).</summary>
        public string CurrentFile { get; }

        public float Fraction => Total <= 0 ? 1f : Mathf.Clamp01(Completed / (float)Total);
    }

    /// <summary>
    /// Adds audio files to the library (FR-001…FR-009).
    ///
    /// Each file is decoded once, and that single decode yields everything the library needs:
    /// duration, the waveform envelope and the tempo estimate. Analysis runs on a worker
    /// thread and the decode yields between files, so importing a folder never blocks the
    /// main thread (NFR-002).
    ///
    /// A file that will not decode is skipped with a reason and the import continues — one
    /// damaged file must not abort the batch (FR-005).
    /// </summary>
    public sealed class TrackImporter : MonoBehaviour
    {
        private DiagnosticLog _log;
        private TrackLibrary _library;
        private Coroutine _running;

        public bool IsImporting => _running != null;

        /// <summary>Raised on the main thread as each file completes.</summary>
        public event Action<ImportProgress> Progress;

        /// <summary>Raised on the main thread when a batch finishes, with one report per file.</summary>
        public event Action<List<AddReport>> Completed;

        public void Initialise(DiagnosticLog log, TrackLibrary library)
        {
            _log = log;
            _library = library;
        }

        /// <summary>Starts importing. A second call while one is running is ignored.</summary>
        public bool Import(IReadOnlyList<string> paths)
        {
            if (_running != null || _library == null || paths == null || paths.Count == 0)
            {
                return false;
            }

            _running = StartCoroutine(ImportRoutine(paths));
            return true;
        }

        private IEnumerator ImportRoutine(IReadOnlyList<string> paths)
        {
            var reports = new List<AddReport>(paths.Count);
            var total = paths.Count;

            for (var i = 0; i < total; i++)
            {
                var path = paths[i];
                var fileName = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
                Progress?.Invoke(new ImportProgress(i, total, fileName));

                if (!TrackInfo.IsSupported(path))
                {
                    reports.Add(new AddReport(path, AddTrackResult.UnsupportedFormat,
                        "Only MP3, WAV and AIFF files can be added.", null));
                    continue;
                }

                // A duplicate path costs nothing to detect and saves a full decode.
                if (_library.GetByPath(path) != null)
                {
                    reports.Add(new AddReport(path, AddTrackResult.DuplicatePath,
                        "This file is already in the library.", _library.GetByPath(path)));
                    continue;
                }

                TrackLoadResult load = default;
                yield return TrackLoader.Load(path, r => load = r);

                if (!load.Success)
                {
                    reports.Add(new AddReport(path, AddTrackResult.Invalid, load.Error, null));
                    _log?.Warning("Library", $"Skipped {fileName}: {load.Error}");
                    continue;
                }

                // A file that decodes to nothing is not a track. It is usually an interrupted
                // recording or a placeholder, and adding it gives a row that can be loaded onto
                // a deck and will never play.
                if (load.DurationSeconds <= 0d || load.Source.FrameCount <= 0)
                {
                    const string reason = "The file contains no audio.";
                    reports.Add(new AddReport(path, AddTrackResult.Invalid, reason, null));
                    _log?.Warning("Library", $"Skipped {fileName}: {reason}");
                    continue;
                }

                var size = TryGetFileSize(path);
                var candidate = new TrackInfo(
                    null,
                    path,
                    null,
                    null,
                    load.DurationSeconds,
                    TrackInfo.FormatFromPath(path),
                    DateTime.UtcNow,
                    0d,
                    size);

                var report = _library.Add(candidate);
                reports.Add(report);

                if (report.Succeeded)
                {
                    // Analysis runs off the main thread; the decoded samples are already here,
                    // so this costs no extra I/O.
                    yield return AnalyseRoutine(report.Track, load.Source.Samples,
                        load.Source.Channels, load.Source.SampleRate);
                }

                // One file per frame at minimum, so the UI keeps painting during a large import.
                yield return null;
            }

            Progress?.Invoke(new ImportProgress(total, total, string.Empty));
            _running = null;
            Completed?.Invoke(reports);
        }

        /// <summary>
        /// Computes the waveform envelope and tempo on a worker thread, then applies the
        /// result on the main thread. Exposed so a deck load can analyse a track that was
        /// restored from a library file written before analysis existed.
        /// </summary>
        public IEnumerator AnalyseRoutine(TrackInfo track, float[] samples, int channels, int sampleRate)
        {
            if (track == null || samples == null || samples.Length == 0)
            {
                yield break;
            }

            WaveformData waveform = null;
            var bpm = BpmResult.None;
            var done = false;
            Exception failure = null;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    waveform = WaveformBuilder.Build(samples, channels, sampleRate);
                    bpm = BpmAnalyzer.Analyse(samples, channels, sampleRate);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    Volatile.Write(ref done, true);
                }
            });

            while (!Volatile.Read(ref done))
            {
                yield return null;
            }

            if (failure != null)
            {
                // Analysis is a nicety: the track is still playable without a waveform or a
                // tempo, so this is logged and the import continues (NFR-007).
                _log?.Exception("Analysis", failure, $"analysing {track.Title}");
                yield break;
            }

            if (waveform != null && !waveform.IsEmpty)
            {
                WaveformCache.TrySave(track.Id, waveform);
            }

            if (bpm.IsUsable)
            {
                _library.Update(track.WithBpm(bpm.Bpm));
            }
            else
            {
                _log?.Debug("Analysis", $"No confident tempo for {track.Title}.");
            }
        }

        private static long TryGetFileSize(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }
    }
}
