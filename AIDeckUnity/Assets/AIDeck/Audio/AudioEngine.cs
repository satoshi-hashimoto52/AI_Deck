using System;
using System.Collections;
using System.IO;
using AIDeck.Core.Audio;
using AIDeck.Core.Deck;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Mixer;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using AIDeck.Platform;
using UnityEngine;

namespace AIDeck.Audio
{
    /// <summary>
    /// The host's audio engine: two decks, a mixer, effects and a recorder.
    ///
    /// Owns the authoritative state. The Mac UI and the network layer both act through this
    /// object and read the same <see cref="BuildSnapshot"/>, which is what keeps the two
    /// interfaces from drifting apart.
    ///
    /// Division of labour: the <see cref="DeckModel"/>s hold the rules, the
    /// <see cref="DeckChannel"/>s hold the audio, and <see cref="Update"/> is the one place
    /// that reconciles them each frame.
    /// </summary>
    public sealed class AudioEngine : MonoBehaviour
    {
        /// <summary>Safety margin for a fade to finish before the voice is actually stopped.</summary>
        private const float FadeTimeoutSeconds = 0.5f;

        private DiagnosticLog _log;
        private AppSettings _settings;

        private AudioOutput _output;
        private MasterBus _master;
        private WavRecorder _recorder;
        private RecorderPump _pump;

        private readonly DeckChannel[] _channels = new DeckChannel[2];
        private readonly DeckModel[] _decks = new DeckModel[2];
        private readonly Coroutine[] _loads = new Coroutine[2];
        private readonly float[] _stopTimers = new float[2];
        private readonly bool[] _stopping = new bool[2];

        private int _sampleRate;
        private int _channelCount;
        private bool _shuttingDown;

        public MixerState Mixer { get; } = new MixerState();

        public DeckModel DeckA => _decks[0];
        public DeckModel DeckB => _decks[1];

        public int SampleRate => _sampleRate;
        public int ChannelCount => _channelCount;

        public bool IsRecording => _recorder != null && _recorder.IsRecording;
        public double RecordingSeconds => _recorder?.ElapsedSeconds ?? 0d;
        public string RecordingPath { get; private set; } = string.Empty;
        public string RecordingFileName => string.IsNullOrEmpty(RecordingPath) ? string.Empty : Path.GetFileName(RecordingPath);
        public string RecordingFailure => _recorder?.FailureReason ?? string.Empty;

        public float MasterPeak => _master?.Peak ?? 0f;
        public float MasterRms => _master?.Rms ?? 0f;
        public bool MasterClipping => _master?.IsClipping ?? false;

        /// <summary>Raised when a deck finishes loading, successfully or not.</summary>
        public event Action<DeckId, bool, string> DeckLoadCompleted;

        /// <summary>Raised when something happens the user should be told about.</summary>
        public event Action<string> Notice;

        public DeckModel Deck(DeckId deck) => _decks[(int)deck];

        public DeckChannel Channel(DeckId deck) => _channels[(int)deck];

        // ---------------------------------------------------------------- lifecycle

        /// <summary>Builds the audio graph. Call once, before anything else.</summary>
        public void Initialise(DiagnosticLog log, AppSettings settings)
        {
            _log = log;
            _settings = settings ?? new AppSettings();

            var configuration = AudioSettings.GetConfiguration();
            _sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            _channelCount = ChannelsFor(configuration.speakerMode);

            for (var i = 0; i < 2; i++)
            {
                var id = (DeckId)i;
                _decks[i] = new DeckModel(id);
                _channels[i] = new DeckChannel(id);
                _channels[i].Prepare(_sampleRate, _channelCount);

                var index = i;
                _decks[i].SeekRequested += seconds => OnModelSeek(index, seconds);
            }

            _master = new MasterBus(_sampleRate, _channelCount) { TargetGain = _settings.MasterVolume };
            Mixer.MasterGain = _settings.MasterVolume;
            Mixer.CrossfaderCurveType = _settings.CrossfaderCurve;
            DeckA.Tempo.RangePercent = _settings.TempoRangePercent;
            DeckB.Tempo.RangePercent = _settings.TempoRangePercent;

            _recorder = new WavRecorder();
            _master.Recorder = _recorder;
            _pump = new RecorderPump(_recorder, _log);

            BuildGraphObjects();

            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;

            _log?.Info("Audio", $"Engine ready at {_sampleRate} Hz, {_channelCount} channels.");
        }

