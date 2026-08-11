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
            INSERT INTO project (name, notes, created_utc) VALUES (@name, @notes, @createdUtc);
            SELECT last_insert_rowid();
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
                   params_json AS ParamsJson, created_utc AS CreatedUtc
            FROM measurement_type WHERE project_id = @projectId ORDER BY name;
            """,
            new { projectId });
        return rows
            .Select(r => new MeasurementTypeRow(r.Id, r.ProjectId, r.Kind, r.Name, r.Notes, r.ParamsJson, Qa403Timestamp.Parse(r.CreatedUtc)))
            .ToList();
    }

    public async Task<MeasurementTypeRow?> GetTypeAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<MeasurementTypeRowRaw>(
            """
            SELECT id AS Id, project_id AS ProjectId, kind AS Kind, name AS Name, notes AS Notes,
                   params_json AS ParamsJson, created_utc AS CreatedUtc
            FROM measurement_type WHERE id = @id;
            """,
            new { id });

        return row is null
            ? null
            : new MeasurementTypeRow(row.Id, row.ProjectId, row.Kind, row.Name, row.Notes, row.ParamsJson, Qa403Timestamp.Parse(row.CreatedUtc));
    }

    public async Task<long> CreateTypeAsync(long projectId, string kind, string name, string? notes, string paramsJson, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO measurement_type (project_id, kind, name, notes, params_json, created_utc)
            VALUES (@projectId, @kind, @name, @notes, @paramsJson, @createdUtc);
            SELECT last_insert_rowid();
            """,
            new { projectId, kind, name, notes, paramsJson, createdUtc = Qa403Timestamp.Now() });
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
                await connection.ExecuteAsync("DELETE FROM capture WHERE id IN @captureIds;", new { captureIds }, transaction);
            }

            await connection.ExecuteAsync("DELETE FROM measurement_result WHERE id IN @resultIds;", new { resultIds }, transaction);
        }

        int affected = await connection.ExecuteAsync("DELETE FROM measurement_type WHERE id = @id;", new { id }, transaction);

        await transaction.CommitAsync(ct);
        return affected > 0;
    }

    private sealed record ProjectRowRaw(long Id, string Name, string? Notes, string CreatedUtc);

    private sealed record MeasurementTypeRowRaw(
        long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, string CreatedUtc);
}
