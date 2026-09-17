using AIDeck.Core.Mixer;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "設定保存／復元" item of §10.1 and FR-080 to FR-084.</summary>
    [TestFixture]
    public class AppSettingsTests
    {
        [Test]
        public void DefaultsAreTheSafeOnes()
        {
            var settings = new AppSettings();
            Assert.That(settings.OnDisconnect, Is.EqualTo(DisconnectPolicy.StopPlayback),
                "§9 mandates the safe stop as the default");
            Assert.That(settings.AutoDiscoverHost, Is.True);
            Assert.That(settings.PreventSleepWhilePlaying, Is.True);
            Assert.That(settings.MasterVolume, Is.InRange(0f, 1f));
            Assert.That(settings.LastHostPort, Is.EqualTo(ProtocolInfo.DefaultTcpPort));
        }

        [Test]
        public void RoundTripPreservesEveryField()
        {
            var settings = new AppSettings
            {
                LastHostAddress = "192.168.1.42",
                LastHostPort = 40000,
                AutoDiscoverHost = false,
                RecordingFolder = "/Users/dj/Recordings",
                MasterVolume = 0.62f,
                CrossfaderCurve = CrossfaderCurveType.Sharp,
                TempoRangePercent = 16f,
                UiScale = 1.2f,
                OnDisconnect = DisconnectPolicy.ContinuePlayback,
                PreventSleepWhilePlaying = false,
                ShowDiagnostics = true
            };

            var restored = AppSettings.Deserialize(settings.Serialize());

            Assert.That(restored.WasRepaired, Is.False);
            Assert.That(restored.LastHostAddress, Is.EqualTo("192.168.1.42"));
            Assert.That(restored.LastHostPort, Is.EqualTo(40000));
            Assert.That(restored.AutoDiscoverHost, Is.False);
            Assert.That(restored.RecordingFolder, Is.EqualTo("/Users/dj/Recordings"));
            Assert.That(restored.MasterVolume, Is.EqualTo(0.62f).Within(1e-6f));
            Assert.That(restored.CrossfaderCurve, Is.EqualTo(CrossfaderCurveType.Sharp));
            Assert.That(restored.TempoRangePercent, Is.EqualTo(16f));
            Assert.That(restored.UiScale, Is.EqualTo(1.2f).Within(1e-6f));
            Assert.That(restored.OnDisconnect, Is.EqualTo(DisconnectPolicy.ContinuePlayback));
            Assert.That(restored.PreventSleepWhilePlaying, Is.False);
            Assert.That(restored.ShowDiagnostics, Is.True);
        }

        [Test]
        public void SettersSanitiseOutOfRangeValues()
        {
            var settings = new AppSettings
            {
                MasterVolume = 9f,
                UiScale = 100f,
                TempoRangePercent = 999f
            };

            Assert.That(settings.MasterVolume, Is.EqualTo(1f));
            Assert.That(settings.UiScale, Is.LessThanOrEqualTo(1.4f));
            Assert.That(settings.TempoRangePercent, Is.EqualTo(50f));

            settings.MasterVolume = float.NaN;
            Assert.That(settings.MasterVolume, Is.EqualTo(0f));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{not json at all")]
        [TestCase("[1,2,3]")]
        [TestCase("\"just a string\"")]
        public void AnUnreadableFileFallsBackToDefaultsAndSaysSo(string text)
        {
            var settings = AppSettings.Deserialize(text);

            Assert.That(settings, Is.Not.Null);
            Assert.That(settings.WasRepaired, Is.True);
            Assert.That(settings.RepairReason, Is.Not.Empty);
            Assert.That(settings.OnDisconnect, Is.EqualTo(DisconnectPolicy.StopPlayback));
            Assert.That(settings.MasterVolume, Is.InRange(0f, 1f));
        }

        [Test]
        public void AFileFromANewerVersionFallsBackToDefaults()
        {
            var settings = AppSettings.Deserialize("{\"version\":999,\"masterVolume\":0.1}");
            Assert.That(settings.WasRepaired, Is.True);
            Assert.That(settings.MasterVolume, Is.Not.EqualTo(0.1f));
        }

        [Test]
        public void GarbageValuesInsideAValidDocumentAreRepairedFieldByField()
        {
            var text = "{\"version\":1," +
                       "\"masterVolume\":99," +
                       "\"uiScale\":-5," +
                       "\"crossfaderCurve\":77," +
                       "\"onDisconnect\":42," +
                       "\"lastHostPort\":999999," +
                       "\"tempoRangePercent\":1234}";

            var settings = AppSettings.Deserialize(text);

            Assert.That(settings.MasterVolume, Is.EqualTo(1f));
            Assert.That(settings.UiScale, Is.GreaterThanOrEqualTo(0.8f));
            Assert.That(settings.CrossfaderCurve, Is.EqualTo(CrossfaderCurveType.Smooth));
            Assert.That(settings.OnDisconnect, Is.EqualTo(DisconnectPolicy.StopPlayback),
                "an unreadable policy must not silently become 'keep playing'");
            Assert.That(settings.LastHostPort, Is.EqualTo(ProtocolInfo.DefaultTcpPort));
            Assert.That(settings.TempoRangePercent, Is.EqualTo(50f));
        }

        [Test]
        public void WrongTypesAreTreatedAsMissing()
        {
            var settings = AppSettings.Deserialize(
                "{\"version\":1,\"masterVolume\":\"loud\",\"autoDiscoverHost\":\"yes\",\"lastHostAddress\":123}");

            Assert.That(settings.MasterVolume, Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(settings.AutoDiscoverHost, Is.True);
            Assert.That(settings.LastHostAddress, Is.Empty);
        }

        [Test]
        public void AnIncompleteDocumentIsFlaggedAsRepaired()
        {
            var settings = AppSettings.Deserialize("{\"version\":1}");
            Assert.That(settings.WasRepaired, Is.True);
            Assert.That(settings.RepairReason, Does.Contain("incomplete"));
        }

        [Test]
        public void OverlongStringsAreTruncatedRatherThanStored()
        {
            var settings = new AppSettings { LastHostAddress = new string('x', 5000) };
            Assert.That(settings.LastHostAddress.Length, Is.LessThanOrEqualTo(128));
        }

        [Test]
        public void CloneProducesAnIndependentCopyWithACleanRepairFlag()
        {
            var original = AppSettings.Deserialize("{not json");
            Assert.That(original.WasRepaired, Is.True);

            original.MasterVolume = 0.3f;
            var clone = original.Clone();
            clone.MasterVolume = 0.9f;

            Assert.That(original.MasterVolume, Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(clone.WasRepaired, Is.False);
        }

        [Test]
        public void ClearRepairFlagResetsTheNotice()
        {
            var settings = AppSettings.Deserialize("{not json");
            settings.ClearRepairFlag();
            Assert.That(settings.WasRepaired, Is.False);
            Assert.That(settings.RepairReason, Is.Empty);
        }
    }
}
