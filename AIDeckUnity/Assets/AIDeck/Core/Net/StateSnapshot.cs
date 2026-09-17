using AIDeck.Core.Mixer;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// The complete host state broadcast on the fast channel every
    /// <see cref="ProtocolInfo.SnapshotIntervalSeconds"/> (§4.4).
    ///
    /// It is a full snapshot rather than a delta on purpose: a controller that missed
    /// packets, backgrounded, or reconnected is corrected by the next one without any
    /// catch-up protocol (FR-064, FR-065).
    /// </summary>
    public struct StateSnapshot
    {
        public long HostTimeMs;

        /// <summary>Increments whenever the library changes, so the controller knows to re-fetch (FR-009).</summary>
        public int LibraryRevision;

        public DeckSnapshot DeckA;
        public DeckSnapshot DeckB;

        public float ChannelGainA;
        public float ChannelGainB;
        public float MasterGain;
        public float Crossfader;
        public CrossfaderCurveType CrossfaderCurve;
        public float FilterA;
        public float FilterB;
        public bool MuteA;
        public bool MuteB;
        public bool EchoA;
        public bool EchoB;
        public bool CueA;
        public bool CueB;

        public float MasterPeak;
        public float MasterRms;
        public bool MasterClipping;

        public bool IsRecording;
        public double RecordingSeconds;

        /// <summary>File name only — the full path stays on the host (NFR-006).</summary>
        public string RecordingFileName;

        /// <summary>Short user-facing notice, or empty. Diagnostic detail stays in the host log (NFR-007).</summary>
        public string Notice;

        public DeckSnapshot Deck(DeckId deck) => deck == DeckId.A ? DeckA : DeckB;

        public static StateSnapshot Empty => new StateSnapshot
        {
            DeckA = DeckSnapshot.Empty(DeckId.A),
            DeckB = DeckSnapshot.Empty(DeckId.B),
            ChannelGainA = 0.8f,
            ChannelGainB = 0.8f,
            MasterGain = 0.8f,
            CrossfaderCurve = CrossfaderCurveType.Smooth,
            RecordingFileName = string.Empty,
            Notice = string.Empty
        };

        /// <summary>Copies the mixer parameters out of the authoritative mixer state.</summary>
        public void CaptureMixer(MixerState mixer)
        {
            if (mixer == null)
            {
                return;
            }

            ChannelGainA = mixer.ChannelGainA;
            ChannelGainB = mixer.ChannelGainB;
            MasterGain = mixer.MasterGain;
            Crossfader = mixer.Crossfader;
            CrossfaderCurve = mixer.CrossfaderCurveType;
            FilterA = mixer.FilterA;
            FilterB = mixer.FilterB;
            MuteA = mixer.MuteA;
            MuteB = mixer.MuteB;
            EchoA = mixer.EchoA;
            EchoB = mixer.EchoB;
            CueA = mixer.CueA;
            CueB = mixer.CueB;
        }

        /// <summary>Applies the mixer parameters onto a local mirror, for the controller's UI.</summary>
        public void ApplyMixer(MixerState mixer)
        {
            if (mixer == null)
            {
                return;
            }

            mixer.ChannelGainA = ChannelGainA;
            mixer.ChannelGainB = ChannelGainB;
            mixer.MasterGain = MasterGain;
            mixer.Crossfader = Crossfader;
            mixer.CrossfaderCurveType = CrossfaderCurve;
            mixer.FilterA = FilterA;
            mixer.FilterB = FilterB;
            mixer.MuteA = MuteA;
            mixer.MuteB = MuteB;
            mixer.EchoA = EchoA;
            mixer.EchoB = EchoB;
            mixer.CueA = CueA;
            mixer.CueB = CueB;
        }

        public byte[] Serialize()
        {
            var writer = new ByteWriter(512);
            writer.WriteInt64(HostTimeMs);
            writer.WriteInt32(LibraryRevision);
            DeckA.Write(writer);
            DeckB.Write(writer);
            writer.WriteSingle(ChannelGainA);
            writer.WriteSingle(ChannelGainB);
            writer.WriteSingle(MasterGain);
            writer.WriteSingle(Crossfader);
            writer.WriteByte((byte)CrossfaderCurve);
            writer.WriteSingle(FilterA);
            writer.WriteSingle(FilterB);
            writer.WriteBool(MuteA);
            writer.WriteBool(MuteB);
            writer.WriteBool(EchoA);
            writer.WriteBool(EchoB);
            writer.WriteBool(CueA);
            writer.WriteBool(CueB);
            writer.WriteSingle(MasterPeak);
            writer.WriteSingle(MasterRms);
            writer.WriteBool(MasterClipping);
            writer.WriteBool(IsRecording);
            writer.WriteDouble(RecordingSeconds);
            writer.WriteString(RecordingFileName);
            writer.WriteString(Notice);
            return writer.ToArray();
        }

        public static StateSnapshot Deserialize(byte[] payload)
        {
            var snapshot = Empty;
            if (payload == null || payload.Length == 0)
            {
                return snapshot;
            }

            var reader = new ByteReader(payload);
            snapshot.HostTimeMs = reader.ReadInt64();
            snapshot.LibraryRevision = reader.ReadInt32();
            snapshot.DeckA = DeckSnapshot.Read(reader);
            snapshot.DeckB = DeckSnapshot.Read(reader);
            snapshot.ChannelGainA = AudioSafety.Sanitize01(reader.ReadSingle(0.8f));
            snapshot.ChannelGainB = AudioSafety.Sanitize01(reader.ReadSingle(0.8f));
            snapshot.MasterGain = AudioSafety.Sanitize01(reader.ReadSingle(0.8f));
            snapshot.Crossfader = AudioSafety.SanitizeBipolar(reader.ReadSingle());
            snapshot.CrossfaderCurve = ToCurve(reader.ReadByte());
            snapshot.FilterA = AudioSafety.SanitizeBipolar(reader.ReadSingle());
            snapshot.FilterB = AudioSafety.SanitizeBipolar(reader.ReadSingle());
            snapshot.MuteA = reader.ReadBool();
            snapshot.MuteB = reader.ReadBool();
            snapshot.EchoA = reader.ReadBool();
            snapshot.EchoB = reader.ReadBool();
            snapshot.CueA = reader.ReadBool();
            snapshot.CueB = reader.ReadBool();
            snapshot.MasterPeak = AudioSafety.Sanitize01(reader.ReadSingle());
            snapshot.MasterRms = AudioSafety.Sanitize01(reader.ReadSingle());
            snapshot.MasterClipping = reader.ReadBool();
            snapshot.IsRecording = reader.ReadBool();
            snapshot.RecordingSeconds = AudioSafety.SanitizeDouble(reader.ReadDouble(), 0d, 24d * 3600d, 0d);
            snapshot.RecordingFileName = reader.ReadString();
            snapshot.Notice = reader.ReadString();
            return snapshot;
        }

        private static CrossfaderCurveType ToCurve(byte value) =>
            value <= (byte)CrossfaderCurveType.Sharp ? (CrossfaderCurveType)value : CrossfaderCurveType.Smooth;
    }
}
