using Dapper;

namespace Qa403.Data;

/// <summary>Projects and their configured measurement-types. Dapper throughout -- metadata only, no BLOBs.</summary>
public sealed class ProjectRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<ProjectRow>> ListProjectsAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var rows = await connection.QueryAsync<ProjectRowRaw>(
            "SELECT id AS Id, name AS Name, notes AS Notes, created_utc AS CreatedUtc FROM project ORDER BY name;");
        return rows.Select(r => new ProjectRow(r.Id, r.Name, r.Notes, Qa403Timestamp.Parse(r.CreatedUtc))).ToList();
    }

    public async Task<long> CreateProjectAsync(string name, string? notes, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO project (name, notes, created_utc) VALUES (@name, @notes, @createdUtc)
            RETURNING id;
            """,
            new { name, notes, createdUtc = Qa403Timestamp.Now() });
    }

    public async Task<bool> UpdateProjectAsync(long id, string name, string? notes, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        int affected = await connection.ExecuteAsync(
            "UPDATE project SET name = @name, notes = @notes WHERE id = @id;",
            new { id, name, notes });
        return affected > 0;
    }

    /// <summary>Rejects deletion while the project still owns measurement-types -- 409 at the endpoint layer.</summary>
    public async Task<bool> DeleteProjectAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        long typeCount = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM measurement_type WHERE project_id = @id;", new { id });
        if (typeCount > 0)
            throw new InvalidOperationException($"Project {id} still has {typeCount} measurement type(s); delete those first.");

        int affected = await connection.ExecuteAsync("DELETE FROM project WHERE id = @id;", new { id });
        return affected > 0;
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
            new { projectId, kind, name, notes, paramsJson, createdUtc = Qa403Timestamp.Now() });
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
        new(r.Id, r.ProjectId, r.Kind, r.Name, r.Notes, r.ParamsJson, (int)r.SortOrder, Qa403Timestamp.Parse(r.CreatedUtc));

    private sealed record ProjectRowRaw(long Id, string Name, string? Notes, string CreatedUtc);

    private sealed record MeasurementTypeRowRaw(
        long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, long SortOrder, string CreatedUtc);
}
