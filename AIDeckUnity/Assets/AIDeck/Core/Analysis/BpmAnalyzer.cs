using System;
using AIDeck.Core.Fx;
using AIDeck.Core.Model;

namespace AIDeck.Core.Analysis
{
    /// <summary>Outcome of a tempo analysis pass.</summary>
    public readonly struct BpmResult
    {
        public BpmResult(double bpm, double confidence)
        {
            Bpm = TrackInfo.NormalizeBpm(bpm);
            Confidence = AudioSafety.SanitizeDouble(confidence, 0d, 1d, 0d);
        }

        /// <summary>Detected tempo, or 0 when analysis did not produce a usable answer.</summary>
        public double Bpm { get; }

        /// <summary>
        /// How far the winning autocorrelation peak stands above the rest of the search range,
        /// 0…1. Below <see cref="BpmAnalyzer.MinConfidence"/> the result is discarded: a wrong
        /// BPM is worse than no BPM, because SYNC would act on it (FR-030).
        /// </summary>
        public double Confidence { get; }

        public bool IsUsable => Bpm > 0d && Confidence >= BpmAnalyzer.MinConfidence;

        public static BpmResult None => new BpmResult(0d, 0d);
    }

    /// <summary>
    /// Onset-energy autocorrelation tempo estimator (FR-029, "簡易BPM解析").
    ///
    /// No FFT and no external library: §14 rules out dependencies of unclear licence, and for
    /// the percussive material AI Deck targets an energy envelope carries the beat clearly.
    /// The estimator reports a confidence and refuses to guess, because SYNC consumes it.
    ///
    /// Three details are load-bearing, and each was put here after the naive version got a
    /// real track wrong:
    ///
    /// 1. **The energy window overlaps.** Measuring RMS over a window as short as the hop makes
    ///    the envelope track the waveform of the bass rather than the loudness of the mix — a
    ///    55 Hz bass note has a 18 ms period, far longer than a 5 ms hop, so its own cycle
    ///    appears in the envelope and swamps the beat. A 40 ms window averages that out, and
    ///    a 200 Hz high-pass ahead of it removes the bass from the measurement entirely —
    ///    without that step a click track over a strong bass line reads 146 BPM instead of 128.
    /// 2. **The autocorrelation is normalised** by the energy of both overlapping windows.
    ///    An unnormalised sum favours short lags and a length-corrected one favours long lags;
    ///    either bias is enough to pick a neighbouring peak over the true one.
    /// 3. **The winning lag is interpolated.** At a 200 Hz envelope rate, 128 BPM is a lag of
    ///    93.75 samples. Rounding to an integer lag is a 0.3 % tempo error, which over a
    ///    four-minute track is several beats of drift.
    ///
    /// Limitations are recorded in docs/KNOWN_LIMITATIONS.md.
    /// </summary>
    public static class BpmAnalyzer
    {
        /// <summary>Envelope sample rate in Hz. 200 Hz resolves a 5 ms onset, plenty for tempo.</summary>
        public const int EnvelopeRate = 200;

        /// <summary>
        /// Energy window length as a multiple of the hop. 8 × 5 ms = 40 ms, which is longer
        /// than one cycle of any pitch a bass line reaches (see note 1 above).
        /// </summary>
        public const int EnergyWindowMultiplier = 8;

        /// <summary>
        /// Cut-off of the high-pass applied before the energy measurement.
        ///
        /// Onsets live in the mid and high band; a sustained bass note carries a great deal of
        /// energy and none of the timing. Removing everything below 200 Hz makes the envelope
        /// measure the transients rather than the bass line, and it is what lifts the detection
        /// confidence on real material from around 0.75 to above 0.95.
        /// </summary>
        public const float OnsetHighPassHz = 200f;

        public const double MinBpm = 70d;
        public const double MaxBpm = 190d;

        /// <summary>
        /// Minimum peak prominence. Calibrated so that a clear four-on-the-floor track scores
        /// 0.75–0.97 while white noise scores below 0.05.
        /// </summary>
        public const double MinConfidence = 0.3d;

        /// <summary>Seconds of audio analysed. Analysing the whole track buys little and costs a lot.</summary>
        public const double AnalysisWindowSeconds = 60d;

        /// <summary>Seconds skipped at the start, to step over an intro or a silent lead-in.</summary>
        public const double SkipLeadInSeconds = 10d;

        public static BpmResult Analyse(float[] samples, int channels, int sampleRate)
        {
            var envelope = BuildOnsetEnvelope(samples, channels, sampleRate);
            return AnalyseEnvelope(envelope);
        }

