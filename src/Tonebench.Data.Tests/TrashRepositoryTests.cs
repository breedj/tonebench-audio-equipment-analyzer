using Microsoft.Data.Sqlite;
using Tonebench.Dsp;

namespace Tonebench.Data.Tests;

/// <summary>
/// The trash: the soft-delete model of migration 013, and the two ways things leave it for good.
/// </summary>
public class TrashRepositoryTests
{
    [Fact]
    public async Task TrashingAType_TakesItOffTheListButDestroysNothing()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long resultId = await bench.SaveResultAsync(typeId);

            Assert.True(await bench.Projects.TrashTypeAsync(typeId, CancellationToken.None));

            Assert.Empty(await bench.Projects.ListTypesAsync(bench.ProjectId, CancellationToken.None));
            Assert.Null(await bench.Projects.GetTypeAsync(typeId, CancellationToken.None));
            // The result is hidden because its *type* is in the trash, and its own row is untouched
            // -- which is what makes restoring the measurement bring it back without a second write.
            Assert.Empty(await bench.Measurements.ListResultsAsync(new MeasurementFilter(), CancellationToken.None));
            Assert.Null(await bench.Measurements.GetResultAsync(resultId, CancellationToken.None));
            Assert.Equal(1L, await bench.ScalarAsync("SELECT COUNT(*) FROM measurement_result WHERE deleted_utc IS NULL;"));
            Assert.Equal(1L, await bench.ScalarAsync("SELECT COUNT(*) FROM capture;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task RestoringAType_BringsBackEveryResultThatWasNotTrashedOnItsOwn()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long kept = await bench.SaveResultAsync(typeId, "kept");
            long alsoKept = await bench.SaveResultAsync(typeId, "also kept");
            long trashedOnItsOwn = await bench.SaveResultAsync(typeId, "deleted separately");

            await bench.Measurements.TrashResultAsync(trashedOnItsOwn, CancellationToken.None);
            await bench.Projects.TrashTypeAsync(typeId, CancellationToken.None);

            var outcome = await bench.Projects.RestoreTypeAsync(typeId, CancellationToken.None);

            Assert.Equal(TypeRestoreStatus.Restored, outcome.Status);
            Assert.Single(await bench.Projects.ListTypesAsync(bench.ProjectId, CancellationToken.None));

            var listed = (await bench.Measurements.ListResultsAsync(new MeasurementFilter(), CancellationToken.None))
                .Select(r => r.ResultId).ToHashSet();
            Assert.Equal([kept, alsoKept], listed);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task AResultTrashedOnItsOwn_StaysInTheTrashWhenItsTypeIsRestored()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long resultId = await bench.SaveResultAsync(typeId);

            await bench.Measurements.TrashResultAsync(resultId, CancellationToken.None);
            await bench.Projects.TrashTypeAsync(typeId, CancellationToken.None);
            await bench.Projects.RestoreTypeAsync(typeId, CancellationToken.None);

            Assert.Null(await bench.Measurements.GetResultAsync(resultId, CancellationToken.None));

            var listing = await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None);
            var group = Assert.Single(listing.Types);
            Assert.Null(group.DeletedUtc); // the measurement is back, so it is only a header now
            Assert.Equal(resultId, Assert.Single(group.Results).ResultId);
            Assert.Equal(1, listing.TotalCount);

            // And it is restorable again now that its measurement exists -- the whole point of the
            // ParentInTrash refusal below.
            Assert.Equal(
                ResultRestoreStatus.Restored,
                await bench.Measurements.RestoreResultAsync(resultId, CancellationToken.None));
            Assert.NotNull(await bench.Measurements.GetResultAsync(resultId, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task AResult_CannotBeRestoredWhileItsTypeIsInTheTrash()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long resultId = await bench.SaveResultAsync(typeId);

            await bench.Measurements.TrashResultAsync(resultId, CancellationToken.None);
            await bench.Projects.TrashTypeAsync(typeId, CancellationToken.None);

            Assert.Equal(
                ResultRestoreStatus.ParentInTrash,
                await bench.Measurements.RestoreResultAsync(resultId, CancellationToken.None));
            Assert.Equal(1L, await bench.ScalarAsync(
                $"SELECT COUNT(*) FROM measurement_result WHERE id = {resultId} AND deleted_utc IS NOT NULL;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task RestoringAResultThatIsNotInTheTrash_IsNotFound()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long resultId = await bench.SaveResultAsync(typeId);

            Assert.Equal(
                ResultRestoreStatus.NotFound,
                await bench.Measurements.RestoreResultAsync(resultId, CancellationToken.None));
            Assert.Equal(
                TypeRestoreStatus.NotFound,
                (await bench.Projects.RestoreTypeAsync(typeId, CancellationToken.None)).Status);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The partial unique index of migration 013. Without it the trashed row still holds its name
    /// and this insert is a 409 against a measurement nobody can see or rename.
    /// </summary>
    [Fact]
    public async Task ANewType_MayTakeTheNameOfATrashedOne_AndTheRestoreIsThenRefused()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long original = await bench.CreateTypeAsync("Amp 1 kHz");
            await bench.Projects.TrashTypeAsync(original, CancellationToken.None);

            long replacement = await bench.CreateTypeAsync("Amp 1 kHz");

            Assert.NotEqual(original, replacement);

            var outcome = await bench.Projects.RestoreTypeAsync(original, CancellationToken.None);

            Assert.Equal(TypeRestoreStatus.NameTaken, outcome.Status);
            Assert.Equal("Amp 1 kHz", outcome.Name);
            Assert.Single(await bench.Projects.ListTypesAsync(bench.ProjectId, CancellationToken.None));

            // And the listing says so up front, so the view can disable the button rather than
            // offering a restore that fails.
            var listing = await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None);
            Assert.True(Assert.Single(listing.Types).NameTaken);

            // Renaming the newcomer clears it.
            await bench.Projects.UpdateTypeAsync(replacement, "Amp 1 kHz (new)", null, "{}", CancellationToken.None);
            Assert.Equal(
                TypeRestoreStatus.Restored,
                (await bench.Projects.RestoreTypeAsync(original, CancellationToken.None)).Status);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task TheListing_ShowsALiveMeasurementAsAHeaderThatCannotBeRestored()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long live = await bench.CreateTypeAsync("Still here");
            long gone = await bench.CreateTypeAsync("Deleted");
            long trashedResult = await bench.SaveResultAsync(live, "one bad run");
            await bench.SaveResultAsync(live, "a good one");
            await bench.SaveResultAsync(gone, "goes with its measurement");

            await bench.Measurements.TrashResultAsync(trashedResult, CancellationToken.None);
            await bench.Projects.TrashTypeAsync(gone, CancellationToken.None);

            var listing = await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None);

            // Two items: the deleted measurement, and the one deleted result. The live measurement
            // appears as their sibling but is not itself an item.
            Assert.Equal(2, listing.TotalCount);
            Assert.Equal(2, listing.Types.Count);

            var deleted = listing.Types.Single(t => t.MeasurementTypeId == gone);
            Assert.NotNull(deleted.DeletedUtc);
            Assert.Empty(deleted.Results);
            Assert.Equal(1, deleted.RestorableResultCount);

            var header = listing.Types.Single(t => t.MeasurementTypeId == live);
            Assert.Null(header.DeletedUtc);
            Assert.Equal(trashedResult, Assert.Single(header.Results).ResultId);

            // A measurement with nothing of its own in the trash is not listed at all.
            long untouched = await bench.CreateTypeAsync("Untouched");
            listing = await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None);
            Assert.DoesNotContain(untouched, listing.Types.Select(t => t.MeasurementTypeId));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task AnEmptyTrash_ListsNothing()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            await bench.SaveResultAsync(await bench.CreateTypeAsync("Amp 1 kHz"));

            var listing = await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None);

            Assert.Empty(listing.Types);
            Assert.Equal(0, listing.TotalCount);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task PurgeOlderThan_TakesTheCapturesAndEverySpectrumAndMetricWithIt()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long expiredType = await bench.CreateTypeAsync("Long gone");
            long expiredResult = await bench.SaveResultAsync(expiredType, "under a deleted measurement");
            long liveType = await bench.CreateTypeAsync("Still here");
            long expiredLoneResult = await bench.SaveResultAsync(liveType, "deleted on its own");
            long recentResult = await bench.SaveResultAsync(liveType, "deleted yesterday");

            await bench.Projects.TrashTypeAsync(expiredType, CancellationToken.None);
            await bench.Measurements.TrashResultAsync(expiredLoneResult, CancellationToken.None);
            await bench.Measurements.TrashResultAsync(recentResult, CancellationToken.None);

            await bench.BackdateTrashAsync(DateTimeOffset.UtcNow.AddDays(-20), typeId: expiredType);
            await bench.BackdateTrashAsync(DateTimeOffset.UtcNow.AddDays(-20), resultId: expiredLoneResult);
            await bench.BackdateTrashAsync(DateTimeOffset.UtcNow.AddDays(-1), resultId: recentResult);

            TrashPurgeReport report;
            await using (var connection = await bench.Factory.OpenAsync(CancellationToken.None))
            {
                report = await TrashRepository.PurgeOlderThanAsync(
                    connection, DateTimeOffset.UtcNow.AddDays(-14));
            }

            Assert.Equal(1, report.MeasurementTypes);
            Assert.Equal(2, report.Results); // the one under the measurement, and the lone one

            // The whole cascade ran, not just the parent rows.
            Assert.Equal(0L, await bench.ScalarAsync($"SELECT COUNT(*) FROM measurement_type WHERE id = {expiredType};"));
            Assert.Equal(0L, await bench.ScalarAsync($"SELECT COUNT(*) FROM measurement_result WHERE id = {expiredResult};"));
            Assert.Equal(0L, await bench.ScalarAsync($"SELECT COUNT(*) FROM measurement_result WHERE id = {expiredLoneResult};"));
            Assert.Equal(0L, await bench.ScalarAsync(
                "SELECT COUNT(*) FROM capture WHERE measurement_result_id NOT IN (SELECT id FROM measurement_result);"));
            Assert.Equal(0L, await bench.ScalarAsync(
                "SELECT COUNT(*) FROM spectrum WHERE capture_id NOT IN (SELECT id FROM capture);"));
            Assert.Equal(0L, await bench.ScalarAsync(
                "SELECT COUNT(*) FROM metric WHERE capture_id NOT IN (SELECT id FROM capture);"));

            // Inside the retention, so still there and still restorable.
            Assert.Equal(1L, await bench.ScalarAsync($"SELECT COUNT(*) FROM measurement_result WHERE id = {recentResult};"));
            Assert.Equal(1, (await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None)).TotalCount);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task PurgeOlderThan_LeavesWhatIsNotInTheTrashAlone()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long resultId = await bench.SaveResultAsync(typeId);

