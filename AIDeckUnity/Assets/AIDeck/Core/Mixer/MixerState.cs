using AIDeck.Core.Model;

namespace AIDeck.Core.Mixer
{
    /// <summary>
    /// Authoritative mixer parameters (FR-040 to FR-046). Lives on the Mac host; the iPad
    /// mirrors it. All setters sanitise, so the class can be driven straight from network
    /// input without a separate validation layer.
    /// </summary>
    public sealed class MixerState
    {
        private float _channelGainA = 0.8f;
        private float _channelGainB = 0.8f;
        private float _masterGain = 0.8f;
        private float _crossfader;
        private float _filterA;
        private float _filterB;

        /// <summary>Deck A channel fader, 0..1.</summary>
        public float ChannelGainA
        {
            get => _channelGainA;
            set => _channelGainA = AudioSafety.Sanitize01(value);
        }

        /// <summary>Deck B channel fader, 0..1.</summary>
        public float ChannelGainB
        {
            get => _channelGainB;
            set => _channelGainB = AudioSafety.Sanitize01(value);
        }

        /// <summary>Master output level, 0..1 (FR-046).</summary>
        public float MasterGain
        {
            get => _masterGain;
            set => _masterGain = AudioSafety.Sanitize01(value);
        }

        /// <summary>Crossfader position, -1 (A) .. +1 (B).</summary>
        public float Crossfader
        {
            get => _crossfader;
            set => _crossfader = AudioSafety.SanitizeBipolar(value);
        }

        public CrossfaderCurveType CrossfaderCurveType { get; set; } = CrossfaderCurveType.Smooth;

        /// <summary>Deck A filter knob, -1 (low-pass) .. +1 (high-pass), 0 = bypass (FR-043).</summary>
        public float FilterA
        {
            get => _filterA;
            set => _filterA = AudioSafety.SanitizeBipolar(value);
        }

        /// <summary>Deck B filter knob, -1 (low-pass) .. +1 (high-pass), 0 = bypass (FR-043).</summary>
        public float FilterB
        {
            get => _filterB;
            set => _filterB = AudioSafety.SanitizeBipolar(value);
        }

        /// <summary>Deck A mute (FR-042).</summary>
        public bool MuteA { get; set; }

        /// <summary>Deck B mute (FR-042).</summary>
        public bool MuteB { get; set; }

        /// <summary>Deck A echo send on/off (FR-044).</summary>
        public bool EchoA { get; set; }

        /// <summary>Deck B echo send on/off (FR-044).</summary>
        public bool EchoB { get; set; }

        /// <summary>Which deck is routed to the cue (headphone) bus, when any.</summary>
        public bool CueA { get; set; }

        public bool CueB { get; set; }

        public float ChannelGain(DeckId deck) => deck == DeckId.A ? ChannelGainA : ChannelGainB;

        public void SetChannelGain(DeckId deck, float value)
        {
            if (deck == DeckId.A)
            {
                ChannelGainA = value;
            }
            else
            {
                ChannelGainB = value;
            }
        }

        public float Filter(DeckId deck) => deck == DeckId.A ? FilterA : FilterB;

        public void SetFilter(DeckId deck, float value)
        {
            if (deck == DeckId.A)
            {
                FilterA = value;
            }
            else
            {
                FilterB = value;
            }
        }

        public bool Mute(DeckId deck) => deck == DeckId.A ? MuteA : MuteB;

        public void SetMute(DeckId deck, bool value)
        {
            if (deck == DeckId.A)
            {
                MuteA = value;
            }
            else
            {
                MuteB = value;
            }
        }

        public bool Echo(DeckId deck) => deck == DeckId.A ? EchoA : EchoB;

        public void SetEcho(DeckId deck, bool value)
        {
            if (deck == DeckId.A)
            {
                EchoA = value;
            }
            else
            {
                EchoB = value;
            }
        }

        public bool Cue(DeckId deck) => deck == DeckId.A ? CueA : CueB;

        public void SetCue(DeckId deck, bool value)
        {
            if (deck == DeckId.A)
            {
                CueA = value;
            }
            else
            {
                CueB = value;
            }
        }

        /// <summary>
        /// Total linear gain for a deck: channel fader × crossfader × mute.
        /// The master gain is applied once on the master bus, not here.
        /// </summary>
        public float EffectiveDeckGain(DeckId deck)
        {
            if (Mute(deck))
            {
                return 0f;
            }

            CrossfaderCurve.Evaluate(Crossfader, CrossfaderCurveType, out var xa, out var xb);
            var cross = deck == DeckId.A ? xa : xb;
            return AudioSafety.Sanitize01(ChannelGain(deck) * cross);
        }
    }
}
