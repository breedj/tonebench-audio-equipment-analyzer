using Dapper;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// Copies measurements and their saved results from one project file into another — the one
/// mechanism behind both Export and Import. Export: the source is the open project. Import: the
/// destination is.
///
/// <para><b>Why <c>ATTACH</c> and <c>INSERT … SELECT</c> rather than Dapper.</b>
/// <see cref="ProjectRepository.CopyTypesAsync"/> is the existing cross-file copy and says in its
/// own remarks that it is metadata only, no BLOBs. What this copies is the opposite case:
/// <c>capture.pcm</c> holds tens of megabytes per imported simulation, every spectrum is a blob,
/// and Dapper's <c>QueryAsync</c> is buffered — so a row-by-row copy of a large export would
/// materialise the whole thing as <c>byte[]</c> on the large-object heap before the first insert.
/// <c>INSERT … SELECT</c> keeps every blob inside SQLite's pager; nothing but type rows and ids
/// ever enters managed memory.</para>
///
/// <para><b><see cref="TransferAsync"/> owns the attach and the transaction</b>, rather than
/// leaving them to callers, because three details of them are load-bearing and a second
/// implementation would get one wrong. (1) The destination connection is <b>unpooled</b>: a pooled
/// handle returned with a second database still attached either fails the next <c>ATTACH</c> or
/// keeps the other file open (see
/// <see cref="SqliteConnectionFactory.OpenUnpooledAtAsync"/>), and <c>DETACH</c> runs in a
/// <c>finally</c> as well, not instead. (2) The transaction is <c>IMMEDIATE</c>: the session's own
/// lock does not reach the repositories, so a <c>POST /api/measurements</c> can still be writing to
/// the open project, and taking the write lock up front turns an obscure
/// <c>SQLITE_BUSY_SNAPSHOT</c> on upgrade into a plain "database is locked" for whichever caller
/// loses. (3) A multi-database transaction is <i>not</i> atomic when a participant is in WAL mode,
/// which every Tonebench file is — harmless here only because the source is never written, so do
/// not "fix" this by writing to both sides. The source is attached read-write (SQLite gives an
/// attached database the main connection's flags) and simply never written; the test that the
/// source comes back byte-for-byte is what holds that.</para>
///
/// <para>The one thing <see cref="TransferAsync"/> does <b>not</b> do is migrate either file. Both
/// must already be at this build's schema version, which is the caller's business because the two
/// sides need opposite treatment: an export destination is being written and so is migrated in
/// place, while an import source may have arrived by e-mail and is read through a throwaway copy
/// instead (<c>ProjectTransferSource</c>).</para>
///
/// <para><b>Id remapping is a map for measurements and an offset for everything under them.</b>
/// A measurement may be <i>merged</i> into one the destination already has, which maps its id to an
/// existing one rather than translating it, so types are inserted one at a time and recorded in a
/// temp table. Results, captures and the child tables cannot collide once their parent is placed, so
/// each gets one offset — <c>MAX(id)</c> of the destination table, read with its own statement
/// <i>before</i> the insert and bound as a parameter. Never as an inline subquery: that would read
/// the table the same statement is inserting into, and when it goes wrong the symptom is
/// <c>UNIQUE constraint failed: capture_preview.capture_id, …</c>, which is the exact signature of
/// the orphaned-rowid fault migrations 005, 007 and 010 exist to repair, arriving from a new
/// direction and reading like a schema bug.</para>
///
/// <para><b>Column lists are explicit</b> (see <see cref="Plans"/>) and checked against
/// <c>PRAGMA table_info</c> by a test. Deriving them at run time would carry a future plain column
/// for free but would also carry a future column holding an <i>id</i> — the set of columns that get
/// rewritten is hand-written either way — leaving rows in the recipient's file pointing at
/// somebody else's capture, with no constraint violation to announce it. An explicit list inverts
/// that: a new column fails a test and forces the decision.</para>
/// </summary>
public static class ProjectTransferRepository
{
    /// <summary>
    /// One table's columns, split by how the copy treats them. <paramref name="Columns"/> are
    /// carried across verbatim; <paramref name="Rewritten"/> are the ones this file decides —
    /// omitted so the destination allocates its own, offset, re-pointed through the type map, or
    /// nulled. Their union must be every column the table has, which
    /// <c>ProjectTransferRepositoryTests.ColumnLists_MatchPragmaTableInfo</c> asserts.
    /// </summary>
    internal sealed record TablePlan(string Table, IReadOnlyList<string> Columns, IReadOnlyList<string> Rewritten);

