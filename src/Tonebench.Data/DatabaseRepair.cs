using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>What one table gave up when a damaged file was rebuilt.</summary>
/// <param name="Name">The table.</param>
/// <param name="RowsCopied">Rows that read cleanly and were written to the new file.</param>
/// <param name="RowsLost">Rows whose own read threw, and which the new file therefore has not got.</param>
/// <param name="MayBeIncomplete">
/// The <i>enumeration</i> of this table's rows threw part way through, so rows past that point were
/// never even offered. <see cref="RowsLost"/> is then a floor rather than a count — there is no way
/// to ask a damaged b-tree how many entries it was supposed to have.
/// </param>
public sealed record RepairedTable(string Name, int RowsCopied, int RowsLost, bool MayBeIncomplete);

/// <summary>What a rebuild did, in the terms a user has to be told it in.</summary>
/// <param name="DamagedCopyPath">Where the damaged original was kept. Never deleted.</param>
/// <param name="Tables">One entry per table, in the order they were copied.</param>
/// <param name="RemainingProblem">
/// What <c>quick_check</c> says about the <i>rebuilt</i> file, or null when it is sound. Non-null
/// means the rebuild did not work and the caller must say so rather than claiming a repair.
/// </param>
public sealed record DatabaseRepairResult(
    string DamagedCopyPath,
    IReadOnlyList<RepairedTable> Tables,
    string? RemainingProblem)
{
    public int RowsCopied => Tables.Sum(t => t.RowsCopied);
    public int RowsLost => Tables.Sum(t => t.RowsLost);

    /// <summary>Whether anything at all was lost — a count, or a table that could not be fully walked.</summary>
    public bool AnythingLost => Tables.Any(t => t.RowsLost > 0 || t.MayBeIncomplete);
}

/// <summary>
/// Rebuilds a damaged SQLite file by copying what still reads into a fresh one.
///
/// <para><b>Rebuild, never patch.</b> The damage this exists for looks the same every time it has
/// been seen here: <c>integrity_check</c> fails on its first line, every table's <c>count(*)</c>
/// still works, and one or two rows have overflow pages that were allocated and never filled. There
/// is nothing to edit — the fix is a new file containing everything that still reads, which is what
/// this does, and the damaged one kept beside it in case anybody ever wants another go at it. The
/// procedure was written down after the two September 2026 incidents and carried out by hand twice;
/// this is that procedure, so that a user can run it instead of a developer.</para>
///
/// <para><b>Row by row, because a cursor dies at the first bad page.</b> A plain
/// <c>INSERT … SELECT</c> — or <c>VACUUM INTO</c>, which is the obvious thing to reach for — aborts
/// the moment it touches the damage and leaves nothing. So each table is walked as a list of
/// rowids first and then read one row at a time, each in its own statement, and a row whose read
/// throws is skipped and counted. Both halves can fail: the enumeration itself can die part way
/// through, which is what <see cref="RepairedTable.MayBeIncomplete"/> reports, since a damaged
/// b-tree cannot be asked how many entries it was supposed to have.</para>
///
/// <para><b>The original is moved aside only once a verified rebuild exists.</b> Everything happens
/// in a staging file next to the target; if any step throws, the staging file is deleted and the
/// damaged original is exactly where it was. That ordering is the whole safety property of this
/// class — the alternative, renaming first, is how the 11 September repair attempt emptied a file
/// it was trying to save.</para>
///
/// <para>Pooling is why <see cref="SqliteConnectionFactory.CloseAllPools"/> is called before the
/// files move: Microsoft.Data.Sqlite keeps native handles in a pool, and a handle still open on the
/// old inode makes the move fail on Windows. It is process-wide, so it also drops the open
/// project's pool — harmless, since every repository opens whatever the holder names.</para>
/// </summary>
public static class DatabaseRepair
{
    /// <summary>The staging file's suffix. Deleted on every path, success or failure.</summary>
    private const string StagingSuffix = ".rebuilding";

    /// <summary>
    /// Rebuilds the database at <paramref name="path"/> in place, keeping the damaged original.
    /// </summary>
    /// <remarks>
    /// Safe to run on a file that is <i>not</i> damaged: everything reads, everything is copied, and
    /// the result is a compacted file with the original kept beside it. The caller decides when to
    /// offer it; this makes no judgement about whether it was needed.
    /// </remarks>
    public static async Task<DatabaseRepairResult> RebuildAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("There is no database at that path to rebuild.", path);

