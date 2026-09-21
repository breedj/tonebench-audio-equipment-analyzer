using Qa403.App.Services;
using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Endpoints;

public static class SweepEndpoints
{
    public static void MapSweepEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sweep");

        group.MapPost("/thd-vs-frequency/start", async (ThdSweepStartRequest request, ThdSweepRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.LevelMode, ThdSweepLevelMode.GeneratorVoltage, out ThdSweepLevelMode levelMode, out var modeError))
                return Results.BadRequest(modeError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Dbv, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.Window, WindowType.Hann, out WindowType window, out var windowError))
                return Results.BadRequest(windowError);

            if (!EnumParsing.TryParse(request.ThdMethod, ThdMethod.Conventional, out ThdMethod thdMethod, out var methodError))
                return Results.BadRequest(methodError);

            var sweep = request.ToMeasurement(levelMode, levelUnit, window, thdMethod);

            try
            {
                return Results.Ok(await runner.StartAsync(sweep, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // Already running, or no device — both are states the UI can reach, same convention
                // as /api/acquisition/start.
                return Results.Conflict(ex.Message);
            }
        }).AddEndpointFilter<MeasurementRunGuard>();

        // The amplitude axis. A far smaller request than its frequency counterpart because most of
        // what that one carries has no meaning here: the level *is* the swept variable, so there is
        // no level mode to solve for and no adaptive resolution to size a moving tone with, and the
        // analysis preset is fixed (see ThdSweepRequest.AmplitudeSweepFftSize).
        group.MapPost("/thd-vs-amplitude/start", async (ThdAmplitudeSweepStartRequest request, ThdSweepRunner runner, CancellationToken ct) =>
        {
            var sweep = request.ToMeasurement();

            try
            {
                return Results.Ok(await runner.StartAsync(sweep, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        }).AddEndpointFilter<MeasurementRunGuard>();

        group.MapPost("/cancel", async (ThdSweepRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        // Carries the accumulated points, not just a progress counter, so a browser that reloaded
        // mid-sweep can redraw everything measured so far instead of waiting for the next one.
        group.MapGet("/status", (ThdSweepRunner runner) => Results.Ok(runner.Status));

        // The sweep counterpart of /api/acquisition/clear, and server-side for the same reason the
        // status endpoint exists at all: the runner holds the points, so a browser-only clear would
        // be undone by the next status poll. Conflict rather than BadRequest while a sweep runs,
        // matching /save's own "finish or cancel first".
        group.MapPost("/clear", (ThdSweepRunner runner) =>
        {
            try
            {
                return Results.Ok(runner.Clear());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Its own endpoint rather than POST /api/measurements, which saves the spectrum pipeline's
        // retained snapshot — a different source and a different row shape (N captures, not one).
        group.MapPost("/save", async (SaveSweepRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveSweepAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Reads a stored sweep back as points, for redrawing a saved result on the chart.
        app.MapGet("/api/measurements/{id:long}/sweep-points", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            var points = await repo.GetSweepPointsAsync(id, ct);
            return points.Count == 0 ? Results.NotFound() : Results.Ok(new StoredSweepResponse(points));
        });
    }
}

/// <summary>
/// Wire shape for starting a THD-vs-frequency sweep. Enums arrive as strings (matching the rest of
/// the API — see <see cref="EnumParsing"/>) rather than as ints, so a bad value gets a 400 naming
/// the allowed set instead of silently meaning something else.
/// </summary>
public sealed record ThdSweepStartRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string? LevelMode,
    double LevelValue,
    string? LevelUnit,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int FftSize,
    string? Window,
    int AverageCount,
    double SampleRateHz,
    bool MeasureNoiseFloor,
    /// <summary>Size the FFT per point and pick the sample rate from the sweep range — see <see cref="ThdSweepRequest.AdaptiveResolution"/>. Defaults off so an older client keeps the fixed behaviour.</summary>
    bool AdaptiveResolution = false,
    /// <summary>"Conventional" or "FrequencyNormalized" — see <see cref="Services.ThdMethod"/>. Absent means conventional, so an older client is unaffected.</summary>
    string? ThdMethod = null)
{
    /// <summary>
    /// The runner's request, field by field. A method rather than an inline construction in the
    /// endpoint so that a test can check every wire field actually arrives: the mapping fails
    /// silently otherwise — a field left out is not an error, it is a default, and the run then
    /// measures settings the user never asked for (see <c>ThdSweepStartRequestTests</c>).
    /// </summary>
    public ThdSweepRequest ToMeasurement(ThdSweepLevelMode levelMode, ThdSweepLevelUnit levelUnit, WindowType window, ThdMethod thdMethod) =>
        new(
            StartFreqHz, EndFreqHz, StepsPerOctave,
            levelMode, LevelValue, levelUnit, LoadOhms,
            ProcessLeft, ProcessRight,
            FftSize, window, AverageCount,
            SampleRateHz, MeasureNoiseFloor, AdaptiveResolution,
            ThdMethod: thdMethod);
}

/// <summary>
/// Wire shape for starting a THD-vs-amplitude sweep. No enums, so nothing to parse: the window,
/// FFT size, sample rate, level mode and noise-floor pass are all fixed for this axis and filled in
/// server-side, which is also what stops a client asking for a combination
/// <see cref="ThdSweepRequest.Validate"/> would only reject.
/// </summary>
public sealed record ThdAmplitudeSweepStartRequest(
    double FixedFreqHz,
    double StartAmplitudeVolt,
    double EndAmplitudeVolt,
    int StepsPerOctave,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int AverageCount,
    /// <summary>Capture one generator-off record before the sweep, for the residual the distortion traces sit above.</summary>
    bool MeasureNoiseFloor = true,
    /// <summary>End the sweep once a channel's THD exceeds this many percent — see <see cref="ThdSweepRequest.StopThdPercent"/>. Absent means sweep to the end.</summary>
    double? StopThdPercent = null)
{
    /// <summary>
    /// The runner's request, with everything this axis fixes filled in. A method for the same
    /// reason <see cref="ThdSweepStartRequest.ToMeasurement"/> is one: a wire field left out of the
    /// mapping is a default, not an error, and <c>ThdAmplitudeSweepStartRequestTests</c> checks
    /// that every field arrives.
    /// </summary>
    public ThdSweepRequest ToMeasurement() =>
        new(
            StartFreqHz: FixedFreqHz,
            EndFreqHz: FixedFreqHz,
            StepsPerOctave: StepsPerOctave,
            LevelMode: ThdSweepLevelMode.GeneratorVoltage,
            LevelValue: 0.0,
            LevelUnit: ThdSweepLevelUnit.Dbv,
            LoadOhms: LoadOhms,
            ProcessLeft: ProcessLeft,
            ProcessRight: ProcessRight,
            FftSize: ThdSweepRequest.AmplitudeSweepFftSize,
            Window: WindowType.Hann,
            AverageCount: AverageCount,
            SampleRateHz: ThdSweepRequest.AmplitudeSweepSampleRateHz,
            MeasureNoiseFloor: MeasureNoiseFloor,
            AdaptiveResolution: false,
            Axis: SweepAxis.Amplitude,
            FixedFreqHz: FixedFreqHz,
            StartAmplitudeVolt: StartAmplitudeVolt,
            EndAmplitudeVolt: EndAmplitudeVolt,
            StopThdPercent: StopThdPercent);
}

public sealed record SaveSweepRequest(long MeasurementTypeId, string Name, string? Notes);

/// <summary>A stored sweep's points, ascending by frequency, each carrying its per-channel metrics.</summary>
public sealed record StoredSweepResponse(IReadOnlyList<StoredSweepPoint> Points);