    /// <summary>
    /// Every table this copies, with its columns. Exposed for the column test above.
    ///
    /// <para>The child tables come from <see cref="CaptureCascade.ChildTables"/> — one list for the
    /// delete cascade and the copy, so a seventh child of <c>capture</c> cannot be added to one and
    /// forgotten in the other. <c>impulse_reference</c> is deliberately not here; see that list's
    /// remarks for why copying it would be wrong rather than merely unnecessary.</para>
    /// </summary>
    internal static readonly IReadOnlyList<TablePlan> Plans =
    [
        new("measurement_type",
            ["kind", "notes", "params_json", "created_utc"],
            // id: the destination allocates it. project_id: the destination's project. name: may be
            // suffixed. sort_order: appended at the destination's end. deleted_utc: never carried.
            ["id", "project_id", "name", "sort_order", "deleted_utc"]),

        new("measurement_result",
            // `ts` is carried, not restamped: when a measurement was taken is the whole point of
            // keeping it, and the save path's own TonebenchTimestamp.Now() would overwrite that.
            // `source` carries so an imported simulation still groups apart in the History list.
            ["name", "notes", "ts", "params_json", "device_name", "serial", "fw_ver", "is_simulated", "source"],
            ["id", "measurement_type_id", "deleted_utc"]),

        new("capture",
            // `seq_index` is carried verbatim: it is the sweep-point order that ix_capture_result
            // sorts by, and renumbering it scrambles the curve. `fs` is carried as stored, which is
            // why the source must already be at this build's schema version — migration 015 rounds
            // rows whose fs was written with REAL affinity, and INSERT … SELECT copies a value with
            // its storage class, so copying from an un-migrated file would reproduce that fault
            // here. `pcm_fs` is carried unrounded and must stay so: the stored samples depend on it.
            ["ts", "fs", "n", "in_range_dbv", "out_range_dbv", "gen_json", "pcm", "channels", "seq_index", "pcm_fs"],
            // session_id is nulled, not carried: migration 003 dropped the `session` table without
            // clearing this column, so an old file can hold ids pointing at a table that no longer
            // exists. (That dangling REFERENCES is also why PRAGMA foreign_keys = OFF is
            // non-negotiable — with enforcement on, SQLite refuses even to *prepare* this insert.)
            ["id", "session_id", "measurement_result_id"]),

        // `metric` is the one child with no `id` of its own — a plain rowid table — so nothing is
        // omitted here but capture_id. It also has no unique index (ix_metric is plain), so nothing
        // would stop a double copy from duplicating its rows; what stops it is that results always
        // get fresh ids and are never merged. Only measurements are.
        new("metric", ["name", "value", "unit", "channel"], ["capture_id"]),

        new("spectrum",
            ["window", "s1", "s2", "enbw_hz", "bin_hz", "norm", "mag_db", "channel"],
            ["id", "capture_id"]),

        new("capture_preview", ["channel", "kind", "x", "min", "max"], ["id", "capture_id"]),

        new("response_trace",
            [
                "kind", "start_hz", "end_hz", "peak_db", "peak_hz",
                "bw1_lower_hz", "bw1_upper_hz", "bw1_lower_clamped", "bw1_upper_clamped",
                "bw3_lower_hz", "bw3_upper_hz", "bw3_lower_clamped", "bw3_upper_clamped",
                "freq_hz", "mag_min_db", "mag_max_db", "phase_deg", "coherence",
                "reference_db", "reference_mode", "group_delay_s", "phase_delay_s",
            ],
            ["id", "capture_id"]),

        new("impulse_result",
            [
                "kind", "sample_rate_hz", "peak_index", "peak_volts",
                "gate_pre_ms", "gate_post_ms", "gate_window", "resolution_limit_hz",
                // reference_name is provenance only. Nothing re-resolves it at read time, which is
                // what makes an imported impulse result correct on another bench: the runner
                // divided the calibration out before this row was ever written.
                "reference_mode", "reference_name", "latency_samples", "ir_samples", "ir",
            ],
            ["id", "capture_id"]),

        new("impulse_harmonic", ["ord", "upper_valid_hz", "fundamental_hz", "level_dbc"], ["id", "capture_id"]),
    ];

