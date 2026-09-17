using AIDeck.Core.Deck;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "デッキ状態遷移" item of §10.1 and FR-023.</summary>
    [TestFixture]
    public class DeckStateMachineTests
    {
        private DeckStateMachine _machine;

        [SetUp]
        public void SetUp() => _machine = new DeckStateMachine();

        private void LoadTrack()
        {
            Assert.That(_machine.TryApply(DeckCommand.BeginLoad), Is.True);
            Assert.That(_machine.TryApply(DeckCommand.LoadSucceeded), Is.True);
        }

        [Test]
        public void StartsEmpty()
        {
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Empty));
            Assert.That(_machine.HasTrack, Is.False);
            Assert.That(_machine.IsPlaying, Is.False);
        }

        [Test]
        public void EmptyDeck_RejectsTransportCommands()
        {
            Assert.That(_machine.TryApply(DeckCommand.Play), Is.False);
            Assert.That(_machine.TryApply(DeckCommand.Pause), Is.False);
            Assert.That(_machine.TryApply(DeckCommand.TogglePlay), Is.False);
            Assert.That(_machine.TryApply(DeckCommand.CueReturn), Is.False);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Empty));
        }

        [Test]
        public void SuccessfulLoad_LeavesTheDeckPausedAtTheStart()
        {
            LoadTrack();
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Paused));
            Assert.That(_machine.HasTrack, Is.True);
        }

        [Test]
        public void FailedLoad_EntersErrorWithAReason()
        {
            _machine.TryApply(DeckCommand.BeginLoad);
            Assert.That(_machine.TryApply(DeckCommand.LoadFailed, "Unsupported sample rate"), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Error));
            Assert.That(_machine.ErrorReason, Is.EqualTo("Unsupported sample rate"));
            Assert.That(_machine.HasTrack, Is.False);
        }

        [Test]
        public void ErrorReason_IsCollapsedToASingleShortLine()
        {
            _machine.TryApply(DeckCommand.BeginLoad);
            _machine.TryApply(DeckCommand.LoadFailed, "line one\nline two\r\n" + new string('x', 400));
            Assert.That(_machine.ErrorReason, Does.Not.Contain("\n"));
            Assert.That(_machine.ErrorReason, Does.Not.Contain("\r"));
            Assert.That(_machine.ErrorReason.Length, Is.LessThanOrEqualTo(160));
        }

        [Test]
        public void EmptyReason_StillProducesSomethingShowable()
        {
            _machine.TryApply(DeckCommand.BeginLoad);
            _machine.TryApply(DeckCommand.LoadFailed, "   ");
            Assert.That(_machine.ErrorReason, Is.EqualTo("Unknown error"));
        }

        [Test]
        public void LoadSucceeded_OutsideALoadIsRejected()
        {
            Assert.That(_machine.TryApply(DeckCommand.LoadSucceeded), Is.False);
            LoadTrack();
            Assert.That(_machine.TryApply(DeckCommand.LoadSucceeded), Is.False);
        }

        [Test]
        public void PlayPauseToggle_MovesBetweenPlayingAndPaused()
        {
            LoadTrack();
            Assert.That(_machine.TryApply(DeckCommand.Play), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Playing));

            Assert.That(_machine.TryApply(DeckCommand.TogglePlay), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Paused));

            Assert.That(_machine.TryApply(DeckCommand.TogglePlay), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Playing));
        }

        [Test]
        public void RepeatedPlay_IsAcceptedButRaisesNoChange()
        {
            LoadTrack();
            var changes = 0;
            _machine.StateChanged += (_, __) => changes++;

            Assert.That(_machine.TryApply(DeckCommand.Play), Is.True);
            Assert.That(_machine.TryApply(DeckCommand.Play), Is.True, "a retried network command must not look like an error");
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void LoadingDeck_RefusesTransportUntilTheLoadSettles()
        {
            _machine.TryApply(DeckCommand.BeginLoad);
            Assert.That(_machine.IsBusy, Is.True);
            Assert.That(_machine.TryApply(DeckCommand.Play), Is.False);
            Assert.That(_machine.TryApply(DeckCommand.CueReturn), Is.False);
        }

        [Test]
        public void LoadingOverAPlayingDeck_IsAllowed()
        {
            LoadTrack();
            _machine.TryApply(DeckCommand.Play);
            Assert.That(_machine.TryApply(DeckCommand.BeginLoad), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Loading));
        }

        [Test]
        public void Eject_AlwaysSucceedsAndIsTheWayOutOfError()
        {
            _machine.TryApply(DeckCommand.BeginLoad);
            _machine.TryApply(DeckCommand.LoadFailed, "broken");
            Assert.That(_machine.TryApply(DeckCommand.Eject), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Empty));
            Assert.That(_machine.ErrorReason, Is.Empty);
        }

        [Test]
        public void Fault_MovesALoadedDeckToErrorButLeavesAnEmptyDeckAlone()
        {
            Assert.That(_machine.TryApply(DeckCommand.Fault, "device lost"), Is.False);

            LoadTrack();
            _machine.TryApply(DeckCommand.Play);
            Assert.That(_machine.TryApply(DeckCommand.Fault, "device lost"), Is.True);
            Assert.That(_machine.State, Is.EqualTo(DeckPlaybackState.Error));
        }

        [Test]
        public void StateChanged_ReportsBothEnds()
        {
            LoadTrack();
            DeckPlaybackState from = DeckPlaybackState.Empty, to = DeckPlaybackState.Empty;
            _machine.StateChanged += (f, t) => { from = f; to = t; };
            _machine.TryApply(DeckCommand.Play);
            Assert.That(from, Is.EqualTo(DeckPlaybackState.Paused));
            Assert.That(to, Is.EqualTo(DeckPlaybackState.Playing));
        }
    }
}
