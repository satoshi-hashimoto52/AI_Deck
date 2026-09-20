using AIDeck.Core.Generation;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// The generation rules that hold with no engine, no panel and no Unity scene: what counts
    /// as a valid request, what the bridge's state names mean, and when a generation may start.
    ///
    /// The last one is the important one. Generating costs this machine a swap file growing to
    /// 19 GB, and the gate is the only thing standing between that and a track that is playing.
    /// </summary>
    [TestFixture]
    public class GenerationValidatorTests
    {
        private static GenerationFields Valid() => new GenerationFields
        {
            Title = "Night Drive",
            Prompt = "melodic deep house",
            Lyrics = string.Empty,
            Duration = "30",
            Bpm = "118",
            Key = "A minor",
            TimeSignature = "4",
            VocalLanguage = "ja",
            Seed = string.Empty
        };

        [Test]
        public void AFullyFilledRequestIsAccepted()
        {
            Assert.That(GenerationValidator.Validate(Valid()).IsValid, Is.True);
        }

        [Test]
        public void ATitleIsRequired()
        {
            var fields = Valid();
            fields.Title = "   ";

            var result = GenerationValidator.Validate(fields);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Field, Is.EqualTo("title"));
        }

        [Test]
        public void AStyleDescriptionIsRequired()
        {
            var fields = Valid();
            fields.Prompt = string.Empty;

            var result = GenerationValidator.Validate(fields);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Field, Is.EqualTo("prompt"));
        }

        [TestCase("9")]
        [TestCase("601")]
        [TestCase("-30")]
        public void ADurationOutsideTheSupportedRangeIsRefused(string duration)
        {
            var fields = Valid();
            fields.Duration = duration;

            Assert.That(GenerationValidator.Validate(fields).Field, Is.EqualTo("duration"));
        }

        [TestCase("10")]
        [TestCase("600")]
        public void TheBoundariesThemselvesAreAccepted(string duration)
        {
            // These are the same numbers the Python client enforces. If one side moves and the
            // other does not, a request the UI accepts is refused by the bridge.
            var fields = Valid();
            fields.Duration = duration;

            Assert.That(GenerationValidator.Validate(fields).IsValid, Is.True);
        }

        [TestCase("29")]
        [TestCase("301")]
        public void ABpmOutsideTheSupportedRangeIsRefused(string bpm)
        {
            var fields = Valid();
            fields.Bpm = bpm;

            Assert.That(GenerationValidator.Validate(fields).Field, Is.EqualTo("bpm"));
        }

        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("-Infinity")]
        public void NaNAndInfinityAreRefusedEvenThoughTheyParse(string value)
        {
            // float.TryParse says yes to all three, and a NaN duration would otherwise travel
            // all the way to the engine before anything noticed.
            var fields = Valid();
            fields.Duration = value;

            Assert.That(GenerationValidator.Validate(fields).Field, Is.EqualTo("duration"));
        }

        [Test]
        public void ADurationThatIsNotANumberIsRefused()
        {
            var fields = Valid();
            fields.Duration = "quite long";

            Assert.That(GenerationValidator.Validate(fields).Field, Is.EqualTo("duration"));
        }

        [TestCase("5")]
        [TestCase("")]
        [TestCase("four")]
        public void AnUnsupportedTimeSignatureIsRefused(string signature)
        {
            var fields = Valid();
            fields.TimeSignature = signature;

            Assert.That(GenerationValidator.Validate(fields).Field, Is.EqualTo("timeSignature"));
        }

        [Test]
        public void AnEmptySeedMeansRandomAndIsValid()
        {
            var fields = Valid();
            fields.Seed = "   ";

            Assert.That(GenerationValidator.Validate(fields).IsValid, Is.True);
        }

        [Test]
        public void ASeedThatIsNotAWholeNumberIsRefused()
        {
            var fields = Valid();
            fields.Seed = "12.5";

            Assert.That(GenerationValidator.Validate(fields).Field, Is.EqualTo("seed"));
        }

        [Test]
        public void TheFieldsCanBeClonedSoARefusalCostsNothing()
        {
            var fields = Valid();
            var copy = fields.Clone();
            copy.Title = "Changed";

            Assert.That(fields.Title, Is.EqualTo("Night Drive"));
        }
    }

    [TestFixture]
    public class GeneratorStateTests
    {
        [TestCase("not-installed", GeneratorState.NotInstalled)]
        [TestCase("stopped", GeneratorState.Stopped)]
        [TestCase("starting", GeneratorState.Starting)]
        [TestCase("ready", GeneratorState.Ready)]
        [TestCase("queued", GeneratorState.Queued)]
        [TestCase("generating", GeneratorState.Generating)]
        [TestCase("completed", GeneratorState.Completed)]
        [TestCase("failed", GeneratorState.Failed)]
        [TestCase("cancelling", GeneratorState.Cancelling)]
        [TestCase("cancelled", GeneratorState.Cancelled)]
        public void EveryBridgeStateNameIsUnderstood(string wire, GeneratorState expected)
        {
            Assert.That(GeneratorStateExtensions.Parse(wire), Is.EqualTo(expected));
        }

        [Test]
        public void AnUnknownNameBecomesUnknownRatherThanThrowing()
        {
            // A newer bridge must never crash an older deck.
            Assert.That(GeneratorStateExtensions.Parse("teleporting"),
                Is.EqualTo(GeneratorState.Unknown));
        }

        [TestCase(GeneratorState.Queued)]
        [TestCase(GeneratorState.Generating)]
        [TestCase(GeneratorState.Cancelling)]
        public void TheBusyStatesRefuseASecondGeneration(GeneratorState state)
        {
            Assert.That(state.IsBusy(), Is.True);
            Assert.That(state.CanGenerate(), Is.False);
        }

        [TestCase(GeneratorState.Ready)]
        [TestCase(GeneratorState.Completed)]
        [TestCase(GeneratorState.Failed)]
        [TestCase(GeneratorState.Cancelled)]
        public void AFinishedOrReadyGeneratorAcceptsAnother(GeneratorState state)
        {
            Assert.That(state.CanGenerate(), Is.True);
        }

        [TestCase(GeneratorState.Stopped)]
        [TestCase(GeneratorState.Starting)]
        [TestCase(GeneratorState.NotInstalled)]
        public void AGeneratorThatIsNotLoadedCannotGenerate(GeneratorState state)
        {
            Assert.That(state.CanGenerate(), Is.False);
        }
    }

    [TestFixture]
    public class GenerationSafetyGateTests
    {
        private static DeckActivity Playing(bool a = false, bool b = false) =>
            new DeckActivity(a, b, false, false, false, false);

        [Test]
        public void AnIdleDeckWithAReadyGeneratorIsAllowed()
        {
            var decision = GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Ready);

            Assert.That(decision.IsAllowed, Is.True);
            Assert.That(decision.Reason, Is.Empty);
        }

        [Test]
        public void GeneratingIsRefusedWhileDeckAIsPlaying()
        {
            var decision = GenerationSafetyGate.Evaluate(Playing(a: true), GeneratorState.Ready);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("Stop both decks"));
        }

        [Test]
        public void GeneratingIsRefusedWhileDeckBIsPlaying()
        {
            Assert.That(
                GenerationSafetyGate.Evaluate(Playing(b: true), GeneratorState.Ready).IsAllowed,
                Is.False);
        }

        [Test]
        public void GeneratingIsRefusedWhileADeckIsStillFadingOut()
        {
            // "Not playing" is not "silent": the transport fades over about twelve
            // milliseconds and sound is still leaving the machine during it.
            var stopping = new DeckActivity(false, false, true, false, false, false);

            var decision = GenerationSafetyGate.Evaluate(stopping, GeneratorState.Ready);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("fading out"));
        }

        [Test]
        public void GeneratingIsRefusedWhileCueMonitoring()
        {
            var cueing = new DeckActivity(false, false, false, false, true, false);

            var decision = GenerationSafetyGate.Evaluate(cueing, GeneratorState.Ready);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("cue monitoring"));
        }

        [Test]
        public void GeneratingIsRefusedWhileRecording()
        {
            // A recording ruined by a dropout cannot be recovered by trying again, so this is
            // checked before anything else.
            var recording = new DeckActivity(false, false, false, false, false, true);

            var decision = GenerationSafetyGate.Evaluate(recording, GeneratorState.Ready);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("recording"));
        }

        [Test]
        public void RecordingIsReportedEvenWhenADeckIsAlsoPlaying()
        {
            var both = new DeckActivity(true, false, false, false, false, true);

            Assert.That(GenerationSafetyGate.Evaluate(both, GeneratorState.Ready).Reason,
                Does.Contain("recording"));
        }

        [Test]
        public void ASecondGenerationIsRefusedWhileOneRuns()
        {
            var decision = GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Generating);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("already running"));
        }

        [Test]
        public void AStoppedGeneratorAsksForTheServerToBeStarted()
        {
            var decision = GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Stopped);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("Start the AI server"));
        }

        [Test]
        public void AStartingGeneratorSaysItIsStillLoading()
        {
            var decision = GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Starting);

            Assert.That(decision.IsAllowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("loading"));
        }

        [Test]
        public void AMissingInstallationNamesTheSetupScript()
        {
            var decision = GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.NotInstalled);

            Assert.That(decision.Reason, Does.Contain("setup_macos.sh"));
        }

        [Test]
        public void EveryRefusalGivesAReason()
        {
            // A disabled button with no explanation is the failure mode this gate is most
            // likely to produce, so every path is checked for a sentence.
            var cases = new[]
            {
                GenerationSafetyGate.Evaluate(Playing(a: true), GeneratorState.Ready),
                GenerationSafetyGate.Evaluate(
                    new DeckActivity(false, false, false, true, false, false), GeneratorState.Ready),
                GenerationSafetyGate.Evaluate(
                    new DeckActivity(false, false, false, false, true, false), GeneratorState.Ready),
                GenerationSafetyGate.Evaluate(
                    new DeckActivity(false, false, false, false, false, true), GeneratorState.Ready),
                GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Stopped),
                GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Starting),
                GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.NotInstalled),
                GenerationSafetyGate.Evaluate(DeckActivity.Idle, GeneratorState.Generating)
            };

            foreach (var decision in cases)
            {
                Assert.That(decision.IsAllowed, Is.False);
                Assert.That(decision.Reason, Is.Not.Empty);
            }
        }
    }
}
