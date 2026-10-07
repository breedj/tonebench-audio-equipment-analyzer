using Dapper;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data;

/// <summary>
/// The trash: what is in it, and the two ways things leave it for good.
///
/// <para>Its own repository because it spans both tables and belongs to neither — the marking and
/// unmarking of a single row live with their own rows (<c>ProjectRepository.TrashTypeAsync</c> /
/// <c>RestoreTypeAsync</c>, <c>MeasurementRepository.TrashResultAsync</c> / <c>RestoreResultAsync</c>),
/// while listing, emptying and the retention purge are about the trash as a whole.</para>
///
/// <para>Nothing here writes a cascade of its own: every permanent delete goes through
/// <see cref="CaptureCascade"/>, which is the single list of <c>capture</c>'s child tables.</para>
/// </summary>
public sealed class TrashRepository(SqliteConnectionFactory connectionFactory)
{
    /// <summary>
    /// Everything in the project's trash, as measurements with their individually-trashed results
    /// nested under them. A measurement that is <em>not</em> in the trash still appears when it has
    /// trashed results, with a null <c>DeletedUtc</c>.
    /// </summary>
    public async Task<TrashListing> ListAsync(long projectId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        // Every measurement that is either in the trash itself or has a result in it.
        //
        // Nothing but plain table columns is selected, here or below, and the two counts this
        // listing needs are worked out in C# from those rows instead. Correlated subqueries and
        // COUNT(*)s have no declared type, so Dapper materializes them as byte[] whenever the
        // result set does not happen to type them from its values — and an *empty* result set never
        // does, which is the common case here: an empty trash. The failure is the one
        // MeasurementRepository.ListResultsAsync documents, "a parameterless default constructor is
        // required", naming nothing that is wrong. EXISTS inside a WHERE is fine; it returns no
        // column.
        var typeRows = (await connection.QueryAsync<TrashTypeRowRaw>(
            """
            SELECT mt.id AS Id, mt.name AS Name, mt.kind AS Kind, mt.deleted_utc AS DeletedUtc
            FROM measurement_type mt
            WHERE mt.project_id = @projectId
              AND (mt.deleted_utc IS NOT NULL
                   OR EXISTS (SELECT 1 FROM measurement_result r
                               WHERE r.measurement_type_id = mt.id AND r.deleted_utc IS NOT NULL))
            ORDER BY mt.deleted_utc IS NULL, mt.deleted_utc DESC, mt.sort_order, mt.id;
            """,
            new { projectId })).ToList();

        if (typeRows.Count == 0)
            return new TrashListing([], 0);

        long[] typeIds = typeRows.Select(t => t.Id).ToArray();

        // Every result under those measurements, trashed or not: the trashed ones become the nested
        // entries, and the rest are what a restore of the measurement would bring back.
        var resultRows = (await connection.QueryAsync<TrashResultRowRaw>(
            """
            SELECT id AS Id, measurement_type_id AS MeasurementTypeId, name AS Name,
                   notes AS Notes, ts AS TimestampUtc, deleted_utc AS DeletedUtc
            FROM measurement_result
            WHERE measurement_type_id IN @typeIds
            ORDER BY deleted_utc DESC, id;
            """,
            new { typeIds })).ToList();

        // The names already taken, so the view can say which restores are refused rather than
        // offering one that fails. See ProjectRepository.RestoreTypeAsync.
        var liveNames = (await connection.QueryAsync<string>(
            """
            SELECT name FROM measurement_type
            WHERE project_id = @projectId AND deleted_utc IS NULL;
            """,
            new { projectId })).ToHashSet(StringComparer.Ordinal);

        var byType = resultRows.ToLookup(r => r.MeasurementTypeId);

        var types = typeRows.Select(t => new TrashTypeEntry(
            t.Id,
            t.Name,
            t.Kind,
            t.DeletedUtc is null ? null : TonebenchTimestamp.Parse(t.DeletedUtc),
            byType[t.Id].Count(r => r.DeletedUtc is null),
            // A measurement in the trash whose name a live one has taken cannot be restored until
            // one of them is renamed. A trashed measurement is not in liveNames itself, so a hit
            // really is someone else.
            NameTaken: t.DeletedUtc is not null && liveNames.Contains(t.Name),
            byType[t.Id]
                .Where(r => r.DeletedUtc is not null)
                .Select(r => new TrashResultEntry(
                    r.Id, r.Name, r.Notes,
                    TonebenchTimestamp.Parse(r.TimestampUtc),
                    TonebenchTimestamp.Parse(r.DeletedUtc!)))
                .ToList())).ToList();

        // What the sidebar's badge counts: the measurements actually in the trash, plus every
        // trashed result. A header row for a live measurement is not itself an item.
        int total = types.Count(t => t.DeletedUtc is not null) + types.Sum(t => t.Results.Count);
        return new TrashListing(types, total);
    }

