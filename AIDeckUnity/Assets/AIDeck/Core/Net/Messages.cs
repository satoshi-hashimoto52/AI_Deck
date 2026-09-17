using System;
using System.Collections.Generic;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>Payload of <see cref="MessageType.Hello"/> and <see cref="MessageType.HelloAck"/>.</summary>
    public struct HelloPayload
    {
        /// <summary>Friendly device name, e.g. "iPad mini". Never a serial number or account id (NFR-006).</summary>
        public string DeviceName;

        /// <summary>Application version string.</summary>
        public string AppVersion;

        /// <summary>UDP port the sender listens on for the fast channel.</summary>
        public int FastPort;

        public byte[] Serialize()
        {
            var writer = new ByteWriter(64);
            writer.WriteString(DeviceName);
            writer.WriteString(AppVersion);
            writer.WriteInt32(FastPort);
            return writer.ToArray();
        }

        public static HelloPayload Deserialize(byte[] payload)
        {
            var reader = new ByteReader(payload);
            return new HelloPayload
            {
                DeviceName = reader.ReadString(),
                AppVersion = reader.ReadString(),
                FastPort = reader.ReadInt32()
            };
        }
    }

    /// <summary>Payload of the discovery beacon the host broadcasts (FR-060).</summary>
    public struct DiscoveryPayload
    {
        public string ServiceName;
        public string HostName;
        public int TcpPort;
        public int UdpPort;

        public byte[] Serialize()
        {
            var writer = new ByteWriter(64);
            writer.WriteString(ServiceName ?? ProtocolInfo.ServiceName);
            writer.WriteString(HostName);
            writer.WriteInt32(TcpPort);
            writer.WriteInt32(UdpPort);
            return writer.ToArray();
        }

        public static DiscoveryPayload Deserialize(byte[] payload)
        {
            var reader = new ByteReader(payload);
            return new DiscoveryPayload
            {
                ServiceName = reader.ReadString(),
                HostName = reader.ReadString(),
                TcpPort = reader.ReadInt32(ProtocolInfo.DefaultTcpPort),
                UdpPort = reader.ReadInt32(ProtocolInfo.DefaultUdpPort)
            };
        }

        public bool IsAiDeck => string.Equals(ServiceName, ProtocolInfo.ServiceName, StringComparison.Ordinal);
    }

    /// <summary>One chunk of the library listing, so a large library streams rather than blocking (FR-064).</summary>
    public struct LibraryChunkPayload
    {
        /// <summary>Revision this chunk belongs to; a controller discards chunks from an older revision.</summary>
        public int Revision;

        public int ChunkIndex;
        public int ChunkCount;
        public List<TrackInfo> Tracks;

        /// <summary>Tracks per chunk. Keeps a chunk comfortably under the 60 kB frame limit.</summary>
        public const int TracksPerChunk = 64;

        public byte[] Serialize()
        {
            var writer = new ByteWriter(2048);
            writer.WriteInt32(Revision);
            writer.WriteInt32(ChunkIndex);
            writer.WriteInt32(ChunkCount);
            var list = Tracks ?? new List<TrackInfo>();
            writer.WriteUInt16((ushort)Math.Min(list.Count, ushort.MaxValue));
            foreach (var track in list)
            {
                writer.WriteString(track.Id);
                writer.WriteString(track.Title);
                writer.WriteString(track.Artist);
                writer.WriteDouble(track.DurationSeconds);
                writer.WriteDouble(track.Bpm);
                writer.WriteByte((byte)track.Format);
                writer.WriteInt64(track.AddedUtc.ToUniversalTime().Ticks);
            }

            return writer.ToArray();
        }

        public static LibraryChunkPayload Deserialize(byte[] payload)
        {
            var reader = new ByteReader(payload);
            var result = new LibraryChunkPayload
            {
                Revision = reader.ReadInt32(),
                ChunkIndex = reader.ReadInt32(),
                ChunkCount = reader.ReadInt32(),
                Tracks = new List<TrackInfo>()
            };

            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                var id = reader.ReadString();
                var title = reader.ReadString();
                var artist = reader.ReadString();
                var duration = reader.ReadDouble();
                var bpm = reader.ReadDouble();
                var format = (TrackFormat)reader.ReadByte();
                var ticks = reader.ReadInt64();
                if (reader.Failed)
                {
                    break;
                }

                var added = ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : DateTime.UtcNow;

                if (!Enum.IsDefined(typeof(TrackFormat), format))
                {
                    format = TrackFormat.Unknown;
                }

                // The controller never opens the file, so the path is deliberately not sent.
                result.Tracks.Add(new TrackInfo(id, id, title, artist, duration, format, added, bpm));
            }

            return result;
        }
    }

    /// <summary>One chunk of a track's waveform envelope (FR-027 on the controller side).</summary>
    public struct WaveformChunkPayload
    {
        public string TrackId;
        public int ChunkIndex;
        public int ChunkCount;
        public int BucketsPerSecond;
        public double DurationSeconds;

        /// <summary>Peak bytes for this chunk. RMS follows in the same order.</summary>
        public byte[] Peaks;

        public byte[] Rms;

        /// <summary>Envelope buckets per chunk. Two arrays of this size fit inside one frame.</summary>
        public const int BucketsPerChunk = 8192;

        public byte[] Serialize()
        {
            var writer = new ByteWriter(BucketsPerChunk * 2 + 128);
            writer.WriteString(TrackId);
            writer.WriteInt32(ChunkIndex);
            writer.WriteInt32(ChunkCount);
            writer.WriteInt32(BucketsPerSecond);
            writer.WriteDouble(DurationSeconds);
            writer.WriteBlob(Peaks);
            writer.WriteBlob(Rms);
            return writer.ToArray();
        }

        public static WaveformChunkPayload Deserialize(byte[] payload)
        {
            var reader = new ByteReader(payload);
            return new WaveformChunkPayload
            {
                TrackId = reader.ReadString(),
                ChunkIndex = reader.ReadInt32(),
                ChunkCount = reader.ReadInt32(),
                BucketsPerSecond = reader.ReadInt32(Analysis.WaveformData.DefaultBucketsPerSecond),
                DurationSeconds = reader.ReadDouble(),
                Peaks = reader.ReadBlob(),
                Rms = reader.ReadBlob()
            };
        }
    }

    /// <summary>
    /// Builders for every outgoing message.
    ///
    /// Centralising payload layout here means the encode and decode sides cannot drift:
    /// each message has exactly one writer and one reader, and the round-trip is covered by
    /// the EditMode suite.
    /// </summary>
    public static class Messages
    {
        public static NetMessage Command(
            MessageType type,
            DeckId? deck,
            uint sequence,
            byte[] payload = null) =>
            new NetMessage(type, deck, sequence, NetMessage.NowMs(), payload);

        public static byte[] Float(float value)
        {
            var writer = new ByteWriter(4);
            writer.WriteSingle(value);
            return writer.ToArray();
        }

        public static byte[] Double(double value)
        {
            var writer = new ByteWriter(8);
            writer.WriteDouble(value);
            return writer.ToArray();
        }

        public static byte[] Bool(bool value)
        {
            var writer = new ByteWriter(1);
            writer.WriteBool(value);
            return writer.ToArray();
        }

        public static byte[] Byte(byte value)
        {
            var writer = new ByteWriter(1);
            writer.WriteByte(value);
            return writer.ToArray();
        }

        public static byte[] Text(string value)
        {
            var writer = new ByteWriter(64);
            writer.WriteString(value);
            return writer.ToArray();
        }

        public static byte[] DoublePair(double a, double b)
        {
            var writer = new ByteWriter(16);
            writer.WriteDouble(a);
            writer.WriteDouble(b);
            return writer.ToArray();
        }

        public static float ReadFloat(NetMessage message, float fallback = 0f) =>
            message == null ? fallback : new ByteReader(message.Payload).ReadSingle(fallback);

        public static double ReadDouble(NetMessage message, double fallback = 0d) =>
            message == null ? fallback : new ByteReader(message.Payload).ReadDouble(fallback);

        public static bool ReadBool(NetMessage message, bool fallback = false) =>
            message == null ? fallback : new ByteReader(message.Payload).ReadBool(fallback);

        public static byte ReadByte(NetMessage message, byte fallback = 0) =>
            message == null ? fallback : new ByteReader(message.Payload).ReadByte(fallback);

        public static string ReadText(NetMessage message, string fallback = "") =>
            message == null ? fallback : new ByteReader(message.Payload).ReadString(fallback);

        public static void ReadDoublePair(NetMessage message, out double a, out double b)
        {
            a = 0d;
            b = 0d;
            if (message == null)
            {
                return;
            }

            var reader = new ByteReader(message.Payload);
            a = reader.ReadDouble();
            b = reader.ReadDouble();
        }

        /// <summary>
        /// Splits a library into frame-sized chunks. An empty library still produces one
        /// chunk, so the controller always learns that the library is empty rather than
        /// waiting forever.
        /// </summary>
        public static List<LibraryChunkPayload> ChunkLibrary(IReadOnlyList<TrackInfo> tracks, int revision)
        {
            var result = new List<LibraryChunkPayload>();
            var list = tracks ?? System.Array.Empty<TrackInfo>();
            var chunkCount = Math.Max(1, (list.Count + LibraryChunkPayload.TracksPerChunk - 1) /
                                         LibraryChunkPayload.TracksPerChunk);

            for (var index = 0; index < chunkCount; index++)
            {
                var slice = new List<TrackInfo>();
                var start = index * LibraryChunkPayload.TracksPerChunk;
                var end = Math.Min(list.Count, start + LibraryChunkPayload.TracksPerChunk);
                for (var i = start; i < end; i++)
                {
                    slice.Add(list[i]);
                }

                result.Add(new LibraryChunkPayload
                {
                    Revision = revision,
                    ChunkIndex = index,
                    ChunkCount = chunkCount,
                    Tracks = slice
                });
            }

            return result;
        }

        /// <summary>Splits a waveform envelope into frame-sized chunks.</summary>
        public static List<WaveformChunkPayload> ChunkWaveform(string trackId, Analysis.WaveformData waveform)
        {
            var result = new List<WaveformChunkPayload>();
            if (waveform == null)
            {
                waveform = Analysis.WaveformData.Empty;
            }

            var total = waveform.BucketCount;
            var chunkCount = Math.Max(1, (total + WaveformChunkPayload.BucketsPerChunk - 1) /
                                         WaveformChunkPayload.BucketsPerChunk);

            for (var index = 0; index < chunkCount; index++)
            {
                var start = index * WaveformChunkPayload.BucketsPerChunk;
                var length = Math.Max(0, Math.Min(WaveformChunkPayload.BucketsPerChunk, total - start));
                var peaks = new byte[length];
                var rms = new byte[length];
                if (length > 0)
                {
                    Buffer.BlockCopy(waveform.Peaks, start, peaks, 0, length);
                    Buffer.BlockCopy(waveform.Rms, start, rms, 0, length);
                }

                result.Add(new WaveformChunkPayload
                {
                    TrackId = trackId ?? string.Empty,
                    ChunkIndex = index,
                    ChunkCount = chunkCount,
                    BucketsPerSecond = waveform.BucketsPerSecond,
                    DurationSeconds = waveform.DurationSeconds,
                    Peaks = peaks,
                    Rms = rms
                });
            }

            return result;
        }
    }
}
