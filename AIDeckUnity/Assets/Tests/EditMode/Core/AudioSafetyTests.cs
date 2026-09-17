using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>
    /// §9 of the master issue forbids NaN, Infinity and out-of-range values from reaching an
    /// audio parameter, and NFR-009 requires gain limiting before output. These tests pin
    /// that contract down, because every other audio class relies on it.
    /// </summary>
    [TestFixture]
    public class AudioSafetyTests
    {
        [Test]
        public void Sanitize_ReplacesNaNWithFallback()
        {
            Assert.That(AudioSafety.Sanitize(float.NaN, 0f, 1f, 0.5f), Is.EqualTo(0.5f));
        }

        [Test]
        public void Sanitize_ReplacesInfinitiesWithFallback()
        {
            Assert.That(AudioSafety.Sanitize(float.PositiveInfinity, 0f, 1f, 0.25f), Is.EqualTo(0.25f));
            Assert.That(AudioSafety.Sanitize(float.NegativeInfinity, 0f, 1f, 0.25f), Is.EqualTo(0.25f));
        }

        [Test]
        public void Sanitize_ClampsToRange()
        {
            Assert.That(AudioSafety.Sanitize(5f, 0f, 1f, 0f), Is.EqualTo(1f));
            Assert.That(AudioSafety.Sanitize(-5f, 0f, 1f, 0f), Is.EqualTo(0f));
        }

        [Test]
        public void Sanitize01_ClampsAndDefaultsToSilence()
        {
            Assert.That(AudioSafety.Sanitize01(1.5f), Is.EqualTo(1f));
            Assert.That(AudioSafety.Sanitize01(-0.2f), Is.EqualTo(0f));
            Assert.That(AudioSafety.Sanitize01(float.NaN), Is.EqualTo(0f));
        }

        [Test]
        public void SanitizeBipolar_KeepsCentreForBadInput()
        {
            Assert.That(AudioSafety.SanitizeBipolar(float.NaN), Is.EqualTo(0f));
            Assert.That(AudioSafety.SanitizeBipolar(2f), Is.EqualTo(1f));
            Assert.That(AudioSafety.SanitizeBipolar(-2f), Is.EqualTo(-1f));
        }

        [Test]
        public void SoftLimit_LeavesSignalBelowCeilingUntouched()
        {
            Assert.That(AudioSafety.SoftLimit(0.5f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(AudioSafety.SoftLimit(-0.5f), Is.EqualTo(-0.5f).Within(1e-6f));
        }

        [Test]
        public void SoftLimit_NeverExceedsUnity()
        {
            for (var value = 0.9f; value < 100f; value *= 1.7f)
            {
                Assert.That(AudioSafety.SoftLimit(value), Is.LessThanOrEqualTo(1f));
                Assert.That(AudioSafety.SoftLimit(-value), Is.GreaterThanOrEqualTo(-1f));
            }
        }

        [Test]
        public void SoftLimit_IsMonotonicAndOddSymmetric()
        {
            var previous = float.NegativeInfinity;
            for (var value = 0f; value <= 4f; value += 0.05f)
            {
                var limited = AudioSafety.SoftLimit(value);
                Assert.That(limited, Is.GreaterThanOrEqualTo(previous), $"not monotonic at {value}");
                Assert.That(AudioSafety.SoftLimit(-value), Is.EqualTo(-limited).Within(1e-5f));
                previous = limited;
            }
        }

        [Test]
        public void SoftLimit_TurnsNonFiniteInputIntoSilence()
        {
            Assert.That(AudioSafety.SoftLimit(float.NaN), Is.EqualTo(0f));
            Assert.That(AudioSafety.SoftLimit(float.PositiveInfinity), Is.EqualTo(0f));
        }

        [Test]
        public void DbConversions_RoundTrip()
        {
            Assert.That(AudioSafety.DbToLinear(0f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(AudioSafety.LinearToDb(1f), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(AudioSafety.LinearToDb(0.5f), Is.EqualTo(-6.0206f).Within(1e-3f));
        }

        [Test]
        public void LinearToDb_FloorsSilence()
        {
            Assert.That(AudioSafety.LinearToDb(0f), Is.EqualTo(-60f));
            Assert.That(AudioSafety.LinearToDb(float.NaN), Is.EqualTo(-60f));
        }

        [Test]
        public void SmoothingCoefficient_StaysInsideUnitRange()
        {
            Assert.That(AudioSafety.SmoothingCoefficient(0.01f, 48000), Is.InRange(0f, 1f));
            Assert.That(AudioSafety.SmoothingCoefficient(0f, 48000), Is.EqualTo(1f));
            Assert.That(AudioSafety.SmoothingCoefficient(0.01f, 0), Is.EqualTo(1f));
            Assert.That(AudioSafety.SmoothingCoefficient(float.NaN, 48000), Is.EqualTo(1f));
        }
    }
}
