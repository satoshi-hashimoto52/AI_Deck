using System;

namespace AIDeck.Core.Generation
{
    /// <summary>What the machine's memory is doing, as the bridge measured it.</summary>
    public readonly struct MemoryPressure
    {
        public MemoryPressure(float swapUsedGb, float compressedGb, float freeGb,
                              bool underPressure, string advice)
        {
            SwapUsedGb = swapUsedGb;
            CompressedGb = compressedGb;
            FreeGb = freeGb;
            UnderPressure = underPressure;
            Advice = advice ?? string.Empty;
        }

        public float SwapUsedGb { get; }
        public float CompressedGb { get; }
        public float FreeGb { get; }

        /// <summary>True when the machine is thrashing rather than merely busy.</summary>
        public bool UnderPressure { get; }

        /// <summary>A sentence with the real numbers in it, or empty.</summary>
        public string Advice { get; }

        public static MemoryPressure None =>
            new MemoryPressure(0f, 0f, 0f, false, string.Empty);
    }

    /// <summary>One snapshot of what the generator is doing.</summary>
    public readonly struct GeneratorStatus
    {
        public GeneratorStatus(
            GeneratorState state,
            string message,
            float elapsedSeconds,
            string errorKind,
            string errorDetail,
            string completedFileName,
            string completedFilePath,
            double completedDurationSeconds,
            bool ownsServer,
            MemoryPressure memory = default)
        {
            Memory = memory;
            State = state;
            Message = message ?? string.Empty;
            ElapsedSeconds = elapsedSeconds;
            ErrorKind = errorKind ?? string.Empty;
            ErrorDetail = errorDetail ?? string.Empty;
            CompletedFileName = completedFileName ?? string.Empty;
            CompletedFilePath = completedFilePath ?? string.Empty;
            CompletedDurationSeconds = completedDurationSeconds;
            OwnsServer = ownsServer;
        }

        public GeneratorState State { get; }

        /// <summary>A short sentence for the panel. Never contains a prompt or a path.</summary>
        public string Message { get; }

        public float ElapsedSeconds { get; }

        /// <summary>Machine-readable failure class from the bridge, or empty.</summary>
        public string ErrorKind { get; }

        /// <summary>The long version, for <c>DiagnosticLog</c> rather than the screen.</summary>
        public string ErrorDetail { get; }

        /// <summary>File name of a finished track. Empty unless <see cref="State"/> is Completed.</summary>
        public string CompletedFileName { get; }

        /// <summary>
        /// Absolute path of the finished track, used only to hand one file to the importer.
        /// Never logged and never shown (NFR-012).
        /// </summary>
        public string CompletedFilePath { get; }

        public double CompletedDurationSeconds { get; }

        /// <summary>True when AI Deck started the engine, and so must stop it when it quits.</summary>
        public bool OwnsServer { get; }

        /// <summary>Swap and compressed memory as the bridge last sampled them.</summary>
        public MemoryPressure Memory { get; }

        /// <summary>
        /// Everything the sheet draws, in one comparable value.
        ///
        /// The panel re-lays out only when this changes. Polling produces an identical status
        /// several times a second, and re-running the layout each time would burn frames on a
        /// machine that is already short of them.
        /// </summary>
        public string DisplaySignature =>
            $"{State}|{Message}|{ErrorKind}|{CompletedFileName}|{ElapsedSeconds:F0}|"
            + $"{Memory.UnderPressure}|{Memory.Advice}";

        public static GeneratorStatus Unknown(string message) => new GeneratorStatus(
            GeneratorState.Unknown, message, 0f, string.Empty, string.Empty,
            string.Empty, string.Empty, 0d, false);
    }

    /// <summary>
    /// Everything the deck may ask of the generator.
    ///
    /// An interface rather than a concrete type because the PlayMode tests drive the whole
    /// panel — real buttons, real input fields, real library reflection — against a fake that
    /// never loads a model. A test suite that needed 10 GB of weights and 85 seconds per case
    /// would not be run, and a test that is not run is not a test.
    ///
    /// Every method returns immediately. Results arrive through <see cref="StatusChanged"/> on
    /// the main thread; nothing here blocks, and nothing here is reachable from the audio
    /// thread.
    /// </summary>
    public interface IGeneratorBridge
    {
        /// <summary>The most recent status. Never blocks.</summary>
        GeneratorStatus Status { get; }

        /// <summary>Raised on the main thread whenever <see cref="Status"/> changes.</summary>
        event Action<GeneratorStatus> StatusChanged;

        /// <summary>Begin watching the generator. Starts no process and loads no model.</summary>
        void Connect();

        /// <summary>
        /// Load the models. Only ever called from an explicit button press: a ten-gigabyte
        /// load must never be a side effect of launching a DJ application.
        /// </summary>
        void StartServer();

        /// <summary>Unload the models. <paramref name="force"/> stops a server we merely adopted.</summary>
        void StopServer(bool force);

        /// <summary>Queue one generation. Refused if one is already running.</summary>
        void Generate(GenerationFields fields);

        /// <summary>
        /// Stop the running generation.
        ///
        /// See the bridge's own documentation for what this costs: ACE-Step v0.1.8 has no
        /// cancellation endpoint, so cancelling stops the engine process and the models must
        /// be loaded again afterwards. The alternative — marking the UI cancelled while the
        /// work continued — was rejected.
        /// </summary>
        void Cancel();

        /// <summary>Called when the application quits. Stops only a server we started.</summary>
        void Shutdown();
    }
}
