using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Endpoints;

/// <summary>
/// The IMD-vs-amplitude sweep's endpoints — <see cref="SweepEndpoints"/>' shape under its own
/// prefix. Stored points come back through the kind-agnostic <c>GET /api/measurements/{id}/sweep-points</c>.
/// </summary>
public static class ImdSweepEndpoints
{
    public static void MapImdSweepEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/imd-sweep");

        group.MapPost("/start", async (ImdSweepStartRequest request, ImdSweepRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Preset, IntermodulationPreset.Custom, out IntermodulationPreset preset, out var presetError))
                return Results.BadRequest(presetError);

            if (!EnumParsing.TryParse(request.Method, IntermodulationMethod.Smpte, out IntermodulationMethod method, out var methodError))
                return Results.BadRequest(methodError);

            var sweep = request.ToMeasurement(preset, method);

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

        group.MapPost("/cancel", async (ImdSweepRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        group.MapGet("/status", (ImdSweepRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (ImdSweepRunner runner) =>
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

        group.MapPost("/save", async (SaveImdSweepRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveImdSweepAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
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
/// Wire shape for starting an IMD-vs-amplitude sweep. Enums arrive as strings (see
/// <see cref="EnumParsing"/>); the FFT size, sample rate and window are fixed server-side
/// (<see cref="ImdSweepRequest"/>). Levels are sine-equivalent volts.
/// </summary>
public sealed record ImdSweepStartRequest(
    string? Preset,
    double ToneLowHz,
    double ToneHighHz,
    double LowToHighRatio,
    string? Method,
    int MaxOrder,
    double StartLevelVolt,
    double EndLevelVolt,
    int StepsPerOctave,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int AverageCount,
    bool MeasureNoiseFloor = true,
    double? MaxProductFreqHz = null)
{
    /// <summary>
    /// The runner's request, field by field — a method so a test can check every wire field
    /// arrives; a field left out of this mapping is not an error, it is a default the run then
    /// silently measures with (see <c>ImdSweepStartRequestTests</c>).
    /// </summary>
    public ImdSweepRequest ToMeasurement(IntermodulationPreset preset, IntermodulationMethod method) =>
        new(
            preset, ToneLowHz, ToneHighHz, LowToHighRatio, method, MaxOrder,
            StartLevelVolt, EndLevelVolt, StepsPerOctave, LoadOhms,
            ProcessLeft, ProcessRight, AverageCount, MeasureNoiseFloor, MaxProductFreqHz);
}

public sealed record SaveImdSweepRequest(long MeasurementTypeId, string Name, string? Notes);
