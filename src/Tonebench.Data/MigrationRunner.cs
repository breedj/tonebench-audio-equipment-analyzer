using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// Applies pending SQL migrations from embedded resources at startup, each in its own
/// transaction with a matching <c>schema_version</c> row — idempotent, so running it against an
/// already-current database is a no-op. Files are embedded rather than shipped loose because the
/// app already publishes with an embedded wwwroot, and loose .sql files copied to the output
/// directory are exactly the kind of thing a published layout drops.
/// </summary>
/// <param name="resourcePrefix">
/// Which embedded set to apply. Defaults to the measurement database's. The configuration database
/// passes its own, and gets its own independent <c>schema_version</c> — the table is created in
/// whichever connection it is handed, so the two version histories cannot collide.
/// </param>
public sealed class MigrationRunner(string resourcePrefix = MigrationRunner.MeasurementResourcePrefix)
{
    public const string MeasurementResourcePrefix = "Tonebench.Data.Migrations.";

    /// <summary>The configuration database's own migrations — see <see cref="TonebenchConfigDatabaseOptions"/> for why it is a separate file.</summary>
    public const string ConfigResourcePrefix = "Tonebench.Data.ConfigMigrations.";

    private static readonly Regex VersionPattern = new(@"^(\d+)_", RegexOptions.Compiled);

    /// <summary>
    /// The highest version this build carries. A file whose <c>schema_version</c> is above it was
    /// written by a newer Tonebench and must be refused rather than read: its tables may have
    /// columns this build knows nothing about, and a copy driven by this build's column lists would
    /// drop them silently. See <see cref="ReadVersionAsync"/>.
    /// </summary>
    public int LatestVersion => DiscoverMigrations().Max(m => m.Version);

    /// <summary>
    /// <c>MAX(version)</c> from <c>schema_version</c>, or 0 when the table is absent — this only
    /// reports, and leaves what that means to the caller. <c>ProjectSessionService</c> reads a
    /// missing table as "an SQLite database but not one of ours"; a transfer compares the number
    /// against <see cref="LatestVersion"/> to decide between reading the file in place, migrating a
    /// throwaway copy of it, and refusing it outright.
    /// </summary>
    public static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE((SELECT MAX(version) FROM schema_version), 0) " +
            "WHERE EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_version');";
        object? result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task RunAsync(SqliteConnection connection, CancellationToken ct)
    {
        await EnsureSchemaVersionTableAsync(connection, ct);
        int currentVersion = await GetCurrentVersionAsync(connection, ct);

        foreach (var migration in DiscoverMigrations().Where(m => m.Version > currentVersion))
            await ApplyAsync(connection, migration, ct);
    }

    private static async Task EnsureSchemaVersionTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
              version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        object? result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private static async Task ApplyAsync(SqliteConnection connection, Migration migration, CancellationToken ct)
    {
        await using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO schema_version (version, name, applied_utc) VALUES ($version, $name, $appliedUtc);";
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$appliedUtc", TonebenchTimestamp.Now());
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Instance rather than static: which set to discover is the constructor parameter above.</summary>
    private IEnumerable<Migration> DiscoverMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name =>
            {
                string fileName = name[resourcePrefix.Length..];
                var match = VersionPattern.Match(fileName);
                if (!match.Success)
                    throw new InvalidOperationException($"Migration resource '{name}' does not start with a numeric version.");

                int version = int.Parse(match.Groups[1].Value);
                string sql = ReadResource(assembly, name);
                return new Migration(version, fileName, sql);
            })
            .OrderBy(m => m.Version);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Migration(int Version, string Name, string Sql);
}
