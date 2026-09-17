using AIDeck.Core.Deck;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "LOOP範囲" item of §10.1 and FR-031.</summary>
    [TestFixture]
    public class LoopRegionTests
    {
        private LoopRegion _loop;

        [SetUp]
        public void SetUp() => _loop = new LoopRegion(200d);

        [Test]
        public void NewRegion_IsInactive()
        {
            Assert.That(_loop.IsActive, Is.False);
            Assert.That(_loop.Enable(), Is.False, "a region with no ends cannot be enabled");
        }

        [Test]
        public void ValidRegion_CanBeEnabled()
        {
            _loop.SetRegion(10d, 14d);
            Assert.That(_loop.Enable(), Is.True);
            Assert.That(_loop.IsActive, Is.True);
            Assert.That(_loop.LengthSeconds, Is.EqualTo(4d).Within(1e-9));
        }

        [Test]
        public void SetRegion_OrdersTheEndsRegardlessOfArgumentOrder()
        {
            _loop.SetRegion(30d, 12d);
            Assert.That(_loop.InSeconds, Is.EqualTo(12d));
            Assert.That(_loop.OutSeconds, Is.EqualTo(30d));
        }

        [Test]
        public void RegionShorterThanTheMinimum_CannotBeEnabled()
        {
            _loop.SetRegion(10d, 10d + LoopRegion.MinLengthSeconds / 2d);
            Assert.That(_loop.Enable(), Is.False);
            Assert.That(_loop.IsActive, Is.False);
        }

        [Test]
        public void ZeroLengthRegion_CannotPinTheTransport()
        {
            _loop.SetRegion(10d, 10d);
            Assert.That(_loop.Enable(), Is.False);
            Assert.That(_loop.Wrap(10d), Is.EqualTo(10d), "an inactive loop leaves the playhead alone");
        }

        [Test]
        public void Wrap_ReturnsPositionsBeforeOutUnchanged()
        {
            _loop.SetRegion(10d, 14d);
            _loop.Enable();
            Assert.That(_loop.Wrap(11d), Is.EqualTo(11d));
            Assert.That(_loop.Wrap(5d), Is.EqualTo(5d), "arming a loop ahead of the playhead must not yank it forward");
        }

        [Test]
        public void Wrap_FoldsThePlayheadBackToTheLoopStart()
        {
            _loop.SetRegion(10d, 14d);
            _loop.Enable();
            Assert.That(_loop.Wrap(14d), Is.EqualTo(10d).Within(1e-9));
            Assert.That(_loop.Wrap(15.5d), Is.EqualTo(11.5d).Within(1e-9));
        }

        [Test]
        public void Wrap_HandlesAnOvershootOfManyLoopLengths()
        {
            _loop.SetRegion(10d, 14d);
            _loop.Enable();
            // 10 + ((51 - 10) mod 4) = 10 + 1 = 11
            Assert.That(_loop.Wrap(51d), Is.EqualTo(11d).Within(1e-9));
        }

        [Test]
        public void Wrap_LeavesNonFinitePositionsAlone()
        {
            _loop.SetRegion(10d, 14d);
            _loop.Enable();
            Assert.That(double.IsNaN(_loop.Wrap(double.NaN)), Is.True);
        }

        [Test]
        public void MovingAnEndToInvalidateTheRegion_DisablesTheLoop()
        {
            _loop.SetRegion(10d, 14d);
            _loop.Enable();
            _loop.SetOut(10.01d);
            Assert.That(_loop.IsActive, Is.False, "a loop that became too short must not stay armed");
        }

        [Test]
        public void Toggle_TurnsAValidLoopOnAndOff()
        {
            _loop.SetRegion(10d, 14d);
            Assert.That(_loop.Toggle(), Is.True);
            Assert.That(_loop.IsActive, Is.True);
            Assert.That(_loop.Toggle(), Is.False);
            Assert.That(_loop.IsActive, Is.False);
        }

        [Test]
        public void SetBeatLoop_ComputesTheLengthFromTheTempo()
        {
            _loop.SetBeatLoop(20d, 120d, 4d); // 4 beats at 120 BPM = 2 s
            Assert.That(_loop.InSeconds, Is.EqualTo(20d));
            Assert.That(_loop.OutSeconds, Is.EqualTo(22d).Within(1e-9));
        }

        [Test]
        public void SetBeatLoop_IgnoresAnUnknownTempoRatherThanProducingAJunkRegion()
        {
            _loop.SetBeatLoop(20d, 0d, 4d);
            Assert.That(_loop.HasIn, Is.False);
            _loop.SetBeatLoop(20d, double.NaN, 4d);
            Assert.That(_loop.HasIn, Is.False);
        }

        [Test]
        public void RegionIsClampedToTheTrackLength()
        {
            _loop.SetRegion(190d, 400d);
            Assert.That(_loop.OutSeconds, Is.EqualTo(200d));
        }

        [Test]
        public void Clear_RemovesEverything()
        {
            _loop.SetRegion(10d, 14d);
            _loop.Enable();
            _loop.Clear();
            Assert.That(_loop.IsActive, Is.False);
            Assert.That(_loop.HasIn, Is.False);
            Assert.That(_loop.HasOut, Is.False);
        }
    }
}
