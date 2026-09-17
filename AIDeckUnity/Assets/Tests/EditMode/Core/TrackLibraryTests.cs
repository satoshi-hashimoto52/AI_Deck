using System;
using System.Collections.Generic;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using NUnit.Framework;

namespace AIDeck.Tests.EditMode.Core
{
    /// <summary>Covers FR-004 to FR-011: bulk add, skip reasons, duplicates, search, persistence.</summary>
    [TestFixture]
    public class TrackLibraryTests
    {
        private TrackLibrary _library;

        [SetUp]
        public void SetUp() => _library = new TrackLibrary();

        private static TrackInfo Track(
            string path,
            string title = null,
            string artist = "Engine",
            double duration = 180d,
            long size = 5_000_000L,
            double bpm = 124d) =>
            new TrackInfo(null, path, title, artist, duration, TrackInfo.FormatFromPath(path),
                new DateTime(2026, 9, 18, 1, 2, 3, DateTimeKind.Utc), bpm, size);

        [Test]
        public void AddStoresTheTrackAndBumpsTheRevision()
        {
            var before = _library.Revision;
            var report = _library.Add(Track("/Music/a.mp3", "Alpha"));

            Assert.That(report.Succeeded, Is.True);
            Assert.That(_library.Count, Is.EqualTo(1));
            Assert.That(_library.Revision, Is.GreaterThan(before));
        }

        [Test]
        public void AddRaisesTheChangedEventOnceForABulkAdd()
        {
            var raised = 0;
            _library.Changed += () => raised++;
            _library.AddRange(new[] { Track("/Music/a.mp3"), Track("/Music/b.wav"), Track("/Music/c.aiff") });
            Assert.That(raised, Is.EqualTo(1));
        }

        [Test]
        public void EverySupportedFormatIsAccepted()
        {
            var reports = _library.AddRange(new[]
            {
                Track("/Music/a.mp3"),
                Track("/Music/b.wav"),
                Track("/Music/c.aiff")
            });

            Assert.That(reports.TrueForAll(r => r.Succeeded), Is.True);
            Assert.That(_library.Count, Is.EqualTo(3));
        }

        [Test]
        public void UnsupportedFilesAreSkippedWithAReason()
        {
            var report = _library.Add(Track("/Music/d.flac"));
            Assert.That(report.Result, Is.EqualTo(AddTrackResult.UnsupportedFormat));
            Assert.That(report.Reason, Is.Not.Empty);
            Assert.That(_library.Count, Is.EqualTo(0));
        }

        [Test]
        public void BulkAddReportsEachFileSeparatelyAndKeepsGoingAfterAFailure()
        {
            var reports = _library.AddRange(new[]
            {
                Track("/Music/a.mp3"),
                Track("/Music/broken.flac"),
                Track("/Music/b.wav"),
                null
            });

            Assert.That(reports.Count, Is.EqualTo(4));
            Assert.That(reports[0].Result, Is.EqualTo(AddTrackResult.Added));
            Assert.That(reports[1].Result, Is.EqualTo(AddTrackResult.UnsupportedFormat));
            Assert.That(reports[2].Result, Is.EqualTo(AddTrackResult.Added));
            Assert.That(reports[3].Result, Is.EqualTo(AddTrackResult.Invalid));
            Assert.That(_library.Count, Is.EqualTo(2), "one bad file must not abort the whole import");
        }

