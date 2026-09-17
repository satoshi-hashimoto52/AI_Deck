using AIDeck.Core.Deck;
using AIDeck.Core.Model;

namespace AIDeck.Core.Net
{
    /// <summary>
    /// The host's authoritative view of one deck, as broadcast to the controller.
    ///
    /// The controller renders this and never derives transport state locally (§4.1), which
    /// is what keeps the two ends from disagreeing after a dropped packet.
    /// </summary>
    public struct DeckSnapshot
    {
        public DeckId Deck;
        public DeckPlaybackState State;
        public MotionMode Motion;

        /// <summary>Id of the loaded track, or empty when the deck is empty.</summary>
        public string TrackId;

        public double PositionSeconds;
        public double LengthSeconds;

        /// <summary>Analysed tempo of the loaded track, 0 when unknown.</summary>
        public double BaseBpm;

        public float TempoFader;
        public float TempoRangePercent;
        public bool SyncEnabled;

        /// <summary>Final playback rate including tempo, sync and platter motion.</summary>
        public float EffectiveRate;

        public double CueSeconds;
        public bool CueIsSet;

        public double LoopInSeconds;
        public double LoopOutSeconds;
        public bool LoopActive;

        /// <summary>Post-fader peak level of this deck, for the channel meter.</summary>
        public float PeakLevel;

        /// <summary>Short user-facing reason when <see cref="State"/> is Error.</summary>
        public string ErrorReason;

        /// <summary>Tempo after the fader and SYNC are applied, 0 when the base tempo is unknown.</summary>
        public double EffectiveBpm => BaseBpm > 0d ? BaseBpm * EffectiveRate : 0d;

        public bool HasTrack => !string.IsNullOrEmpty(TrackId);

        public double RemainingSeconds
        {
            get
            {
                var remaining = LengthSeconds - PositionSeconds;
                return remaining > 0d ? remaining : 0d;
            }
        }

        public static DeckSnapshot Empty(DeckId deck) => new DeckSnapshot
        {
            Deck = deck,
            State = DeckPlaybackState.Empty,
            Motion = MotionMode.Normal,
            TrackId = string.Empty,
            TempoRangePercent = TempoControl.DefaultRangePercent,
            EffectiveRate = 1f,
            ErrorReason = string.Empty
        };

        public void Write(ByteWriter writer)
        {
            writer.WriteByte((byte)Deck);
            writer.WriteByte((byte)State);
            writer.WriteByte((byte)Motion);
            writer.WriteString(TrackId);
            writer.WriteDouble(PositionSeconds);
            writer.WriteDouble(LengthSeconds);
            writer.WriteDouble(BaseBpm);
            writer.WriteSingle(TempoFader);
            writer.WriteSingle(TempoRangePercent);
            writer.WriteBool(SyncEnabled);
            writer.WriteSingle(EffectiveRate);
            writer.WriteDouble(CueSeconds);
            writer.WriteBool(CueIsSet);
            writer.WriteDouble(LoopInSeconds);
            writer.WriteDouble(LoopOutSeconds);
            writer.WriteBool(LoopActive);
            writer.WriteSingle(PeakLevel);
            writer.WriteString(ErrorReason);
        }

        public static DeckSnapshot Read(ByteReader reader)
        {
            var snapshot = new DeckSnapshot
            {
                Deck = DeckIdExtensions.FromByte(reader.ReadByte()),
                State = ToState(reader.ReadByte()),
                Motion = ToMotion(reader.ReadByte()),
                TrackId = reader.ReadString(),
                PositionSeconds = reader.ReadDouble(),
                LengthSeconds = reader.ReadDouble(),
                BaseBpm = reader.ReadDouble(),
                TempoFader = reader.ReadSingle(),
                TempoRangePercent = reader.ReadSingle(TempoControl.DefaultRangePercent),
                SyncEnabled = reader.ReadBool(),
                EffectiveRate = reader.ReadSingle(1f),
                CueSeconds = reader.ReadDouble(),
                CueIsSet = reader.ReadBool(),
                LoopInSeconds = reader.ReadDouble(),
                LoopOutSeconds = reader.ReadDouble(),
                LoopActive = reader.ReadBool(),
                PeakLevel = reader.ReadSingle(),
                ErrorReason = reader.ReadString()
            };

            // A snapshot drives the UI only; clamping here means a damaged packet shows a
            // conservative reading rather than a bar drawn off the end of the meter.
            snapshot.PeakLevel = AudioSafety.Sanitize01(snapshot.PeakLevel);
            snapshot.TempoFader = AudioSafety.SanitizeBipolar(snapshot.TempoFader);
            snapshot.EffectiveRate = AudioSafety.Sanitize(
                snapshot.EffectiveRate, -PlatterMotion.MaxScratchRate, PlatterMotion.MaxScratchRate, 1f);
            return snapshot;
        }

        private static DeckPlaybackState ToState(byte value) =>
            value <= (byte)DeckPlaybackState.Error ? (DeckPlaybackState)value : DeckPlaybackState.Empty;

        private static MotionMode ToMotion(byte value) =>
            value <= (byte)MotionMode.Backspin ? (MotionMode)value : MotionMode.Normal;
    }
}