    /// <summary>How many suffixed names to try before giving up. A bench with a thousand "Amp (n)" is not a case to serve.</summary>
    private const int MaxRenameAttempts = 1000;

    private static TablePlan PlanFor(string table) =>
        Plans.FirstOrDefault(p => p.Table == table)
        ?? throw new InvalidOperationException($"No column plan for '{table}'.");

    /// <summary>
    /// One project file's live measurements and their live saved results — the tree both transfer
    /// dialogs show. Static over a connection, this assembly's convention for a file that is not
    /// (yet) the current one, so the open project and a file being inspected answer from the same
    /// code and the two dialogs cannot disagree about the shape.
    /// </summary>
    public static async Task<IReadOnlyList<ProjectContentsType>> ListContentsAsync(
        SqliteConnection connection, long projectId, CancellationToken ct)
    {
        var typeRows = await connection.QueryAsync<ContentsTypeRaw>(
            """
            SELECT id AS Id, kind AS Kind, name AS Name, notes AS Notes, sort_order AS SortOrder
            FROM measurement_type
            WHERE project_id = @projectId AND deleted_utc IS NULL
            ORDER BY sort_order, id;
            """,
            new { projectId });

        var resultRows = await ReadResultsAsync(connection, projectId, ct);

        var bytesByResult = await ReadApproximateBytesAsync(connection, projectId, ct);

        var resultsByType = resultRows
            .GroupBy(r => r.TypeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ProjectContentsResult>)g.Select(r => new ProjectContentsResult(
                    r.ResultId, r.Name, TonebenchTimestamp.Parse(r.Ts), r.Source, (int)r.Captures,
                    bytesByResult.TryGetValue(r.ResultId, out long bytes) ? bytes : 0)).ToList());

