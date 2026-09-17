using System;
using AIDeck.Core.Fx;
using AIDeck.Core.Model;

namespace AIDeck.Core.Audio
{
    /// <summary>
    /// The master stage: sums the decks, applies master gain, limits, meters, and taps the
    /// result for the recorder.
    ///
    /// The recording is taken **after** the limiter so the file matches what was heard, and
    /// the limiter runs before anything leaves the bus so that an overloaded mix saturates
    /// instead of producing the hard-clip crackle FR-047 forbids (also NFR-009).
    /// </summary>
    public sealed class MasterBus
    {
        /// <summary>Gain ramp length, matching the deck channels (§9).</summary>
        public const float FadeSeconds = 0.012f;

        private readonly LevelMeter _meter;
        private readonly int _sampleRate;
        private readonly int _channels;
        private float _currentGain;
        private volatile float _targetGain = 0.8f;
        private readonly float _fadeStep;

        public MasterBus(int sampleRate, int channels)
        {
            _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            _channels = channels < 1 ? 1 : channels;
            _meter = new LevelMeter(_sampleRate);
            _fadeStep = 1f / Math.Max(1f, FadeSeconds * _sampleRate);
        }

        public int SampleRate => _sampleRate;
        public int Channels => _channels;

        /// <summary>Master output level (FR-046). Ramped, never stepped.</summary>
        public float TargetGain
        {
            get => _targetGain;
            set => _targetGain = AudioSafety.Sanitize01(value);
        }

        public float Peak => _meter.Peak;
        public float Rms => _meter.Rms;

        /// <summary>True when at least one sample has clipped since the last <see cref="ResetClip"/> (FR-045).</summary>
        public bool IsClipping => _meter.IsClipping;

        public long ClipCount => _meter.ClipCount;

        /// <summary>The recorder to feed, or null. Set from the main thread.</summary>
        public WavRecorder Recorder { get; set; }

        public bool IsSilent => _currentGain <= 1e-4f;

        /// <summary>Forces the ramp to restart from silence, used when the device is (re)started.</summary>
        public void Reset()
        {
            _currentGain = 0f;
            _meter.Reset();
        }

        public void ResetClip() => _meter.ResetClip();

        /// <summary>
        /// Audio thread. Applies the master stage to <paramref name="mix"/> in place and hands
        /// the result to the recorder.
        /// </summary>
        public void Process(float[] mix, int channels)
        {
            if (mix == null || channels < 1)
            {
                return;
            }

            var target = _targetGain;
            var frames = mix.Length / channels;

            for (var frame = 0; frame < frames; frame++)
            {
                if (_currentGain < target)
                {
                    _currentGain = Math.Min(target, _currentGain + _fadeStep);
                }
                else if (_currentGain > target)
                {
                    _currentGain = Math.Max(target, _currentGain - _fadeStep);
                }

                var offset = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = offset + channel;
                    mix[index] = AudioSafety.SoftLimit(mix[index] * _currentGain);
                }
            }

            _meter.Analyse(mix, channels);

            // Submit only copies into a lock-free ring; the file is written elsewhere.
            Recorder?.Submit(mix, mix.Length);
        }
    }
}
