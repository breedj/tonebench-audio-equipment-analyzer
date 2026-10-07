using Dapper;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// The one hand-written delete cascade: a set of <c>measurement_result</c> rows, their captures, and
/// every child table of <c>capture</c>.
///
/// <para>Every child table has to be listed here by hand. Foreign keys are off (see
/// <see cref="SqliteConnectionFactory"/>), so a table missed from this cascade is orphaned silently
/// and then collides with a future capture that reuses the freed rowid — surfacing as
/// <c>UNIQUE constraint failed</c> on a save that has nothing wrong with it, and repeating on every
/// save until the orphans are deleted. That has happened twice, <c>capture_preview</c> (repaired by
/// migration 005) and <c>response_trace</c> (007), both times because the table was added after the
/// cascade was written. <b>A new child of <c>capture</c> belongs in the list below and in a repair
/// migration</b> — fixing the code is not enough, the rows already written need the migration.</para>
///
/// <para>It lives here, in one place, because there are now four callers:
/// <see cref="MeasurementRepository.DeleteResultAsync"/>, <see cref="ProjectRepository.DeleteTypeAsync"/>
/// and the trash's own permanent delete and purge (<see cref="TrashRepository"/>). It used to be
/// two copies of the same seven statements, which is precisely how a table comes to be in one list
/// and not the other.</para>
/// </summary>
internal static class CaptureCascade
{
    /// <summary>
    /// Deletes the given results' captures (and their spectra, metrics, previews, response traces
    /// and impulse rows) and then the result rows themselves. The caller owns the transaction, and
    /// nothing here touches <c>measurement_type</c>.
    /// </summary>
    public static async Task<int> DeleteResultsAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<long> resultIds)
    {
        // Dapper cannot expand an empty `IN @list`, so both counts are guarded rather than relying
        // on the statements being harmless no-ops.
        if (resultIds.Count == 0)
            return 0;

        var captureIds = (await connection.QueryAsync<long>(
            "SELECT id FROM capture WHERE measurement_result_id IN @resultIds;",
            new { resultIds }, transaction)).ToList();

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

        return await connection.ExecuteAsync(
            "DELETE FROM measurement_result WHERE id IN @resultIds;", new { resultIds }, transaction);
    }
}
