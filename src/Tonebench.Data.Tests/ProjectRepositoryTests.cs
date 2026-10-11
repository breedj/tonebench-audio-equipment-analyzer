using Microsoft.Data.Sqlite;

namespace Tonebench.Data.Tests;

public class ProjectRepositoryTests
{
    [Fact]
    public async Task CreateProject_DuplicateName_Fails()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));

            await repo.CreateProjectAsync("GM70", "notes", CancellationToken.None);

            await Assert.ThrowsAsync<SqliteException>(() =>
                repo.CreateProjectAsync("GM70", "different notes", CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task CreateType_StoresParamsJsonAndNotes()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);

            long typeId = await repo.CreateTypeAsync(
                projectId, "FFTSpectrum", "1 kHz THD @ 8R", "bench notes", """{"fftSize":65536}""", CancellationToken.None);

            var types = await repo.ListTypesAsync(projectId, CancellationToken.None);
            var type = Assert.Single(types);
            Assert.Equal(typeId, type.Id);
            Assert.Equal("FFTSpectrum", type.Kind);
            Assert.Equal("1 kHz THD @ 8R", type.Name);
            Assert.Equal("bench notes", type.Notes);
            Assert.Equal("""{"fftSize":65536}""", type.ParamsJson);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task ListTypes_ScopedToProject()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectA = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long projectB = await repo.CreateProjectAsync("Other", null, CancellationToken.None);

            await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A1", null, "{}", CancellationToken.None);
            await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A2", null, "{}", CancellationToken.None);
            await repo.CreateTypeAsync(projectB, "FFTSpectrum", "B1", null, "{}", CancellationToken.None);

            var typesA = await repo.ListTypesAsync(projectA, CancellationToken.None);
            var typesB = await repo.ListTypesAsync(projectB, CancellationToken.None);

            Assert.Equal(2, typesA.Count);
            Assert.All(typesA, t => Assert.Equal(projectA, t.ProjectId));
            Assert.Single(typesB);
            Assert.Equal("B1", typesB[0].Name);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task CreateType_AppendsToTheEndOfTheList_NotIntoNameOrder()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);

            await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Zulu", null, "{}", CancellationToken.None);
            await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Alpha", null, "{}", CancellationToken.None);

            var types = await repo.ListTypesAsync(projectId, CancellationToken.None);

            Assert.Equal(new[] { "Zulu", "Alpha" }, types.Select(t => t.Name));
            Assert.Equal(new[] { 0, 1 }, types.Select(t => t.SortOrder));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task ReorderTypes_ListsInTheGivenOrder_AndLeavesOtherProjectsAlone()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectA = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long projectB = await repo.CreateProjectAsync("Other", null, CancellationToken.None);

            long a1 = await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A1", null, "{}", CancellationToken.None);
            long a2 = await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A2", null, "{}", CancellationToken.None);
            long a3 = await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A3", null, "{}", CancellationToken.None);
            long b1 = await repo.CreateTypeAsync(projectB, "FFTSpectrum", "B1", null, "{}", CancellationToken.None);
            long b2 = await repo.CreateTypeAsync(projectB, "FFTSpectrum", "B2", null, "{}", CancellationToken.None);

            Assert.True(await repo.ReorderTypesAsync(projectA, new[] { a3, a1, a2 }, CancellationToken.None));

            var typesA = await repo.ListTypesAsync(projectA, CancellationToken.None);
            var typesB = await repo.ListTypesAsync(projectB, CancellationToken.None);

            Assert.Equal(new[] { a3, a1, a2 }, typesA.Select(t => t.Id));
            Assert.Equal(new[] { b1, b2 }, typesB.Select(t => t.Id));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// A partial, foreign or duplicated list is refused outright rather than applied: writing only
    /// some of the rows leaves two types sharing a sort_order, and the list order then comes from
    /// the id tiebreak instead of from the user's drag.
    /// </summary>
    [Fact]
    public async Task ReorderTypes_WithAListThatIsNotExactlyTheProjectsTypes_IsRejected()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectA = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long projectB = await repo.CreateProjectAsync("Other", null, CancellationToken.None);

            long a1 = await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A1", null, "{}", CancellationToken.None);
            long a2 = await repo.CreateTypeAsync(projectA, "FFTSpectrum", "A2", null, "{}", CancellationToken.None);
            long b1 = await repo.CreateTypeAsync(projectB, "FFTSpectrum", "B1", null, "{}", CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.ReorderTypesAsync(projectA, new[] { a1 }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.ReorderTypesAsync(projectA, new[] { a1, b1 }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.ReorderTypesAsync(projectA, new[] { a1, a1 }, CancellationToken.None));

            var typesA = await repo.ListTypesAsync(projectA, CancellationToken.None);
            Assert.Equal(new[] { a1, a2 }, typesA.Select(t => t.Id));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The client sends the list it is showing, which is ListTypesAsync's — so "exactly the
    /// project's own type ids" has to mean the ones not in the trash. Counting a trashed row would
    /// make that list short by one and refuse every drag in a project that has ever had a
    /// measurement deleted.
    /// </summary>
    [Fact]
    public async Task ReorderTypes_WithTheVisibleListOnly_IsNotRefusedBecauseOfATrashedType()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long first = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "First", null, "{}", CancellationToken.None);
            long second = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Second", null, "{}", CancellationToken.None);
            long trashed = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Trashed", null, "{}", CancellationToken.None);

            await repo.TrashTypeAsync(trashed, CancellationToken.None);

            Assert.True(await repo.ReorderTypesAsync(projectId, [second, first], CancellationToken.None));

            var types = await repo.ListTypesAsync(projectId, CancellationToken.None);
            Assert.Equal([second, first], types.Select(t => t.Id));

            // And a list that still names the trashed one is refused, as any wrong list is.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.ReorderTypesAsync(projectId, [second, first, trashed], CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The count is what the delete confirmation quotes, so it has to be what the delete is about to
    /// move to the trash — not what is already in it.
    /// </summary>
    [Fact]
    public async Task CountTypeResults_CountsOnlyTheResultsThatWouldGoToTheTrash()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var repo = new ProjectRepository(factory);
            var measurements = new MeasurementRepository(factory);
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long typeId = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Amp", null, "{}", CancellationToken.None);

            long kept = await SaveBareResultAsync(factory, typeId, "kept");
            long alreadyTrashed = await SaveBareResultAsync(factory, typeId, "already trashed");
            await measurements.TrashResultAsync(alreadyTrashed, CancellationToken.None);

            Assert.Equal(1, await repo.CountTypeResultsAsync(typeId, CancellationToken.None));
            Assert.NotEqual(0L, kept);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// Save As without results makes a fresh file holding the measurements; carrying someone's
    /// deleted ones into it would be a surprise. (Save As *with* results is a page-level database
    /// copy and does carry the trash — see CopyTypesAsync's remarks.)
    /// </summary>
    [Fact]
    public async Task CopyTypes_DoesNotCopyATrashedMeasurement()
    {
        string source = TestDb.CreateTempPath();
        string destination = TestDb.CreateTempPath();
        try
        {
            var sourceFactory = await TestDb.CreateMigratedFactoryAsync(source);
            var destinationFactory = await TestDb.CreateMigratedFactoryAsync(destination);
            var repo = new ProjectRepository(sourceFactory);
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Kept", null, "{}", CancellationToken.None);
            long trashed = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Trashed", null, "{}", CancellationToken.None);
            await repo.TrashTypeAsync(trashed, CancellationToken.None);

            await using (var from = await sourceFactory.OpenAsync(CancellationToken.None))
            await using (var to = await destinationFactory.OpenAsync(CancellationToken.None))
            {
                long copiedProject = await ProjectRepository.CreateProjectAsync(to, "GM70 copy", null);
                await ProjectRepository.CopyTypesAsync(from, projectId, to, copiedProject);

                var copied = await new ProjectRepository(destinationFactory)
                    .ListTypesAsync(copiedProject, CancellationToken.None);
                Assert.Equal("Kept", Assert.Single(copied).Name);
            }
        }
        finally
        {
            TestDb.Cleanup(source);
            TestDb.Cleanup(destination);
        }
    }

    /// <summary>A result row with one capture — enough to be counted, without a spectrum blob.</summary>
    private static async Task<long> SaveBareResultAsync(SqliteConnectionFactory factory, long typeId, string name)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO measurement_result
                (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated)
            VALUES (@typeId, @name, NULL, @ts, '{}', 'QA403 (Sim)', 'SIM-001', '1.0.0', 1)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@typeId", typeId);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@ts", TonebenchTimestamp.Now());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Migration 008 backfills the order every existing database already appeared to have -- the
    /// name sort ListTypesAsync did before sort_order existed -- so opening one after the upgrade
    /// moves nothing. Rolls the column back off a migrated database and re-runs the real runner,
    /// rather than asserting against a copy of the SQL.
    /// </summary>
    [Fact]
    public async Task Migration008_BackfillsTheOrderTheListHadWhenItSortedByName()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var repo = new ProjectRepository(factory);
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long zulu = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Zulu", null, "{}", CancellationToken.None);
            long alpha = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Alpha", null, "{}", CancellationToken.None);

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using (var command = connection.CreateCommand())
                {
                    // Every version at or above 008 is wound back, not just 008: the runner resumes
                    // from MAX(version), so leaving a later row in place makes it skip the migration
                    // under test entirely and this passes for the wrong reason. Each later migration
                    // therefore has to undo whatever it created, which is why the impulse tables go
                    // too.
                    // 013's partial index goes before its columns -- SQLite refuses to drop a column
                    // an index refers to -- and the plain index has to come back in its place, or
                    // 013's own DROP INDEX finds nothing to drop when the runner replays it.
                    command.CommandText = """
                        DROP INDEX ix_measurement_result_source;
                        ALTER TABLE measurement_result DROP COLUMN source;
                        ALTER TABLE capture DROP COLUMN pcm_fs;
                        DROP INDEX ix_measurement_type_name;
                        DROP INDEX ix_measurement_type_trashed;
                        DROP INDEX ix_measurement_result_trashed;
                        ALTER TABLE measurement_type DROP COLUMN deleted_utc;
                        ALTER TABLE measurement_result DROP COLUMN deleted_utc;
                        CREATE UNIQUE INDEX ix_measurement_type_name ON measurement_type(project_id, name);
                        ALTER TABLE measurement_type DROP COLUMN sort_order;
                        DROP TABLE impulse_result;
                        DROP TABLE impulse_harmonic;
                        DROP TABLE impulse_reference;
                        ALTER TABLE response_trace DROP COLUMN reference_db;
                        ALTER TABLE response_trace DROP COLUMN reference_mode;
                        ALTER TABLE response_trace DROP COLUMN group_delay_s;
                        ALTER TABLE response_trace DROP COLUMN phase_delay_s;
                        DELETE FROM schema_version WHERE version >= 8;
                        """;
                    await command.ExecuteNonQueryAsync();
                }

                await new MigrationRunner().RunAsync(connection, CancellationToken.None);
            }

            var types = await repo.ListTypesAsync(projectId, CancellationToken.None);

            Assert.Equal(new[] { alpha, zulu }, types.Select(t => t.Id));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }
    /// <summary>
    /// The number the sidebar's delete confirmation quotes. Scoped to the one type, because the
    /// whole point of the figure is to say what <em>this</em> delete destroys, and counted past the
    /// 100-row page size of the results listing, which is the other way it could have been read.
    /// </summary>
    [Fact]
    public async Task CountTypeResults_CountsOnlyItsOwnResults_AndIsNotCappedByAPageSize()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var repo = new ProjectRepository(factory);
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long amp = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Amp 1 kHz at 1 W", null, "{}", CancellationToken.None);
            long other = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Loopback", null, "{}", CancellationToken.None);
            long empty = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Never run", null, "{}", CancellationToken.None);

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await InsertResultsAsync(connection, amp, 105);
                await InsertResultsAsync(connection, other, 3);
            }

            Assert.Equal(105, await repo.CountTypeResultsAsync(amp, CancellationToken.None));
            Assert.Equal(3, await repo.CountTypeResultsAsync(other, CancellationToken.None));
            Assert.Equal(0, await repo.CountTypeResultsAsync(empty, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The count is what the confirmation promises and the delete is what keeps it, so they are
    /// pinned to each other here: whatever the count said is gone afterwards, and nothing else is.
    /// </summary>
    [Fact]
    public async Task DeleteType_RemovesExactlyTheResultsTheCountNamed()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var repo = new ProjectRepository(factory);
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long amp = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Amp 1 kHz at 1 W", null, "{}", CancellationToken.None);
            long other = await repo.CreateTypeAsync(projectId, "FFTSpectrum", "Loopback", null, "{}", CancellationToken.None);

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await InsertResultsAsync(connection, amp, 5);
                await InsertResultsAsync(connection, other, 2);
            }

            int counted = await repo.CountTypeResultsAsync(amp, CancellationToken.None);
            Assert.Equal(5, counted);

            Assert.True(await repo.DeleteTypeAsync(amp, CancellationToken.None));

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                Assert.Equal(2, await TestDb.ScalarAsync(connection, "SELECT COUNT(*) FROM measurement_result;"));

            Assert.Equal(2, await repo.CountTypeResultsAsync(other, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static async Task InsertResultsAsync(SqliteConnection connection, long typeId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO measurement_result (measurement_type_id, name, notes, ts, params_json, is_simulated)
                VALUES ($typeId, $name, NULL, '2026-10-02T12:00:00.000Z', '{}', 1);
                """;
            command.Parameters.AddWithValue("$typeId", typeId);
            command.Parameters.AddWithValue("$name", $"run {i}");
            await command.ExecuteNonQueryAsync();
        }
    }
}
