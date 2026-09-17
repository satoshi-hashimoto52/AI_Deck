using System;
using System.Collections.Generic;
using AIDeck.Core.Analysis;
using AIDeck.Core.Deck;
using AIDeck.Core.Mixer;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "状態スナップショット" item of §10.1 and FR-064.</summary>
    [TestFixture]
    public class StateSnapshotTests
    {
        private static StateSnapshot MakePopulated()
        {
            var snapshot = StateSnapshot.Empty;
            snapshot.HostTimeMs = 1_700_000_000_000L;
            snapshot.LibraryRevision = 17;

            snapshot.DeckA = new DeckSnapshot
            {
                Deck = DeckId.A,
                State = DeckPlaybackState.Playing,
                Motion = MotionMode.Scratching,
                TrackId = "abc123",
                PositionSeconds = 45.25d,
                LengthSeconds = 210d,
                BaseBpm = 128d,
                TempoFader = 0.25f,
                TempoRangePercent = 16f,
                SyncEnabled = true,
                EffectiveRate = 1.04f,
                CueSeconds = 12d,
                CueIsSet = true,
                LoopInSeconds = 40d,
                LoopOutSeconds = 44d,
                LoopActive = true,
                PeakLevel = 0.6f,
                ErrorReason = string.Empty
            };

            snapshot.DeckB = new DeckSnapshot
            {
                Deck = DeckId.B,
                State = DeckPlaybackState.Error,
                Motion = MotionMode.Normal,
                TrackId = string.Empty,
                TempoRangePercent = 8f,
                EffectiveRate = 1f,
                ErrorReason = "Decode failed"
            };

            snapshot.ChannelGainA = 0.9f;
            snapshot.ChannelGainB = 0.1f;
            snapshot.MasterGain = 0.75f;
            snapshot.Crossfader = -0.5f;
            snapshot.CrossfaderCurve = CrossfaderCurveType.Sharp;
            snapshot.FilterA = -0.3f;
            snapshot.FilterB = 0.8f;
            snapshot.MuteB = true;
            snapshot.EchoA = true;
            snapshot.CueB = true;
            snapshot.MasterPeak = 0.88f;
            snapshot.MasterRms = 0.4f;
            snapshot.MasterClipping = true;
            snapshot.IsRecording = true;
            snapshot.RecordingSeconds = 91.5d;
            snapshot.RecordingFileName = "AIDeck_20260918_010203.wav";
            snapshot.Notice = "Recording";
            return snapshot;
        }

        [Test]
        public void RoundTrip_PreservesEveryField()
        {
            var original = MakePopulated();
            var restored = StateSnapshot.Deserialize(original.Serialize());

            Assert.That(restored.HostTimeMs, Is.EqualTo(original.HostTimeMs));
            Assert.That(restored.LibraryRevision, Is.EqualTo(17));

            Assert.That(restored.DeckA.State, Is.EqualTo(DeckPlaybackState.Playing));
            Assert.That(restored.DeckA.Motion, Is.EqualTo(MotionMode.Scratching));
            Assert.That(restored.DeckA.TrackId, Is.EqualTo("abc123"));
            Assert.That(restored.DeckA.PositionSeconds, Is.EqualTo(45.25d).Within(1e-9));
            Assert.That(restored.DeckA.LengthSeconds, Is.EqualTo(210d).Within(1e-9));
            Assert.That(restored.DeckA.BaseBpm, Is.EqualTo(128d).Within(1e-9));
            Assert.That(restored.DeckA.TempoFader, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(restored.DeckA.TempoRangePercent, Is.EqualTo(16f).Within(1e-6f));
            Assert.That(restored.DeckA.SyncEnabled, Is.True);
            Assert.That(restored.DeckA.EffectiveRate, Is.EqualTo(1.04f).Within(1e-6f));
            Assert.That(restored.DeckA.CueSeconds, Is.EqualTo(12d).Within(1e-9));
            Assert.That(restored.DeckA.CueIsSet, Is.True);
            Assert.That(restored.DeckA.LoopInSeconds, Is.EqualTo(40d).Within(1e-9));
            Assert.That(restored.DeckA.LoopOutSeconds, Is.EqualTo(44d).Within(1e-9));
            Assert.That(restored.DeckA.LoopActive, Is.True);
            Assert.That(restored.DeckA.PeakLevel, Is.EqualTo(0.6f).Within(1e-5f));

            Assert.That(restored.DeckB.State, Is.EqualTo(DeckPlaybackState.Error));
            Assert.That(restored.DeckB.ErrorReason, Is.EqualTo("Decode failed"));
            Assert.That(restored.DeckB.HasTrack, Is.False);

            Assert.That(restored.ChannelGainA, Is.EqualTo(0.9f).Within(1e-6f));
            Assert.That(restored.ChannelGainB, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(restored.MasterGain, Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(restored.Crossfader, Is.EqualTo(-0.5f).Within(1e-6f));
            Assert.That(restored.CrossfaderCurve, Is.EqualTo(CrossfaderCurveType.Sharp));
            Assert.That(restored.FilterA, Is.EqualTo(-0.3f).Within(1e-6f));
            Assert.That(restored.FilterB, Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(restored.MuteB, Is.True);
            Assert.That(restored.EchoA, Is.True);
            Assert.That(restored.CueB, Is.True);
            Assert.That(restored.MasterPeak, Is.EqualTo(0.88f).Within(1e-5f));
            Assert.That(restored.MasterClipping, Is.True);
            Assert.That(restored.IsRecording, Is.True);
            Assert.That(restored.RecordingSeconds, Is.EqualTo(91.5d).Within(1e-9));
            Assert.That(restored.RecordingFileName, Is.EqualTo("AIDeck_20260918_010203.wav"));
            Assert.That(restored.Notice, Is.EqualTo("Recording"));
        }

        [Test]
        public void SnapshotFitsInsideASingleFrame()
        {
            var bytes = MakePopulated().Serialize();
            Assert.That(bytes.Length, Is.LessThan(ProtocolInfo.MaxPayloadSize));
        }

        [Test]
        public void SnapshotTravelsThroughTheCodecIntact()
        {
            var original = MakePopulated();
            var frame = MessageCodec.Encode(
                new NetMessage(MessageType.StateSnapshot, null, 1u, 0L, original.Serialize()));
            var decoded = MessageCodec.Decode(frame, 0, frame.Length);

            Assert.That(decoded.Success, Is.True);
            var restored = StateSnapshot.Deserialize(decoded.Message.Payload);
            Assert.That(restored.DeckA.TrackId, Is.EqualTo("abc123"));
        }

        [Test]
        public void EmptyOrTruncatedPayloadYieldsSafeDefaults()
        {
            var fromNull = StateSnapshot.Deserialize(null);
            Assert.That(fromNull.DeckA.State, Is.EqualTo(DeckPlaybackState.Empty));
            Assert.That(fromNull.MasterGain, Is.EqualTo(0.8f));

            var truncated = StateSnapshot.Deserialize(new byte[] { 1, 2, 3, 4, 5 });
            Assert.That(truncated.MasterGain, Is.InRange(0f, 1f));
            Assert.That(truncated.Crossfader, Is.InRange(-1f, 1f));
            Assert.That(truncated.DeckA.EffectiveRate, Is.Not.NaN);
        }

        [Test]
        public void CorruptCurveAndStateBytesFallBackToSafeValues()
        {
            var bytes = MakePopulated().Serialize();
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = 0xFF;
            }

            var restored = StateSnapshot.Deserialize(bytes);
            Assert.That(restored.CrossfaderCurve, Is.EqualTo(CrossfaderCurveType.Smooth));
            Assert.That(restored.DeckA.State, Is.EqualTo(DeckPlaybackState.Empty));
            Assert.That(restored.DeckA.Motion, Is.EqualTo(MotionMode.Normal));
            Assert.That(restored.MasterGain, Is.InRange(0f, 1f));
        }

        [Test]
        public void MixerCaptureAndApplyRoundTrip()
        {
            var mixer = new MixerState
            {
                ChannelGainA = 0.33f,
                ChannelGainB = 0.66f,
                MasterGain = 0.5f,
                Crossfader = 0.2f,
                CrossfaderCurveType = CrossfaderCurveType.Linear,
                FilterA = -0.4f,
                FilterB = 0.4f,
                MuteA = true,
                EchoB = true,
                CueA = true
            };

            var snapshot = StateSnapshot.Empty;
            snapshot.CaptureMixer(mixer);

            var mirror = new MixerState();
            StateSnapshot.Deserialize(snapshot.Serialize()).ApplyMixer(mirror);

            Assert.That(mirror.ChannelGainA, Is.EqualTo(0.33f).Within(1e-6f));
            Assert.That(mirror.ChannelGainB, Is.EqualTo(0.66f).Within(1e-6f));
            Assert.That(mirror.CrossfaderCurveType, Is.EqualTo(CrossfaderCurveType.Linear));
            Assert.That(mirror.MuteA, Is.True);
            Assert.That(mirror.EchoB, Is.True);
            Assert.That(mirror.CueA, Is.True);
        }

        [Test]
        public void HelloPayloadRoundTrips()
        {
            var hello = new HelloPayload { DeviceName = "iPad mini", AppVersion = "1.0.0", FastPort = 47811 };
            var restored = HelloPayload.Deserialize(hello.Serialize());
            Assert.That(restored.DeviceName, Is.EqualTo("iPad mini"));
            Assert.That(restored.AppVersion, Is.EqualTo("1.0.0"));
            Assert.That(restored.FastPort, Is.EqualTo(47811));
        }

        [Test]
        public void DiscoveryPayloadRoundTripsAndIdentifiesTheService()
        {
            var beacon = new DiscoveryPayload
            {
                ServiceName = ProtocolInfo.ServiceName,
                HostName = "Studio Mac",
                TcpPort = ProtocolInfo.DefaultTcpPort,
                UdpPort = ProtocolInfo.DefaultUdpPort
            };

            var restored = DiscoveryPayload.Deserialize(beacon.Serialize());
            Assert.That(restored.IsAiDeck, Is.True);
            Assert.That(restored.HostName, Is.EqualTo("Studio Mac"));
            Assert.That(restored.TcpPort, Is.EqualTo(ProtocolInfo.DefaultTcpPort));

            var foreign = DiscoveryPayload.Deserialize(
                new DiscoveryPayload { ServiceName = "SomethingElse" }.Serialize());
            Assert.That(foreign.IsAiDeck, Is.False);
        }

        [Test]
        public void LibraryChunksCoverEveryTrackAndFitInAFrame()
        {
            var tracks = new List<TrackInfo>();
            for (var i = 0; i < 200; i++)
            {
                tracks.Add(new TrackInfo(null, $"/Music/track{i}.mp3", $"Track {i}", "Engine",
                    180d + i, TrackFormat.Mp3, DateTime.UtcNow, 120d + i % 40));
            }

            var chunks = Messages.ChunkLibrary(tracks, 3);
            var total = 0;
            foreach (var chunk in chunks)
            {
                var bytes = chunk.Serialize();
                Assert.That(bytes.Length, Is.LessThan(ProtocolInfo.MaxPayloadSize));

                var restored = LibraryChunkPayload.Deserialize(bytes);
                Assert.That(restored.Revision, Is.EqualTo(3));
                Assert.That(restored.ChunkCount, Is.EqualTo(chunks.Count));
                total += restored.Tracks.Count;
            }

            Assert.That(total, Is.EqualTo(200));
        }

        [Test]
        public void AnEmptyLibraryStillProducesOneChunk()
        {
            var chunks = Messages.ChunkLibrary(new List<TrackInfo>(), 1);
            Assert.That(chunks.Count, Is.EqualTo(1));
            Assert.That(chunks[0].Tracks, Is.Empty);
        }

        [Test]
        public void WaveformChunksReassembleToTheOriginalEnvelope()
        {
            var peaks = new byte[20000];
            var rms = new byte[20000];
            for (var i = 0; i < peaks.Length; i++)
            {
                peaks[i] = (byte)(i % 256);
                rms[i] = (byte)((i * 7) % 256);
            }

            var waveform = new WaveformData(peaks, rms, 232.5d, 86);
            var chunks = Messages.ChunkWaveform("track-1", waveform);

            var rebuiltPeaks = new List<byte>();
            var rebuiltRms = new List<byte>();
            foreach (var chunk in chunks)
            {
                var bytes = chunk.Serialize();
                Assert.That(bytes.Length, Is.LessThan(ProtocolInfo.MaxPayloadSize));

                var restored = WaveformChunkPayload.Deserialize(bytes);
                Assert.That(restored.TrackId, Is.EqualTo("track-1"));
                Assert.That(restored.DurationSeconds, Is.EqualTo(232.5d).Within(1e-9));
                rebuiltPeaks.AddRange(restored.Peaks);
                rebuiltRms.AddRange(restored.Rms);
            }

            Assert.That(rebuiltPeaks.ToArray(), Is.EqualTo(peaks));
            Assert.That(rebuiltRms.ToArray(), Is.EqualTo(rms));
        }

        [Test]
        public void AnEmptyWaveformStillProducesOneChunk()
        {
            var chunks = Messages.ChunkWaveform("t", WaveformData.Empty);
            Assert.That(chunks.Count, Is.EqualTo(1));
            Assert.That(chunks[0].Peaks, Is.Empty);
        }
    }
}