        private void BuildGraphObjects()
        {
            var listenerObject = new GameObject("AIDeck Audio Output");
            listenerObject.transform.SetParent(transform, false);
            listenerObject.AddComponent<AudioListener>();
            _output = listenerObject.AddComponent<AudioOutput>();

            var keepAlive = new GameObject("Keep Alive");
            keepAlive.transform.SetParent(listenerObject.transform, false);
            keepAlive.AddComponent<AudioSource>();
            keepAlive.AddComponent<AudioGraphKeepAlive>();

            _output.Attach(_channels[0], _channels[1], _master);
        }

        private static int ChannelsFor(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }

        /// <summary>
        /// The user changed output device or format. The DSP objects are sized for a specific
        /// rate and channel count, so they are rebuilt rather than reused.
        /// </summary>
        private void OnAudioConfigurationChanged(bool deviceChanged)
        {
            var configuration = AudioSettings.GetConfiguration();
            var newRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            var newChannels = ChannelsFor(configuration.speakerMode);
            if (newRate == _sampleRate && newChannels == _channelCount)
            {
                return;
            }

            _log?.Info("Audio", $"Output changed to {newRate} Hz, {newChannels} channels; rebuilding.");

            var wasRecording = IsRecording;
            if (wasRecording)
            {
                StopRecording();
                RaiseNotice("Recording stopped because the audio output changed.");
            }

            _output?.Detach();

            _sampleRate = newRate;
            _channelCount = newChannels;
            for (var i = 0; i < 2; i++)
            {
                _channels[i].Prepare(_sampleRate, _channelCount);
            }

            var gain = _master.TargetGain;
            _master = new MasterBus(_sampleRate, _channelCount) { TargetGain = gain, Recorder = _recorder };
            _output?.Attach(_channels[0], _channels[1], _master);
        }

        private void OnDestroy() => Shutdown();

        private void OnApplicationQuit() => Shutdown();

        /// <summary>
        /// Fades both decks out, finishes any recording and detaches the graph. Nothing may be
        /// audible after this returns (NFR-010).
        /// </summary>
        public void Shutdown()
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;

            for (var i = 0; i < 2; i++)
            {
                _channels[i]?.ResetDsp();
                _channels[i]?.Voice.Clear();
            }

            if (IsRecording)
            {
                StopRecording();
            }

            _pump?.Dispose();
            _recorder?.Dispose();
            _output?.Detach();
        }

        // ---------------------------------------------------------------- per frame

        private void Update()
        {
            if (_shuttingDown || _master == null)
            {
                return;
            }

            var delta = Time.deltaTime;
            for (var i = 0; i < 2; i++)
            {
                SyncDeck(i, delta);
            }

            ApplyMixer();
            _master.TargetGain = Mixer.MasterGain;
        }

        private void SyncDeck(int index, float delta)
        {
            var model = _decks[index];
            var channel = _channels[index];
            var voice = channel.Voice;

            // 1. Advance the platter model; BRAKE completing pauses the transport here.
            model.TickMotion(delta);

            // 2. The voice owns the playhead. Tell the model where it actually is, and follow
            //    the model back if it wants a correction it could not make itself.
            if (voice.HasSource)
            {
                var correction = model.ReportPosition(voice.PositionSeconds);
                if (correction.HasValue)
                {
                    voice.SeekSeconds(correction.Value);
                    channel.RequestFadeIn();
                }
            }

            if (voice.ConsumeReachedEnd())
            {
                model.Pause();
                RaiseNotice($"Deck {model.Deck.ToDisplayName()} reached the end of the track.");
            }

            // 3. Rate and loop window follow the model every frame.
            voice.Rate = model.EffectiveRate;
            voice.SetLoop(model.Loop.IsActive, model.Loop.InSeconds, model.Loop.OutSeconds);

            // 4. Transport, with fades on both edges (§9).
            if (model.IsPlaying)
            {
                _stopping[index] = false;
                if (!voice.IsPlaying && voice.HasSource)
                {
                    channel.RequestFadeIn();
                    voice.IsPlaying = true;
                }
            }
            else if (voice.IsPlaying)
            {
                if (!_stopping[index])
                {
                    _stopping[index] = true;
                    _stopTimers[index] = 0f;
                }

                _stopTimers[index] += delta;

                // The channel gain target drops to zero below; once the ramp has run out the
                // voice is stopped. The timeout covers the case where the audio device is not
                // producing callbacks at all, so a stop can never hang.
                if (channel.IsSilent || _stopTimers[index] > FadeTimeoutSeconds)
                {
                    voice.IsPlaying = false;
                    _stopping[index] = false;
                    channel.Voice.Rate = 1f;
                }
            }
        }

