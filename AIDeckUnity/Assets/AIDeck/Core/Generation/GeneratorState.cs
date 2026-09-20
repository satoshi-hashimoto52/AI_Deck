namespace AIDeck.Core.Generation
{
    /// <summary>
    /// Where the local generator is.
    ///
    /// These names are the wire contract with the Python bridge — they are the strings it
    /// returns — and they are deliberately the *only* vocabulary the deck learns. ACE-Step's
    /// own task states, its unified-repository download behaviour and its missing cancel
    /// endpoint all stop at the bridge.
    /// </summary>
    public enum GeneratorState : byte
    {
        /// <summary>The bridge could not be reached at all.</summary>
        Unknown = 0,

        /// <summary>The runtime has never been installed. Nothing can run until setup is run.</summary>
        NotInstalled = 1,

        /// <summary>Installed, no engine process running. No model is in memory.</summary>
        Stopped = 2,

        /// <summary>The engine is loading. On a 16 GB M1 this takes minutes, not seconds.</summary>
        Starting = 3,

        /// <summary>Models are loaded and a request would be accepted.</summary>
        Ready = 4,

        /// <summary>A request has been accepted but has not started rendering.</summary>
        Queued = 5,

        /// <summary>Audio is being rendered.</summary>
        Generating = 6,

        /// <summary>A track finished and passed validation. Only now may it reach the library.</summary>
        Completed = 7,

        /// <summary>The attempt failed. Nothing is offered to the library.</summary>
        Failed = 8,

        /// <summary>A cancellation is in progress; the engine is being stopped.</summary>
        Cancelling = 9,

        /// <summary>The user cancelled. Nothing is offered to the library.</summary>
        Cancelled = 10
    }

    public static class GeneratorStateExtensions
    {
        /// <summary>Parses the bridge's wire value. Unknown strings become <see cref="GeneratorState.Unknown"/>.</summary>
        public static GeneratorState Parse(string value)
        {
            switch (value)
            {
                case "not-installed": return GeneratorState.NotInstalled;
                case "stopped": return GeneratorState.Stopped;
                case "starting": return GeneratorState.Starting;
                case "ready": return GeneratorState.Ready;
                case "queued": return GeneratorState.Queued;
                case "generating": return GeneratorState.Generating;
                case "completed": return GeneratorState.Completed;
                case "failed": return GeneratorState.Failed;
                case "cancelling": return GeneratorState.Cancelling;
                case "cancelled": return GeneratorState.Cancelled;
                default: return GeneratorState.Unknown;
            }
        }

        /// <summary>True while a request is in flight and a second one must be refused.</summary>
        public static bool IsBusy(this GeneratorState state) =>
            state == GeneratorState.Queued
            || state == GeneratorState.Generating
            || state == GeneratorState.Cancelling;

        /// <summary>True when a generation could be started right now.</summary>
        public static bool CanGenerate(this GeneratorState state) =>
            state == GeneratorState.Ready
            || state == GeneratorState.Completed
            || state == GeneratorState.Failed
            || state == GeneratorState.Cancelled;

        /// <summary>A short sentence for the panel.</summary>
        public static string ToDisplayText(this GeneratorState state)
        {
            switch (state)
            {
                case GeneratorState.NotInstalled: return "Not installed";
                case GeneratorState.Stopped: return "Stopped";
                case GeneratorState.Starting: return "Starting — loading models";
                case GeneratorState.Ready: return "Ready";
                case GeneratorState.Queued: return "Queued";
                case GeneratorState.Generating: return "Generating";
                case GeneratorState.Completed: return "Completed";
                case GeneratorState.Failed: return "Failed";
                case GeneratorState.Cancelling: return "Cancelling";
                case GeneratorState.Cancelled: return "Cancelled";
                default: return "Not connected";
            }
        }
    }
}
