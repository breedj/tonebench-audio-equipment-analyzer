using Microsoft.Data.Sqlite;

namespace Qa403.Data.Tests;

public class SqliteConnectionFactoryTests
{
    [Fact]
    public async Task Open_SetsWalJournalMode_ReadBack()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
            await using var connection = await factory.OpenAsync(CancellationToken.None);

            Assert.Equal("wal", await ReadPragmaStringAsync(connection, "journal_mode"), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Open_AppliesAllPragmas_ReadBack()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
            await using var connection = await factory.OpenAsync(CancellationToken.None);

            Assert.Equal(8192L, await TestDb.ScalarAsync(connection, "PRAGMA page_size;"));
            Assert.Equal(1L, await TestDb.ScalarAsync(connection, "PRAGMA synchronous;")); // NORMAL
            Assert.Equal(268435456L, await TestDb.ScalarAsync(connection, "PRAGMA mmap_size;"));
            Assert.Equal(2L, await TestDb.ScalarAsync(connection, "PRAGMA temp_store;")); // MEMORY
            Assert.Equal(-64000L, await TestDb.ScalarAsync(connection, "PRAGMA cache_size;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Open_PooledReuse_ReappliesPragmas()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });

            // Open and immediately dispose: Microsoft.Data.Sqlite returns the underlying native
            // handle to its pool rather than actually closing it.
            await using (await factory.OpenAsync(CancellationToken.None))
            {
            }

            await using var second = await factory.OpenAsync(CancellationToken.None);
            Assert.Equal("wal", await ReadPragmaStringAsync(second, "journal_mode"), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(-64000L, await TestDb.ScalarAsync(second, "PRAGMA cache_size;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Open_JournalModeInsideTransaction_Throws()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();

            // SQLite's own documented behaviour: journal_mode cannot change while a transaction
            // is active -- it silently returns the pre-existing mode instead. The factory's
            // verification must turn that into a loud failure, not a silent no-op.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteConnectionFactory.SetAndVerifyJournalModeAsync(connection, transaction, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static async Task<string> ReadPragmaStringAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        object? result = await command.ExecuteScalarAsync();
        return (string)result!;
    }
}
