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
}
