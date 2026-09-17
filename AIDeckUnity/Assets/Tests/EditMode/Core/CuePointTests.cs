using AIDeck.Core.Deck;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "CUE位置" item of §10.1 and FR-024.</summary>
    [TestFixture]
    public class CuePointTests
    {
        [Test]
        public void UnsetCue_ReturnsToTheTrackStart()
        {
            var cue = new CuePoint(180d);
            Assert.That(cue.IsSet, Is.False);
            Assert.That(cue.ReturnPosition, Is.EqualTo(0d));
        }

        [Test]
        public void Set_StoresThePositionAndMarksItSet()
        {
            var cue = new CuePoint(180d);
            cue.Set(42.5d);
            Assert.That(cue.IsSet, Is.True);
            Assert.That(cue.PositionSeconds, Is.EqualTo(42.5d));
            Assert.That(cue.ReturnPosition, Is.EqualTo(42.5d));
        }

        [Test]
        public void Set_ClampsOutOfRangeRequestsInsteadOfRejectingThem()
        {
            var cue = new CuePoint(180d);
            cue.Set(500d);
            Assert.That(cue.PositionSeconds, Is.EqualTo(180d));
            cue.Set(-10d);
            Assert.That(cue.PositionSeconds, Is.EqualTo(0d));
        }

        [Test]
        public void Set_SendsNonFiniteInputToTheTrackStartRatherThanTheEnd()
        {
            // A corrupt value is not a request to cue at the end of the track: the safe
            // reading of "unknown position" is the start, so every non-finite input lands
            // on 0 rather than being clamped to the track length.
            var cue = new CuePoint(180d);
            cue.Set(double.NaN);
            Assert.That(cue.PositionSeconds, Is.EqualTo(0d));
            cue.Set(double.PositiveInfinity);
            Assert.That(cue.PositionSeconds, Is.EqualTo(0d));
            cue.Set(double.NegativeInfinity);
            Assert.That(cue.PositionSeconds, Is.EqualTo(0d));
        }

        [Test]
        public void ShorteningTheTrack_PullsTheCueBackInsideIt()
        {
            var cue = new CuePoint(180d);
            cue.Set(170d);
            cue.TrackLengthSeconds = 100d;
            Assert.That(cue.PositionSeconds, Is.EqualTo(100d));
        }

        [Test]
        public void Reset_ClearsTheCueBackToTheStart()
        {
            var cue = new CuePoint(180d);
            cue.Set(60d);
            cue.Reset();
            Assert.That(cue.IsSet, Is.False);
            Assert.That(cue.PositionSeconds, Is.EqualTo(0d));
            Assert.That(cue.ReturnPosition, Is.EqualTo(0d));
        }
    }
}
