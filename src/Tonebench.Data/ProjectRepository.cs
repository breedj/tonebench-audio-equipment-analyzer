using Dapper;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// The project and its configured measurement-types. Dapper throughout -- metadata only, no BLOBs.
///
/// <para>A project is one file, and the file holds exactly one <c>project</c> row (the schema's
/// <c>project_id</c> columns predate that and are kept). So there is no list and no delete here:
/// listing projects is the recent-projects list and deleting one is deleting its file, both the
/// project session's business. The static overloads that take a connection exist for that session,
/// which works on files that are not (yet) the current one.</para>
/// </summary>
public sealed class ProjectRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task<long> CreateProjectAsync(string name, string? notes, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await CreateProjectAsync(connection, name, notes);
    }

    public static Task<long> CreateProjectAsync(SqliteConnection connection, string name, string? notes) =>
        connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO project (name, notes, created_utc) VALUES (@name, @notes, @createdUtc)
            RETURNING id;
            """,
            new { name, notes, createdUtc = TonebenchTimestamp.Now() });

    /// <summary>Every project row in the file — one for a project file, several for a legacy <c>qa403.db</c>.</summary>
    public static async Task<IReadOnlyList<ProjectRow>> ListProjectsAsync(SqliteConnection connection)
    {
        var rows = await connection.QueryAsync<ProjectRowRaw>(
            "SELECT id AS Id, name AS Name, notes AS Notes, created_utc AS CreatedUtc FROM project ORDER BY id;");
        return rows.Select(r => new ProjectRow(r.Id, r.Name, r.Notes, TonebenchTimestamp.Parse(r.CreatedUtc))).ToList();
    }

    public async Task<bool> UpdateProjectAsync(long id, string name, string? notes, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await UpdateProjectAsync(connection, id, name, notes);
    }

    public static async Task<bool> UpdateProjectAsync(SqliteConnection connection, long id, string name, string? notes)
    {
        int affected = await connection.ExecuteAsync(
            "UPDATE project SET name = @name, notes = @notes WHERE id = @id;",
            new { id, name, notes });
        return affected > 0;
    }

    /// <summary>
    /// Copies the measurement types of one project into another file — Save As without results.
    /// The order is preserved by copying <c>sort_order</c> as is, which is what the sidebar sorts on;
    /// ids are not, since the destination allocates its own.
    /// </summary>
    public static async Task CopyTypesAsync(SqliteConnection from, long fromProjectId, SqliteConnection to, long toProjectId)
    {
        var rows = await from.QueryAsync<MeasurementTypeRowRaw>(
            """
            SELECT id AS Id, project_id AS ProjectId, kind AS Kind, name AS Name, notes AS Notes,
                   params_json AS ParamsJson, sort_order AS SortOrder, created_utc AS CreatedUtc
            FROM measurement_type WHERE project_id = @fromProjectId ORDER BY sort_order, id;
            """,
            new { fromProjectId });

        foreach (var row in rows)
        {
            await to.ExecuteAsync(
                """
                INSERT INTO measurement_type (project_id, kind, name, notes, params_json, sort_order, created_utc)
                VALUES (@projectId, @kind, @name, @notes, @paramsJson, @sortOrder, @createdUtc);
                """,
                new
                {
                    projectId = toProjectId, kind = row.Kind, name = row.Name, notes = row.Notes,
                    paramsJson = row.ParamsJson, sortOrder = row.SortOrder, createdUtc = row.CreatedUtc,
                });
        }
    }

    public async Task<IReadOnlyList<MeasurementTypeRow>> ListTypesAsync(long projectId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var rows = await connection.QueryAsync<MeasurementTypeRowRaw>(
            """
            SELECT id AS Id, project_id AS ProjectId, kind AS Kind, name AS Name, notes AS Notes,
                   params_json AS ParamsJson, sort_order AS SortOrder, created_utc AS CreatedUtc
            FROM measurement_type WHERE project_id = @projectId ORDER BY sort_order, id;
            """,
            new { projectId });
        return rows.Select(ToRow).ToList();
    }

    public async Task<MeasurementTypeRow?> GetTypeAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<MeasurementTypeRowRaw>(
            """
            SELECT id AS Id, project_id AS ProjectId, kind AS Kind, name AS Name, notes AS Notes,
                   params_json AS ParamsJson, sort_order AS SortOrder, created_utc AS CreatedUtc
            FROM measurement_type WHERE id = @id;
            """,
            new { id });

        return row is null ? null : ToRow(row);
    }

    /// <summary>Appends to the end of the project's ordered list -- see ReorderTypesAsync.</summary>
    public async Task<long> CreateTypeAsync(long projectId, string kind, string name, string? notes, string paramsJson, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO measurement_type (project_id, kind, name, notes, params_json, sort_order, created_utc)
            VALUES (@projectId, @kind, @name, @notes, @paramsJson,
                    (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM measurement_type WHERE project_id = @projectId),
                    @createdUtc)
            RETURNING id;
            """,
            new { projectId, kind, name, notes, paramsJson, createdUtc = TonebenchTimestamp.Now() });
    }

    /// <summary>
    /// Rewrites the project's list order from <paramref name="orderedIds"/> -- position in that
    /// list becomes sort_order. Rejects a list that is not exactly the project's own type ids
    /// (any missing, extra, foreign or duplicated id), because a partial rewrite would leave two
    /// types sharing a sort_order and the list order then decided by the id tiebreak instead of
    /// by the user's drag.
    /// </summary>
    public async Task<bool> ReorderTypesAsync(long projectId, IReadOnlyList<long> orderedIds, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var existing = (await connection.QueryAsync<long>(
            "SELECT id FROM measurement_type WHERE project_id = @projectId;", new { projectId })).ToHashSet();

        if (existing.Count == 0) return false;
        if (orderedIds.Count != existing.Count || !orderedIds.ToHashSet().SetEquals(existing))
            throw new InvalidOperationException(
                $"Reorder must list every measurement type in project {projectId} exactly once.");

        await using var transaction = connection.BeginTransaction();
        for (int i = 0; i < orderedIds.Count; i++)
        {
            await connection.ExecuteAsync(
                "UPDATE measurement_type SET sort_order = @sortOrder WHERE id = @id AND project_id = @projectId;",
                new { sortOrder = i, id = orderedIds[i], projectId }, transaction);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> UpdateTypeAsync(long id, string name, string? notes, string paramsJson, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        int affected = await connection.ExecuteAsync(
            "UPDATE measurement_type SET name = @name, notes = @notes, params_json = @paramsJson WHERE id = @id;",
            new { id, name, notes, paramsJson });
        return affected > 0;
    }

    /// <summary>
    /// How many results are stored under the type -- what the sidebar's delete confirmation names,
    /// since that is the part of what a delete destroys that the row itself does not show.
    ///
    /// <para>Counted here rather than carried on <see cref="ListTypesAsync"/>: that list is reloaded
    /// when a <em>type</em> is created, saved, deleted or reordered and never when a <em>result</em>
    /// is saved, so a count riding on it would read 0 for a measurement whose five results were
    /// stored since the project was opened -- stale in exactly the direction that makes the
    /// confirmation reassuring and wrong. And a count over the existing results listing would be
    /// capped by its own page size (take defaults to 100), which under-reports the same way.</para>
    /// </summary>
    public async Task<int> CountTypeResultsAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM measurement_result WHERE measurement_type_id = @id;", new { id });
    }

    /// <summary>
    /// Deletes the type together with all of its results and their captures/spectra/metrics, in one
    /// transaction -- same cascade MeasurementRepository.DeleteResultAsync does for a single result,
    /// just scoped to every result under this type instead of one.
    /// </summary>
    public async Task<bool> DeleteTypeAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        var resultIds = (await connection.QueryAsync<long>(
            "SELECT id FROM measurement_result WHERE measurement_type_id = @id;", new { id }, transaction)).ToList();

        if (resultIds.Count > 0)
        {
            var captureIds = (await connection.QueryAsync<long>(
                "SELECT id FROM capture WHERE measurement_result_id IN @resultIds;", new { resultIds }, transaction)).ToList();

            if (captureIds.Count > 0)
            {
                await connection.ExecuteAsync("DELETE FROM metric WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
                await connection.ExecuteAsync("DELETE FROM spectrum WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
                await connection.ExecuteAsync("DELETE FROM capture_preview WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
                await connection.ExecuteAsync("DELETE FROM response_trace WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
                await connection.ExecuteAsync("DELETE FROM impulse_result WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
                await connection.ExecuteAsync("DELETE FROM impulse_harmonic WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
                await connection.ExecuteAsync("DELETE FROM capture WHERE id IN @captureIds;", new { captureIds }, transaction);
            }

            await connection.ExecuteAsync("DELETE FROM measurement_result WHERE id IN @resultIds;", new { resultIds }, transaction);
        }

        int affected = await connection.ExecuteAsync("DELETE FROM measurement_type WHERE id = @id;", new { id }, transaction);

        await transaction.CommitAsync(ct);
        return affected > 0;
    }

    private static MeasurementTypeRow ToRow(MeasurementTypeRowRaw r) =>
        new(r.Id, r.ProjectId, r.Kind, r.Name, r.Notes, r.ParamsJson, (int)r.SortOrder, TonebenchTimestamp.Parse(r.CreatedUtc));

    private sealed record ProjectRowRaw(long Id, string Name, string? Notes, string CreatedUtc);

    private sealed record MeasurementTypeRowRaw(
        long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, long SortOrder, string CreatedUtc);
}
