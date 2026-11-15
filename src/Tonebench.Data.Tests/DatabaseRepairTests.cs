using Microsoft.Data.Sqlite;

namespace Tonebench.Data.Tests;

/// <summary>
/// Rebuilding a damaged file, which is the one recovery procedure this app has.
///
/// <para>It was written down after the two September 2026 incidents and carried out by hand twice —
/// most recently on 2026-11-15, on the configuration database, which is what turned it from a note
/// in CLAUDE.md into code a user can run. The damage looks the same every time: a <c>quick_check</c>
/// that fails on its first line, and one or two rows whose overflow pages were allocated and never
/// filled.</para>
/// </summary>
public class DatabaseRepairTests
{
    [Fact]
    public async Task Rebuild_OfASoundFile_KeepsEveryRowAndTheOriginal()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await SeedAsync(path, rows: 40);

            var result = await DatabaseRepair.RebuildAsync(path, CancellationToken.None);

            Assert.Null(result.RemainingProblem);
            Assert.False(result.AnythingLost);
            Assert.Equal(40, result.RowsCopied);
            Assert.True(File.Exists(result.DamagedCopyPath), "the original is kept, always");
            Assert.Equal(40, await CountAsync(path));
        }
        finally
        {
            CleanupAll(path);
        }
    }

    /// <summary>
    /// The schema has to come across whole — a rebuilt file that has lost an index is a file whose
    /// next write can insert the duplicate the index existed to refuse.
    /// </summary>
    [Fact]
    public async Task Rebuild_CarriesTheSchemaOverNotJustTheRows()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await SeedAsync(path, rows: 5);

            await DatabaseRepair.RebuildAsync(path, CancellationToken.None);

            await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: false, CancellationToken.None);
            Assert.True(await TestDb.TableExistsAsync(connection, "note"));
            Assert.Equal(1, await TestDb.ScalarAsync(
                connection, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_note_label';"));
        }
        finally
        {
            CleanupAll(path);
        }
    }

    /// <summary>
    /// The point of the whole class: a file SQLite calls damaged comes back sound, and whatever
    /// still read is in it.
    /// </summary>
    [Fact]
    public async Task Rebuild_OfADamagedFile_SalvagesWhatStillReads()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await SeedAsync(path, rows: 400);
            CorruptAPage(path);

            // The premise, asserted rather than assumed -- a test that silently stopped corrupting
            // the file would go on passing while proving nothing.
            string? before = await SqliteConnectionFactory.QuickCheckAsync(path, CancellationToken.None);
            Assert.False(string.IsNullOrEmpty(before), "the file under test is not actually damaged");

            var result = await DatabaseRepair.RebuildAsync(path, CancellationToken.None);

            Assert.Null(result.RemainingProblem);
            Assert.True(result.RowsCopied > 0, "nothing at all was salvaged");
            Assert.True(File.Exists(result.DamagedCopyPath));

            // And the file now in place really is sound, by the app's own check.
            Assert.Null(await SqliteConnectionFactory.QuickCheckAsync(path, CancellationToken.None));
        }
        finally
        {
            CleanupAll(path);
        }
    }

    /// <summary>
    /// A <c>-wal</c> left behind by a file that has moved away is adopted by whatever takes its
    /// name next — which here is the rebuilt database. Replaying a damaged file's log into the file
    /// just salvaged from it is the one outcome worse than not repairing at all.
    /// </summary>
    [Fact]
    public async Task Rebuild_LeavesNoneOfTheOldLogBesideTheNewFile()
    {
        const string sentinel = "this log belongs to the damaged file";

        string path = TestDb.CreateTempPath();
        try
        {
            await SeedAsync(path, rows: 5);
            await File.WriteAllTextAsync(path + "-wal", sentinel);

            var result = await DatabaseRepair.RebuildAsync(path, CancellationToken.None);

            // The rebuilt file has a log of its own -- every connection this app opens is WAL, and
            // `quick_check` opens one at the end of the repair. What it must not have is *that*
            // log, so this is asserted by content rather than by the file's existence.
            SqliteConnectionFactory.CloseAllPools();
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                    Assert.DoesNotContain(sentinel, ReadShared(path + suffix));
            }

            Assert.True(File.Exists(result.DamagedCopyPath));
        }
        finally
        {
            CleanupAll(path);
        }
    }

    /// <summary>
    /// Two repairs in one day must not have the second overwrite the first: the earliest copy is
    /// the one closest to the original damage, and therefore the one worth keeping.
    /// </summary>
    [Fact]
    public async Task Rebuild_Twice_KeepsBothDamagedCopies()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await SeedAsync(path, rows: 5);

            var first = await DatabaseRepair.RebuildAsync(path, CancellationToken.None);
            var second = await DatabaseRepair.RebuildAsync(path, CancellationToken.None);

            Assert.NotEqual(first.DamagedCopyPath, second.DamagedCopyPath);
            Assert.True(File.Exists(first.DamagedCopyPath));
            Assert.True(File.Exists(second.DamagedCopyPath));
        }
        finally
        {
            CleanupAll(path);
        }
    }

    /// <summary>
    /// Nothing is moved until a rebuilt file exists. A target that cannot be read at all leaves the
    /// user exactly where they were, rather than with a renamed original and nothing in its place —
    /// which is how the 11 September repair attempt emptied the file it was trying to save.
    /// </summary>
    [Fact]
    public async Task Rebuild_OfSomethingThatIsNotADatabase_LeavesItWhereItWas()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "this is not a database at all");

            await Assert.ThrowsAnyAsync<SqliteException>(
                () => DatabaseRepair.RebuildAsync(path, CancellationToken.None));

            Assert.True(File.Exists(path));
            Assert.Equal("this is not a database at all", await File.ReadAllTextAsync(path));
            Assert.False(File.Exists(path + ".rebuilding"), "the staging file was left behind");
        }
        finally
        {
            CleanupAll(path);
        }
    }

    /// <summary>
    /// The case this was built for, on the real schema: a configuration database comes back with
    /// every setting in it, its migration history intact so the app does not re-run anything, and
    /// the unique index that makes a key a key.
    ///
    /// <para>On 2026-11-15 a damaged one on this bench had lost exactly two rows —
    /// <c>impedance.fixture</c> and <c>device.clockDrift</c>, the impedance-fixture calibration and
    /// the measured clock drift, both of which need the bench itself to recreate. That is what
    /// "settings" means here and why the repair copies rather than recreates.</para>
    /// </summary>
    [Fact]
    public async Task Rebuild_OfAConfigurationDatabase_KeepsEverySettingAndItsSchema()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedConfigFactoryAsync(path);
            var repository = new ConfigRepository(factory);
            await repository.SetAsync("impedance.fixture", "{\"referenceOhms\":100}", CancellationToken.None);
            await repository.SetAsync("device.clockDrift", "{\"ppm\":83.18}", CancellationToken.None);
            await repository.SetAsync("device.analyzer", "{\"kind\":\"Split\"}", CancellationToken.None);
            SqliteConnectionFactory.CloseAllPools();

            var result = await DatabaseRepair.RebuildAsync(path, CancellationToken.None);

            Assert.Null(result.RemainingProblem);
            Assert.False(result.AnythingLost);

            var reopened = new ConfigRepository(
                new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = path }));
            Assert.Equal("{\"referenceOhms\":100}", await reopened.GetAsync("impedance.fixture", CancellationToken.None));
            Assert.Equal("{\"ppm\":83.18}", await reopened.GetAsync("device.clockDrift", CancellationToken.None));
            Assert.Equal("{\"kind\":\"Split\"}", await reopened.GetAsync("device.analyzer", CancellationToken.None));

            await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: false, CancellationToken.None);

            // The migration history, or the next startup replays migrations over a migrated file.
            Assert.True(await TestDb.ScalarAsync(connection, "SELECT max(version) FROM schema_version;") > 0);

            // And `key` is still a key: a rebuilt file that lost the index accepts the duplicate
            // the index existed to refuse, and the next read then gets whichever row came back first.
            await using var duplicate = connection.CreateCommand();
            duplicate.CommandText =
                "INSERT INTO app_setting (key, value, updated_utc) VALUES ('device.analyzer', '{}', '');";
            await Assert.ThrowsAsync<SqliteException>(() => duplicate.ExecuteNonQueryAsync());
        }
        finally
        {
            CleanupAll(path);
        }
    }

    [Fact]
    public async Task Rebuild_OfAMissingFile_SaysSoRatherThanCreatingOne()
    {
        string path = TestDb.CreateTempPath();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => DatabaseRepair.RebuildAsync(path, CancellationToken.None));

        Assert.False(File.Exists(path));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task SeedAsync(string path, int rows)
    {
        await using (var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: true, CancellationToken.None))
        {
            await ExecuteAsync(connection, "CREATE TABLE note (id INTEGER PRIMARY KEY, label TEXT NOT NULL, body TEXT NOT NULL);");
            await ExecuteAsync(connection, "CREATE INDEX ix_note_label ON note(label);");

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO note (id, label, body) VALUES (@id, @label, @body);";
            insert.Parameters.Add(new SqliteParameter("@id", 0));
            insert.Parameters.Add(new SqliteParameter("@label", ""));
            insert.Parameters.Add(new SqliteParameter("@body", ""));

            for (int i = 1; i <= rows; i++)
            {
                insert.Parameters[0].Value = i;
                insert.Parameters[1].Value = $"note-{i}";
                // Wide enough that the rows span many pages, so corrupting one reaches some of them.
                insert.Parameters[2].Value = new string((char)('a' + (i % 26)), 600);
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await SqliteConnectionFactory.CheckpointAsync(path, CancellationToken.None);
        SqliteConnectionFactory.CloseAllPools();
    }

    /// <summary>
    /// Overwrites a page in the middle of the file with bytes that are not a b-tree page.
    ///
    /// <para>Page 1 is the header and page 2 the schema root; both are left alone, because
    /// destroying either gives "file is not a database" rather than the damage this is about.</para>
    /// </summary>
    private static void CorruptAPage(string path)
    {
        SqliteConnectionFactory.CloseAllPools();

        byte[] bytes = File.ReadAllBytes(path);
        int pageSize = (bytes[16] << 8) | bytes[17];
        if (pageSize == 1)
            pageSize = 65536;

        int pages = bytes.Length / pageSize;
        Assert.True(pages >= 6, $"the seeded file is only {pages} pages; corrupting one would destroy it");

        int page = pages / 2;
        var rubbish = new byte[pageSize];
        new Random(20261115).NextBytes(rubbish);
        rubbish.CopyTo(bytes, page * pageSize);

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// A `-shm` is mapped by whatever has the database open, so a plain read of it fails with
    /// "being used by another process" -- which says nothing about the question being asked.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<long> CountAsync(string path)
    {
        await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: false, CancellationToken.None);
        return await TestDb.ScalarAsync(connection, "SELECT count(*) FROM note;");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void CleanupAll(string path)
    {
        TestDb.Cleanup(path);
        TestDb.Cleanup(path + ".rebuilding");

        string? directory = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (directory is null)
            return;

        foreach (string file in Directory.EnumerateFiles(directory, $"corrupt-*-{name}*"))
            File.Delete(file);
    }
}