        string staging = path + StagingSuffix;
        DeleteWithSidecars(staging);

        List<RepairedTable> tables;
        try
        {
            tables = await CopyAsync(path, staging, ct);
            await CheckpointAndCloseAsync(staging, ct);
        }
        catch
        {
            SqliteConnectionFactory.CloseAllPools();
            DeleteWithSidecars(staging);
            throw;
        }

        // Only now, with a complete rebuilt file on disk, does anything move.
        SqliteConnectionFactory.CloseAllPools();

        string damagedCopy = NextDamagedCopyPath(path);
        MoveWithSidecars(path, damagedCopy);
        try
        {
            File.Move(staging, path);
        }
        catch
        {
            // Put the original back rather than leaving the app with no file at all.
            MoveWithSidecars(damagedCopy, path);
            DeleteWithSidecars(staging);
            throw;
        }

        // The staging file's own WAL and index mirror were checkpointed away above; anything left
        // belongs to the name that has just been vacated and must not be adopted by the new file.
        DeleteSidecars(staging);

        string? remaining = await SqliteConnectionFactory.QuickCheckAsync(path, ct);
        return new DatabaseRepairResult(damagedCopy, tables, remaining);
    }

    private static async Task<List<RepairedTable>> CopyAsync(string source, string staging, CancellationToken ct)
    {
        await using var from = await SqliteConnectionFactory.OpenUnpooledAtAsync(source, create: false, ct);
        await using var to = await SqliteConnectionFactory.OpenUnpooledAtAsync(staging, create: true, ct);

        var schema = await ReadSchemaAsync(from, ct);

        // Tables first and everything else last: an index created ahead of the data is maintained
        // on every insert for no benefit, and a trigger would *fire* on them.
        foreach (var (_, _, sql) in schema.Where(s => s.Type == "table"))
            await ExecuteAsync(to, sql, ct);

        var tables = new List<RepairedTable>();
        foreach (var (_, name, _) in schema.Where(s => s.Type == "table"))
            tables.Add(await CopyTableAsync(from, to, name, ct));

        foreach (var (_, _, sql) in schema.Where(s => s.Type != "table"))
        {
            // An index that cannot be built is not a reason to lose the data: a UNIQUE index over
            // rows that were salvaged from a damaged file may genuinely have duplicates in it now.
            try { await ExecuteAsync(to, sql, ct); }
            catch (SqliteException) { }
        }

        return tables;
    }

    private static async Task<List<(string Type, string Name, string Sql)>> ReadSchemaAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var schema = new List<(string, string, string)>();
        await using var command = connection.CreateCommand();

        // `sqlite_%` covers the autoindexes SQLite creates for a PRIMARY KEY, which are implied by
        // the table's own CREATE and cannot be issued as statements.
        command.CommandText =
            "SELECT type, name, sql FROM sqlite_master "
            + "WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' "
            + "ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, rootpage;";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            schema.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        return schema;
    }

    private static async Task<RepairedTable> CopyTableAsync(
        SqliteConnection from, SqliteConnection to, string table, CancellationToken ct)
    {
        var columns = await ColumnsAsync(from, table, ct);
        if (columns.Count == 0)
            return new RepairedTable(table, 0, 0, MayBeIncomplete: false);

        string quoted = string.Join(", ", columns.Select(c => $"\"{c}\""));
        string parameters = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        var (rowids, enumerationFailed) = await RowidsAsync(from, table, ct);

        // A WITHOUT ROWID table has no rowid to address a row by, so it is copied in one pass and
        // whatever the cursor manages before it dies is what is saved.
        if (rowids is null)
            return await CopyStreamingAsync(from, to, table, quoted, parameters, columns.Count, ct);

        int copied = 0;
        int lost = 0;

        await using var transaction = (SqliteTransaction)await to.BeginTransactionAsync(ct);
        await using var insert = to.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO \"{table}\" ({quoted}) VALUES ({parameters});";
        for (int i = 0; i < columns.Count; i++)
            insert.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

        foreach (long rowid in rowids)
        {
            object?[] values;
            try
            {
                values = await ReadRowAsync(from, table, quoted, rowid, columns.Count, ct);
            }
            catch (SqliteException)
            {
                lost++;
                continue;
            }

            for (int i = 0; i < columns.Count; i++)
                insert.Parameters[i].Value = values[i] ?? DBNull.Value;

            await insert.ExecuteNonQueryAsync(ct);
            copied++;
        }

        await transaction.CommitAsync(ct);
        return new RepairedTable(table, copied, lost, enumerationFailed);
    }

    private static async Task<object?[]> ReadRowAsync(
        SqliteConnection from, string table, string quoted, long rowid, int count, CancellationToken ct)
    {
        await using var select = from.CreateCommand();
        select.CommandText = $"SELECT {quoted} FROM \"{table}\" WHERE rowid = @rowid;";
        select.Parameters.AddWithValue("@rowid", rowid);

        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new SqliteException($"Row {rowid} of {table} could not be read.", 11);

        var values = new object?[count];
        for (int i = 0; i < count; i++)
            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);

        return values;
    }

    private static async Task<RepairedTable> CopyStreamingAsync(
        SqliteConnection from, SqliteConnection to, string table,
        string quoted, string parameters, int count, CancellationToken ct)
    {
        int copied = 0;
        bool incomplete = false;

        await using var transaction = (SqliteTransaction)await to.BeginTransactionAsync(ct);
        await using var insert = to.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO \"{table}\" ({quoted}) VALUES ({parameters});";
        for (int i = 0; i < count; i++)
            insert.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

        try
        {
            await using var select = from.CreateCommand();
            select.CommandText = $"SELECT {quoted} FROM \"{table}\";";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                for (int i = 0; i < count; i++)
                    insert.Parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);

                await insert.ExecuteNonQueryAsync(ct);
                copied++;
            }
        }
        catch (SqliteException)
        {
            incomplete = true;
        }

        await transaction.CommitAsync(ct);
        return new RepairedTable(table, copied, 0, incomplete);
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));

        return columns;
    }

    /// <returns>
    /// The rowids that could be enumerated and whether the walk died part way through, or
    /// <c>(null, false)</c> for a table that has no rowid to address its rows by.
    /// </returns>
    private static async Task<(List<long>? Rowids, bool Failed)> RowidsAsync(
        SqliteConnection connection, string table, CancellationToken ct)
    {
        var rowids = new List<long>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rowid FROM \"{table}\" ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rowids.Add(reader.GetInt64(0));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && rowids.Count == 0)
        {
            // SQLITE_ERROR on the first statement: "no such column: rowid", i.e. WITHOUT ROWID.
            return (null, false);
        }
        catch (SqliteException)
        {
            // Damage in the table's own b-tree. Keep what was reached and say the rest is unknown.
            return (rowids, true);
        }

        return (rowids, false);
    }

    private static async Task CheckpointAndCloseAsync(string path, CancellationToken ct)
    {
        await SqliteConnectionFactory.CheckpointAsync(path, ct);
        SqliteConnectionFactory.CloseAllPools();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Where the damaged file is kept: <c>corrupt-yyyyMMdd-&lt;name&gt;</c> beside it, suffixed when
    /// that is taken. Two repairs on one day must not have the second overwrite the first — the
    /// first copy is the one closest to the original damage.
    /// </summary>
    private static string NextDamagedCopyPath(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string stamp = DateTime.Now.ToString("yyyyMMdd");
        string name = Path.GetFileName(path);

        string candidate = Path.Combine(directory, $"corrupt-{stamp}-{name}");
        for (int n = 1; File.Exists(candidate); n++)
            candidate = Path.Combine(directory, $"corrupt-{stamp}-{n:000}-{name}");

        return candidate;
    }

    /// <summary>
    /// Moves a database and its <c>-wal</c> / <c>-shm</c> together.
    ///
    /// <para>Together, and that is not tidiness. A <c>-wal</c> left behind by a file that has moved
    /// away is adopted by whatever takes its name next, which here is the <i>rebuilt</i> database —
    /// so the one thing worse than losing the damaged file would be replaying its log into the file
    /// that was just salvaged from it.</para>
    /// </summary>
    private static void MoveWithSidecars(string from, string to)
    {
        File.Move(from, to, overwrite: false);
        foreach (string suffix in Sidecars)
        {
            if (File.Exists(from + suffix))
                File.Move(from + suffix, to + suffix, overwrite: true);
        }
    }

    private static void DeleteWithSidecars(string path)
    {
        File.Delete(path);
        DeleteSidecars(path);
    }

    private static void DeleteSidecars(string path)
    {
        foreach (string suffix in Sidecars)
            File.Delete(path + suffix);
    }

    private static readonly string[] Sidecars = ["-wal", "-shm"];
}
