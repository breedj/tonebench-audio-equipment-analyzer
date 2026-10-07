using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The trash. Deleting a measurement or a saved result marks it (see
/// <c>ProjectRepository.TrashTypeAsync</c>, <c>MeasurementRepository.TrashResultAsync</c>, reached
/// through the ordinary <c>DELETE /api/measurement-types/{id}</c> and <c>DELETE
/// /api/measurements/{id}</c>); this group is what the Trash view lists, restores and destroys.
///
/// <para><b>None of these is behind <see cref="MeasurementRunGuard"/>, deliberately.</b> A restore
/// is one UPDATE; a permanent delete removes rows that no running measurement holds a reference to,
/// through the same cascade an ordinary delete has always used mid-run; and the retention purge runs
/// inside <c>ProjectSessionService.OpenAsync</c>, which already refuses while anything is running.
/// A guard here would only stop someone tidying up while a long sweep runs, which is exactly when
/// they have time to.</para>
///
/// <para>No project open is already a 409 everywhere, from
/// <see cref="NoProjectOpenExceptionHandler"/> — every repository call raises it.</para>
/// </summary>
public static class TrashEndpoints
{
    public static void MapTrashEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/trash");

        // The project id comes from the open session rather than the route: a project file holds
        // exactly one project row, and the trash is per file.
        group.MapGet("", async (
            TrashRepository trash, ProjectSessionService session, CancellationToken ct) =>
        {
            // The same 409 every other no-project request gets, from the one place that wording
            // lives -- raising it rather than writing a second copy of the message here.
            long projectId = session.Current?.Id ?? throw new NoProjectOpenException();

            var listing = await trash.ListAsync(projectId, ct);
            return Results.Ok(ToResponse(listing));
        });

        group.MapPost("/measurement-types/{id:long}/restore", async (
            long id, ProjectRepository repo, CancellationToken ct) =>
        {
            var outcome = await repo.RestoreTypeAsync(id, ct);
            return outcome.Status switch
            {
                TypeRestoreStatus.Restored => Results.NoContent(),
                TypeRestoreStatus.NotFound => Results.NotFound(),
                // The partial unique index let a new measurement take the trashed one's name while
                // it sat here; one of the two has to be renamed first. See RestoreTypeAsync.
                _ => Results.Conflict(
                    $"A measurement named '{outcome.Name}' already exists. Rename it, or the one in the trash, before restoring."),
            };
        });

        group.MapPost("/measurements/{id:long}/restore", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            var status = await repo.RestoreResultAsync(id, ct);
            return status switch
            {
                ResultRestoreStatus.Restored => Results.NoContent(),
                ResultRestoreStatus.NotFound => Results.NotFound(),
                _ => Results.Conflict("Its measurement is in the trash too — restore that first."),
            };
        });

        // Permanent, one item. The confirmation is the client's (see trash.ts); there is nothing
        // this end can add to it.
        group.MapDelete("/measurement-types/{id:long}", async (
            long id, ProjectRepository repo, CancellationToken ct) =>
        {
            bool deleted = await repo.DeleteTypeAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/measurements/{id:long}", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            bool deleted = await repo.DeleteResultAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // Empty Trashcan. Returns what it destroyed rather than 204, because that is the one number
        // worth showing afterwards.
        group.MapDelete("", async (
            TrashRepository trash, ProjectSessionService session, CancellationToken ct) =>
        {
            long projectId = session.Current?.Id ?? throw new NoProjectOpenException();

            var report = await trash.EmptyAsync(projectId, ct);
            return Results.Ok(new TrashEmptiedResponse(report.MeasurementTypes, report.Results));
        });
    }

    private static TrashListingResponse ToResponse(TrashListing listing) =>
        new(
            listing.Types.Select(t => new TrashTypeResponse(
                t.MeasurementTypeId, t.Name, t.Kind, t.DeletedUtc, t.RestorableResultCount, t.NameTaken,
                t.Results.Select(r => new TrashResultResponse(
                    r.ResultId, r.Name, r.Notes, r.TimestampUtc, r.DeletedUtc)).ToList())).ToList(),
            listing.TotalCount);
}

/// <summary>
/// The trash as the two levels the view draws. A <see cref="TrashTypeResponse"/> with a null
/// <c>DeletedUtc</c> is a measurement that is still there, listed only as the parent of trashed
/// results — the view draws it as a header that cannot itself be restored.
/// </summary>
public sealed record TrashListingResponse(IReadOnlyList<TrashTypeResponse> Types, int TotalCount);

public sealed record TrashTypeResponse(
    long MeasurementTypeId,
    string Name,
    string Kind,
    DateTimeOffset? DeletedUtc,
    int RestorableResultCount,
    bool NameTaken,
    IReadOnlyList<TrashResultResponse> Results);

public sealed record TrashResultResponse(
    long ResultId, string Name, string? Notes, DateTimeOffset TimestampUtc, DateTimeOffset DeletedUtc);

public sealed record TrashEmptiedResponse(int MeasurementTypes, int Results);
