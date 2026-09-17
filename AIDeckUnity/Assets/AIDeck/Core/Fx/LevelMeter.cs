using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Fx
{
    /// <summary>
    /// Peak / RMS meter with clip detection (FR-045, plus the level display of §5.4).
    ///
    /// The peak reading falls back with a fixed release so the UI shows a readable bar
    /// instead of flicker, while <see cref="ClipCount"/> latches every overload so a brief
    /// transient is not missed between UI frames.
    /// </summary>
    public sealed class LevelMeter
    {
        /// <summary>How long the peak indicator takes to fall by the full scale.</summary>
        public const float PeakReleaseSeconds = 1.2f;

        private readonly int _sampleRate;
        private float _peak;

        public LevelMeter(int sampleRate)
        {
            _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
        }

        /// <summary>Held peak level, 0..1.</summary>
        public float Peak => _peak;

        /// <summary>RMS of the most recent analysed block, 0..1.</summary>
        public float Rms { get; private set; }

        /// <summary>Number of samples that reached or exceeded the clip threshold since the last reset.</summary>
        public long ClipCount { get; private set; }

        /// <summary>True when at least one sample clipped since the last <see cref="ResetClip"/>.</summary>
        public bool IsClipping => ClipCount > 0;

        public float PeakDb => AudioSafety.LinearToDb(_peak);

        /// <summary>Analyses an interleaved block. Does not modify the buffer.</summary>
        public void Analyse(float[] buffer, int channels)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return;
            }

            var blockPeak = 0f;
            double sum = 0d;
            var counted = 0;

            for (var i = 0; i < buffer.Length; i++)
            {
                var sample = buffer[i];
                if (float.IsNaN(sample) || float.IsInfinity(sample))
                {
                    // A non-finite sample is itself a fault worth surfacing as an overload.
                    ClipCount++;
                    continue;
                }

                var magnitude = sample < 0f ? -sample : sample;
                if (magnitude > blockPeak)
                {
                    blockPeak = magnitude;
                }

                if (magnitude >= AudioSafety.ClipThreshold)
                {
                    ClipCount++;
                }

                sum += (double)sample * sample;
                counted++;
            }

            Rms = counted > 0 ? AudioSafety.Sanitize01((float)Math.Sqrt(sum / counted)) : 0f;

            if (blockPeak > _peak)
            {
                _peak = AudioSafety.Sanitize01(blockPeak);
                return;
            }

            // Linear release proportional to the block length, so the fall rate does not
            // depend on the DSP buffer size.
            var frames = channels > 0 ? buffer.Length / channels : buffer.Length;
            var seconds = frames / (float)_sampleRate;
            var decay = seconds / PeakReleaseSeconds;
            _peak = AudioSafety.Sanitize01(_peak - decay);
        }

        public void ResetClip() => ClipCount = 0;

        public void Reset()
        {
            _peak = 0f;
            Rms = 0f;
            ClipCount = 0;
        }
    }
}
