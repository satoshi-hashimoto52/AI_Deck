using System;
using System.IO;
using System.Text;
using AIDeck.Core.Audio;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "WAVヘッダーと録音ファイル生成" item of §10.1 and FR-050 to FR-055.</summary>
    [TestFixture]
    public class RecordingTests
    {
        private const int SampleRate = 48000;

        // ------------------------------------------------------------------ header

        [Test]
        public void HeaderHasTheCanonicalRiffLayout()
        {
            var header = WavHeader.Build(SampleRate, 2, 1000);

            Assert.That(header.Length, Is.EqualTo(WavHeader.HeaderSize));
            Assert.That(Ascii(header, 0, 4), Is.EqualTo("RIFF"));
            Assert.That(Ascii(header, 8, 4), Is.EqualTo("WAVE"));
            Assert.That(Ascii(header, 12, 4), Is.EqualTo("fmt "));
            Assert.That(Ascii(header, 36, 4), Is.EqualTo("data"));

            Assert.That(Int32At(header, 4), Is.EqualTo(36 + 1000), "RIFF size is 36 + data size");
            Assert.That(Int32At(header, 16), Is.EqualTo(16), "PCM fmt chunk is 16 bytes");
            Assert.That(Int16At(header, 20), Is.EqualTo(1), "format tag is PCM");
            Assert.That(Int16At(header, 22), Is.EqualTo(2), "channel count");
            Assert.That(Int32At(header, 24), Is.EqualTo(SampleRate));
            Assert.That(Int32At(header, 28), Is.EqualTo(SampleRate * 4), "byte rate");
            Assert.That(Int16At(header, 32), Is.EqualTo(4), "block align");
            Assert.That(Int16At(header, 34), Is.EqualTo(16), "bits per sample");
            Assert.That(Int32At(header, 40), Is.EqualTo(1000), "data size");
        }

        [Test]
        public void HeaderRejectsImpossibleFormats()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeader.Build(0, 2, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeader.Build(SampleRate, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavHeader.Build(SampleRate, 99, 0));
        }

        [Test]
        public void MonoHeaderUsesTheRightBlockAlign()
        {
            var header = WavHeader.Build(44100, 1, 0);
            Assert.That(Int16At(header, 32), Is.EqualTo(2));
            Assert.That(Int32At(header, 28), Is.EqualTo(44100 * 2));
        }

        [Test]
        public void PcmConversionIsSymmetricAndClamped()
        {
            var samples = new[] { 0f, 1f, -1f, 2f, -2f, float.NaN, float.PositiveInfinity };
            var bytes = new byte[samples.Length * 2];
            var written = WavHeader.WritePcm16(samples, samples.Length, bytes, 0);

            Assert.That(written, Is.EqualTo(samples.Length * 2));
            Assert.That(Int16At(bytes, 0), Is.EqualTo(0));
            Assert.That(Int16At(bytes, 2), Is.EqualTo(32767));
            Assert.That(Int16At(bytes, 4), Is.EqualTo(-32767));
            Assert.That(Int16At(bytes, 6), Is.EqualTo(32767), "an overshoot clamps rather than wrapping");
            Assert.That(Int16At(bytes, 8), Is.EqualTo(-32767));
            Assert.That(Int16At(bytes, 10), Is.EqualTo(0), "NaN becomes silence, never noise");
            Assert.That(Int16At(bytes, 12), Is.EqualTo(0));
        }

        [Test]
        public void PcmConversionStopsAtTheEndOfTheDestination()
        {
            var samples = new[] { 1f, 1f, 1f };
            var small = new byte[2];
            Assert.That(WavHeader.WritePcm16(samples, samples.Length, small, 0), Is.EqualTo(0).Or.EqualTo(2));
        }

        [Test]
        public void FileNameIsSortableAndCarriesNoUserData()
        {
            var name = WavRecorder.BuildFileName(new DateTime(2026, 9, 18, 1, 2, 3));
            Assert.That(name, Is.EqualTo("AIDeck_20260918_010203.wav"));
        }

        [Test]
        public void RecordingFileNamesAreRecognisable()
        {
            // So a library scan can keep them out while still allowing one to be named outright.
            Assert.That(WavRecorder.IsRecordingFileName("AIDeck_20260918_085224.wav"), Is.True);
            Assert.That(WavRecorder.IsRecordingFileName(WavRecorder.BuildFileName(DateTime.Now)), Is.True);

            Assert.That(WavRecorder.IsRecordingFileName("Neon Drive.wav"), Is.False);
            Assert.That(WavRecorder.IsRecordingFileName("AIDeck_notadate.wav"), Is.False);
            Assert.That(WavRecorder.IsRecordingFileName("AIDeck_2026_08.wav"), Is.False);
            Assert.That(WavRecorder.IsRecordingFileName("My AIDeck_20260918_085224.wav"), Is.False);
            Assert.That(WavRecorder.IsRecordingFileName(null), Is.False);
            Assert.That(WavRecorder.IsRecordingFileName(string.Empty), Is.False);
        }

        // ------------------------------------------------------------------ recorder

        [Test]
        public void RecordingProducesAPlayableFileWithCorrectSizes()
        {
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();

            Assert.That(recorder.Start(stream, SampleRate, 2), Is.True);
            Assert.That(recorder.IsRecording, Is.True);

            var block = new float[4800];
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = 0.25f;
            }

            for (var i = 0; i < 10; i++)
            {
                recorder.Submit(block, block.Length);
                recorder.Drain();
            }

            recorder.Stop();

            var bytes = stream.ToArray();
            var expectedData = 10 * block.Length * 2;
            Assert.That(bytes.Length, Is.EqualTo(WavHeader.HeaderSize + expectedData));
            Assert.That(Int32At(bytes, WavHeader.DataSizeOffset), Is.EqualTo(expectedData));
            Assert.That(Int32At(bytes, WavHeader.RiffSizeOffset), Is.EqualTo(36 + expectedData));
            Assert.That(Ascii(bytes, 0, 4), Is.EqualTo("RIFF"));
        }

        [Test]
        public void ElapsedTimeTracksTheRecordedAudio()
        {
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            recorder.Start(stream, SampleRate, 2);

            var oneSecond = new float[SampleRate * 2];
            recorder.Submit(oneSecond, oneSecond.Length);
            recorder.Drain();

            Assert.That(recorder.ElapsedSeconds, Is.EqualTo(1d).Within(0.01d));
            recorder.Stop();
        }

        [Test]
        public void AnInterruptedRecordingIsStillAValidFileUpToTheLastHeaderRefresh()
        {
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            recorder.Start(stream, SampleRate, 2);

            // Push more than the header refresh interval so the sizes get written at least once.
            var oneSecond = new float[SampleRate * 2];
            for (var i = 0; i < 7; i++)
            {
                recorder.Submit(oneSecond, oneSecond.Length);
                recorder.Drain();
            }

            // Simulate a crash: read the bytes without ever calling Stop().
            var bytes = stream.ToArray();

            Assert.That(Ascii(bytes, 0, 4), Is.EqualTo("RIFF"));
            var declared = Int32At(bytes, WavHeader.DataSizeOffset);
            Assert.That(declared, Is.GreaterThan(0), "the interrupted file must declare the audio it holds");
            Assert.That(declared, Is.LessThanOrEqualTo(bytes.Length - WavHeader.HeaderSize));
        }

        [Test]
        public void StartingTwiceIsRefusedWithoutDisturbingTheFirstRecording()
        {
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            recorder.Start(stream, SampleRate, 2);

            using var second = new MemoryStream();
            Assert.That(recorder.Start(second, SampleRate, 2), Is.False);
            Assert.That(recorder.FailureReason, Is.Not.Empty);
        }

        [Test]
        public void AnUnwritableTargetFailsWithAReasonInsteadOfThrowing()
        {
            var recorder = new WavRecorder();
            Assert.That(recorder.Start((Stream)null, SampleRate, 2), Is.False);
            Assert.That(recorder.State, Is.EqualTo(RecorderState.Failed));
            Assert.That(recorder.FailureReason, Is.Not.Empty);

            Assert.That(recorder.Start("", SampleRate, 2), Is.False);
            Assert.That(recorder.Start("/this/path/does/not/exist/and/cannot/be/made\0/x.wav", SampleRate, 2), Is.False);
        }

        [Test]
        public void AnUnsupportedFormatIsRefused()
        {
            using var stream = new MemoryStream();
            var recorder = new WavRecorder();
            Assert.That(recorder.Start(stream, 0, 2), Is.False);
            recorder.ClearFailure();
            Assert.That(recorder.Start(stream, SampleRate, 0), Is.False);
        }

        [Test]
        public void SubmitIsSafeWhenNoRecordingIsRunning()
        {
            var recorder = new WavRecorder();
            Assert.DoesNotThrow(() => recorder.Submit(new float[128], 128));
            Assert.DoesNotThrow(() => recorder.Submit(null, 0));
            Assert.That(recorder.Drain(), Is.EqualTo(0));
            Assert.That(recorder.Stop(), Is.Null);
        }

        [Test]
        public void ClearFailureLetsTheUserTryAgain()
        {
            var recorder = new WavRecorder();
            recorder.Start((Stream)null, SampleRate, 2);
            Assert.That(recorder.State, Is.EqualTo(RecorderState.Failed));

            recorder.ClearFailure();
            Assert.That(recorder.State, Is.EqualTo(RecorderState.Idle));

            using var stream = new MemoryStream();
            Assert.That(recorder.Start(stream, SampleRate, 2), Is.True);
            recorder.Stop();
        }

        [Test]
        public void AFileRecordingWritesToDiskAndReportsItsPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "aideck-test-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var recorder = new WavRecorder();
                Assert.That(recorder.Start(path, SampleRate, 2), Is.True);
                Assert.That(recorder.OutputPath, Is.EqualTo(path));

                var block = new float[9600];
                recorder.Submit(block, block.Length);
                recorder.Drain();

                Assert.That(recorder.Stop(), Is.EqualTo(path));
                Assert.That(File.Exists(path), Is.True);
                Assert.That(new FileInfo(path).Length, Is.EqualTo(WavHeader.HeaderSize + block.Length * 2));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void DisposeFinishesAnOpenRecording()
        {
            var path = Path.Combine(Path.GetTempPath(), "aideck-test-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using (var recorder = new WavRecorder())
                {
                    recorder.Start(path, SampleRate, 2);
                    recorder.Submit(new float[1024], 1024);
                }

                Assert.That(File.Exists(path), Is.True);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        // ------------------------------------------------------------------ ring buffer

        [Test]
        public void RingBufferRoundTripsInOrder()
        {
            var ring = new AudioRingBuffer(16);
            var input = new float[] { 1, 2, 3, 4, 5 };
            Assert.That(ring.Write(input, 0, input.Length), Is.EqualTo(5));
            Assert.That(ring.Available, Is.EqualTo(5));

            var output = new float[5];
            Assert.That(ring.Read(output, 0, 5), Is.EqualTo(5));
            Assert.That(output, Is.EqualTo(input));
            Assert.That(ring.Available, Is.EqualTo(0));
        }

        [Test]
        public void RingBufferWrapsAroundCorrectly()
        {
            var ring = new AudioRingBuffer(8);
            var scratch = new float[8];

            for (var cycle = 0; cycle < 10; cycle++)
            {
                var block = new float[6];
                for (var i = 0; i < block.Length; i++)
                {
                    block[i] = cycle * 10 + i;
                }

                Assert.That(ring.Write(block, 0, block.Length), Is.EqualTo(6));
                Assert.That(ring.Read(scratch, 0, 6), Is.EqualTo(6));
                for (var i = 0; i < 6; i++)
                {
                    Assert.That(scratch[i], Is.EqualTo(cycle * 10 + i));
                }
            }
        }

        [Test]
        public void RingBufferDropsInsteadOfGrowingWhenTheConsumerFallsBehind()
        {
            var ring = new AudioRingBuffer(8);
            var block = new float[100];

            var written = ring.Write(block, 0, block.Length);

            Assert.That(written, Is.EqualTo(8), "the buffer is bounded");
            Assert.That(ring.OverflowCount, Is.EqualTo(92), "the shortfall is counted, not hidden");
        }

        [Test]
        public void RingBufferHandlesDegenerateArguments()
        {
            var ring = new AudioRingBuffer(8);
            Assert.That(ring.Write(null, 0, 4), Is.EqualTo(0));
            Assert.That(ring.Write(new float[4], -1, 4), Is.EqualTo(0));
            Assert.That(ring.Write(new float[4], 10, 4), Is.EqualTo(0));
            Assert.That(ring.Read(null, 0, 4), Is.EqualTo(0));
            Assert.That(ring.Read(new float[4], 0, 0), Is.EqualTo(0));
        }

        [Test]
        public void RingBufferClearEmptiesIt()
        {
            var ring = new AudioRingBuffer(8);
            ring.Write(new float[4], 0, 4);
            ring.Clear();
            Assert.That(ring.Available, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ helpers

        private static string Ascii(byte[] bytes, int offset, int length) =>
            Encoding.ASCII.GetString(bytes, offset, length);

        private static int Int32At(byte[] bytes, int offset) =>
            bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24);

        private static short Int16At(byte[] bytes, int offset) =>
            (short)(bytes[offset] | (bytes[offset + 1] << 8));
    }
}
