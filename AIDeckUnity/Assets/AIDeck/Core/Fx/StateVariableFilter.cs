using System;
using AIDeck.Core.Model;

namespace AIDeck.Core.Fx
{
    /// <summary>
    /// Zero-delay-feedback state variable filter (Andrew Simper's topology-preserving
    /// transform form), one instance per audio channel.
    ///
    /// Chosen over a biquad because it stays stable while the cutoff is swept quickly —
    /// exactly what the FILTER knob does — and because its two integrator states are easy
    /// to reset on a discontinuity, which the safety rules require whenever a deck is
    /// loaded or stopped.
    /// </summary>
    public sealed class StateVariableFilter
    {
        private readonly int _sampleRate;
        private float _ic1Eq;
        private float _ic2Eq;
        private float _g;
        private float _k = 1f / FilterParams.Resonance;
        private float _a1;
        private float _a2;
        private float _a3;
        private FilterKind _kind = FilterKind.Bypass;

        public StateVariableFilter(int sampleRate)
        {
            _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            Configure(FilterKind.Bypass, FilterParams.LowPassTopHz);
        }

        public FilterKind Kind => _kind;

        /// <summary>Sets filter kind and cutoff. Safe to call every buffer.</summary>
        public void Configure(FilterKind kind, float cutoffHz)
        {
            _kind = kind;

            // Never let the cutoff approach Nyquist: tan() blows up there.
            var nyquist = _sampleRate * 0.5f;
            var maxCutoff = nyquist * 0.49f;
            var f = AudioSafety.Sanitize(cutoffHz, FilterParams.MinCutoffHz, maxCutoff, maxCutoff);

            _g = (float)Math.Tan(Math.PI * f / _sampleRate);
            _k = 1f / FilterParams.Resonance;
            _a1 = 1f / (1f + _g * (_g + _k));
            _a2 = _g * _a1;
            _a3 = _g * _a2;
        }

        /// <summary>Configures directly from the bipolar knob value.</summary>
        public void ConfigureFromKnob(float knob) =>
            Configure(FilterParams.KindFor(knob), FilterParams.CutoffHz(knob));

        /// <summary>Clears the integrator states. Call on load, stop, and seek to avoid a thump.</summary>
        public void Reset()
        {
            _ic1Eq = 0f;
            _ic2Eq = 0f;
        }

        /// <summary>Processes one sample. Bypass returns the input untouched.</summary>
        public float Process(float input)
        {
            if (_kind == FilterKind.Bypass)
            {
                return input;
            }

            if (float.IsNaN(input) || float.IsInfinity(input))
            {
                // A bad sample would persist in the integrator states forever. Drop it
                // and clear the filter rather than let it poison the deck's output.
                Reset();
                return 0f;
            }

            var v3 = input - _ic2Eq;
            var v1 = _a1 * _ic1Eq + _a2 * v3;
            var v2 = _ic2Eq + _a2 * _ic1Eq + _a3 * v3;
            _ic1Eq = 2f * v1 - _ic1Eq;
            _ic2Eq = 2f * v2 - _ic2Eq;

            var output = _kind == FilterKind.LowPass
                ? v2
                : input - _k * v1 - v2;

            if (float.IsNaN(output) || float.IsInfinity(output))
            {
                Reset();
                return 0f;
            }

            return output;
        }
    }

    /// <summary>
    /// Convenience wrapper holding one <see cref="StateVariableFilter"/> per channel and
    /// processing an interleaved buffer in place — the shape Unity's OnAudioFilterRead uses.
    /// </summary>
    public sealed class MultiChannelFilter
    {
        private StateVariableFilter[] _channels;
        private readonly int _sampleRate;
        private float _lastKnob = float.NaN;

        public MultiChannelFilter(int sampleRate, int channels = 2)
        {
            _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
            EnsureChannels(channels);
        }

        public int ChannelCount => _channels.Length;

        public FilterKind Kind => _channels[0].Kind;

        public void EnsureChannels(int channels)
        {
            var count = channels < 1 ? 1 : channels;
            if (_channels != null && _channels.Length == count)
            {
                return;
            }

            _channels = new StateVariableFilter[count];
            for (var i = 0; i < count; i++)
            {
                _channels[i] = new StateVariableFilter(_sampleRate);
            }

            _lastKnob = float.NaN;
        }

        /// <summary>Re-configures only when the knob actually moved, keeping the audio thread cheap.</summary>
        public void SetKnob(float knob)
        {
            var k = AudioSafety.SanitizeBipolar(knob);
            if (_lastKnob == k)
            {
                return;
            }

            _lastKnob = k;
            var kind = FilterParams.KindFor(k);
            var cutoff = FilterParams.CutoffHz(k);
            foreach (var channel in _channels)
            {
                channel.Configure(kind, cutoff);
            }
        }

        public void Reset()
        {
            foreach (var channel in _channels)
            {
                channel.Reset();
            }
        }

        /// <summary>Filters an interleaved buffer in place.</summary>
        public void ProcessInterleaved(float[] buffer, int channels)
        {
            if (buffer == null || channels < 1)
            {
                return;
            }

            EnsureChannels(channels);
            if (_channels[0].Kind == FilterKind.Bypass)
            {
                return;
            }

            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = _channels[i % channels].Process(buffer[i]);
            }
        }
    }
}
