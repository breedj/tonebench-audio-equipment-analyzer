using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Qa403.Data;

/// <summary>
/// Applies pending SQL migrations from embedded resources at startup, each in its own
/// transaction with a matching <c>schema_version</c> row — idempotent, so running it against an
/// already-current database is a no-op. Files are embedded rather than shipped loose because the
/// app already publishes with an embedded wwwroot, and loose .sql files copied to the output
/// directory are exactly the kind of thing a published layout drops.
/// </summary>
public sealed class MigrationRunner
{
    private const string ResourcePrefix = "Qa403.Data.Migrations.";
    private static readonly Regex VersionPattern = new(@"^(\d+)_", RegexOptions.Compiled);

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
            command.Parameters.AddWithValue("$appliedUtc", Qa403Timestamp.Now());
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static IEnumerable<Migration> DiscoverMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name =>
            {
                string fileName = name[ResourcePrefix.Length..];
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
