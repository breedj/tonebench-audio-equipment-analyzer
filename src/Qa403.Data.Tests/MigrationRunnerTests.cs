namespace Qa403.Data.Tests;

public class MigrationRunnerTests
{
    [Fact]
    public async Task Run_FreshDatabase_AppliesAllMigrations()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            Assert.True(await TestDb.TableExistsAsync(connection, "project"));
            Assert.True(await TestDb.TableExistsAsync(connection, "measurement_type"));
            Assert.True(await TestDb.TableExistsAsync(connection, "measurement_result"));
            Assert.True(await TestDb.TableExistsAsync(connection, "capture"));
            Assert.True(await TestDb.TableExistsAsync(connection, "spectrum"));
            Assert.True(await TestDb.TableExistsAsync(connection, "metric"));

            Assert.True(await TestDb.ColumnExistsAsync(connection, "spectrum", "channel"));
            Assert.True(await TestDb.ColumnExistsAsync(connection, "metric", "channel"));
            Assert.True(await TestDb.ColumnExistsAsync(connection, "capture", "channels"));
            Assert.True(await TestDb.ColumnExistsAsync(connection, "capture", "measurement_result_id"));
            Assert.True(await TestDb.ColumnExistsAsync(connection, "capture", "seq_index"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Run_DropsSupersededTables()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            Assert.False(await TestDb.TableExistsAsync(connection, "dut"));
            Assert.False(await TestDb.TableExistsAsync(connection, "session"));
            Assert.False(await TestDb.TableExistsAsync(connection, "sweep"));
            Assert.False(await TestDb.TableExistsAsync(connection, "sweep_point"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Run_Twice_IsIdempotent()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            // Running against an already-current database must not throw or re-apply anything.
            await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            long count = await TestDb.ScalarAsync(connection, "SELECT COUNT(*) FROM schema_version;");
            Assert.Equal(5L, count);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Run_RecordsEachVersionInSchemaVersion()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version, name FROM schema_version ORDER BY version;";
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<(long Version, string Name)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));

            Assert.Equal(
                [(1L, "001_initial.sql"), (2L, "002_channels_and_indexes.sql"), (3L, "003_project_model.sql"),
                 (4L, "004_capture_preview.sql"), (5L, "005_orphan_cleanup.sql")],
                rows);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Run_ClearsOrphanedCapturePreviewsSoARecycledRowidDoesNotCollide()
    {
        // The rows migration 005 exists for: previews left behind by a delete that removed their
        // capture. SQLite hands the freed rowid to the next capture inserted, and its preview
        // insert then hits the UNIQUE index on (capture_id, channel, kind) -- a save that fails on
        // a sweep with nothing wrong with it. Written here at 004's state and then migrated, which
        // is exactly the shape an existing database is in.
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM schema_version WHERE version = 5;
                    INSERT INTO capture_preview (capture_id, channel, kind, x, min, max)
                    VALUES (4242, 0, 'fft', zeroblob(4), zeroblob(4), zeroblob(4));
                    INSERT INTO metric (capture_id, channel, name, value, unit)
                    VALUES (4242, 0, 'thd_percent', 1.0, '%');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            Assert.Equal(0L, await TestDb.ScalarAsync(
                connection, "SELECT COUNT(*) FROM capture_preview WHERE capture_id NOT IN (SELECT id FROM capture);"));
            Assert.Equal(0L, await TestDb.ScalarAsync(
                connection, "SELECT COUNT(*) FROM metric WHERE capture_id NOT IN (SELECT id FROM capture);"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }
}