        [Test]
        public void TheSamePathIsRejectedAsADuplicate()
        {
            _library.Add(Track("/Music/a.mp3"));
            var report = _library.Add(Track("/Music/a.mp3"));
            Assert.That(report.Result, Is.EqualTo(AddTrackResult.DuplicatePath));
            Assert.That(_library.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheSameFileUnderAnotherFolderIsFlaggedAsAContentDuplicate()
        {
            _library.Add(Track("/Music/a.mp3", "Alpha", duration: 180d, size: 5_000_000L));
            var report = _library.Add(Track("/Backup/a.mp3", "Alpha", duration: 180d, size: 5_000_000L));

            Assert.That(report.Result, Is.EqualTo(AddTrackResult.DuplicateContent));
            Assert.That(report.Track.FilePath, Is.EqualTo("/Music/a.mp3"), "the report points at the existing copy");
        }

        [Test]
        public void DifferentFilesOfTheSameSizeAreNotConfused()
        {
            _library.Add(Track("/Music/a.mp3", size: 5_000_000L));
            var report = _library.Add(Track("/Music/b.mp3", size: 5_000_000L));
            Assert.That(report.Succeeded, Is.True);
        }

        [Test]
        public void SearchFiltersByTitleArtistAndFileName()
        {
            _library.AddRange(new[]
            {
                Track("/Music/neon.mp3", "Neon Drive", "Synth Engine"),
                Track("/Music/rain.wav", "Rain Pattern", "Ambient Engine"),
                Track("/Music/city.aiff", "City Lights", "Synth Engine")
            });

            Assert.That(_library.Search("neon").Count, Is.EqualTo(1));
            Assert.That(_library.Search("synth").Count, Is.EqualTo(2));
            Assert.That(_library.Search(".wav").Count, Is.EqualTo(1));
            Assert.That(_library.Search("").Count, Is.EqualTo(3));
            Assert.That(_library.Search(null).Count, Is.EqualTo(3));
            Assert.That(_library.Search("nothing").Count, Is.EqualTo(0));
        }

        [Test]
        public void TracksAreOrderedByTitle()
        {
            _library.AddRange(new[]
            {
                Track("/Music/c.mp3", "Charlie"),
                Track("/Music/a.mp3", "Alpha"),
                Track("/Music/b.mp3", "Bravo")
            });

            var all = _library.All;
            Assert.That(all[0].Title, Is.EqualTo("Alpha"));
            Assert.That(all[1].Title, Is.EqualTo("Bravo"));
            Assert.That(all[2].Title, Is.EqualTo("Charlie"));
        }

        [Test]
        public void LookupByIdAndPathWork()
        {
            var track = Track("/Music/a.mp3", "Alpha");
            _library.Add(track);

            Assert.That(_library.GetById(track.Id)?.Title, Is.EqualTo("Alpha"));
            Assert.That(_library.GetByPath("/Music/a.mp3")?.Title, Is.EqualTo("Alpha"));
            Assert.That(_library.GetById("missing"), Is.Null);
            Assert.That(_library.GetByPath(null), Is.Null);
        }

        [Test]
        public void RemoveTakesTheEntryOutOfEveryIndex()
        {
            var track = Track("/Music/a.mp3");
            _library.Add(track);

            Assert.That(_library.Remove(track.Id), Is.True);
            Assert.That(_library.Count, Is.EqualTo(0));
            Assert.That(_library.GetByPath("/Music/a.mp3"), Is.Null);
            Assert.That(_library.Remove(track.Id), Is.False);
        }

        [Test]
        public void UpdateReplacesARecordInPlace()
        {
            var track = Track("/Music/a.mp3", "Alpha", bpm: 0d);
            _library.Add(track);

            Assert.That(_library.Update(track.WithBpm(126d)), Is.True);
            Assert.That(_library.GetById(track.Id).Bpm, Is.EqualTo(126d));
            Assert.That(_library.Count, Is.EqualTo(1));
        }

        [Test]
        public void UpdateOfAnUnknownTrackIsRejected()
        {
            Assert.That(_library.Update(Track("/Music/ghost.mp3")), Is.False);
            Assert.That(_library.Update(null), Is.False);
        }

        [Test]
        public void JsonRoundTripRestoresTheCatalogue()
        {
            _library.AddRange(new[]
            {
                Track("/Music/a.mp3", "Alpha", "Engine A", 180.5d, 1000L, 124d),
                Track("/Music/b.wav", "Bravo", "Engine B", 240d, 2000L, 0d)
            });

            var json = _library.ToJson();
            var restored = new TrackLibrary();
            var report = restored.LoadJson(json);

            Assert.That(report.Success, Is.True);
            Assert.That(report.Loaded, Is.EqualTo(2));
            Assert.That(report.Skipped, Is.EqualTo(0));

            var alpha = restored.GetByPath("/Music/a.mp3");
            Assert.That(alpha, Is.Not.Null);
            Assert.That(alpha.Title, Is.EqualTo("Alpha"));
            Assert.That(alpha.Artist, Is.EqualTo("Engine A"));
            Assert.That(alpha.DurationSeconds, Is.EqualTo(180.5d).Within(1e-6));
            Assert.That(alpha.Bpm, Is.EqualTo(124d).Within(1e-6));
            Assert.That(alpha.AddedUtc, Is.EqualTo(new DateTime(2026, 9, 18, 1, 2, 3, DateTimeKind.Utc)));
            Assert.That(restored.GetByPath("/Music/b.wav").Bpm, Is.EqualTo(0d));
        }

        [Test]
        public void AnUnreadableLibraryFileFailsWithAReasonRatherThanThrowing()
        {
            var report = _library.LoadJson("{not json");
            Assert.That(report.Success, Is.False);
            Assert.That(report.Reason, Is.Not.Empty);
        }

        [Test]
        public void ALibraryFileFromANewerVersionIsRefused()
        {
            var report = _library.LoadJson("{\"version\":999,\"tracks\":[]}");
            Assert.That(report.Success, Is.False);
            Assert.That(report.Reason, Does.Contain("newer"));
        }

        [Test]
        public void DamagedRowsAreSkippedAndCountedRatherThanLosingTheWholeLibrary()
        {
            var json = "{\"version\":1,\"tracks\":[" +
                       "{\"path\":\"/Music/a.mp3\",\"title\":\"Alpha\",\"format\":1}," +
                       "{\"title\":\"No path here\"}," +
                       "{\"path\":\"/Music/b.wav\",\"title\":\"Bravo\",\"format\":2}" +
                       "]}";

            var report = _library.LoadJson(json);

            Assert.That(report.Success, Is.True);
            Assert.That(report.Loaded, Is.EqualTo(2));
            Assert.That(report.Skipped, Is.EqualTo(1));
        }

        [Test]
        public void DuplicatePathsInAStoredFileAreCollapsed()
        {
            var json = "{\"version\":1,\"tracks\":[" +
                       "{\"path\":\"/Music/a.mp3\",\"title\":\"Alpha\",\"format\":1}," +
                       "{\"path\":\"/Music/a.mp3\",\"title\":\"Alpha again\",\"format\":1}" +
                       "]}";

            var report = _library.LoadJson(json);
            Assert.That(report.Loaded, Is.EqualTo(1));
            Assert.That(report.Skipped, Is.EqualTo(1));
        }

        [Test]
        public void ClearEmptiesTheLibrary()
        {
            _library.AddRange(new[] { Track("/Music/a.mp3"), Track("/Music/b.wav") });
            _library.Clear();
            Assert.That(_library.Count, Is.EqualTo(0));
            Assert.That(_library.All, Is.Empty);
        }

        [Test]
        public void AddRangeOfNullIsHarmless()
        {
            Assert.That(_library.AddRange(null), Is.Empty);
            Assert.That(_library.Count, Is.EqualTo(0));
        }

        [Test]
        public void TheLibraryNeverExposesAWayToWriteTheSourceFile()
        {
            // Documents FR-010 / NFR-008 structurally: there is no API on the library or the
            // track record that mutates the media file. If one is ever added this test is the
            // place that has to change deliberately.
            var type = typeof(TrackLibrary);
            foreach (var method in type.GetMethods())
            {
                var name = method.Name.ToLowerInvariant();
                Assert.That(
                    name.Contains("delete") || name.Contains("movefile") || name.Contains("write"),
                    Is.False,
                    $"TrackLibrary.{method.Name} looks like it could touch the source media");
            }
        }
    }
}
