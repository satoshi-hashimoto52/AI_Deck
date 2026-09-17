using AIDeck.Core.Deck;
using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// End-to-end behaviour of a deck without an audio device: load, transport, cue, loop,
    /// sync and the safety rule that a lost input releases every continuous control.
    /// </summary>
    [TestFixture]
    public class DeckModelTests
    {
        private DeckModel _deck;

        [SetUp]
        public void SetUp() => _deck = new DeckModel(DeckId.A);

        private void Load(double length = 200d, double bpm = 128d)
        {
            _deck.BeginLoad();
            Assert.That(_deck.CompleteLoad("track-1", length, bpm), Is.True);
        }

        [Test]
        public void NewDeck_IsEmpty()
        {
            Assert.That(_deck.HasTrack, Is.False);
            Assert.That(_deck.Transport.State, Is.EqualTo(DeckPlaybackState.Empty));
            Assert.That(_deck.EffectiveRate, Is.EqualTo(1f));
        }

        [Test]
        public void CompleteLoad_ResetsEverythingFromThePreviousTrack()
        {
            Load();
            _deck.Seek(50d);
            _deck.SetCueHere();
            _deck.Loop.SetRegion(10d, 20d);
            _deck.Loop.Enable();
            _deck.Tempo.Fader = 0.5f;
            _deck.EnableSync(120d);

            _deck.BeginLoad();
            _deck.CompleteLoad("track-2", 120d, 90d);

            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
            Assert.That(_deck.Cue.IsSet, Is.False);
            Assert.That(_deck.Loop.IsActive, Is.False, "a loop from another song would be silently wrong");
            Assert.That(_deck.Tempo.SyncEnabled, Is.False);
            Assert.That(_deck.TrackLengthSeconds, Is.EqualTo(120d));
            Assert.That(_deck.BaseBpm, Is.EqualTo(90d));
        }

        [Test]
        public void FailedLoad_LeavesNoTrackAndAReason()
        {
            _deck.BeginLoad();
            Assert.That(_deck.FailLoad("Decode failed"), Is.True);
            Assert.That(_deck.HasTrack, Is.False);
            Assert.That(_deck.Transport.State, Is.EqualTo(DeckPlaybackState.Error));
            Assert.That(_deck.Transport.ErrorReason, Is.EqualTo("Decode failed"));
            Assert.That(_deck.TrackLengthSeconds, Is.EqualTo(0d));
        }

        [Test]
        public void Advance_MovesThePlayheadAtTheEffectiveRate()
        {
            Load();
            _deck.Play();
            _deck.Tempo.RangePercent = 8f;
            _deck.Tempo.Fader = 1f; // 1.08x

            _deck.Advance(10d);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(10.8d).Within(1e-6));
        }

        [Test]
        public void Advance_DoesNothingWhilePaused()
        {
            Load();
            _deck.Advance(5d);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
        }

        [Test]
        public void ReachingTheEnd_StopsTheDeckAndRaisesTheEvent()
        {
            Load(length: 10d);
            _deck.Play();
            var raised = false;
            _deck.ReachedEnd += () => raised = true;

            _deck.Advance(20d);

            Assert.That(raised, Is.True);
            Assert.That(_deck.IsPlaying, Is.False);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(10d));
        }

        [Test]
        public void ActiveLoop_WrapsThePlayheadAndRequestsASeek()
        {
            Load();
            _deck.Play();
            _deck.Seek(10d);
            _deck.Loop.SetRegion(10d, 12d);
            _deck.Loop.Enable();

            double? seeked = null;
            _deck.SeekRequested += p => seeked = p;

            _deck.Advance(2.5d);

            Assert.That(_deck.PositionSeconds, Is.EqualTo(10.5d).Within(1e-6));
            Assert.That(seeked, Is.Not.Null);
            Assert.That(seeked.Value, Is.EqualTo(10.5d).Within(1e-6));
        }

        [Test]
        public void ActiveLoop_PreventsTheEndOfTrackStop()
        {
            Load(length: 20d);
            _deck.Play();
            _deck.Seek(15d);
            _deck.Loop.SetRegion(15d, 17d);
            _deck.Loop.Enable();

            _deck.Advance(30d);

            Assert.That(_deck.IsPlaying, Is.True);
            Assert.That(_deck.PositionSeconds, Is.InRange(15d, 17d));
        }

        [Test]
        public void ReversePastTheStart_ParksAtZeroRatherThanWrapping()
        {
            Load();
            _deck.Play();
            _deck.Seek(1d);
            _deck.Motion.BeginScratch();
            _deck.Motion.UpdateScratch(-5f);

            _deck.Advance(2d);

            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
        }

        [Test]
        public void CueReturn_StopsAndJumpsToTheStoredCue()
        {
            Load();
            _deck.Play();
            _deck.Seek(30d);
            _deck.SetCueHere();
            _deck.Seek(90d);

            Assert.That(_deck.CueReturn(), Is.True);
            Assert.That(_deck.IsPlaying, Is.False);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(30d));
        }

        [Test]
        public void CueReturn_WithNoCueGoesToTheTrackStart()
        {
            Load();
            _deck.Play();
            _deck.Seek(90d);
            _deck.CueReturn();
            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
        }

        [Test]
        public void SetCueHere_IsRefusedOnAnEmptyDeck()
        {
            Assert.That(_deck.SetCueHere(), Is.False);
            Assert.That(_deck.Seek(10d), Is.False);
        }

        [Test]
        public void ReportPosition_ReturnsNullWhenTheDeviceMayContinue()
        {
            Load();
            _deck.Play();
            Assert.That(_deck.ReportPosition(12.5d), Is.Null);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(12.5d));
        }

        [Test]
        public void ReportPosition_ReturnsACorrectionWhenALoopWraps()
        {
            Load();
            _deck.Play();
            _deck.Seek(10d);
            _deck.Loop.SetRegion(10d, 12d);
            _deck.Loop.Enable();

            var correction = _deck.ReportPosition(12.25d);

            Assert.That(correction, Is.Not.Null);
            Assert.That(correction.Value, Is.EqualTo(10.25d).Within(1e-6));
        }

        [Test]
        public void EffectiveRate_CombinesTempoAndPlatterMotion()
        {
            Load();
            _deck.Tempo.RangePercent = 8f;
            _deck.Tempo.Fader = 1f;      // 1.08
            _deck.Motion.BeginScratch();
            _deck.Motion.UpdateScratch(2f);

            Assert.That(_deck.EffectiveRate, Is.EqualTo(2.16f).Within(1e-4f));
        }

        [Test]
        public void Pause_ReleasesAnInFlightGesture()
        {
            Load();
            _deck.Play();
            _deck.Motion.BeginScratch();
            _deck.Motion.UpdateScratch(-3f);

            _deck.Pause();

            Assert.That(_deck.Motion.Mode, Is.EqualTo(MotionMode.Normal));
            Assert.That(_deck.EffectiveRate, Is.GreaterThan(0f), "the next play must not start in reverse");
        }

        [Test]
        public void ReleaseContinuousControls_CancelsTheGesture()
        {
            Load();
            _deck.Play();
            _deck.Motion.BeginScratch();
            _deck.Motion.UpdateScratch(4f);

            _deck.ReleaseContinuousControls();

            Assert.That(_deck.Motion.Mode, Is.EqualTo(MotionMode.Normal));
            Assert.That(_deck.Motion.Rate, Is.EqualTo(1f));
        }

        [Test]
        public void TickMotion_StopsTheTransportWhenTheBrakeCompletes()
        {
            Load();
            _deck.Play();
            _deck.Motion.BrakeSeconds = 0.2f;
            _deck.Motion.StartBrake();

            for (var i = 0; i < 40; i++)
            {
                _deck.TickMotion(0.01f);
            }

            Assert.That(_deck.IsPlaying, Is.False, "BRAKE must leave the deck stopped, not spinning at zero");
        }

        [Test]
        public void EnableSync_UsesTheAnalysedTempoAndRefusesWhenItIsUnknown()
        {
            Load(bpm: 0d);
            Assert.That(_deck.EnableSync(128d), Is.False);

            _deck.SetAnalysedBpm(124d);
            Assert.That(_deck.EnableSync(128d), Is.True);
            Assert.That(_deck.Tempo.SyncEnabled, Is.True);
        }

        [Test]
        public void Eject_ReturnsTheDeckToItsInitialState()
        {
            Load();
            _deck.Play();
            _deck.Seek(40d);
            _deck.SetCueHere();
            _deck.Tempo.Fader = 0.7f;

            _deck.Eject();

            Assert.That(_deck.HasTrack, Is.False);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
            Assert.That(_deck.Cue.IsSet, Is.False);
            Assert.That(_deck.Tempo.Fader, Is.EqualTo(0f));
            Assert.That(_deck.TrackId, Is.Empty);
        }

        [Test]
        public void Snapshot_ReflectsTheModel()
        {
            Load(length: 180d, bpm: 124d);
            _deck.Play();
            _deck.Seek(30d);
            _deck.SetCueHere();
            _deck.Loop.SetRegion(30d, 34d);
            _deck.Loop.Enable();

            var snapshot = _deck.ToSnapshot(0.42f);

            Assert.That(snapshot.Deck, Is.EqualTo(DeckId.A));
            Assert.That(snapshot.State, Is.EqualTo(DeckPlaybackState.Playing));
            Assert.That(snapshot.TrackId, Is.EqualTo("track-1"));
            Assert.That(snapshot.PositionSeconds, Is.EqualTo(30d));
            Assert.That(snapshot.LengthSeconds, Is.EqualTo(180d));
            Assert.That(snapshot.BaseBpm, Is.EqualTo(124d));
            Assert.That(snapshot.CueIsSet, Is.True);
            Assert.That(snapshot.LoopActive, Is.True);
            Assert.That(snapshot.PeakLevel, Is.EqualTo(0.42f).Within(1e-5f));
            Assert.That(snapshot.RemainingSeconds, Is.EqualTo(150d).Within(1e-6));
        }

        [Test]
        public void Seek_ClampsInsteadOfLeavingTheTrack()
        {
            Load(length: 100d);
            _deck.Seek(500d);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(100d));
            _deck.Seek(-20d);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
            _deck.Seek(double.NaN);
            Assert.That(_deck.PositionSeconds, Is.EqualTo(0d));
        }
    }
}
