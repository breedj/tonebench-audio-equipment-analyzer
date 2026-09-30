using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.Data;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The database's own lifecycle: a startup check that says so when the configuration file is
/// damaged, and a shutdown that leaves the WAL empty and nothing open — neither of which happened
/// before 11 Sept 2026, when qa403.db was found with zeroed pages for the second time in two days.
/// The project file's check moved with the open, to <see cref="ProjectSessionService"/>; at
/// shutdown this service checkpoints whichever project is open at that moment.
/// </summary>
/// <summary>
/// Runs alone. <see cref="SqliteConnection.ClearAllPools"/> is <b>process-wide</b>, and both
/// <c>Cleanup</c> here and <c>DatabaseLifecycleHostedService.StopAsync</c> call it — so a parallel
/// test doing the same thing closes this one's connection, SQLite checkpoints and truncates the WAL
/// on the last connection closing, and the assertion that the WAL is non-empty <i>before</i>
/// shutdown fails. Intermittently, and more often on a busy machine, which is exactly how it
/// presented: green alone, roughly one full-suite run in three red.
/// </summary>
[CollectionDefinition(nameof(DatabaseLifecycleCollection), DisableParallelization = true)]
public sealed class DatabaseLifecycleCollection;

[Collection(nameof(DatabaseLifecycleCollection))]
public class DatabaseLifecycleHostedServiceTests
{
    private static (DatabaseLifecycleHostedService Service, DatabaseHealth Health, CurrentProjectFile Current, string DbPath, string ConfigPath) Create()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"qa403-lifecycle-{Guid.NewGuid():N}.db");
        string configPath = Path.Combine(Path.GetTempPath(), $"qa403-lifecycle-{Guid.NewGuid():N}-config.db");
        var health = new DatabaseHealth();
        var current = new CurrentProjectFile();
        var service = new DatabaseLifecycleHostedService(
            current,
            new Qa403ConfigDatabaseOptions { Path = configPath },
            health,
            NullLogger<DatabaseLifecycleHostedService>.Instance);
        return (service, health, current, dbPath, configPath);
    }

    private static void Cleanup(params string[] paths)
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in paths)
        {
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    private static async Task WriteABlobAsync(string path)
    {
        var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS t (id INTEGER PRIMARY KEY, blob BLOB); INSERT INTO t (blob) VALUES (randomblob(200000));";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Start_OnASoundConfigFile_ReportsNoProblem()
    {
        var (service, health, _, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(configPath);
            await service.StartAsync(CancellationToken.None);

            Assert.Null(health.Problem);
        }
        finally
        {
            Cleanup(dbPath, configPath);
        }
    }

    /// <summary>
    /// The 11 Sept 2026 damage, reproduced — the last pages of the file zeroed — has to come out as a
    /// message on the status, not as a startup crash and not as silence until some read hits it.
    /// </summary>
    [Fact]
    public async Task Start_OnAConfigFileWithZeroedPages_FlagsTheProblemAndKeepsRunning()
    {
        var (service, health, _, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(configPath);
            await SqliteConnectionFactory.CheckpointAsync(configPath, CancellationToken.None);
            SqliteConnectionFactory.CloseAllPools();

            ZeroLastPages(configPath);

            await service.StartAsync(CancellationToken.None);

            Assert.NotNull(health.Problem);
            Assert.Contains("configuration database is damaged", health.Problem);
        }
        finally
        {
            Cleanup(dbPath, configPath);
        }
    }

    /// <summary>
    /// After a write the WAL holds frames; after the service's StopAsync it holds none, and the row
    /// is in the main file. That is the state a process terminated by Windows a millisecond later
    /// leaves behind — and the state it never used to.
    /// </summary>
    [Fact]
    public async Task Stop_CheckpointsTheOpenProjectsWalAndClosesEveryConnection()
    {
        var (service, _, current, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(configPath);
            await WriteABlobAsync(dbPath);
            current.Path = dbPath;
            Assert.True(new FileInfo(dbPath + "-wal").Length > 0);

            await service.StopAsync(CancellationToken.None);

            Assert.True(!File.Exists(dbPath + "-wal") || new FileInfo(dbPath + "-wal").Length == 0, "the WAL should be empty after shutdown");

            // Nothing open: the file can be deleted outright, which a pooled handle would refuse.
            File.Delete(dbPath);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            Cleanup(dbPath, configPath);
        }
    }

    /// <summary>No project open is the welcome screen, not a failure: only the configuration file is touched.</summary>
    [Fact]
    public async Task Stop_WithNoProjectOpen_StillCheckpointsTheConfigFile()
    {
        var (service, _, _, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(configPath);
            Assert.True(new FileInfo(configPath + "-wal").Length > 0);

            await service.StopAsync(CancellationToken.None);

            Assert.True(!File.Exists(configPath + "-wal") || new FileInfo(configPath + "-wal").Length == 0);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            Cleanup(dbPath, configPath);
        }
    }

    internal static void ZeroLastPages(string path)
    {
        const int pageSize = 8192;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        long pages = file.Length / pageSize;
        file.Seek((pages - 3) * pageSize, SeekOrigin.Begin);
        file.Write(new byte[3 * pageSize]);
    }
}
