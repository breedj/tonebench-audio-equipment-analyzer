using Microsoft.Data.Sqlite;
using Qa403.App.Services;
using Qa403.Data;

namespace Qa403.App.Endpoints;

public static class ProjectsEndpoints
{
    public static void MapProjectsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("", async (ProjectRepository repo, CancellationToken ct) =>
        {
            var projects = await repo.ListProjectsAsync(ct);
            return Results.Ok(projects.Select(ToResponse));
        });

        group.MapPost("", async (SaveProjectRequest request, ProjectRepository repo, CancellationToken ct) =>
        {
            try
            {
                long id = await repo.CreateProjectAsync(request.Name, request.Notes, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (SqliteException)
            {
                return Results.Conflict($"A project named '{request.Name}' already exists.");
            }
        });

        group.MapPut("/{id:long}", async (long id, SaveProjectRequest request, ProjectRepository repo, CancellationToken ct) =>
        {
            bool updated = await repo.UpdateProjectAsync(id, request.Name, request.Notes, ct);
            return updated ? Results.Ok() : Results.NotFound();
        });

        group.MapDelete("/{id:long}", async (long id, ProjectRepository repo, CancellationToken ct) =>
        {
            try
            {
                bool deleted = await repo.DeleteProjectAsync(id, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapGet("/{id:long}/measurement-types", async (long id, ProjectRepository repo, CancellationToken ct) =>
        {
            var types = await repo.ListTypesAsync(id, ct);
            return Results.Ok(types.Select(ToResponse));
        });

        // An omitted ParamsJson defaults to a snapshot of whatever the pipeline/device is currently
        // configured to — every FftSpectrumParameters property is required, so an empty object
        // would fail to deserialize the first time ApplyAsync tried to load this type back, making
        // "Apply" useless for anything created without params. Staged carries the generator/
        // acquisition/range fields the UI has edited but not yet pushed to the device (see
        // AcquisitionPanel's "stage until Run" panel) — without it, saving before ever pressing Run
        // would silently persist stale defaults instead of what's actually showing on screen.
        group.MapPost("/{id:long}/measurement-types", async (
            long id, SaveMeasurementTypeRequest request, ProjectRepository repo, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                string paramsJson = string.IsNullOrWhiteSpace(request.ParamsJson)
                    ? saveService.BuildCurrentParametersJson(request.Staged)
                    : request.ParamsJson;
                long typeId = await repo.CreateTypeAsync(id, request.Kind, request.Name, request.Notes, paramsJson, ct);
                return Results.Ok(new IdResponse(typeId));
            }
            catch (SqliteException)
            {
                return Results.Conflict($"A measurement type named '{request.Name}' already exists in this project.");
            }
        });

        var typeGroup = app.MapGroup("/api/measurement-types");

        // Same omitted-ParamsJson fallback as create, above: the "Save" button sends only
        // {kind, name, notes, staged} and relies on this to snapshot the live spectrum options
        // together with whatever's staged in the Acquisition/Generator panels. A bare "{}" default
        // here would silently wipe out the type's params instead.
        typeGroup.MapPut("/{id:long}", async (
            long id, SaveMeasurementTypeRequest request, ProjectRepository repo, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            string paramsJson = string.IsNullOrWhiteSpace(request.ParamsJson)
                ? saveService.BuildCurrentParametersJson(request.Staged)
                : request.ParamsJson;
            bool updated = await repo.UpdateTypeAsync(id, request.Name, request.Notes, paramsJson, ct);
            return updated ? Results.Ok() : Results.NotFound();
        });

        // Deletes the type's own results (and their captures/spectra/metrics) along with it — see
        // ProjectRepository.DeleteTypeAsync — rather than rejecting while any exist.
        typeGroup.MapDelete("/{id:long}", async (long id, ProjectRepository repo, CancellationToken ct) =>
        {
            bool deleted = await repo.DeleteTypeAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // Pushes this type's stored parameters (FFT size, window, averaging, band, ranges) into the
        // live pipeline/device and returns its generator/acquisition settings for the UI to stage —
        // "load this configured measurement", not "start playing it". See MeasurementSaveService.ApplyAsync.
        typeGroup.MapPost("/{id:long}/apply", async (long id, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                var response = await saveService.ApplyAsync(id, ct);
                return Results.Ok(response);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
        });
    }

    private static ProjectResponse ToResponse(ProjectRow row) => new(row.Id, row.Name, row.Notes, row.CreatedUtc);

    private static MeasurementTypeResponse ToResponse(MeasurementTypeRow row) =>
        new(row.Id, row.ProjectId, row.Kind, row.Name, row.Notes, row.ParamsJson, row.CreatedUtc);
}

public sealed record IdResponse(long Id);

public sealed record SaveProjectRequest(string Name, string? Notes);

public sealed record ProjectResponse(long Id, string Name, string? Notes, DateTimeOffset CreatedUtc);

public sealed record SaveMeasurementTypeRequest(string Kind, string Name, string? Notes, string? ParamsJson, StagedMeasurementConfig? Staged = null);

/// <summary>
/// The Acquisition/Generator panels' current staged (possibly not-yet-applied-to-the-device)
/// values, sent along with a measurement-type save so params_json reflects what's on screen rather
/// than whatever the device last actually ran — see MeasurementSaveService.BuildParameters. Reuses
/// GeneratorEndpoints' own channel request shape and mapping (GeneratorEndpoints.TryMapChannel)
/// rather than a second, drifting copy of it.
/// </summary>
public sealed record StagedMeasurementConfig(
    double SampleRateHz, int ChannelCount, double InputRangeDbv, bool? AutoRangeEnabled,
    GeneratorChannelRequest GeneratorLeft, GeneratorChannelRequest GeneratorRight, bool? SnapToBin,
    IReadOnlyList<string>? SelectedMetrics = null);

public sealed record MeasurementTypeResponse(
    long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, DateTimeOffset CreatedUtc);

/// <summary>
/// What a measurement type's stored generator/acquisition config actually is, for the UI to load
/// into its forms — same wire shape as live device status (<see cref="GeneratorChannelResponse"/>)
/// so the frontend's existing form-sync logic works from either source.
/// </summary>
public sealed record MeasurementTypeAppliedResponse(
    GeneratorChannelResponse GeneratorLeft,
    GeneratorChannelResponse GeneratorRight,
    bool SnapToBin,
    double SampleRateHz,
    int ChannelCount,
    int BlockFrameCount,
    double InputRangeDbv,
    double OutputRangeDbv,
    bool? AutoRangeEnabled,
    IReadOnlyList<string>? SelectedMetrics);