        private void ApplyMixer()
        {
            for (var i = 0; i < 2; i++)
            {
                var id = (DeckId)i;
                var channel = _channels[i];
                var model = _decks[i];

                // A deck that is no longer playing fades to silence regardless of where its
                // fader sits. _stopping is deliberately not consulted here: it means "still
                // fading out", so treating it as playing would hold the gain up and the fade
                // would never complete.
                channel.TargetGain = model.IsPlaying ? Mixer.EffectiveDeckGain(id) : 0f;
                channel.Muted = Mixer.Mute(id);
                channel.FilterKnob = Mixer.Filter(id);
                channel.EchoEnabled = Mixer.Echo(id);
            }
        }

        private void OnModelSeek(int index, double seconds)
        {
            var channel = _channels[index];
            channel.Voice.SeekSeconds(seconds);
            channel.RequestFadeIn();
        }

        // ---------------------------------------------------------------- loading

        /// <summary>
        /// Loads a track onto a deck (FR-020, FR-021). Replaces any load already in flight.
        /// </summary>
        public void LoadTrack(DeckId deck, TrackInfo track)
        {
            var index = (int)deck;
            if (track == null)
            {
                return;
            }

            if (_loads[index] != null)
            {
                StopCoroutine(_loads[index]);
                _loads[index] = null;
            }

            _loads[index] = StartCoroutine(LoadRoutine(deck, track));
        }

        private IEnumerator LoadRoutine(DeckId deck, TrackInfo track)
        {
            var index = (int)deck;
            var model = _decks[index];
            var channel = _channels[index];

            // Silence the outgoing track before its samples are pulled away.
            model.BeginLoad();
            channel.Voice.IsPlaying = false;
            channel.Voice.Clear();
            channel.ResetDsp();
            _stopping[index] = false;

            TrackLoadResult result = default;
            yield return TrackLoader.Load(track.FilePath, r => result = r);

            if (!result.Success)
            {
                model.FailLoad(result.Error);
                _log?.Warning("Audio", $"Deck {deck.ToDisplayName()} load failed: {result.Error}");
                DeckLoadCompleted?.Invoke(deck, false, result.Error);
                _loads[index] = null;
                yield break;
            }

            channel.Voice.SetSource(result.Source);
            model.CompleteLoad(track.Id, result.DurationSeconds, track.Bpm);
            channel.RequestFadeIn();

            _log?.Info("Audio", $"Deck {deck.ToDisplayName()} loaded {track.Title}.");
            DeckLoadCompleted?.Invoke(deck, true, string.Empty);
            _loads[index] = null;
        }

        /// <summary>Unloads a deck (recovery path out of an error state).</summary>
        public void Eject(DeckId deck)
        {
            var index = (int)deck;
            if (_loads[index] != null)
            {
                StopCoroutine(_loads[index]);
                _loads[index] = null;
            }

            _decks[index].Eject();
            _channels[index].Voice.IsPlaying = false;
            _channels[index].Voice.Clear();
            _channels[index].ResetDsp();
            _stopping[index] = false;
        }

        // ---------------------------------------------------------------- recording

