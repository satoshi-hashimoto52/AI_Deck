using System;
using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers the "楽曲メタデータモデル" item of §10.1, plus FR-007, FR-008 and FR-011.</summary>
    [TestFixture]
    public class TrackInfoTests
    {
        private static TrackInfo Make(
            string path = "/Music/Neon Drive.mp3",
            string title = "Neon Drive",
            string artist = "Synth Engine",
            double duration = 214.5,
            double bpm = 128d) =>
            new TrackInfo(null, path, title, artist, duration, TrackInfo.FormatFromPath(path), DateTime.UtcNow, bpm, 4096);

        [Test]
        public void FormatFromPath_RecognisesEverySupportedContainer()
        {
            Assert.That(TrackInfo.FormatFromPath("a.mp3"), Is.EqualTo(TrackFormat.Mp3));
            Assert.That(TrackInfo.FormatFromPath("a.MP3"), Is.EqualTo(TrackFormat.Mp3));
            Assert.That(TrackInfo.FormatFromPath("a.wav"), Is.EqualTo(TrackFormat.Wav));
            Assert.That(TrackInfo.FormatFromPath("a.wave"), Is.EqualTo(TrackFormat.Wav));
            Assert.That(TrackInfo.FormatFromPath("a.aif"), Is.EqualTo(TrackFormat.Aiff));
            Assert.That(TrackInfo.FormatFromPath("a.aiff"), Is.EqualTo(TrackFormat.Aiff));
            Assert.That(TrackInfo.FormatFromPath("a.flac"), Is.EqualTo(TrackFormat.Unknown));
            Assert.That(TrackInfo.FormatFromPath("noextension"), Is.EqualTo(TrackFormat.Unknown));
            Assert.That(TrackInfo.FormatFromPath(null), Is.EqualTo(TrackFormat.Unknown));
        }

        [Test]
        public void Id_IsDeterministicForTheSamePath()
        {
            Assert.That(TrackInfo.MakeId("/a/b.mp3"), Is.EqualTo(TrackInfo.MakeId("/a/b.mp3")));
            Assert.That(TrackInfo.MakeId("/a/b.mp3"), Is.Not.EqualTo(TrackInfo.MakeId("/a/c.mp3")));
        }

        [Test]
        public void MissingTitle_FallsBackToTheFileName()
        {
            var track = Make(title: "   ");
            Assert.That(track.Title, Is.EqualTo("Neon Drive"));
        }

        [Test]
        public void MissingArtist_FallsBackToUnknown()
        {
            Assert.That(Make(artist: null).Artist, Is.EqualTo(TrackInfo.UnknownArtist));
        }

        [Test]
        public void NormalizeBpm_RejectsImplausibleValues()
        {
            Assert.That(TrackInfo.NormalizeBpm(128d), Is.EqualTo(128d));
            Assert.That(TrackInfo.NormalizeBpm(39d), Is.EqualTo(0d));
            Assert.That(TrackInfo.NormalizeBpm(251d), Is.EqualTo(0d));
            Assert.That(TrackInfo.NormalizeBpm(double.NaN), Is.EqualTo(0d));
            Assert.That(TrackInfo.NormalizeBpm(double.PositiveInfinity), Is.EqualTo(0d));
        }

        [Test]
        public void Duration_IsClampedAndFormatted()
        {
            Assert.That(Make(duration: -5d).DurationSeconds, Is.EqualTo(0d));
            Assert.That(Make(duration: double.NaN).DurationSeconds, Is.EqualTo(0d));
            Assert.That(TrackInfo.FormatDuration(214.5d), Is.EqualTo("3:34"));
            Assert.That(TrackInfo.FormatDuration(0d), Is.EqualTo("0:00"));
            Assert.That(TrackInfo.FormatDuration(double.NaN), Is.EqualTo("0:00"));
        }

        [Test]
        public void Matches_SearchesTitleArtistAndFileName()
        {
            var track = Make();
            Assert.That(track.Matches("neon"), Is.True, "title, case-insensitive");
            Assert.That(track.Matches("ENGINE"), Is.True, "artist, case-insensitive");
            Assert.That(track.Matches(".mp3"), Is.True, "file name");
            Assert.That(track.Matches("nothing here"), Is.False);
            Assert.That(track.Matches(""), Is.True, "an empty query matches everything");
            Assert.That(track.Matches(null), Is.True);
        }

        [Test]
        public void JsonRoundTrip_PreservesEveryField()
        {
            var original = Make();
            var restored = TrackInfo.FromJson(original.ToJson());

            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.Id, Is.EqualTo(original.Id));
            Assert.That(restored.FilePath, Is.EqualTo(original.FilePath));
            Assert.That(restored.Title, Is.EqualTo(original.Title));
            Assert.That(restored.Artist, Is.EqualTo(original.Artist));
            Assert.That(restored.DurationSeconds, Is.EqualTo(original.DurationSeconds).Within(1e-6));
            Assert.That(restored.Format, Is.EqualTo(original.Format));
            Assert.That(restored.Bpm, Is.EqualTo(original.Bpm).Within(1e-6));
            Assert.That(restored.FileSizeBytes, Is.EqualTo(original.FileSizeBytes));
            Assert.That(restored.AddedUtc, Is.EqualTo(original.AddedUtc));
        }

        [Test]
        public void FromJson_ReturnsNullForAnEntryWithNoPath()
        {
            Assert.That(TrackInfo.FromJson(null), Is.Null);
            Assert.That(TrackInfo.FromJson(AIDeck.Core.Json.JsonValue.NewObject()), Is.Null);
        }

        [Test]
        public void WithBpm_ProducesANewRecordAndLeavesTheOriginalAlone()
        {
            var original = Make(bpm: 0d);
            var updated = original.WithBpm(126d);
            Assert.That(original.Bpm, Is.EqualTo(0d));
            Assert.That(updated.Bpm, Is.EqualTo(126d));
            Assert.That(updated.Id, Is.EqualTo(original.Id));
        }
    }
}
