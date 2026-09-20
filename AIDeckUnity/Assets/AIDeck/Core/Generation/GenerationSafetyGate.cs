namespace AIDeck.Core.Generation
{
    /// <summary>What the decks and the recorder are doing, as far as the gate cares.</summary>
    public readonly struct DeckActivity
    {
        public DeckActivity(
            bool deckAPlaying,
            bool deckBPlaying,
            bool deckAStopping,
            bool deckBStopping,
            bool cueMonitoring,
            bool recording)
        {
            DeckAPlaying = deckAPlaying;
            DeckBPlaying = deckBPlaying;
            DeckAStopping = deckAStopping;
            DeckBStopping = deckBStopping;
            CueMonitoring = cueMonitoring;
            Recording = recording;
        }

        public bool DeckAPlaying { get; }
        public bool DeckBPlaying { get; }

        /// <summary>Mid fade-out. The deck is not "playing" but audio is still coming out.</summary>
        public bool DeckAStopping { get; }
        public bool DeckBStopping { get; }

        /// <summary>Either channel is routed to the headphones for cueing.</summary>
        public bool CueMonitoring { get; }

        public bool Recording { get; }

        public static DeckActivity Idle => new DeckActivity(false, false, false, false, false, false);
    }

    /// <summary>Whether a generation may start, and if not, why not in one sentence.</summary>
    public readonly struct GateDecision
    {
        private GateDecision(bool allowed, string reason)
        {
            IsAllowed = allowed;
            Reason = reason;
        }

        public bool IsAllowed { get; }

        /// <summary>Shown to the user verbatim. Empty when allowed.</summary>
        public string Reason { get; }

        public static GateDecision Allow() => new GateDecision(true, string.Empty);

        public static GateDecision Refuse(string reason) => new GateDecision(false, reason);
    }

    /// <summary>
    /// Generation is a preparation activity, not a performance one.
    ///
    /// The measured cost of one 30-second track on the 16 GB M1 this is built for is a swap
    /// file growing from nothing to 19 GB while a 4.5 GB model renders. Nobody has yet shown
    /// that audio output survives that, and the soak test that would show it is Phase 4 work.
    /// Until then the honest position is not "it is probably fine" but "we do not start one
    /// while sound is coming out", which is what this gate enforces.
    ///
    /// It refuses while either deck is playing or fading out, while a deck is being cued into
    /// the headphones, and while the master output is being recorded — a recording ruined by
    /// a dropout cannot be recovered by trying again.
    ///
    /// This is not a claim that generating during playback is unsafe *in principle*. It is a
    /// claim that it has not been measured, and the difference is the whole point.
    /// </summary>
    public static class GenerationSafetyGate
    {
        public static GateDecision Evaluate(DeckActivity activity, GeneratorState state)
        {
            if (activity.Recording)
            {
                return GateDecision.Refuse(
                    "Stop recording first. Generating while the master output is being "
                    + "recorded risks a dropout in a take you cannot repeat.");
            }

            if (activity.DeckAPlaying || activity.DeckBPlaying)
            {
                return GateDecision.Refuse(
                    "Stop both decks first. Generating needs the whole machine, and playing "
                    + "through it has not been proven safe yet.");
            }

            if (activity.DeckAStopping || activity.DeckBStopping)
            {
                return GateDecision.Refuse("A deck is still fading out. Try again in a moment.");
            }

            if (activity.CueMonitoring)
            {
                return GateDecision.Refuse(
                    "Turn off cue monitoring first — that is still audio coming out of the "
                    + "machine.");
            }

            if (state.IsBusy())
            {
                return GateDecision.Refuse("A generation is already running.");
            }

            if (state == GeneratorState.NotInstalled)
            {
                return GateDecision.Refuse(
                    "The local generator is not installed. Run "
                    + "./generator/scripts/setup_macos.sh once, then try again.");
            }

            if (state == GeneratorState.Stopped || state == GeneratorState.Unknown)
            {
                return GateDecision.Refuse("Start the AI server first.");
            }

            if (state == GeneratorState.Starting)
            {
                return GateDecision.Refuse(
                    "The generator is still loading its models. This takes a few minutes on "
                    + "the first start.");
            }

            return GateDecision.Allow();
        }
    }
}