        /// <summary>
        /// Builds the rectified onset envelope. Exposed so tests can drive the autocorrelation
        /// stage from a synthetic envelope with an exactly known tempo.
        /// </summary>
        public static float[] BuildOnsetEnvelope(float[] samples, int channels, int sampleRate)
        {
            if (samples == null || channels < 1 || sampleRate <= 0 || samples.Length < channels)
            {
                return Array.Empty<float>();
            }

            var totalFrames = samples.Length / channels;
            var startFrame = (long)(SkipLeadInSeconds * sampleRate);
            if (startFrame >= totalFrames)
            {
                startFrame = 0;
            }

            var maxFrames = (long)(AnalysisWindowSeconds * sampleRate);
            var endFrame = Math.Min(totalFrames, startFrame + maxFrames);

            var hop = Math.Max(1, sampleRate / EnvelopeRate);
            var window = hop * EnergyWindowMultiplier;
            var spanFrames = (int)(endFrame - startFrame);
            var bucketCount = spanFrames / hop;
            if (bucketCount < 8)
            {
                return Array.Empty<float>();
            }

            // Stage 0: mono mixdown through a high-pass. The filter has to run over contiguous
            // samples in order, so this is a separate pass rather than something folded into
            // the windowed energy loop below (whose windows overlap and would re-filter frames).
            var filtered = new float[spanFrames];
            var highPass = new StateVariableFilter(sampleRate);
            highPass.Configure(FilterKind.HighPass, OnsetHighPassHz);

            for (var frame = 0; frame < spanFrames; frame++)
            {
                var baseIndex = (startFrame + frame) * channels;
                var sum = 0f;
                var counted = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = baseIndex + channel;
                    if (index >= samples.Length)
                    {
                        break;
                    }

                    var sample = samples[index];
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                    {
                        continue;
                    }

                    sum += sample;
                    counted++;
                }

                filtered[frame] = highPass.Process(counted > 0 ? sum / counted : 0f);
            }

            // Stage 1: short-time energy over an overlapping window.
            var energy = new float[bucketCount];
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                var from = bucket * hop;
                var to = Math.Min(spanFrames, from + window);

                double sum = 0d;
                for (var frame = from; frame < to; frame++)
                {
                    double value = filtered[frame];
                    sum += value * value;
                }

                var count = to - from;
                energy[bucket] = count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
            }

            // Stage 2: half-wave rectified first difference — the onset function. Only rises in
            // energy mark a beat; decays are discarded.
            var envelope = new float[bucketCount];
            for (var i = 1; i < bucketCount; i++)
            {
                var difference = energy[i] - energy[i - 1];
                envelope[i] = difference > 0f ? difference : 0f;
            }

            return envelope;
        }

        /// <summary>Runs the autocorrelation stage on a prepared onset envelope.</summary>
        public static BpmResult AnalyseEnvelope(float[] envelope)
        {
            if (envelope == null || envelope.Length < EnvelopeRate)
            {
                return BpmResult.None;
            }

            var minLag = (int)Math.Floor(60d * EnvelopeRate / MaxBpm);
            var maxLag = (int)Math.Ceiling(60d * EnvelopeRate / MinBpm);
            if (maxLag >= envelope.Length)
            {
                maxLag = envelope.Length - 1;
            }

            if (minLag < 1 || maxLag <= minLag + 2)
            {
                return BpmResult.None;
            }

            var lagCount = maxLag - minLag + 1;
            var scores = new double[lagCount];
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;
            var total = 0d;

            for (var lag = minLag; lag <= maxLag; lag++)
            {
                var count = envelope.Length - lag;
                double cross = 0d;
                double energyA = 0d;
                double energyB = 0d;

                for (var i = 0; i < count; i++)
                {
                    double a = envelope[i];
                    double b = envelope[i + lag];
                    cross += a * b;
                    energyA += a * a;
                    energyB += b * b;
                }

                // Normalised correlation: independent of both the overlap length and the
                // loudness of either half, so no lag is favoured by arithmetic alone.
                var score = energyA > 0d && energyB > 0d
                    ? cross / Math.Sqrt(energyA * energyB)
                    : 0d;

                var index = lag - minLag;
                scores[index] = score;
                total += score;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }

            if (bestIndex < 0 || bestScore <= 0d)
            {
                return BpmResult.None;
            }

            // Confidence is how far the winner stands above the average of the search range.
            // A genuinely periodic signal produces one tall peak; noise produces a plateau at
            // which every lag correlates about equally well.
            var mean = total / lagCount;
            var confidence = bestScore <= mean
                ? 0d
                : (bestScore - mean) / Math.Max(1e-9d, 1d - mean);

            var refinedLag = (minLag + bestIndex) + InterpolatePeak(scores, bestIndex);
            if (refinedLag <= 0d)
            {
                return BpmResult.None;
            }

            var bpm = 60d * EnvelopeRate / refinedLag;
            return new BpmResult(FoldIntoRange(bpm), confidence);
        }

        /// <summary>
        /// Sub-sample peak position by fitting a parabola through the winning lag and its two
        /// neighbours. Returns an offset in [-0.5, 0.5].
        /// </summary>
        private static double InterpolatePeak(double[] scores, int index)
        {
            if (index <= 0 || index >= scores.Length - 1)
            {
                return 0d;
            }

            var left = scores[index - 1];
            var centre = scores[index];
            var right = scores[index + 1];
            var denominator = left - 2d * centre + right;
            if (Math.Abs(denominator) < 1e-12d)
            {
                return 0d;
            }

            var offset = 0.5d * (left - right) / denominator;
            if (!AudioSafety.IsFinite(offset))
            {
                return 0d;
            }

            return Math.Max(-0.5d, Math.Min(0.5d, offset));
        }

        /// <summary>
        /// Folds a tempo into the 70–190 BPM window that DJ software conventionally reports,
        /// so a track detected at 75 BPM and its 150 BPM double land on the same value.
        /// </summary>
        public static double FoldIntoRange(double bpm)
        {
            if (!AudioSafety.IsFinite(bpm) || bpm <= 0d)
            {
                return 0d;
            }

            var guard = 0;
            while (bpm < MinBpm && guard++ < 8)
            {
                bpm *= 2d;
            }

            guard = 0;
            while (bpm > MaxBpm && guard++ < 8)
            {
                bpm /= 2d;
            }

            return bpm >= MinBpm && bpm <= MaxBpm ? bpm : 0d;
        }
    }
}
