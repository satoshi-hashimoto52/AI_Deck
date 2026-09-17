using AIDeck.Core.Mixer;
using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "クロスフェーダーカーブ" item of §10.1 and FR-040 to FR-046.</summary>
    [TestFixture]
    public class MixerTests
    {
        [Test]
        public void Crossfader_EndsIsolateASingleDeck()
        {
            Assert.That(CrossfaderCurve.GainA(-1f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(CrossfaderCurve.GainB(-1f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(CrossfaderCurve.GainA(1f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(CrossfaderCurve.GainB(1f), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void SmoothCurve_HoldsConstantPowerAcrossTheThrow()
        {
            for (var p = -1f; p <= 1f; p += 0.1f)
            {
                CrossfaderCurve.Evaluate(p, CrossfaderCurveType.Smooth, out var a, out var b);
                Assert.That(a * a + b * b, Is.EqualTo(1f).Within(1e-4f), $"constant power broken at {p}");
            }
        }

        [Test]
        public void LinearCurve_SumsToUnity()
        {
            for (var p = -1f; p <= 1f; p += 0.1f)
            {
                CrossfaderCurve.Evaluate(p, CrossfaderCurveType.Linear, out var a, out var b);
                Assert.That(a + b, Is.EqualTo(1f).Within(1e-4f));
            }
        }

        [Test]
        public void SharpCurve_KeepsBothDecksAtFullLevelAcrossTheMiddle()
        {
            CrossfaderCurve.Evaluate(0f, CrossfaderCurveType.Sharp, out var a, out var b);
            Assert.That(a, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(b, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void EveryCurve_IsMonotonicInBothDirections()
        {
            foreach (CrossfaderCurveType curve in System.Enum.GetValues(typeof(CrossfaderCurveType)))
            {
                var previousA = float.MaxValue;
                var previousB = float.MinValue;
                for (var p = -1f; p <= 1.0001f; p += 0.02f)
                {
                    CrossfaderCurve.Evaluate(p, curve, out var a, out var b);
                    Assert.That(a, Is.LessThanOrEqualTo(previousA + 1e-5f), $"{curve} A not monotonic at {p}");
                    Assert.That(b, Is.GreaterThanOrEqualTo(previousB - 1e-5f), $"{curve} B not monotonic at {p}");
                    previousA = a;
                    previousB = b;
                }
            }
        }

        [Test]
        public void EveryCurve_ReturnsFiniteGainsForCorruptInput()
        {
            foreach (CrossfaderCurveType curve in System.Enum.GetValues(typeof(CrossfaderCurveType)))
            {
                foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 99f, -99f })
                {
                    CrossfaderCurve.Evaluate(bad, curve, out var a, out var b);
                    Assert.That(a, Is.InRange(0f, 1f));
                    Assert.That(b, Is.InRange(0f, 1f));
                }
            }
        }

        [Test]
        public void MixerState_SanitisesEverySetter()
        {
            var mixer = new MixerState
            {
                ChannelGainA = 5f,
                ChannelGainB = float.NaN,
                MasterGain = -3f,
                Crossfader = 9f,
                FilterA = float.NegativeInfinity
            };

            Assert.That(mixer.ChannelGainA, Is.EqualTo(1f));
            Assert.That(mixer.ChannelGainB, Is.EqualTo(0f));
            Assert.That(mixer.MasterGain, Is.EqualTo(0f));
            Assert.That(mixer.Crossfader, Is.EqualTo(1f));
            Assert.That(mixer.FilterA, Is.EqualTo(0f));
        }

        [Test]
        public void EffectiveDeckGain_CombinesFaderAndCrossfader()
        {
            var mixer = new MixerState
            {
                ChannelGainA = 0.5f,
                Crossfader = -1f,
                CrossfaderCurveType = CrossfaderCurveType.Linear
            };

            Assert.That(mixer.EffectiveDeckGain(DeckId.A), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(mixer.EffectiveDeckGain(DeckId.B), Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void Mute_SilencesTheDeckRegardlessOfTheFaders()
        {
            var mixer = new MixerState { ChannelGainA = 1f, Crossfader = -1f };
            mixer.SetMute(DeckId.A, true);
            Assert.That(mixer.EffectiveDeckGain(DeckId.A), Is.EqualTo(0f));
        }

        [Test]
        public void PerDeckAccessors_RouteToTheRightChannel()
        {
            var mixer = new MixerState();
            mixer.SetChannelGain(DeckId.B, 0.3f);
            mixer.SetFilter(DeckId.B, -0.7f);
            mixer.SetEcho(DeckId.A, true);
            mixer.SetCue(DeckId.B, true);

            Assert.That(mixer.ChannelGainB, Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(mixer.Filter(DeckId.B), Is.EqualTo(-0.7f).Within(1e-6f));
            Assert.That(mixer.ChannelGainA, Is.Not.EqualTo(0.3f));
            Assert.That(mixer.Echo(DeckId.A), Is.True);
            Assert.That(mixer.Echo(DeckId.B), Is.False);
            Assert.That(mixer.Cue(DeckId.B), Is.True);
        }
    }
}