        /// <summary>
        /// Starts recording the master output (FR-050). Returns false with the reason on
        /// <see cref="RecordingFailure"/>; playback is never affected (FR-055).
        /// </summary>
        public bool StartRecording()
        {
            if (_recorder == null || IsRecording)
            {
                return false;
            }

            _recorder.ClearFailure();

            var folder = string.IsNullOrWhiteSpace(_settings.RecordingFolder)
                ? AppPaths.DefaultRecordingFolder
                : _settings.RecordingFolder;

            if (!AppPaths.EnsureFolder(folder))
            {
                RaiseNotice("The recording folder could not be created.");
                _log?.Error("Recorder", "The recording folder could not be created.");
                return false;
            }

            var path = Path.Combine(folder, WavRecorder.BuildFileName(DateTime.Now));
            if (!_recorder.Start(path, _sampleRate, _channelCount))
            {
                RaiseNotice(_recorder.FailureReason);
                _log?.Error("Recorder", _recorder.FailureReason);
                return false;
            }

            RecordingPath = path;
            _pump.Start();
            _log?.Info("Recorder", "Recording started.");
            return true;
        }

        /// <summary>Stops recording and finalises the file (FR-053, FR-054).</summary>
        public string StopRecording()
        {
            if (_recorder == null)
            {
                return null;
            }

            _pump.Stop();
            var path = _recorder.Stop();

            if (path == null)
            {
                var reason = string.IsNullOrEmpty(_recorder.FailureReason)
                    ? "The recording could not be saved."
                    : _recorder.FailureReason;
                RaiseNotice(reason);
                _log?.Error("Recorder", reason);
                return null;
            }

            if (_recorder.DroppedSamples > 0)
            {
                // Reported rather than hidden: the file has a gap and the user should know.
                var message = $"The recording dropped {_recorder.DroppedSamples} samples because the disk could not keep up.";
                RaiseNotice(message);
                _log?.Warning("Recorder", message);
            }

            _log?.Info("Recorder", "Recording saved.");
            return path;
        }

        // ---------------------------------------------------------------- safety

        /// <summary>
        /// Releases every continuous control on both decks and, when
        /// <paramref name="stopPlayback"/> is set, fades both decks out and pauses them.
        ///
        /// This is the disconnect path of §9 and FR-066. It is also what the Mac UI's panic
        /// button does.
        /// </summary>
        public void AllStop(bool stopPlayback)
        {
            for (var i = 0; i < 2; i++)
            {
                _decks[i].ReleaseContinuousControls();
                if (stopPlayback)
                {
                    _decks[i].Pause();
                }
            }

            if (stopPlayback)
            {
                _log?.Info("Audio", "All decks stopped.");
            }
        }

        /// <summary>Applies the configured disconnect policy (§9, default is the safe stop).</summary>
        public void ApplyDisconnectPolicy()
        {
            AllStop(_settings.OnDisconnect == DisconnectPolicy.StopPlayback);
        }

        private void RaiseNotice(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Notice?.Invoke(message);
            }
        }

        // ---------------------------------------------------------------- snapshot

        /// <summary>Builds the state broadcast to the controller and rendered by the Mac UI (FR-064).</summary>
        public StateSnapshot BuildSnapshot(int libraryRevision, string notice = null)
        {
            var snapshot = StateSnapshot.Empty;
            snapshot.HostTimeMs = NetMessage.NowMs();
            snapshot.LibraryRevision = libraryRevision;
            snapshot.DeckA = DeckA.ToSnapshot(_channels[0].PeakLevel);
            snapshot.DeckB = DeckB.ToSnapshot(_channels[1].PeakLevel);
            snapshot.CaptureMixer(Mixer);
            snapshot.MasterPeak = MasterPeak;
            snapshot.MasterRms = MasterRms;
            snapshot.MasterClipping = MasterClipping;
            snapshot.IsRecording = IsRecording;
            snapshot.RecordingSeconds = RecordingSeconds;
            snapshot.RecordingFileName = RecordingFileName;
            snapshot.Notice = notice ?? string.Empty;
            return snapshot;
        }

        /// <summary>Clears the latched clip indicator once the user has seen it.</summary>
        public void ResetClipIndicator() => _master?.ResetClip();
    }
}
