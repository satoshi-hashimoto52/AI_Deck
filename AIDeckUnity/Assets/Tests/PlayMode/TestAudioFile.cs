using System;
using System.IO;
using AIDeck.Core.Audio;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// Writes a real WAV file for the tests to load.
    ///
    /// The project may not ship binary test fixtures — a committed audio file is exactly the
    /// kind of asset whose provenance §14 asks about — so the tests generate their own. The
    /// file is written with the shipping <see cref="WavRecorder"/>, which means every load
    /// test is also an end-to-end check that what AI Deck writes, AI Deck can read.
    /// </summary>
    public static class TestAudioFile
    {
        /// <summary>Creates a stereo sine-tone WAV and returns its path.</summary>
        public static string CreateTone(double seconds, float frequency = 440f, int sampleRate = 48000)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "aideck-test-" + Guid.NewGuid().ToString("N") + ".wav");

            var recorder = new WavRecorder();
            if (!recorder.Start(path, sampleRate, 2))
            {
                throw new IOException("The test audio file could not be created: " + recorder.FailureReason);
            }

            var frames = (int)(seconds * sampleRate);
            var block = new float[4800 * 2];
            var written = 0;

            while (written < frames)
            {
                var count = Math.Min(block.Length / 2, frames - written);
                for (var i = 0; i < count; i++)
                {
                    var value = 0.5f * (float)Math.Sin(2d * Math.PI * frequency * (written + i) / sampleRate);
                    block[i * 2] = value;
                    block[i * 2 + 1] = value;
                }

                recorder.Submit(block, count * 2);
                recorder.Drain();
                written += count;
            }

            var result = recorder.Stop();
            if (result == null)
            {
                throw new IOException("The test audio file could not be finalised: " + recorder.FailureReason);
            }

            return path;
        }

        public static void Delete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover file in the temp folder is harmless.
            }
        }
    }
}
