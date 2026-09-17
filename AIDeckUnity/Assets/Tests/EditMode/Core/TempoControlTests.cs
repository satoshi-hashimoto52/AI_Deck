using AIDeck.Core.Deck;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "テンポ値の境界" item of §10.1, plus FR-028 and FR-030.</summary>
    [TestFixture]
    public class TempoControlTests
    {
        private TempoControl _tempo;

        [SetUp]
        public void SetUp() => _tempo = new TempoControl();

        [Test]
        public void Default_IsNominalSpeed()
        {
            Assert.That(_tempo.Fader, Is.EqualTo(0f));
            Assert.That(_tempo.EffectiveRate, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(_tempo.RangePercent, Is.EqualTo(TempoControl.DefaultRangePercent));
        }

        [Test]
        public void FaderEnds_MapToTheConfiguredRange()
        {
            _tempo.RangePercent = 8f;
            _tempo.Fader = 1f;
            Assert.That(_tempo.EffectiveRate, Is.EqualTo(1.08f).Within(1e-5f));
            _tempo.Fader = -1f;
            Assert.That(_tempo.EffectiveRate, Is.EqualTo(0.92f).Within(1e-5f));
        }

        [Test]
        public void Fader_IsClampedAndNaNSafe()
        {
            _tempo.Fader = 5f;
            Assert.That(_tempo.Fader, Is.EqualTo(1f));
            _tempo.Fader = -5f;
            Assert.That(_tempo.Fader, Is.EqualTo(-1f));
            _tempo.Fader = float.NaN;
            Assert.That(_tempo.Fader, Is.EqualTo(0f));
        }

        [Test]
        public void RangePercent_SnapsToASupportedValue()
        {
            _tempo.RangePercent = 17f;
            Assert.That(_tempo.RangePercent, Is.EqualTo(16f));
            _tempo.RangePercent = 1000f;
            Assert.That(_tempo.RangePercent, Is.EqualTo(50f));
            _tempo.RangePercent = float.NaN;
            Assert.That(_tempo.RangePercent, Is.EqualTo(TempoControl.DefaultRangePercent));
        }

        [Test]
        public void EffectiveRate_NeverLeavesTheSafeWindow()
        {
            _tempo.RangePercent = 50f;
            _tempo.Fader = 1f;
            Assert.That(_tempo.EffectiveRate, Is.InRange(TempoControl.MinRate, TempoControl.MaxRate));
            _tempo.Fader = -1f;
            Assert.That(_tempo.EffectiveRate, Is.InRange(TempoControl.MinRate, TempoControl.MaxRate));
        }

        [Test]
        public void EnableSync_MatchesTheTargetTempo()
        {
            Assert.That(_tempo.EnableSync(120d, 128d), Is.True);
            Assert.That(_tempo.SyncEnabled, Is.True);
            Assert.That(_tempo.EffectiveBpm(120d), Is.EqualTo(128d).Within(1e-4));
        }

        [Test]
        public void EnableSync_KeepsTheFaderContributionIntact()
        {
            _tempo.RangePercent = 8f;
            _tempo.Fader = 0.5f; // +4 %
            Assert.That(_tempo.EnableSync(120d, 128d), Is.True);
            Assert.That(_tempo.EffectiveBpm(120d), Is.EqualTo(128d).Within(1e-4),
                "SYNC supplies only the remainder so the fader stays where the DJ left it");
        }

        [Test]
        public void EnableSync_RefusesWhenEitherTempoIsUnknown()
        {
            Assert.That(_tempo.EnableSync(0d, 128d), Is.False);
            Assert.That(_tempo.EnableSync(120d, 0d), Is.False);
            Assert.That(_tempo.EnableSync(double.NaN, 128d), Is.False);
            Assert.That(_tempo.SyncEnabled, Is.False);
            Assert.That(_tempo.SyncRatio, Is.EqualTo(1f));
        }

        [Test]
        public void EnableSync_RefusesAMatchThatWouldNeedAnUnsafeRate()
        {
            Assert.That(_tempo.EnableSync(60d, 240d), Is.False, "4x would leave the safe rate window");
            Assert.That(_tempo.SyncEnabled, Is.False);
            Assert.That(_tempo.EffectiveRate, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void DisableSync_RestoresExactlyTheFaderRate()
        {
            _tempo.RangePercent = 8f;
            _tempo.Fader = 0.25f;
            var faderRate = _tempo.EffectiveRate;
            _tempo.EnableSync(120d, 128d);
            _tempo.DisableSync();
            Assert.That(_tempo.EffectiveRate, Is.EqualTo(faderRate).Within(1e-6f));
        }

        [Test]
        public void EffectiveBpm_IsZeroWhenTheTrackTempoIsUnknown()
        {
            Assert.That(_tempo.EffectiveBpm(0d), Is.EqualTo(0d));
            Assert.That(_tempo.EffectiveBpm(double.NaN), Is.EqualTo(0d));
        }

        [Test]
        public void DisplayPercent_TracksTheEffectiveRate()
        {
            _tempo.RangePercent = 16f;
            _tempo.Fader = 0.5f;
            Assert.That(_tempo.DisplayPercent, Is.EqualTo(8f).Within(1e-3f));
        }
    }
}