            TrashPurgeReport report;
            await using (var connection = await bench.Factory.OpenAsync(CancellationToken.None))
                report = await TrashRepository.PurgeOlderThanAsync(connection, DateTimeOffset.UtcNow.AddYears(100));

            Assert.False(report.AnythingDeleted);
            Assert.NotNull(await bench.Measurements.GetResultAsync(resultId, CancellationToken.None));
            Assert.Single(await bench.Projects.ListTypesAsync(bench.ProjectId, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The failure mode this class of change has had twice (migrations 005 and 007): a cascade that
    /// misses a child table orphans its rows, SQLite hands the freed capture rowid to the next
    /// insert, and a save with nothing wrong with it fails on a UNIQUE index. Asserting the *next
    /// save* rather than a row count is the point — a count passes against the bug.
    /// </summary>
    [Fact]
    public async Task Purge_ThenSaveAnother_DoesNotCollideOnARecycledCaptureRowid()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long typeId = await bench.CreateTypeAsync("Amp 1 kHz");
            long doomed = await bench.SaveResultAsync(typeId, "doomed");

            await bench.Measurements.TrashResultAsync(doomed, CancellationToken.None);
            await bench.BackdateTrashAsync(DateTimeOffset.UtcNow.AddDays(-30), resultId: doomed);

            await using (var connection = await bench.Factory.OpenAsync(CancellationToken.None))
                await TrashRepository.PurgeOlderThanAsync(connection, DateTimeOffset.UtcNow.AddDays(-14));

            long fresh = await bench.SaveResultAsync(typeId, "fresh");

            Assert.NotNull(await bench.Measurements.GetResultAsync(fresh, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task EmptyTrash_DestroysEverythingInItAndNothingElse()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var bench = await Bench.OpenAsync(path);
            long gone = await bench.CreateTypeAsync("Deleted");
            await bench.SaveResultAsync(gone, "goes with it");
            long live = await bench.CreateTypeAsync("Still here");
            long trashedResult = await bench.SaveResultAsync(live, "one bad run");
            long keptResult = await bench.SaveResultAsync(live, "a good one");

            await bench.Projects.TrashTypeAsync(gone, CancellationToken.None);
            await bench.Measurements.TrashResultAsync(trashedResult, CancellationToken.None);

            // Nothing is backdated: Empty ignores the age entirely, which is the whole difference
            // between it and the purge.
            var report = await bench.Trash.EmptyAsync(bench.ProjectId, CancellationToken.None);

            Assert.Equal(1, report.MeasurementTypes);
            Assert.Equal(2, report.Results);
            Assert.Equal(0, (await bench.Trash.ListAsync(bench.ProjectId, CancellationToken.None)).TotalCount);

            var types = await bench.Projects.ListTypesAsync(bench.ProjectId, CancellationToken.None);
            Assert.Equal(live, Assert.Single(types).Id);
            Assert.NotNull(await bench.Measurements.GetResultAsync(keptResult, CancellationToken.None));
            Assert.Equal(0L, await bench.ScalarAsync(
                "SELECT COUNT(*) FROM capture WHERE measurement_result_id NOT IN (SELECT id FROM measurement_result);"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task EmptyTrash_LeavesAnotherProjectsTrashAlone()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            // A project file holds one project, but the schema's project_id columns predate that and
            // a legacy file can hold several -- so the scoping is still worth pinning.
            var bench = await Bench.OpenAsync(path);
            long otherProject = await bench.Projects.CreateProjectAsync("Other", null, CancellationToken.None);
            long mine = await bench.CreateTypeAsync("Mine");
            long theirs = await bench.Projects.CreateTypeAsync(
                otherProject, "FFTSpectrum", "Theirs", null, "{}", CancellationToken.None);

            await bench.Projects.TrashTypeAsync(mine, CancellationToken.None);
            await bench.Projects.TrashTypeAsync(theirs, CancellationToken.None);

            var report = await bench.Trash.EmptyAsync(bench.ProjectId, CancellationToken.None);

            Assert.Equal(1, report.MeasurementTypes);
            Assert.Equal(1L, await bench.ScalarAsync($"SELECT COUNT(*) FROM measurement_type WHERE id = {theirs};"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>One migrated file, its repositories, and the bits of SQL the tests poke at directly.</summary>
    private sealed class Bench
    {
        private int _nextSeed = 1;

        public required SqliteConnectionFactory Factory { get; init; }
        public required ProjectRepository Projects { get; init; }
        public required MeasurementRepository Measurements { get; init; }
        public required TrashRepository Trash { get; init; }
        public required long ProjectId { get; init; }

        public static async Task<Bench> OpenAsync(string path)
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var projects = new ProjectRepository(factory);
            return new Bench
            {
                Factory = factory,
                Projects = projects,
                Measurements = new MeasurementRepository(factory),
                Trash = new TrashRepository(factory),
                ProjectId = await projects.CreateProjectAsync("GM70", null, CancellationToken.None),
            };
        }

        public Task<long> CreateTypeAsync(string name) =>
            Projects.CreateTypeAsync(ProjectId, "FFTSpectrum", name, null, "{}", CancellationToken.None);

        public Task<long> SaveResultAsync(long typeId, string name = "result") =>
            Measurements.SaveResultAsync(BuildSaveRequest(typeId, name, _nextSeed++), CancellationToken.None);

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            return await TestDb.ScalarAsync(connection, sql);
        }

        /// <summary>
        /// Puts a trashed row's <c>deleted_utc</c> in the past. The repositories only ever stamp
        /// "now", so this is the only way to reach the purge's cutoff without sleeping for a
        /// fortnight.
        /// </summary>
        public async Task BackdateTrashAsync(DateTimeOffset when, long? typeId = null, long? resultId = null)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = typeId is not null
                ? "UPDATE measurement_type SET deleted_utc = @when WHERE id = @id;"
                : "UPDATE measurement_result SET deleted_utc = @when WHERE id = @id;";
            command.Parameters.AddWithValue("@when", TonebenchTimestamp.FormatUtc(when));
            command.Parameters.AddWithValue("@id", typeId ?? resultId!.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        private static MeasurementSaveRequest BuildSaveRequest(long typeId, string name, int seed)
        {
            var random = new Random(seed);
            var spectra = new List<StoredSpectrumInput>();
            var metrics = new List<MeasurementMetric>();

            for (int channel = 0; channel < 2; channel++)
            {
                var dbv = new double[8];
                for (int i = 0; i < dbv.Length; i++)
                    dbv[i] = random.NextDouble() * 220.0 - 200.0;

                spectra.Add(new StoredSpectrumInput(
                    channel, WindowType.Hann, new WindowSums(S1: 32768.0, S2: 24576.0, Length: 65536),
                    SampleRateHz: 48000.0, BinSizeHz: 48000.0 / 65536.0, SpectrumNorm.Amplitude, dbv));

                metrics.Add(new MeasurementMetric(channel, "thd_percent", 1.0 + channel, "%"));
                metrics.Add(new MeasurementMetric(channel, "fundamental_hz", 1000.0, "Hz"));
            }

            return new MeasurementSaveRequest(
                typeId, name, "notes", """{"fftSize":65536}""",
                "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                SampleRateHz: 48000.0, FftSize: 65536, Channels: 2,
                InRangeDbv: 12.0, OutRangeDbv: 8.0, GenJson: """{"left":{"enabled":true}}""",
                spectra, metrics);
        }
    }
}
