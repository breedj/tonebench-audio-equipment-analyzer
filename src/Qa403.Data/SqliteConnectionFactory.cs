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
    public Task<SqliteConnection> OpenAsync(CancellationToken ct) => OpenAtAsync(options.Path, ct);

    /// <summary>
    /// Opens any of this app's SQLite files with the pragmas above applied.
    ///
    /// <para>Shared with <see cref="ConfigConnectionFactory"/> rather than duplicated: the
    /// configuration database wants the identical treatment, and two copies of this list is how one
    /// database quietly ends up in rollback-journal mode while the other is in WAL.</para>
    /// </summary>
    internal static async Task<SqliteConnection> OpenAtAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={path}");
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

        // FULL rather than §6's NORMAL, and no mmap. Both are the durability end of SQLite's
        // trade-offs and this workload cannot feel the cost: the database is a few megabytes and a
        // write is a measurement save, seconds to minutes apart. What it buys is a smaller set of
        // ways to lose a page. qa403.db came up "database disk image is malformed" twice in two days
        // (10 and 11 Sept 2026), each time with pages at the end of the file zeroed — allocated,
        // never filled — after the console window was closed. NORMAL fsyncs the WAL only at
        // checkpoint time; FULL fsyncs it on every commit, so a committed save is on the platter
        // before the process can be torn down. mmap_size was 256 MB: memory-mapped I/O is the one
        // mode SQLite's own documentation lists as making a database vulnerable to a stray write
        // from inside the process, and this process runs native USB code against pooled buffers.
        // Neither is *known* to have been the cause; both are known to be safe to remove.
        await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", ct);
        await ExecuteAsync(connection, "PRAGMA mmap_size = 0;", ct);
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

    /// <summary>
    /// Folds the WAL back into the main file and releases every pooled connection, so the database
    /// is a single self-contained file with nothing open on it.
    ///
    /// <para>This is the shutdown step Microsoft.Data.Sqlite never gets to take on its own here.
    /// It pools native handles and only closes them from a <c>ProcessExit</c> hook — and when the
    /// console window is closed, Windows terminates this process the instant the CTRL_CLOSE handler
    /// returns, so that hook never runs. In ten days of logs (65 shutdowns) the host's own last line
    /// never appeared once. Left to itself, every close therefore ended with the WAL un-checkpointed
    /// and the connections simply gone. WAL mode is designed to survive that, but "designed to
    /// survive" is a weaker guarantee than "nothing was open", and the file <i>was</i> found damaged
    /// twice. <c>TRUNCATE</c> rather than <c>PASSIVE</c> so the -wal is left empty, which is also
    /// what makes a leftover WAL after the next start diagnostic rather than routine.</para>
    ///
    /// <para><see cref="SqliteConnection.ClearAllPools"/> is process-wide, so the caller runs this
    /// for every database first and the pools are cleared once at the end — see
    /// <see cref="CloseAllPools"/>. A checkpoint that finds a connection still writing returns
    /// <c>busy</c>; that is logged by the caller rather than retried, because on this path nothing
    /// should be writing any more and a retry would only hide the bug that is.</para>
    /// </summary>
    /// <returns>
    /// Whether the checkpoint completed, and how many WAL frames it folded in. Two pragmas rather
    /// than one: <c>TRUNCATE</c> reports the log <i>after</i> resetting it — 0 frames on success,
    /// which reads exactly like "nothing to do" — so the count comes from a <c>PASSIVE</c> pass first.
    /// </returns>
    public static async Task<(bool Completed, long WalFrames)> CheckpointAsync(string path, CancellationToken ct)
    {
        await using var connection = await OpenAtAsync(path, ct);

        long frames = 0;
        bool busy = false;
        foreach (string mode in new[] { "PASSIVE", "TRUNCATE" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA wal_checkpoint({mode});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                continue;

            busy |= reader.GetInt64(0) != 0;
            if (mode == "PASSIVE")
                frames = reader.GetInt64(1);
        }

        return (!busy, frames);
    }

    /// <summary>Closes every pooled native handle, for every database this process opened. See <see cref="CheckpointAsync"/>.</summary>
    public static void CloseAllPools() => SqliteConnection.ClearAllPools();

    /// <summary>
    /// <c>PRAGMA quick_check</c>, returned as a message rather than thrown: <c>null</c> when the file
    /// is sound, otherwise what SQLite said about it.
    ///
    /// <para>Run at startup, because a damaged file otherwise announces itself as
    /// a 500 on whichever read first touches the bad page — possibly days later, on one history
    /// entry, with a stack trace that names the repository and not the disease. Both real cases were
    /// like that. <c>quick_check</c> rather than <c>integrity_check</c>: it skips index consistency,
    /// which on a file of this size is a distinction of milliseconds either way, but it is the one
    /// SQLite documents as safe to run routinely. A malformed page can make even that throw, which is
    /// why the exception is folded into the message too.</para>
    /// </summary>
    public static async Task<string?> QuickCheckAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAtAsync(path, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            await using var reader = await command.ExecuteReaderAsync(ct);

            var lines = new List<string>();
            while (await reader.ReadAsync(ct))
                lines.Add(reader.GetString(0));

            if (lines.Count == 1 && string.Equals(lines[0], "ok", StringComparison.OrdinalIgnoreCase))
                return null;

            return string.Join("; ", lines);
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
