using Microsoft.Data.Sqlite;

namespace Tonebench.Data.Tests;

public class SqliteConnectionFactoryTests
{
    /// <summary>
    /// Nothing open is an error carrying a message, not an empty database at a null path. The
    /// endpoints surface this one as a 409 — see NoProjectOpenExceptionHandler.
    /// </summary>
    [Fact]
    public async Task Open_WithNoProjectOpen_ThrowsNoProjectOpen()
    {
        var factory = new SqliteConnectionFactory(new CurrentProjectFile());
        await Assert.ThrowsAsync<NoProjectOpenException>(() => factory.OpenAsync(CancellationToken.None));
    }

    /// <summary>
    /// The app's factory never creates a file: a typo in a typed path, or a quick_check on a file
    /// that has just been deleted, must fail rather than leave an empty database behind at that path.
    /// Only the pinned (test) constructor and the project session's own create path may.
    /// </summary>
    [Fact]
    public async Task Open_FollowingTheCurrentProject_DoesNotCreateAMissingFile()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(CurrentProjectFile.At(path));
            await Assert.ThrowsAsync<SqliteException>(() => factory.OpenAsync(CancellationToken.None));
            Assert.False(File.Exists(path));

            Assert.False(File.Exists(path));
            Assert.NotNull(await SqliteConnectionFactory.QuickCheckAsync(path, CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Open_SetsWalJournalMode_ReadBack()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
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
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using var connection = await factory.OpenAsync(CancellationToken.None);

            Assert.Equal(8192L, await TestDb.ScalarAsync(connection, "PRAGMA page_size;"));
            // FULL, and no memory mapping — see ApplyPragmasAsync for the two corruptions behind both.
            Assert.Equal(2L, await TestDb.ScalarAsync(connection, "PRAGMA synchronous;")); // FULL
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, "PRAGMA mmap_size;"));
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
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });

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

    /// <summary>
    /// The shutdown step: after a write the WAL holds frames, and after the checkpoint it holds none
    /// and the row is in the main file. Asserted on the -wal file's <i>size</i> rather than on the
    /// pragma's return columns alone, because an empty WAL on disk is the property the next start
    /// depends on — a WAL still holding frames when the process is terminated is exactly the state
    /// this exists to avoid.
    /// </summary>
    [Fact]
    public async Task Checkpoint_EmptiesTheWalAndLeavesTheDataInTheMainFile()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, blob BLOB); INSERT INTO t (blob) VALUES (zeroblob(100000));";
                await create.ExecuteNonQueryAsync();
            }

            long walBefore = new FileInfo(path + "-wal").Length;
            Assert.True(walBefore > 0, "the write should still be sitting in the WAL");

            var (completed, walFrames) = await SqliteConnectionFactory.CheckpointAsync(path, CancellationToken.None);
            SqliteConnectionFactory.CloseAllPools();

            Assert.True(completed);
            Assert.True(walFrames > 0, $"checkpoint reported {walFrames} frames from a WAL of {walBefore} bytes");
            Assert.True(!File.Exists(path + "-wal") || new FileInfo(path + "-wal").Length == 0, "TRUNCATE should leave the WAL empty");

            await using var reopened = await factory.OpenAsync(CancellationToken.None);
            Assert.Equal(1L, await TestDb.ScalarAsync(reopened, "SELECT count(*) FROM t;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task QuickCheck_OnASoundFile_ReportsNothing()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using (var connection = await TestDb.OpenMigratedAsync(path))
            {
            }

            Assert.Null(await SqliteConnectionFactory.QuickCheckAsync(path, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The real damage, reproduced: pages allocated and never filled. A blob large enough to spill
    /// into overflow pages is written and checkpointed, then those pages are zeroed on disk — which
    /// is what both corrupted copies of qa403.db looked like. The check has to come back with a
    /// message, not throw, because startup reads it into the status bar rather than crashing on it.
    /// </summary>
    [Fact]
    public async Task QuickCheck_OnAFileWithZeroedPages_ReportsTheDamageInsteadOfThrowing()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, blob BLOB); INSERT INTO t (blob) VALUES (randomblob(200000));";
                await create.ExecuteNonQueryAsync();
            }

            await SqliteConnectionFactory.CheckpointAsync(path, CancellationToken.None);
            SqliteConnectionFactory.CloseAllPools();

            const int pageSize = 8192;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                long pages = file.Length / pageSize;
                Assert.True(pages >= 6, $"expected a multi-page file, got {pages} pages");
                // The last three pages, as found on 11 Sept 2026: the header still says they exist.
                file.Seek((pages - 3) * pageSize, SeekOrigin.Begin);
                file.Write(new byte[3 * pageSize]);
            }

            string? problem = await SqliteConnectionFactory.QuickCheckAsync(path, CancellationToken.None);
            Assert.NotNull(problem);
            Assert.NotEqual("ok", problem, StringComparer.OrdinalIgnoreCase);
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
