using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Fx
{
    /// <summary>
    /// Feedback delay for the ECHO button (FR-044).
    ///
    /// The wet level is ramped rather than switched, so toggling ECHO mid-phrase does not
    /// produce the click that a hard gate would. Feedback is hard-capped below unity, which
    /// is what guarantees the tail decays instead of building into the runaway level that
    /// FR-047 and NFR-009 forbid.
    /// </summary>
    public sealed class EchoProcessor
    {
        /// <summary>Longest delay the buffer is sized for.</summary>
        public const float MaxDelaySeconds = 2f;

        /// <summary>Hard ceiling on feedback. Above this the tail would grow without bound.</summary>
        public const float MaxFeedback = 0.85f;

        public const float DefaultDelaySeconds = 0.375f;
        public const float DefaultFeedback = 0.45f;
        public const float DefaultWet = 0.45f;

        /// <summary>Time the wet level takes to fade in or out when ECHO is toggled.</summary>
        public const float WetRampSeconds = 0.05f;

        private readonly int _sampleRate;
        private float[] _buffer;
        private int _channels;
        private int _writeIndex;
        private int _delaySamples;
        private float _feedback = DefaultFeedback;
        private float _targetWet;
        private float _currentWet;
        private float _wetStep;

        public EchoProcessor(int sampleRate, int channels = 2)
        {
            _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            DelaySeconds = DefaultDelaySeconds;
            EnsureChannels(channels);
            RecomputeWetStep();
        }

        public bool IsEnabled { get; private set; }

        /// <summary>True while the tail is still audible, so the host knows the deck is not silent yet.</summary>
        public bool IsTailActive => _currentWet > 1e-4f;

        public float CurrentWet => _currentWet;

        private float _delaySeconds = DefaultDelaySeconds;

        /// <summary>Delay time. Clamped to [10 ms, 2 s].</summary>
        public float DelaySeconds
        {
            get => _delaySeconds;
            set
            {
                _delaySeconds = AudioSafety.Sanitize(value, 0.01f, MaxDelaySeconds, DefaultDelaySeconds);
                _delaySamples = Math.Max(1, (int)(_delaySeconds * _sampleRate));
                if (_buffer != null && _channels > 0)
                {
                    var maxFrames = _buffer.Length / _channels;
                    if (_delaySamples >= maxFrames)
                    {
                        _delaySamples = maxFrames - 1;
                    }
                }
            }
        }

        /// <summary>Feedback amount. Clamped to [0, 0.85].</summary>
        public float Feedback
        {
            get => _feedback;
            set => _feedback = AudioSafety.Sanitize(value, 0f, MaxFeedback, DefaultFeedback);
        }

        private float _wetLevel = DefaultWet;

        /// <summary>Wet mix when enabled. Clamped to [0, 1].</summary>
        public float WetLevel
        {
            get => _wetLevel;
            set
            {
                _wetLevel = AudioSafety.Sanitize01(value);
                if (IsEnabled)
                {
                    _targetWet = _wetLevel;
                }
            }
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            _targetWet = enabled ? _wetLevel : 0f;
        }

        /// <summary>
        /// Clears the delay line and silences the tail immediately. Used when a deck is
        /// ejected or the connection drops: the tail must not outlive the deck (NFR-010).
        /// </summary>
        public void Reset()
        {
            if (_buffer != null)
            {
                System.Array.Clear(_buffer, 0, _buffer.Length);
            }

            _writeIndex = 0;
            _currentWet = 0f;
            _targetWet = IsEnabled ? _wetLevel : 0f;
        }

        public void EnsureChannels(int channels)
        {
            var count = channels < 1 ? 1 : channels;
            if (_channels == count && _buffer != null)
            {
                return;
            }

            _channels = count;
            var frames = (int)(MaxDelaySeconds * _sampleRate) + 1;
            _buffer = new float[frames * count];
            _writeIndex = 0;
            DelaySeconds = _delaySeconds;
        }

        private void RecomputeWetStep()
        {
            var samples = Math.Max(1f, WetRampSeconds * _sampleRate);
            _wetStep = 1f / samples;
        }

        /// <summary>
        /// Processes an interleaved buffer in place.
        /// Returns without touching the audio once the tail has fully decayed and ECHO is
        /// off, so a disabled effect costs nothing on the audio thread.
        /// </summary>
        public void ProcessInterleaved(float[] buffer, int channels)
        {
            if (buffer == null || channels < 1)
            {
                return;
            }

            EnsureChannels(channels);
            if (_wetStep <= 0f)
            {
                RecomputeWetStep();
            }

            if (!IsEnabled && _currentWet <= 0f)
            {
                return;
            }

            var frames = buffer.Length / channels;
            var bufferFrames = _buffer.Length / _channels;

            for (var frame = 0; frame < frames; frame++)
            {
                // One ramp step per frame keeps both channels perfectly in step.
                if (_currentWet < _targetWet)
                {
                    _currentWet = Math.Min(_targetWet, _currentWet + _wetStep);
                }
                else if (_currentWet > _targetWet)
                {
                    _currentWet = Math.Max(_targetWet, _currentWet - _wetStep);
                }

                var readIndex = _writeIndex - _delaySamples;
                if (readIndex < 0)
                {
                    readIndex += bufferFrames;
                }

                for (var ch = 0; ch < channels; ch++)
                {
                    var sampleIndex = frame * channels + ch;
                    var dry = buffer[sampleIndex];
                    if (float.IsNaN(dry) || float.IsInfinity(dry))
                    {
                        dry = 0f;
                    }

                    var delayed = _buffer[readIndex * _channels + ch];
                    if (float.IsNaN(delayed) || float.IsInfinity(delayed))
                    {
                        delayed = 0f;
                    }

                    // Write the dry signal plus the decaying tail back into the line.
                    _buffer[_writeIndex * _channels + ch] = dry + delayed * _feedback;

                    buffer[sampleIndex] = dry + delayed * _currentWet;
                }

                _writeIndex++;
                if (_writeIndex >= bufferFrames)
                {
                    _writeIndex = 0;
                }
            }
        }
    }
}
