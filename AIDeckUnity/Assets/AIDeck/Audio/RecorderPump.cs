using System;
using System.Threading;
using AIDeck.Core.Audio;
using AIDeck.Core.Diagnostics;

namespace AIDeck.Audio
{
    /// <summary>
    /// Drives <see cref="WavRecorder.Drain"/> on a background thread.
    ///
    /// The audio thread must never touch the disk (NFR-001) and the main thread must never
    /// block on it (NFR-002), so the only place left is a dedicated thread. It wakes every
    /// <see cref="IntervalMilliseconds"/>, which is far more often than the recorder's four
    /// seconds of buffering needs — the margin is there so an occasional slow write does not
    /// turn into dropped audio.
    /// </summary>
    public sealed class RecorderPump : IDisposable
    {
        public const int IntervalMilliseconds = 50;

        private readonly WavRecorder _recorder;
        private readonly DiagnosticLog _log;
        private Thread _thread;
        private volatile bool _running;

        public RecorderPump(WavRecorder recorder, DiagnosticLog log)
        {
            _recorder = recorder;
            _log = log;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            if (_running || _recorder == null)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Run)
            {
                Name = "AIDeck.RecorderPump",
                IsBackground = true
            };
            _thread.Start();
        }

        /// <summary>Stops the pump and waits briefly for the last write to finish.</summary>
        public void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            var thread = _thread;
            _thread = null;

            try
            {
                // Long enough for an in-flight write, short enough not to stall a quit.
                thread?.Join(1000);
            }
            catch (Exception)
            {
                // A pump that will not join is a background thread; the process can still exit.
            }
        }

        private void Run()
        {
            while (_running)
            {
                try
                {
                    _recorder.Drain();
                }
                catch (Exception ex)
                {
                    // WavRecorder already converts I/O faults into its Failed state; reaching
                    // here means something unexpected, which is logged rather than swallowed
                    // (NFR-007) and must not kill the thread mid-recording.
                    _log?.Exception("Recorder", ex, "while draining");
                }

                Thread.Sleep(IntervalMilliseconds);
            }

            try
            {
                _recorder.Drain();
            }
            catch (Exception ex)
            {
                _log?.Exception("Recorder", ex, "on final drain");
            }
        }

        public void Dispose() => Stop();
    }
}
