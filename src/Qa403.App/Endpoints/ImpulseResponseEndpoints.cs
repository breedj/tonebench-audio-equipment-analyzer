using Qa403.App.Services;
using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Endpoints;

public static class ImpulseResponseEndpoints
{
    public static void MapImpulseResponseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/impulse-response");

        group.MapPost("/start", async (
            ImpulseResponseStartRequest request, ImpulseResponseRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.LevelMode, ThdSweepLevelMode.GeneratorVoltage, out ThdSweepLevelMode levelMode, out var modeError))
                return Results.BadRequest(modeError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Dbv, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.ReferenceMode, ImpulseReferenceMode.None, out ImpulseReferenceMode referenceMode, out var referenceError))
                return Results.BadRequest(referenceError);

            if (!EnumParsing.TryParse(request.GateWindow, WindowType.Hann, out WindowType gateWindow, out var windowError))
                return Results.BadRequest(windowError);

            var measurement = request.ToMeasurement(levelMode, levelUnit, referenceMode, gateWindow);

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
                // Already running, or no device — both states the UI can reach.
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/cancel", async (ImpulseResponseRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        // Carries the finished traces, impulses and harmonic curves rather than a progress counter,
        // so a browser that reloaded mid-measurement can redraw what has been measured.
        group.MapGet("/status", (ImpulseResponseRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (ImpulseResponseRunner runner) =>
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

        group.MapPost("/save", async (
            SaveImpulseResponseRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveImpulseResponseAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Loopback calibrations. Their own sub-resource rather than a mode of /start: capturing one
        // has a physical precondition (patch the generator output straight to the analyzer input),
        // it must not replace the traces on screen, and what it produces is a calibration rather than
        // a measurement result.
        group.MapGet("/reference", async (ILoopbackReferenceStore store, CancellationToken ct) =>
            Results.Ok(new LoopbackReferencesResponse(await store.ListAsync(ct))));

        group.MapPost("/reference/capture", async (
            ImpulseReferenceCaptureRequest request, ImpulseResponseRunner runner, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await runner.CaptureReferenceAsync(request, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // Covers both "a measurement is already running" and "that was not a loopback",
                // which is what the capture's own sanity check throws.
                return Results.Conflict(ex.Message);
            }
        });

        group.MapDelete("/reference/{id:long}", async (long id, ILoopbackReferenceStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Reads a stored impulse response back, for redrawing a saved result. The gated magnitude and
        // phase curves are not here: they are response_trace rows and come back from
        // /api/measurements/{id}/response-traces, the same as a frequency response's.
        app.MapGet("/api/measurements/{id:long}/impulse", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            var stored = await repo.GetImpulseResultAsync(id, ct);
            return stored is null ? Results.NotFound() : Results.Ok(stored);
        });
    }
}

/// <summary>
/// Wire shape for starting an impulse-response measurement. Enums arrive as strings, matching the
/// rest of the API — see <see cref="EnumParsing"/>.
/// </summary>
/// <param name="FrequencySpanHz">Nyquist, i.e. half the sample rate: 24000, 48000 or 96000.</param>
/// <param name="ResolutionMultiplier">1, 2 or 4 — the FFT size multiplier, and so the sweep's length.</param>
/// <param name="ReferenceMode">None, RightChannel or StoredLoopback.</param>
/// <param name="GatePreMs">
/// The <b>display</b> gate, not the sweep's anti-distortion one. Clamped by the latter, which is
/// fixed by the sweep geometry — see <see cref="ImpulseWindow"/>.
/// </param>
public sealed record ImpulseResponseStartRequest(
    double FrequencySpanHz,
    int ResolutionMultiplier,
    string? LevelMode,
    double LevelValue,
    string? LevelUnit,
    int SmoothingDenominator,
    int AverageCount,
    string? ReferenceMode,
    long? ReferenceCalibrationId,
    double GatePreMs,
    double GatePostMs,
    string? GateWindow,
    bool ExtractHarmonics = true,
    int MaxHarmonicOrder = 5,
    /// <summary>RUN sets this and keeps measuring until stopped; SINGLE leaves it false.</summary>
    bool Continuous = false,
    double ChirpStartHz = FrequencyResponseRequest.DefaultChirpStartHz,
    /// <summary>The load a watts target is defined against, in ohms. Only read by the OutputPower mode.</summary>
    double LoadOhms = 8.0)
{
    /// <summary>
    /// The measurement this request asks for. A method rather than inline in the endpoint so it can
    /// be tested, and this record needs that more than any other in the app: the mapping is written
    /// out <b>field by field</b>, a field left out of it is not an error but a <i>default</i>, and
    /// the run then succeeds and returns a perfectly plausible measurement of settings the user did
    /// not ask for. That has happened once already, on a record with a third as many fields — three
    /// frequency-response runs at 2, 5 and 8 Hz came back identical to 0.05 dB because
    /// <c>ChirpStartHz</c> was added everywhere except here.
    ///
    /// <para><c>ImpulseResponseStartRequestTests</c> checks the mapping value by value, and
    /// <c>BothRecordsStillHaveTheFieldsTheMappingTestChecks</c> fails the moment either record gains
    /// a parameter the other does not.</para>
    /// </summary>
    public ImpulseResponseRequest ToMeasurement(
        ThdSweepLevelMode levelMode,
        ThdSweepLevelUnit levelUnit,
        ImpulseReferenceMode referenceMode,
        WindowType gateWindow) =>
        new(FrequencySpanHz,
            ResolutionMultiplier,
            levelMode,
            LevelValue,
            levelUnit,
            SmoothingDenominator,
            AverageCount,
            referenceMode,
            ReferenceCalibrationId,
            GatePreMs,
            GatePostMs,
            gateWindow,
            ExtractHarmonics,
            MaxHarmonicOrder,
            Continuous,
            ChirpStartHz,
            LoadOhms);
}

public sealed record SaveImpulseResponseRequest(long MeasurementTypeId, string Name, string? Notes);

public sealed record LoopbackReferencesResponse(IReadOnlyList<LoopbackReferenceInfo> References);
