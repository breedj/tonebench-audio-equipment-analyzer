using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The crosstalk measurement's REST surface — the same verbs and status codes as
/// <see cref="SteppedResponseEndpoints"/>, and the same status payload, because the browser drives
/// it through the same response store. Stored traces come back through
/// <c>/api/measurements/{id}/response-traces</c> like every other response-shaped result.
/// </summary>
public static class CrosstalkEndpoints
{
    public static void MapCrosstalkEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/crosstalk");

        group.MapPost("/start", async (CrosstalkStartRequest request, CrosstalkRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Direction, CrosstalkDirection.Both, out CrosstalkDirection direction, out var directionError))
                return Results.BadRequest(directionError);

            if (!EnumParsing.TryParse(request.LevelMode, ThdSweepLevelMode.GeneratorVoltage, out ThdSweepLevelMode levelMode, out var modeError))
                return Results.BadRequest(modeError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Dbv, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.Window, WindowType.Hann, out WindowType window, out var windowError))
                return Results.BadRequest(windowError);

            var measurement = request.ToMeasurement(direction, levelMode, levelUnit, window);

            try
            {
                return Results.Ok(await runner.StartAsync(measurement, ct));
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

        group.MapPost("/cancel", async (CrosstalkRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        group.MapGet("/status", (CrosstalkRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (CrosstalkRunner runner) =>
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

        // Points the runner at the measurement now on screen, parking the one being left — see
        // CrosstalkRunner.SwitchOwner, and POST /api/frequency-response/owner for what this family
        // of endpoints is and when the client posts to one.
        group.MapPost("/owner", (RunOwnerRequest request, CrosstalkRunner runner) =>
            Results.Ok(runner.SwitchOwner(request.Normalized)));

        group.MapPost("/save", async (
            SaveFrequencyResponseRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveCrosstalkAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });
    }
}

/// <summary>
/// Wire shape for starting a crosstalk run. Enums arrive as strings — see <see cref="EnumParsing"/>.
/// </summary>
/// <param name="Direction">"LeftToRight", "RightToLeft" or "Both" (the default): which channel is the aggressor.</param>
/// <param name="LevelMode">GeneratorVoltage ("Input voltage"), OutputVoltage or OutputPower — the latter two probed on the driven channel.</param>
public sealed record CrosstalkStartRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string? Direction,
    string? LevelMode,
    double LevelValue,
    string? LevelUnit,
    double LoadOhms,
    int AverageCount,
    bool AdaptiveResolution = true,
    int FftSize = CrosstalkRequest.DefaultFftSize,
    string? Window = null,
    double SampleRateHz = CrosstalkRequest.DefaultSampleRateHz)
{
    /// <summary>
    /// The measurement this request asks for, written out field by field so a field left out is a
    /// test failure rather than a silent default — see <see cref="SteppedResponseStartRequest.ToMeasurement"/>.
    /// </summary>
    public CrosstalkRequest ToMeasurement(
        CrosstalkDirection direction, ThdSweepLevelMode levelMode, ThdSweepLevelUnit levelUnit, WindowType window) =>
        new(StartFreqHz,
            EndFreqHz,
            StepsPerOctave,
            direction,
            levelMode,
            LevelValue,
            levelUnit,
            LoadOhms,
            FftSize,
            window,
            AverageCount,
            SampleRateHz,
            AdaptiveResolution);
}
