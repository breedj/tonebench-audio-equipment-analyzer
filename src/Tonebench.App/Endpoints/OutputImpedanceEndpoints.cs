using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The output-impedance (damping factor) measurement's REST surface: the usual start/cancel/status/
/// clear/save, plus <c>/continue</c> — the CONTINUE button, which resumes a run waiting for the load
/// to be changed and is a 409 at any other time. Stored traces come back through
/// <c>/api/measurements/{id}/response-traces</c> like every other response-shaped result.
/// </summary>
public static class OutputImpedanceEndpoints
{
    public static void MapOutputImpedanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/output-impedance");

        group.MapPost("/start", async (OutputImpedanceStartRequest request, OutputImpedanceRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.LevelMode, ThdSweepLevelMode.OutputPower, out ThdSweepLevelMode levelMode, out var modeError))
                return Results.BadRequest(modeError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Watt, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.Window, WindowType.Hann, out WindowType window, out var windowError))
                return Results.BadRequest(windowError);

            var measurement = request.ToMeasurement(levelMode, levelUnit, window);

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

        group.MapPost("/continue", (OutputImpedanceRunner runner) =>
        {
            try
            {
                runner.Continue();
                return Results.Ok(runner.Status);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/cancel", async (OutputImpedanceRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        group.MapGet("/status", (OutputImpedanceRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (OutputImpedanceRunner runner) =>
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
        // OutputImpedanceRunner.SwitchOwner, and POST /api/frequency-response/owner for what this
        // family of endpoints is and when the client posts to one.
        group.MapPost("/owner", (RunOwnerRequest request, OutputImpedanceRunner runner) =>
            Results.Ok(runner.SwitchOwner(request.Normalized)));

        group.MapPost("/save", async (
            SaveFrequencyResponseRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveOutputImpedanceAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
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
/// Wire shape for starting an output-impedance run. Enums arrive as strings — see <see cref="EnumParsing"/>.
/// </summary>
/// <param name="LevelMode">GeneratorVoltage ("Input voltage"), OutputVoltage or OutputPower (the default) — the latter two probed on the loaded pass.</param>
/// <param name="LoadOhms">The resistor connected for the first pass; also what a power target converts through.</param>
/// <param name="SecondLoadOhms">The resistor connected for the second pass, or null for an open output (the tutorial's way — not for a tube amplifier).</param>
/// <param name="NominalOhms">What the damping factor is quoted against — 8 Ω on every specification sheet.</param>
/// <param name="RightChannelIsReference">Loop the right output to the right input in both passes and measure Zout as a complex value.</param>
public sealed record OutputImpedanceStartRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string? LevelMode,
    double LevelValue,
    string? LevelUnit,
    double LoadOhms,
    double? SecondLoadOhms,
    double NominalOhms,
    bool RightChannelIsReference,
    int AverageCount,
    bool AdaptiveResolution = true,
    int FftSize = OutputImpedanceRequest.DefaultFftSize,
    string? Window = null,
    double SampleRateHz = OutputImpedanceRequest.DefaultSampleRateHz)
{
    /// <summary>
    /// The measurement this request asks for, written out field by field so a field left out is a
    /// test failure rather than a silent default — see <see cref="SteppedResponseStartRequest.ToMeasurement"/>.
    /// </summary>
    public OutputImpedanceRequest ToMeasurement(ThdSweepLevelMode levelMode, ThdSweepLevelUnit levelUnit, WindowType window) =>
        new(StartFreqHz,
            EndFreqHz,
            StepsPerOctave,
            levelMode,
            LevelValue,
            levelUnit,
            LoadOhms,
            SecondLoadOhms,
            NominalOhms,
            RightChannelIsReference,
            FftSize,
            window,
            AverageCount,
            SampleRateHz,
            AdaptiveResolution);
}
