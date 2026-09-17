using System;
using System.Threading;
using AIDeck.Core.Fx;
using AIDeck.Core.Model;

namespace AIDeck.Core.Audio
{
    /// <summary>
    /// One deck's audio chain: voice → filter → echo → gain → meter.
    ///
    /// Everything here runs on the audio thread and is written to that discipline: no
    /// allocation after <see cref="Prepare"/>, no locks, no I/O, no Unity types (NFR-001).
    /// Parameters arrive from the main thread through plain fields; the worst case is a
    /// parameter taking effect one buffer late, which is inaudible.
    /// </summary>
    public sealed class DeckChannel
    {
        /// <summary>Gain ramp length for play, pause, load, cue and disconnect (§9).</summary>
        public const float FadeSeconds = 0.012f;

        /// <summary>
        /// Shorter ramp used after a loop wrap. A full 12 ms fade at a loop point would be an
        /// audible dip on a tight loop; 3 ms is long enough to remove the edge and short
        /// enough to stay inaudible.
        /// </summary>
        public const float LoopFadeSeconds = 0.003f;

        private readonly DeckId _deck;
        private MultiChannelFilter _filter;
        private EchoProcessor _echo;
        private LevelMeter _meter;
        private float[] _scratch = Array.Empty<float>();

        private int _sampleRate;
        private int _channels;

        private float _currentGain;
        private volatile float _targetGain;
        private float _fadeStep;
        private float _loopFadeStep;

        private volatile float _filterKnob;
        private volatile bool _echoEnabled;
        private volatile bool _muted;

        /// <summary>Set by the main thread to force an instant de-click ramp from silence.</summary>
        private int _restartFade;

        public DeckChannel(DeckId deck)
        {
            _deck = deck;
            Voice = new DeckVoice();
        }

        public DeckId Deck => _deck;

        public DeckVoice Voice { get; }

        /// <summary>Post-fader peak for the channel meter (§5.4).</summary>
        public float PeakLevel => _meter?.Peak ?? 0f;

        public float CurrentGain => _currentGain;

        /// <summary>
        /// True when the channel is fully faded out and its echo tail has decayed. The host
        /// waits for this before pausing the voice, so a stop never cuts a live buffer (§9).
        /// </summary>
        public bool IsSilent => _currentGain <= 1e-4f && (_echo == null || !_echo.IsTailActive);

        /// <summary>Target gain: channel fader × crossfader × mute. Ramped, never stepped.</summary>
        public float TargetGain
        {
            get => _targetGain;
            set => _targetGain = AudioSafety.Sanitize01(value);
        }

        /// <summary>Bipolar FILTER knob (FR-043).</summary>
        public float FilterKnob
        {
            get => _filterKnob;
            set => _filterKnob = AudioSafety.SanitizeBipolar(value);
        }

        /// <summary>ECHO on/off (FR-044).</summary>
        public bool EchoEnabled
        {
            get => _echoEnabled;
            set => _echoEnabled = value;
        }

        /// <summary>MUTE (FR-042). Applied as a gain target so it also fades.</summary>
        public bool Muted
        {
            get => _muted;
            set => _muted = value;
        }

        /// <summary>Allocates the DSP objects for a device format. Main thread only.</summary>
        public void Prepare(int sampleRate, int channels)
        {
            _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            _channels = channels < 1 ? 1 : channels;

            _filter = new MultiChannelFilter(_sampleRate, _channels);
            _echo = new EchoProcessor(_sampleRate, _channels);
            _meter = new LevelMeter(_sampleRate);

            _fadeStep = 1f / Math.Max(1f, FadeSeconds * _sampleRate);
            _loopFadeStep = 1f / Math.Max(1f, LoopFadeSeconds * _sampleRate);
            _currentGain = 0f;
        }

        /// <summary>Ensures the scratch buffer matches the block size. Audio thread, allocates only on a size change.</summary>
        private void EnsureScratch(int length)
        {
            if (_scratch.Length != length)
            {
                _scratch = new float[length];
            }
        }

        /// <summary>
        /// Clears every piece of state that could outlive the track: filter memory, echo
        /// tail, meter and gain. Called on load, eject and the disconnect stop, so nothing
        /// from the previous track can be heard after it (NFR-010).
        /// </summary>
        public void ResetDsp()
        {
            _filter?.Reset();
            _echo?.Reset();
            _meter?.Reset();
            _currentGain = 0f;
            Interlocked.Exchange(ref _restartFade, 1);
        }

        /// <summary>Asks for a short de-click ramp from silence after a seek or a loop wrap.</summary>
        public void RequestFadeIn() => Interlocked.Exchange(ref _restartFade, 1);

        /// <summary>
        /// Audio thread. Renders this deck and adds it into <paramref name="mix"/>.
        /// </summary>
        public void RenderInto(float[] mix, int channels, int sampleRate)
        {
            if (mix == null || _filter == null || channels < 1)
            {
                return;
            }

            if (channels != _channels || sampleRate != _sampleRate)
            {
                // The device changed under us. Rebuilding here would allocate on the audio
                // thread, so the block is skipped; the host rebuilds on the next frame.
                return;
            }

            EnsureScratch(mix.Length);

            var rendered = Voice.Render(_scratch, channels, sampleRate);
            var echoActive = _echoEnabled || (_echo?.IsTailActive ?? false);

            if (rendered == 0 && !echoActive && _currentGain <= 1e-4f)
            {
                // Nothing to contribute and nothing left ringing.
                _meter?.Analyse(_scratch, channels);
                return;
            }

            _filter.SetKnob(_filterKnob);
            _filter.ProcessInterleaved(_scratch, channels);

            _echo.SetEnabled(_echoEnabled);
            _echo.ProcessInterleaved(_scratch, channels);

            var target = _muted ? 0f : _targetGain;
            var step = Interlocked.Exchange(ref _restartFade, 0) != 0 ? _loopFadeStep : _fadeStep;
            if (step == _loopFadeStep)
            {
                _currentGain = 0f;
            }

            var frames = _scratch.Length / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                // One gain step per frame keeps the channels of a stereo pair identical.
                if (_currentGain < target)
                {
                    _currentGain = Math.Min(target, _currentGain + step);
                }
                else if (_currentGain > target)
                {
                    _currentGain = Math.Max(target, _currentGain - step);
                }

                var offset = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = offset + channel;
                    var sample = _scratch[index] * _currentGain;
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                    {
                        sample = 0f;
                    }

                    _scratch[index] = sample;
                    mix[index] += sample;
                }
            }

            _meter.Analyse(_scratch, channels);
        }
    }
}
