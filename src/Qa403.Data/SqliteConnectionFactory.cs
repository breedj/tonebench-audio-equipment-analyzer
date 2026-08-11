using Microsoft.Data.Sqlite;

namespace Qa403.Data;

/// <summary>
/// Opens connections to <c>qa403.db</c> with the §6 pragmas applied. Pragmas are per-connection
/// state, and Microsoft.Data.Sqlite pools the underlying native handle, so every open — first use
/// or pooled reuse alike — re-applies them; the calls are idempotent, so re-applying costs nothing
/// but skipping it on a reused handle would be a silent correctness bug.
/// </summary>
public sealed class SqliteConnectionFactory(Qa403DatabaseOptions options)
{
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={options.Path}");
        try
        {
            await connection.OpenAsync(ct);
            await ApplyPragmasAsync(connection, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        // page_size must be set before journal_mode/WAL locks it in; changing it afterwards
        // needs a VACUUM.
        await ExecuteAsync(connection, "PRAGMA page_size = 8192;", ct);
        await SetAndVerifyJournalModeAsync(connection, transaction: null, ct);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", ct);
        await ExecuteAsync(connection, "PRAGMA mmap_size = 268435456;", ct);
        await ExecuteAsync(connection, "PRAGMA temp_store = MEMORY;", ct);
        await ExecuteAsync(connection, "PRAGMA cache_size = -64000;", ct);

        // Not in §6's list, but required to restore it: unlike vanilla SQLite (where this
        // defaults off), Microsoft.Data.Sqlite turns FK enforcement ON for every connection.
        // §6.1's schema was never designed for that -- referential integrity here is managed
        // by hand (see MeasurementRepository.DeleteResultAsync's explicit cascade), and a
        // dangling REFERENCES clause left behind by a dropped table (e.g. capture.session_id
        // after 003_project_model.sql drops `session`) makes SQLite refuse to even *prepare*
        // an INSERT into the child table while enforcement is on, regardless of whether the
        // statement touches that column.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", ct);
    }

    /// <summary>
    /// <c>PRAGMA journal_mode = WAL</c> silently no-ops and returns the previous mode when run
    /// inside a transaction — the single most common cause of "SQLite is slow" reports. Reading
    /// back the scalar result and failing loudly here turns that into a startup error instead of
    /// a mystery. Internal (not private), with an optional <paramref name="transaction"/>, purely
    /// so a test can reproduce the trap directly: production code always calls this with
    /// <see langword="null"/>, immediately after <see cref="SqliteConnection.OpenAsync"/> and
    /// before any transaction exists.
    /// </summary>
    internal static async Task SetAndVerifyJournalModeAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA journal_mode = WAL;";
        object? result = await command.ExecuteScalarAsync(ct);

        if (result is not string mode || !string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Failed to set journal_mode = WAL (got '{result}'). This pragma silently fails " +
                "when executed inside a transaction — check that no transaction is open here.");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