        return typeRows.Select(t => new ProjectContentsType(
            t.Id, t.Kind, t.Name, t.Notes, (int)t.SortOrder,
            resultsByType.TryGetValue(t.Id, out var results) ? results : [])).ToList();
    }

    /// <summary>
    /// The project's live results, read by ordinal rather than through Dapper.
    ///
    /// <para><b>Why by hand.</b> The capture count is a correlated subquery, and a computed column
    /// has no declared type. Dapper builds its deserializer from the reader's <i>schema</i> before
    /// it reads a row, so on a project with no results yet — a new one, or one whose measurements
    /// have never been run — there is no value for Microsoft.Data.Sqlite to infer from and
    /// <c>GetFieldType</c> falls back to <c>byte[]</c>. Materialising a record whose member is a
    /// <c>long</c> then throws "a parameterless default constructor or one matching signature …",
    /// which names the whole constructor and nothing that is actually wrong. That is the trap
    /// <see cref="MeasurementRepository.ListResultsAsync"/>'s <c>IFNULL</c> guards document,
    /// arriving from the empty-set direction — where <c>IFNULL</c> cannot help, because the problem
    /// is the absence of a row rather than a NULL in one. Reported from the Export dialog on
    /// 2026-10-17; pinned by <c>ListContents_WithNoResultsToReport_StillAnswers</c>.</para>
    /// </summary>
    private static async Task<List<ContentsResultRaw>> ReadResultsAsync(
        SqliteConnection connection, long projectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mr.measurement_type_id, mr.id, mr.name, mr.ts, mr.source,
                   (SELECT COUNT(*) FROM capture c WHERE c.measurement_result_id = mr.id)
            FROM measurement_result mr
            JOIN measurement_type mt ON mt.id = mr.measurement_type_id
            WHERE mt.project_id = $projectId AND mt.deleted_utc IS NULL AND mr.deleted_utc IS NULL
            ORDER BY mr.ts, mr.id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        var rows = new List<ContentsResultRaw>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ContentsResultRaw(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
        }

        return rows;
    }

    /// <summary>
    /// Roughly what each result's stored blobs come to, so a dialog can say "512 MB" before someone
    /// tries to e-mail it. One query rather than a correlated subquery per row.
    ///
    /// <para>The blob columns are named here by hand and this is the one place in this file where
    /// that is acceptable: a future blob column left out makes the <i>estimate</i> low, which is
    /// cosmetic, where a column left out of <see cref="Plans"/> loses data. It is also why this is
    /// called <c>Approximate</c> — it counts blob payloads, not pages, indexes or row overhead.</para>
    /// </summary>
    private static async Task<Dictionary<long, long>> ReadApproximateBytesAsync(
        SqliteConnection connection, long projectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        // Every column here is computed too, so this is read by ordinal for the same reason
        // ReadResultsAsync is. The blob columns are named by hand and that is acceptable in this one
        // place: a future blob column left out makes the *estimate* low, which is cosmetic, where a
        // column left out of Plans loses data. It is also why this is called Approximate -- it
        // counts blob payloads, not pages, indexes or row overhead.
        command.CommandText =
            """
            WITH caps AS (
              SELECT c.id AS capture_id, c.measurement_result_id AS result_id, IFNULL(LENGTH(c.pcm), 0) AS bytes
              FROM capture c
              JOIN measurement_result mr ON mr.id = c.measurement_result_id
              JOIN measurement_type mt ON mt.id = mr.measurement_type_id
              WHERE mt.project_id = $projectId AND mt.deleted_utc IS NULL AND mr.deleted_utc IS NULL
            ),
            blobs AS (
              SELECT result_id, bytes FROM caps
              UNION ALL SELECT caps.result_id, LENGTH(s.mag_db)
                FROM spectrum s JOIN caps ON caps.capture_id = s.capture_id
              UNION ALL SELECT caps.result_id, LENGTH(cp.x) + LENGTH(cp."min") + LENGTH(cp."max")
                FROM capture_preview cp JOIN caps ON caps.capture_id = cp.capture_id
              UNION ALL SELECT caps.result_id,
                     LENGTH(rt.freq_hz) + LENGTH(rt.mag_min_db) + LENGTH(rt.mag_max_db)
                   + LENGTH(rt.phase_deg) + LENGTH(rt.coherence)
                   + IFNULL(LENGTH(rt.group_delay_s), 0) + IFNULL(LENGTH(rt.phase_delay_s), 0)
                FROM response_trace rt JOIN caps ON caps.capture_id = rt.capture_id
              UNION ALL SELECT caps.result_id, LENGTH(ir.ir)
                FROM impulse_result ir JOIN caps ON caps.capture_id = ir.capture_id
              UNION ALL SELECT caps.result_id, LENGTH(ih.fundamental_hz) + LENGTH(ih.level_dbc)
                FROM impulse_harmonic ih JOIN caps ON caps.capture_id = ih.capture_id
            )
            SELECT result_id, SUM(bytes) FROM blobs GROUP BY result_id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        var bytes = new Dictionary<long, long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            bytes[reader.GetInt64(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);

        return bytes;
    }

    /// <summary>The schema name the source is attached under. One word, and never user input.</summary>
    private const string SourceSchema = "src";

    /// <summary>
    /// Copies the picked measurements and results from the project file at
    /// <paramref name="sourcePath"/> into project <paramref name="destinationProjectId"/> of the one
    /// at <paramref name="destinationPath"/>. Both files must already be at this build's schema
    /// version — see this class's remarks for why that is the caller's job and not this method's.
    /// </summary>
    /// <param name="onClash">
    /// The default for a measurement whose name is taken in the destination.
    /// <see cref="TransferSelection.OnClash"/> overrides it per measurement.
    /// </param>
    public static async Task<TransferReport> TransferAsync(
        string destinationPath,
        string sourcePath,
        long destinationProjectId,
        IReadOnlyList<TransferSelection> selection,
        TypeNameClash onClash,
        CancellationToken ct)
    {
        await using var connection = await SqliteConnectionFactory.OpenUnpooledAtAsync(destinationPath, create: false, ct);

        // A bound parameter rather than an interpolated literal: ATTACH's filename is an
        // expression, so this sidesteps quoting a Windows path's backslashes entirely. It also has
        // to run before the transaction — ATTACH is not allowed inside one.
        await connection.ExecuteAsync($"ATTACH DATABASE @path AS {SourceSchema};", new { path = sourcePath });
        try
        {
            // deferred: false is BEGIN IMMEDIATE. The async overload takes no such argument, so this
            // is the synchronous one -- it only writes the BEGIN, which is not an I/O wait worth
            // an await.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var report = await CopyAsync(connection, transaction, SourceSchema, destinationProjectId, selection, onClash);
            await transaction.CommitAsync(ct);
            return report;
        }
        finally
        {
            await connection.ExecuteAsync($"DETACH DATABASE {SourceSchema};");
        }
    }

    /// <summary>
    /// The row work, inside a transaction the caller owns. Separate from
    /// <see cref="TransferAsync"/> only so a test can drive it against two connections it set up
    /// itself; production always goes through the method above.
    /// </summary>
    internal static async Task<TransferReport> CopyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceSchema,
        long destinationProjectId,
        IReadOnlyList<TransferSelection> selection,
        TypeNameClash onClash)
    {
        if (selection.Count == 0)
            return new TransferReport([], 0, 0);

        // Both schema names are interpolated into the statements below. `sourceSchema` is a constant
        // supplied by the caller ("src"), and every table name comes from Plans or
        // CaptureCascade.ChildTables, which are compile-time constants in this assembly. Nothing
        // from a request reaches SQL except through a bound parameter.
        string src = sourceSchema;

        await CreateScratchTablesAsync(connection, transaction);
        try
        {
            var placed = await ResolveTypesAsync(connection, transaction, src, destinationProjectId, selection, onClash);

            // The picks are filled only for measurements that were actually placed, so a Skip needs
            // no second pass to take its results back out again. The statement also validates: a
            // result id reaches the temp table only if it really belongs to that measurement in the
            // source and is not in its trash. INSERT OR IGNORE against the primary key makes a
            // duplicated id in the request harmless rather than a UNIQUE failure on a copied row.
            foreach (var item in selection)
            {
                if (!placed.TryGetValue(item.SourceTypeId, out long _) || item.SourceResultIds.Count == 0)
                    continue;

                await connection.ExecuteAsync(
                    $"""
                    INSERT OR IGNORE INTO temp.transfer_pick (src_type_id, src_result_id)
                    SELECT r.measurement_type_id, r.id
                    FROM {src}.measurement_result r
                    WHERE r.measurement_type_id = @typeId AND r.deleted_utc IS NULL AND r.id IN @resultIds;
                    """,
                    new { typeId = item.SourceTypeId, resultIds = item.SourceResultIds }, transaction);
            }

            long resultOffset = await MaxIdAsync(connection, transaction, "measurement_result");
            long captureOffset = await MaxIdAsync(connection, transaction, "capture");

            int results = await CopyResultsAsync(connection, transaction, src, resultOffset);
            int captures = results == 0
                ? 0
                : await CopyCapturesAsync(connection, transaction, src, resultOffset, captureOffset);

            if (captures > 0)
            {
                foreach (string table in CaptureCascade.ChildTables)
                    await CopyChildAsync(connection, transaction, src, table, captureOffset);
            }

            return new TransferReport(
                [.. selection.Select(s => placed.Report(s.SourceTypeId))],
                results,
                captures);
        }
        finally
        {
            // The connection is unpooled and these would go with it, but a caller that reuses it
            // (a second transfer, a test) must not inherit a half-filled selection.
            await connection.ExecuteAsync("DROP TABLE IF EXISTS temp.transfer_pick; DROP TABLE IF EXISTS temp.transfer_type;", transaction: transaction);
        }
    }

    private static Task CreateScratchTablesAsync(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteAsync(
            """
            DROP TABLE IF EXISTS temp.transfer_pick;
            DROP TABLE IF EXISTS temp.transfer_type;

            -- src_result_id is the primary key on its own: a result belongs to exactly one
            -- measurement, so this both deduplicates a repeated id in the request and makes the
            -- joins below one-to-one, which is what keeps the copied ids unique.
            CREATE TABLE temp.transfer_pick (
              src_type_id INTEGER NOT NULL,
              src_result_id INTEGER NOT NULL PRIMARY KEY);

            CREATE TABLE temp.transfer_type (
              src_type_id INTEGER NOT NULL PRIMARY KEY,
              dst_type_id INTEGER NOT NULL);
            """,
            transaction: transaction);

    /// <summary>
    /// Places each picked measurement in the destination — created (possibly renamed), merged into
    /// one that is already there, or skipped — and records the mapping in <c>temp.transfer_type</c>.
    /// Row by row, because a merge maps to an existing id rather than translating one and because
    /// the appended <c>sort_order</c> has to be re-read after each insert.
    /// </summary>
    private static async Task<PlacedTypes> ResolveTypesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string src,
        long destinationProjectId, IReadOnlyList<TransferSelection> selection, TypeNameClash onClash)
    {
        var plan = PlanFor("measurement_type");
        string carried = string.Join(", ", plan.Columns.Select(Quote));
        var placed = new PlacedTypes();

        foreach (var item in selection)
        {
            var source = await connection.QuerySingleOrDefaultAsync<SourceTypeRaw>(
                $"SELECT id AS Id, kind AS Kind, name AS Name FROM {src}.measurement_type WHERE id = @id AND deleted_utc IS NULL;",
                new { id = item.SourceTypeId }, transaction);

            if (source is null)
                throw new InvalidOperationException($"Measurement {item.SourceTypeId} is not in the file being copied from.");

            var existing = await connection.QuerySingleOrDefaultAsync<SourceTypeRaw>(
                """
                SELECT id AS Id, kind AS Kind, name AS Name FROM main.measurement_type
                WHERE project_id = @projectId AND name = @name AND deleted_utc IS NULL;
                """,
                new { projectId = destinationProjectId, name = source.Name }, transaction);

            var policy = item.OnClash ?? onClash;

            if (existing is not null && policy == TypeNameClash.Skip)
            {
                placed.Skip(source.Id, source.Name);
                continue;
            }

            if (existing is not null && policy == TypeNameClash.Merge)
            {
                // Never a silent fall back to Rename. An FFT result sitting under a sweep
                // measurement is a NullReferenceException 500 out of
                // MeasurementSaveService.ApplyAsync the moment that measurement is merely selected,
                // so a mismatch has to stop the transfer and say what is there.
                if (!string.Equals(existing.Kind, source.Kind, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"'{source.Name}' cannot be merged: the measurement of that name here is a "
                            + $"{existing.Kind} and the one being copied is a {source.Kind}.");
                }

                await connection.ExecuteAsync(
                    "INSERT INTO temp.transfer_type (src_type_id, dst_type_id) VALUES (@src, @dst);",
                    new { src = source.Id, dst = existing.Id }, transaction);
                placed.Merge(source.Id, source.Name, existing.Id);
                continue;
            }

            string name = existing is null
                ? source.Name
                : await FreeNameAsync(connection, transaction, destinationProjectId, source.Name);

            // sort_order is read with its own statement and bound, rather than as a subquery inside
            // the insert, for the reason this class's remarks give about reading a table the same
            // statement writes. MAX counts trashed rows deliberately, matching
            // ProjectRepository.CreateTypeAsync: a restored measurement keeps the number it had, so
            // excluding the trash would hand it out twice.
            long sortOrder = await connection.ExecuteScalarAsync<long>(
                "SELECT COALESCE(MAX(sort_order) + 1, 0) FROM main.measurement_type WHERE project_id = @projectId;",
                new { projectId = destinationProjectId }, transaction);

            await connection.ExecuteAsync(
                $"""
                INSERT INTO main.measurement_type (project_id, "name", sort_order, deleted_utc, {carried})
                SELECT @projectId, @name, @sortOrder, NULL, {carried}
                FROM {src}.measurement_type WHERE id = @id;
                """,
                new { projectId = destinationProjectId, name, sortOrder, id = source.Id }, transaction);

            long destinationId = await connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();", transaction: transaction);

            await connection.ExecuteAsync(
                "INSERT INTO temp.transfer_type (src_type_id, dst_type_id) VALUES (@src, @dst);",
                new { src = source.Id, dst = destinationId }, transaction);
            placed.Create(source.Id, source.Name, destinationId, name);
        }

        return placed;
    }

    /// <summary>
    /// The first of "Amp", "Amp (2)", "Amp (3)" … that no <i>live</i> measurement in the destination
    /// project has. Live only, matching <c>ix_measurement_type_name</c>'s own
    /// <c>WHERE deleted_utc IS NULL</c> — a name sitting in the trash does not clash, and treating
    /// it as if it did would rename around a measurement nobody can see. Names inserted earlier in
    /// this same transfer are already visible to this query, so they need no separate bookkeeping.
    /// </summary>
    private static async Task<string> FreeNameAsync(
        SqliteConnection connection, SqliteTransaction transaction, long projectId, string name)
    {
        for (int suffix = 2; suffix < MaxRenameAttempts; suffix++)
        {
            string candidate = $"{name} ({suffix})";
            long taken = await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM main.measurement_type
                WHERE project_id = @projectId AND name = @candidate AND deleted_utc IS NULL;
                """,
                new { projectId, candidate }, transaction);

            if (taken == 0)
                return candidate;
        }

        throw new InvalidOperationException($"Could not find a free name for '{name}'; rename or delete some of the copies already here.");
    }

    private static Task<long> MaxIdAsync(SqliteConnection connection, SqliteTransaction transaction, string table) =>
        connection.ExecuteScalarAsync<long>($"SELECT IFNULL(MAX(id), 0) FROM main.{table};", transaction: transaction);

    private static async Task<int> CopyResultsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string src, long resultOffset)
    {
        var plan = PlanFor("measurement_result");
        string carried = string.Join(", ", plan.Columns.Select(Quote));
        string selected = string.Join(", ", plan.Columns.Select(c => $"r.{Quote(c)}"));

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO main.measurement_result (id, measurement_type_id, deleted_utc, {carried})
            SELECT r.id + @resultOffset, t.dst_type_id, NULL, {selected}
            FROM {src}.measurement_result r
            JOIN temp.transfer_pick p ON p.src_result_id = r.id
            JOIN temp.transfer_type t ON t.src_type_id = p.src_type_id
            WHERE r.deleted_utc IS NULL;
            """,
            new { resultOffset }, transaction);
    }

    private static async Task<int> CopyCapturesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string src,
        long resultOffset, long captureOffset)
    {
        var plan = PlanFor("capture");
        string carried = string.Join(", ", plan.Columns.Select(Quote));
        string selected = string.Join(", ", plan.Columns.Select(c => $"c.{Quote(c)}"));

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO main.capture (id, session_id, measurement_result_id, {carried})
            SELECT c.id + @captureOffset, NULL, c.measurement_result_id + @resultOffset, {selected}
            FROM {src}.capture c
            JOIN temp.transfer_pick p ON p.src_result_id = c.measurement_result_id;
            """,
            new { resultOffset, captureOffset }, transaction);
    }

    /// <summary>
    /// One child table of <c>capture</c>. Its own <c>id</c> is omitted where it has one — nothing
    /// references these rows, so the destination allocating fresh ids is both correct and the thing
    /// that keeps a second copy of the same source from colliding.
    /// </summary>
    private static async Task CopyChildAsync(
        SqliteConnection connection, SqliteTransaction transaction, string src, string table,
        long captureOffset)
    {
        var plan = PlanFor(table);
        string carried = string.Join(", ", plan.Columns.Select(Quote));
        string selected = string.Join(", ", plan.Columns.Select(c => $"ch.{Quote(c)}"));

        await connection.ExecuteAsync(
            $"""
            INSERT INTO main.{table} (capture_id, {carried})
            SELECT ch.capture_id + @captureOffset, {selected}
            FROM {src}.{table} ch
            JOIN {src}.capture c ON c.id = ch.capture_id
            JOIN temp.transfer_pick p ON p.src_result_id = c.measurement_result_id;
            """,
            new { captureOffset }, transaction);
    }

    /// <summary>
    /// Double quotes, so a column named like an SQL function is unambiguous in an expression list.
    /// <c>capture_preview</c> has <c>min</c> and <c>max</c>, which is the case that needs it.
    /// </summary>
    private static string Quote(string column) => $"\"{column}\"";

    /// <summary>What happened to each picked measurement, in the order they were picked.</summary>
    private sealed class PlacedTypes
    {
        private readonly Dictionary<long, TransferredType> _byId = [];

        public void Create(long sourceId, string sourceName, long destinationId, string destinationName) =>
            _byId[sourceId] = new TransferredType(sourceId, sourceName, destinationId, destinationName, TransferOutcome.Created);

        public void Merge(long sourceId, string sourceName, long destinationId) =>
            _byId[sourceId] = new TransferredType(sourceId, sourceName, destinationId, sourceName, TransferOutcome.Merged);

        public void Skip(long sourceId, string sourceName) =>
            _byId[sourceId] = new TransferredType(sourceId, sourceName, null, null, TransferOutcome.Skipped);

        public bool TryGetValue(long sourceId, out long destinationId)
        {
            destinationId = 0;
            if (!_byId.TryGetValue(sourceId, out var placed) || placed.DestinationTypeId is null)
                return false;

            destinationId = placed.DestinationTypeId.Value;
            return true;
        }

        public TransferredType Report(long sourceId) => _byId[sourceId];
    }

    private sealed record SourceTypeRaw(long Id, string Kind, string Name);

    /// <summary>Dapper needs the stored types exactly: <c>sort_order</c> is INTEGER, so <c>long</c>.</summary>
    private sealed record ContentsTypeRaw(long Id, string Kind, string Name, string? Notes, long SortOrder);

    /// <summary><c>Ts</c> is the stored <see cref="TonebenchTimestamp"/> string, parsed by the caller.</summary>
    private sealed record ContentsResultRaw(long TypeId, long ResultId, string Name, string Ts, string? Source, long Captures);
}
