using System;
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
        /// 0..1 strength of the winning autocorrelation peak relative to the envelope's own
        /// energy. Below <see cref="BpmAnalyzer.MinConfidence"/> the result is discarded:
        /// a wrong BPM is worse than no BPM because SYNC would act on it (FR-030).
        /// </summary>
        public double Confidence { get; }

        public bool IsUsable => Bpm > 0d && Confidence >= BpmAnalyzer.MinConfidence;

        public static BpmResult None => new BpmResult(0d, 0d);
    }

    /// <summary>
    /// Simple onset-energy + autocorrelation tempo estimator (FR-029, "簡易BPM解析").
    ///
    /// No FFT and no external library: the master issue forbids paid or unclear-licence
    /// dependencies, and for the four-on-the-floor material AI Deck targets, a rectified
    /// energy envelope carries the beat clearly enough. The estimator deliberately reports
    /// a confidence and refuses to guess, because SYNC consumes the result.
    ///
    /// Limitations are recorded in docs/KNOWN_LIMITATIONS.md.
    /// </summary>
    public static class BpmAnalyzer
    {
        /// <summary>Envelope sample rate in Hz. 200 Hz resolves a 5 ms onset, plenty for tempo.</summary>
        public const int EnvelopeRate = 200;

        public const double MinBpm = 70d;
        public const double MaxBpm = 190d;

        /// <summary>Peaks weaker than this relative to the envelope's own energy are rejected.</summary>
        public const double MinConfidence = 0.15d;

        /// <summary>Seconds of audio analysed. Analysing the whole track buys little and costs a lot.</summary>
        public const double AnalysisWindowSeconds = 60d;

        /// <summary>Seconds skipped at the start, to step over an intro or a silent lead-in.</summary>
        public const double SkipLeadInSeconds = 10d;

        public static BpmResult Analyse(float[] samples, int channels, int sampleRate)
        {
            if (samples == null || channels < 1 || sampleRate <= 0)
            {
                return BpmResult.None;
            }

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
                return System.Array.Empty<float>();
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
            var bucketCount = (int)((endFrame - startFrame) / hop);
            if (bucketCount < 4)
            {
                return System.Array.Empty<float>();
            }

            // Stage 1: short-time energy.
            var energy = new float[bucketCount];
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                var frameStart = startFrame + (long)bucket * hop;
                double sum = 0d;
                var counted = 0;
                for (var frame = frameStart; frame < frameStart + hop && frame < endFrame; frame++)
                {
                    var baseIndex = frame * channels;
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var index = baseIndex + ch;
                        if (index >= samples.Length)
                        {
                            break;
                        }

                        var s = samples[index];
                        if (float.IsNaN(s) || float.IsInfinity(s))
                        {
                            continue;
                        }

                        sum += (double)s * s;
                        counted++;
                    }
                }

                energy[bucket] = counted > 0 ? (float)Math.Sqrt(sum / counted) : 0f;
            }

            // Stage 2: half-wave rectified first difference — the onset function. Only rises
            // in energy mark a beat; decays are discarded.
            var envelope = new float[bucketCount];
            for (var i = 1; i < bucketCount; i++)
            {
                var diff = energy[i] - energy[i - 1];
                envelope[i] = diff > 0f ? diff : 0f;
            }

            // Stage 3: remove the running mean so a track that gets louder does not bias
            // the autocorrelation toward long lags.
            SubtractRunningMean(envelope, EnvelopeRate);
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

            if (minLag < 1 || maxLag <= minLag)
            {
                return BpmResult.None;
            }

            // Energy at lag 0 normalises the correlation into a 0..1 confidence.
            double zeroLag = 0d;
            foreach (var v in envelope)
            {
                zeroLag += (double)v * v;
            }

            if (zeroLag <= 1e-9d)
            {
                return BpmResult.None;
            }

            var bestLag = -1;
            var bestScore = double.NegativeInfinity;

            for (var lag = minLag; lag <= maxLag; lag++)
            {
                double sum = 0d;
                var count = envelope.Length - lag;
                for (var i = 0; i < count; i++)
                {
                    sum += (double)envelope[i] * envelope[i + lag];
                }

                // Normalise by the overlap so short lags are not favoured purely by length.
                var score = sum / count * envelope.Length;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLag = lag;
                }
            }

            if (bestLag <= 0 || bestScore <= 0d)
            {
                return BpmResult.None;
            }

            var bpm = 60d * EnvelopeRate / bestLag;
            var confidence = bestScore / zeroLag;
            return new BpmResult(FoldIntoRange(bpm), confidence);
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

        private static void SubtractRunningMean(float[] envelope, int window)
        {
            if (envelope.Length == 0 || window < 2)
            {
                return;
            }

            var half = window / 2;
            var prefix = new double[envelope.Length + 1];
            for (var i = 0; i < envelope.Length; i++)
            {
                prefix[i + 1] = prefix[i] + envelope[i];
            }

            for (var i = 0; i < envelope.Length; i++)
            {
                var lo = Math.Max(0, i - half);
                var hi = Math.Min(envelope.Length, i + half + 1);
                var mean = (prefix[hi] - prefix[lo]) / (hi - lo);
                var value = envelope[i] - (float)mean;
                envelope[i] = value > 0f ? value : 0f;
            }
        }
    }
}
