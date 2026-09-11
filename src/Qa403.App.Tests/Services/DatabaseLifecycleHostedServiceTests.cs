using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.Data;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The database's own lifecycle: a startup check that says so when a file is damaged, and a
/// shutdown that leaves the WAL empty and nothing open — neither of which happened before 11 Sept
/// 2026, when qa403.db was found with zeroed pages for the second time in two days.
/// </summary>
public class DatabaseLifecycleHostedServiceTests
{
    private static (DatabaseLifecycleHostedService Service, DatabaseHealth Health, string DbPath, string ConfigPath) Create()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"qa403-lifecycle-{Guid.NewGuid():N}.db");
        string configPath = Path.Combine(Path.GetTempPath(), $"qa403-lifecycle-{Guid.NewGuid():N}-config.db");
        var health = new DatabaseHealth();
        var service = new DatabaseLifecycleHostedService(
            new Qa403DatabaseOptions { Path = dbPath },
            new Qa403ConfigDatabaseOptions { Path = configPath },
            health,
            NullLogger<DatabaseLifecycleHostedService>.Instance);
        return (service, health, dbPath, configPath);
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
    public async Task Start_OnSoundFiles_ReportsNoProblem()
    {
        var (service, health, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(dbPath);
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
    public async Task Start_OnAFileWithZeroedPages_FlagsTheProblemAndKeepsRunning()
    {
        var (service, health, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(dbPath);
            await SqliteConnectionFactory.CheckpointAsync(dbPath, CancellationToken.None);
            SqliteConnectionFactory.CloseAllPools();

            const int pageSize = 8192;
            using (var file = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite))
            {
                long pages = file.Length / pageSize;
                file.Seek((pages - 3) * pageSize, SeekOrigin.Begin);
                file.Write(new byte[3 * pageSize]);
            }

            await service.StartAsync(CancellationToken.None);

            Assert.NotNull(health.Problem);
            Assert.Contains("measurement database is damaged", health.Problem);
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
    public async Task Stop_CheckpointsTheWalAndClosesEveryConnection()
    {
        var (service, _, dbPath, configPath) = Create();
        try
        {
            await WriteABlobAsync(dbPath);
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
}
