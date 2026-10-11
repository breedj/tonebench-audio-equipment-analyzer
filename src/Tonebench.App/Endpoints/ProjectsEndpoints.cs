using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Endpoints;

public static class ProjectsEndpoints
{
    public static void MapProjectsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        // The project itself — one per file — is ProjectSessionEndpoints' business (/api/project);
        // this group is its measurement types, addressed by the project id the file holds.
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
            long id, SaveMeasurementTypeRequest request, ProjectRepository repo, MeasurementSaveService saveService,
            ConfigRepository config, CancellationToken ct) =>
        {
            try
            {
                // A new impedance measurement starts on the global fixture calibration when there is
                // one. Only read for that kind: it is a second database round trip, and every other
                // kind ignores the answer.
                bool globalCalibration = MeasurementSaveService.IsImpedanceKind(request.Kind)
                    && await HasGlobalCalibrationAsync(config, ct);

                string paramsJson = saveService.ResolveTypeParamsJson(
                    request.ParamsJson, request.Kind, existingParamsJson: null, request.Staged, globalCalibration);
                long typeId = await repo.CreateTypeAsync(id, request.Kind, request.Name, request.Notes, paramsJson, ct);
                return Results.Ok(new IdResponse(typeId));
            }
            catch (SqliteException)
            {
                return Results.Conflict($"A measurement type named '{request.Name}' already exists in this project.");
            }
        });

        // The sidebar's drag-drop reorder. Whole-list rewrite rather than a per-type "move to
        // index": the client already holds the full ordered list it just rendered, and a partial
        // update would leave two types sharing a sort_order -- see ProjectRepository.ReorderTypesAsync.
        group.MapPut("/{id:long}/measurement-types/order", async (
            long id, ReorderMeasurementTypesRequest request, ProjectRepository repo, CancellationToken ct) =>
        {
            try
            {
                bool reordered = await repo.ReorderTypesAsync(id, request.Ids, ct);
                return reordered ? Results.Ok() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
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
            // Read first, so an omitted ParamsJson on a sweep type can fall back to what it already
            // has rather than to a live-state snapshot that would describe a different measurement
            // entirely — see MeasurementSaveService.ResolveTypeParamsJson. The sweep screen always
            // sends its own params (App.vue's buildTypeParamsJson); the fallback is the path a bulk
            // project save takes for a dirty sweep type that isn't the open one.
            var existing = await repo.GetTypeAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            string paramsJson = saveService.ResolveTypeParamsJson(
                request.ParamsJson, existing.Kind, existing.ParamsJson, request.Staged);

            bool updated = await repo.UpdateTypeAsync(id, request.Name, request.Notes, paramsJson, ct);
            return updated ? Results.Ok() : Results.NotFound();
        });

        // How many results the delete below would take with the type. Its own endpoint rather than a
        // column on the type listing, and read fresh at the moment the question is asked — see
        // ProjectRepository.CountTypeResultsAsync for why a count carried on that list is stale in
        // the one direction that matters.
        typeGroup.MapGet("/{id:long}/result-count", async (long id, ProjectRepository repo, CancellationToken ct) =>
        {
            // A type that is already gone has no results to lose, so this is a 404 rather than a 0:
            // the caller is about to tell the user what a delete destroys, and "nothing" and "no
            // such measurement" are not the same answer.
            if (await repo.GetTypeAsync(id, ct) is null)
                return Results.NotFound();

            return Results.Ok(new MeasurementTypeResultCountResponse(await repo.CountTypeResultsAsync(id, ct)));
        });

        // Moves the type to the trash, results and all — see ProjectRepository.TrashTypeAsync, and
        // TrashEndpoints for what then lists and restores it. Nothing is destroyed here: the
        // permanent delete is DELETE /api/trash/measurement-types/{id}, and the retention purge.
        //
        // The route keeps its meaning rather than gaining a sibling, because there is one client
        // and one ordinary way to delete a measurement. There is deliberately no 409 for "still
        // has results": the sidebar asks first (useProjects.deleteMeasurementType, over the count
        // above), which is where that refusal would otherwise have to live.
        typeGroup.MapDelete("/{id:long}", async (long id, ProjectRepository repo, CancellationToken ct) =>
        {
            bool trashed = await repo.TrashTypeAsync(id, ct);
            return trashed ? Results.NoContent() : Results.NotFound();
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
        new(row.Id, row.ProjectId, row.Kind, row.Name, row.Notes, row.ParamsJson, row.SortOrder, row.CreatedUtc);

    /// <summary>
    /// Whether a usable global impedance fixture calibration is stored.
    ///
    /// <para>Tolerant of a configuration database that cannot be read or holds something that is not
    /// a fixture: the answer only decides a checkbox's starting position on a new measurement, so
    /// failing closed (no calibration, panel starts on its own standards) is right and throwing a 500
    /// out of "create a measurement" because of it would not be.</para>
    /// </summary>
    private static async Task<bool> HasGlobalCalibrationAsync(ConfigRepository config, CancellationToken ct)
    {
        try
        {
            string? json = await config.GetAsync(ConfigRepository.ImpedanceFixtureKey, ct);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            return JsonSerializer.Deserialize<ImpedanceFixtureConfig>(json, JsonSerializerOptions.Web)
                is { HasCalibration: true };
        }
        catch (Exception ex) when (ex is JsonException or SqliteException)
        {
            return false;
        }
    }
}

public sealed record IdResponse(long Id);

/// <summary>How many results are stored under a measurement type — see the result-count endpoint.</summary>
public sealed record MeasurementTypeResultCountResponse(int Count);

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
    IReadOnlyList<string>? SelectedMetrics = null, int? ScopePeriods = null,
    double? ChartYTopDbv = null, double? ChartYBottomDbv = null, double? ChartXFromHz = null, double? ChartXToHz = null,
    bool? ChartThickLines = null, bool? ChartShowDataPoints = null, bool? ChartShowMinMax = null, bool? ChartShowPeakHold = null,
    bool? ChartAutofitOnDone = null,
    bool? ChartShowWaterfall = null, bool? ChartShowOscilloscope = null, bool? ChartShowHarmonics = null, bool? ChartShowOtherPeaks = null,
    /// <summary>The generator panel's IMD template and analysis choice -- see <see cref="Tonebench.Data.FftSpectrumParameters.ImdPreset"/>. The tones themselves ride in the generator config.</summary>
    string? ImdPreset = null, string? ImdMethod = null, int? ImdMaxOrder = null,
    /// <summary>The DUT gain under the chart's unit dropdown, and that unit -- see <see cref="Tonebench.Data.FftSpectrumParameters.DutGainDb"/>.</summary>
    double? DutGainDb = null, string? ChartUnit = null, bool? ChartShowWeightingCurve = null,
    /// <summary>The unit the generator panel's level boxes are typed in -- see <see cref="Tonebench.Data.FftSpectrumParameters.GeneratorLevelUnit"/>. The tones themselves are always dBV.</summary>
    string? GeneratorLevelUnit = null,
    /// <summary>Whether the spectrum chart draws the live trace itself -- see <see cref="Tonebench.Data.FftSpectrumParameters.ChartShowFftTrace"/>.</summary>
    bool? ChartShowFftTrace = null,
    /// <summary>Whether the spectrum chart draws the harmonic markers -- see <see cref="Tonebench.Data.FftSpectrumParameters.ChartShowHarmonicMarkers"/>.</summary>
    bool? ChartShowHarmonicMarkers = null,
    /// <summary>How the two peak tables are sorted -- see <see cref="Tonebench.Data.FftSpectrumParameters.ChartHarmonicsSort"/>.</summary>
    string? ChartHarmonicsSort = null,
    string? ChartOtherPeaksSort = null);

public sealed record MeasurementTypeResponse(
    long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, int SortOrder, DateTimeOffset CreatedUtc);

/// <summary>Every measurement-type id in the project, in the order the sidebar should list them.</summary>
public sealed record ReorderMeasurementTypesRequest(IReadOnlyList<long> Ids);

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
    IReadOnlyList<string>? SelectedMetrics,
    int? ScopePeriods,
    double? ChartYTopDbv,
    double? ChartYBottomDbv,
    double? ChartXFromHz,
    double? ChartXToHz,
    bool? ChartThickLines,
    bool? ChartShowDataPoints,
    bool? ChartShowMinMax,
    bool? ChartShowPeakHold,
    bool? ChartAutofitOnDone,
    bool? ChartShowWaterfall,
    bool? ChartShowOscilloscope,
    bool? ChartShowHarmonics,
    bool? ChartShowOtherPeaks,
    string? ImdPreset,
    string? ImdMethod,
    int? ImdMaxOrder,
    /// <summary>The stored weighting, for the panel to show while a type is selected — see <see cref="Tonebench.Data.FftSpectrumParameters.Weighting"/>.</summary>
    string? Weighting = null,
    bool? WeightingUser1 = null,
    bool? WeightingUser2 = null,
    double? DutGainDb = null,
    string? ChartUnit = null,
    bool? ChartShowWeightingCurve = null,
    /// <summary>Whether the oscilloscope shows the THD residual — see <see cref="Tonebench.Data.FftSpectrumParameters.ScopeResidual"/>.</summary>
    bool? ScopeResidual = null,
    /// <summary>The unit the generator panel's level boxes are typed in — see <see cref="Tonebench.Data.FftSpectrumParameters.GeneratorLevelUnit"/>.</summary>
    string? GeneratorLevelUnit = null,
    /// <summary>Whether the spectrum chart draws the live trace itself — see <see cref="Tonebench.Data.FftSpectrumParameters.ChartShowFftTrace"/>.</summary>
    bool? ChartShowFftTrace = null,
    /// <summary>Whether the spectrum chart draws the harmonic markers — see <see cref="Tonebench.Data.FftSpectrumParameters.ChartShowHarmonicMarkers"/>.</summary>
    bool? ChartShowHarmonicMarkers = null,
    /// <summary>How the two peak tables are sorted — see <see cref="Tonebench.Data.FftSpectrumParameters.ChartHarmonicsSort"/>.</summary>
    string? ChartHarmonicsSort = null,
    string? ChartOtherPeaksSort = null);
