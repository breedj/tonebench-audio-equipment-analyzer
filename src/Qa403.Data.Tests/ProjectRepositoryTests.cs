using Microsoft.Data.Sqlite;

namespace Qa403.Data.Tests;

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
    public async Task DeleteProject_WithTypes_Fails()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new ProjectRepository(await TestDb.CreateMigratedFactoryAsync(path));
            long projectId = await repo.CreateProjectAsync("GM70", null, CancellationToken.None);
            await repo.CreateTypeAsync(projectId, "FFTSpectrum", "type", null, "{}", CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.DeleteProjectAsync(projectId, CancellationToken.None));
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
                    command.CommandText =
                        "ALTER TABLE measurement_type DROP COLUMN sort_order; DELETE FROM schema_version WHERE version = 8;";
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
}
