using Dapper;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// The one hand-written delete cascade: a set of <c>measurement_result</c> rows, their captures, and
/// every child table of <c>capture</c>.
///
/// <para>Every child table has to be listed in <see cref="ChildTables"/> by hand. Foreign keys are
/// off (see <see cref="SqliteConnectionFactory"/>), so a table missed from this cascade is orphaned
/// silently and then collides with a future capture that reuses the freed rowid — surfacing as
/// <c>UNIQUE constraint failed</c> on a save that has nothing wrong with it, and repeating on every
/// save until the orphans are deleted. That has happened twice, <c>capture_preview</c> (repaired by
/// migration 005) and <c>response_trace</c> (007), both times because the table was added after the
/// cascade was written. <b>A new child of <c>capture</c> belongs in the list below and in a repair
/// migration</b> — fixing the code is not enough, the rows already written need the migration.</para>
///
/// <para>It lives here, in one place, because there are now five callers:
/// <see cref="MeasurementRepository.DeleteResultAsync"/>, <see cref="ProjectRepository.DeleteTypeAsync"/>,
/// the trash's own permanent delete and purge (<see cref="TrashRepository"/>), and
/// <see cref="ProjectTransferRepository"/>, which <i>copies</i> the same set of tables between two
/// project files. It used to be two copies of the same seven statements, which is precisely how a
/// table comes to be in one list and not the other — and a copier with its own list would be a
/// third.</para>
/// </summary>
internal static class CaptureCascade
{
    /// <summary>
    /// Every child table of <c>capture</c>, in delete order (<c>capture</c> itself goes last, and is
    /// not in this list). Also exactly what <see cref="ProjectTransferRepository"/> copies, so one
    /// list serves both directions and a seventh child cannot be in one and not the other.
    ///
    /// <para>The list alone does not make a forgotten table <i>fail</i>:
    /// <c>ProjectTransferRepositoryTests.ChildTables_IsEveryTableThatReferencesCapture</c> is what
    /// does, by asking <c>sqlite_master</c> which tables actually reference <c>capture(id)</c> on a
    /// migrated file and comparing the answer with this.</para>
    ///
    /// <para><c>impulse_reference</c> is deliberately absent and must stay absent. It hangs off
    /// nothing — it describes the analyzer, not a measurement — the table in a project file is
    /// deliberately empty (<c>ProjectSessionService.SaveAsAsync</c> even deletes its rows after a
    /// page-level copy), and the live loopback calibrations live in the configuration database. A
    /// copier that "completed" this list with it would carry one bench's analyzer response into
    /// another bench's file, which is what config migration 002 moved it out of the project file to
    /// prevent.</para>
    /// </summary>
    internal static readonly IReadOnlyList<string> ChildTables =
        ["metric", "spectrum", "capture_preview", "response_trace", "impulse_result", "impulse_harmonic"];

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
            // The table name is interpolated, which is safe and deliberate: every element of
            // ChildTables is a compile-time constant in this file, never anything from a request.
            foreach (string table in ChildTables)
                await connection.ExecuteAsync($"DELETE FROM {table} WHERE capture_id IN @captureIds;", new { captureIds }, transaction);

            await connection.ExecuteAsync("DELETE FROM capture WHERE id IN @captureIds;", new { captureIds }, transaction);
        }

        return await connection.ExecuteAsync(
            "DELETE FROM measurement_result WHERE id IN @resultIds;", new { resultIds }, transaction);
    }
}