    /// <summary>Permanently deletes everything in the project's trash.</summary>
    public async Task<TrashPurgeReport> EmptyAsync(long projectId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        return await PurgeAsync(connection, projectId: projectId, cutoffUtc: null);
    }

    /// <summary>
    /// Permanently deletes everything that has been in the trash since before
    /// <paramref name="cutoffUtc"/>, in every project the file holds.
    ///
    /// <para>Static, taking a connection, following this assembly's convention for a file that is
    /// not (yet) the current one: this runs inside <c>ProjectSessionService.OpenAsync</c>, on the
    /// connection that just migrated the file, before the project is adopted and visible to any
    /// request. The hosted services cannot do it — no project file is open when they start.</para>
    /// </summary>
    public static Task<TrashPurgeReport> PurgeOlderThanAsync(SqliteConnection connection, DateTimeOffset cutoffUtc) =>
        PurgeAsync(connection, projectId: null, cutoffUtc);

    private static async Task<TrashPurgeReport> PurgeAsync(
        SqliteConnection connection, long? projectId, DateTimeOffset? cutoffUtc)
    {
        // deleted_utc is a fixed-width ISO-8601 UTC string, so "older than" is a plain string
        // compare -- the whole reason TonebenchTimestamp's format is what it is.
        string? cutoff = cutoffUtc is { } c ? TonebenchTimestamp.FormatUtc(c) : null;

        await using var transaction = connection.BeginTransaction();

        // Expired measurements first, which take every result under them whether that result was
        // trashed on its own or not: the measurement is going, so there is nothing left to own one.
        var typeIds = (await connection.QueryAsync<long>(
            """
            SELECT id FROM measurement_type
            WHERE deleted_utc IS NOT NULL
              AND (@projectId IS NULL OR project_id = @projectId)
              AND (@cutoff IS NULL OR deleted_utc < @cutoff);
            """,
            new { projectId, cutoff }, transaction)).ToList();

        int types = 0;
        int results = 0;

        if (typeIds.Count > 0)
        {
            var resultIds = (await connection.QueryAsync<long>(
                "SELECT id FROM measurement_result WHERE measurement_type_id IN @typeIds;",
                new { typeIds }, transaction)).ToList();

            results += await CaptureCascade.DeleteResultsAsync(connection, transaction, resultIds);
            types = await connection.ExecuteAsync(
                "DELETE FROM measurement_type WHERE id IN @typeIds;", new { typeIds }, transaction);
        }

        // Then the results trashed on their own, under measurements that are staying. Anything
        // under a measurement just deleted is already gone, so this cannot double-count.
        var loneResultIds = (await connection.QueryAsync<long>(
            """
            SELECT mr.id FROM measurement_result mr
            JOIN measurement_type mt ON mt.id = mr.measurement_type_id
            WHERE mr.deleted_utc IS NOT NULL
              AND (@projectId IS NULL OR mt.project_id = @projectId)
              AND (@cutoff IS NULL OR mr.deleted_utc < @cutoff);
            """,
            new { projectId, cutoff }, transaction)).ToList();

        results += await CaptureCascade.DeleteResultsAsync(connection, transaction, loneResultIds);

        await transaction.CommitAsync();
        return new TrashPurgeReport(types, results);
    }

    // INTEGER affinity means long, not int: Dapper's constructor binding needs an exact type match.
    // Both of these select plain table columns only -- see the remarks in ListAsync.
    private sealed record TrashTypeRowRaw(long Id, string Name, string Kind, string? DeletedUtc);

    private sealed record TrashResultRowRaw(
        long Id, long MeasurementTypeId, string Name, string? Notes, string TimestampUtc, string? DeletedUtc);
}
