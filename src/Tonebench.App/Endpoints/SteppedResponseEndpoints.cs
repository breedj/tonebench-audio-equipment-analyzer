using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The stepped-sine Bode plot's REST surface. Deliberately shaped exactly like
/// <see cref="FrequencyResponseEndpoints"/> — same verbs, same status codes, same status payload —
/// because the two measurements produce the same data and the browser drives them through one store.
/// Stored traces are read back through that file's <c>/api/measurements/{id}/response-traces</c>;
/// there is nothing kind-specific about a saved response.
/// </summary>
public static class SteppedResponseEndpoints
{
    public static void MapSteppedResponseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/stepped-response");

        group.MapPost("/start", async (
            SteppedResponseStartRequest request, SteppedResponseRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.LevelMode, ThdSweepLevelMode.GeneratorVoltage, out ThdSweepLevelMode levelMode, out var modeError))
                return Results.BadRequest(modeError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Dbv, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.Window, WindowType.Hann, out WindowType window, out var windowError))
                return Results.BadRequest(windowError);

            if (!EnumParsing.TryParse(
                    request.BandwidthReference, BandwidthReferenceMode.BandMedian,
                    out BandwidthReferenceMode bandwidthReference, out var referenceError))
            {
                return Results.BadRequest(referenceError);
            }

            var measurement = request.ToMeasurement(levelMode, levelUnit, window, bandwidthReference);

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
                // Already running, or no device — both states the UI can reach, same convention as
                // /api/sweep/*/start, /api/frequency-response/start and /api/acquisition/start.
                return Results.Conflict(ex.Message);
            }
        }).AddEndpointFilter<MeasurementRunGuard>();

        group.MapPost("/cancel", async (SteppedResponseRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        // Carries the finished traces, not just a progress counter, so a browser that reloaded
        // mid-run can redraw what has been measured rather than waiting for a rerun.
        group.MapGet("/status", (SteppedResponseRunner runner) => Results.Ok(runner.Status));

        // The bandwidth reference is a reading of the response rather than a parameter of the
        // stimulus, so it can be changed after the run: the runner re-finds the corners on the
        // full-resolution estimates it kept and pushes the rebuilt traces out. See
        // SteppedResponseRunner.ApplyBandwidthReference.
        group.MapPut("/bandwidth-reference", (BandwidthReferenceRequest request, SteppedResponseRunner runner) =>
        {
            if (!EnumParsing.TryParse(
                    request.BandwidthReference, BandwidthReferenceMode.BandMedian,
                    out BandwidthReferenceMode mode, out var referenceError))
            {
                return Results.BadRequest(referenceError);
            }

            try
            {
                return Results.Ok(runner.ApplyBandwidthReference(
                    new BandwidthReferenceSpec(mode, request.BandwidthReferenceLowHz, request.BandwidthReferenceHighHz)));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/clear", (SteppedResponseRunner runner) =>
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
        // SteppedResponseRunner.SwitchOwner, and POST /api/frequency-response/owner for what this
        // family of endpoints is and when the client posts to one.
        group.MapPost("/owner", (RunOwnerRequest request, SteppedResponseRunner runner) =>
            Results.Ok(runner.SwitchOwner(request.Normalized)));

        group.MapPost("/save", async (
            SaveFrequencyResponseRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveSteppedResponseAsync(
                    request.MeasurementTypeId, request.Name, request.Notes, request.ChartAnnotations, ct);
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
/// Wire shape for starting a stepped-sine response. Enums arrive as strings, matching the rest of
/// the API — see <see cref="EnumParsing"/>.
/// </summary>
/// <param name="LevelMode">GeneratorVoltage ("Input voltage"), OutputVoltage or OutputPower.</param>
/// <param name="GenerateLeft">Play the tone on the left generator channel. Both inputs are analyzed regardless — see <see cref="SteppedResponseRequest.GenerateLeft"/>.</param>
public sealed record SteppedResponseStartRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string? LevelMode,
    double LevelValue,
    string? LevelUnit,
    double LoadOhms,
    bool GenerateLeft,
    bool GenerateRight,
    int AverageCount,
    bool RightChannelIsReference,
    /// <summary>Size the FFT per point and pick the sample rate from the sweep range — see <see cref="SteppedResponseRequest.AdaptiveResolution"/>. Defaults on: there are no harmonics here to keep in band.</summary>
    bool AdaptiveResolution = true,
    int FftSize = SteppedResponseRequest.DefaultFftSize,
    string? Window = null,
    double SampleRateHz = SteppedResponseRequest.DefaultSampleRateHz,
    /// <summary>
    /// "BandMedian" (the default) or "Peak" — what the corners are measured down from. See
    /// <see cref="BandwidthReferenceSpec"/>.
    /// </summary>
    string? BandwidthReference = null,
    double BandwidthReferenceLowHz = BandwidthReferenceSpec.DefaultLowHz,
    double BandwidthReferenceHighHz = BandwidthReferenceSpec.DefaultHighHz)
{
    /// <summary>
    /// The measurement this request asks for. A method rather than inline in the endpoint so it can
    /// be tested: the mapping is written out field by field, and a field left out of it is dropped
    /// in silence — the run simply uses the default and returns a perfectly plausible measurement of
    /// something else. That is exactly what happened when <c>ChirpStartHz</c> was added to the chirp
    /// measurement: three runs at 2, 5 and 8 Hz came back identical to 0.05 dB.
    /// </summary>
    public SteppedResponseRequest ToMeasurement(
        ThdSweepLevelMode levelMode,
        ThdSweepLevelUnit levelUnit,
        WindowType window,
        BandwidthReferenceMode bandwidthReference) =>
        new(StartFreqHz,
            EndFreqHz,
            StepsPerOctave,
            levelMode,
            LevelValue,
            levelUnit,
            LoadOhms,
            GenerateLeft,
            GenerateRight,
            FftSize,
            window,
            AverageCount,
            SampleRateHz,
            AdaptiveResolution,
            RightChannelIsReference,
            bandwidthReference,
            BandwidthReferenceLowHz,
            BandwidthReferenceHighHz);
}
