using Microsoft.Data.Sqlite;

namespace Qa403.Data.Tests;

internal static class TestDb
{
    public static string CreateTempPath() => Path.Combine(Path.GetTempPath(), $"qa403-test-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Must be called before deleting the file: Microsoft.Data.Sqlite pools the underlying native
    /// handle on Dispose rather than closing it, so the OS file lock can outlive a `using` block.
    /// </summary>
    public static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    public static async Task<SqliteConnection> OpenMigratedAsync(string path, CancellationToken ct = default)
    {
        var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
        var connection = await factory.OpenAsync(ct);
        await new MigrationRunner().RunAsync(connection, ct);
        return connection;
    }

    /// <summary>Migrates the database at <paramref name="path"/>, then returns a factory pointed at
    /// the same file for repositories under test to open their own connections against.</summary>
    public static async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(string path, CancellationToken ct = default)
    {
        var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
        await using (var connection = await factory.OpenAsync(ct))
            await new MigrationRunner().RunAsync(connection, ct);
        return factory;
    }

    /// <summary>The configuration database's counterpart: its own migration set, its own file.</summary>
    public static async Task<ConfigConnectionFactory> CreateMigratedConfigFactoryAsync(string path, CancellationToken ct = default)
    {
        var factory = new ConfigConnectionFactory(new Qa403ConfigDatabaseOptions { Path = path });
        await using (var connection = await factory.OpenAsync(ct))
            await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, ct);
        return factory;
    }

    public static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    /// <summary>tableName is always a hardcoded literal from test code, never external input.</summary>
    public static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
