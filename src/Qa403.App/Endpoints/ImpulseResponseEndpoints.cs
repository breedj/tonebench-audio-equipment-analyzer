using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Qa403.App.Services;
using Qa403.Data;
using Qa403.Dsp;
using Qa403.Dsp.Fft;

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
        }).AddEndpointFilter<MeasurementRunGuard>();

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

        // The two decay surfaces. POSTs rather than GETs, and separate from /status, because they
        // are analyses of the record already captured rather than parts of it: every setting they
        // take is a question put to that record, and asking a different question must not need
        // another sweep. Their own endpoint each rather than one with two optional bodies, so each
        // validates only what it actually uses.
        group.MapPost("/decay/csd", async (
            ImpulseCsdRequest request, ImpulseResponseRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.WindowMode, CsdWindowMode.Apodizing, out CsdWindowMode windowMode, out var windowError))
                return Results.BadRequest(windowError);

            try
            {
                return Results.Ok(await runner.ComputeCumulativeSpectralDecayAsync(request.ToSettings(windowMode), ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // Every one of these is a settings problem the user can act on — a transform too
                // long for the record, a band the transform cannot resolve — so the message is the
                // whole point of the response.
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/decay/burst", async (
            ImpulseBurstDecayRequest request, ImpulseResponseRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Resolution, BurstDecayResolution.PreferTime, out BurstDecayResolution resolution, out var resolutionError))
                return Results.BadRequest(resolutionError);

            try
            {
                return Results.Ok(await runner.ComputeBurstDecayAsync(request.ToSettings(resolution), ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
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

        // The same stored result in the shape the chart draws: the left impulse gated and windowed the
        // way it was on screen, min/max decimated. The raw IR above is the record; this is the picture,
        // built by the runner's own BuildImpulseTrace so a report of a stored result and the live
        // screen cannot draw the same measurement two different ways.
        app.MapGet("/api/measurements/{id:long}/impulse-trace", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            var stored = await repo.GetImpulseResultAsync(id, ct);
            if (stored is null)
                return Results.NotFound();

            var left = stored.Impulses.FirstOrDefault(i => i.Kind == "left") ?? stored.Impulses.FirstOrDefault();
            if (left is null || left.Ir.Length == 0)
                return Results.Ok(new StoredImpulseTraceResponse(null));

            var window = Enum.TryParse<WindowType>(left.GateWindow, ignoreCase: true, out var parsed) ? parsed : WindowType.Hann;
            var ir = Array.ConvertAll(left.Ir, v => (double)v);
            var trace = ImpulseResponseRunner.BuildImpulseTrace(
                FrequencyResponseTraceKind.Left, ir, left.PeakIndex,
                left.GatePreMs, left.GatePostMs, left.SampleRateHz, window);
            return Results.Ok(new StoredImpulseTraceResponse(trace));
        });

        // The two decay surfaces of a stored result, computed from its stored IR the way the live ones
        // are computed from the runner's (the DSP is stateless; only the live endpoints go through the
        // runner's memory). The record's own run settings supply the sweep gate — see
        // ImpulseResponseRunner.StoredAvailablePostSamples — so a stored result's decay reads the same
        // record the screen read when it was measured, cut at the same place.
        app.MapPost("/api/measurements/{id:long}/decay/csd", async (
            long id, ImpulseCsdRequest request, MeasurementRepository repo,
            IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.WindowMode, CsdWindowMode.Apodizing, out CsdWindowMode windowMode, out var windowError))
                return Results.BadRequest(windowError);

            var stored = await LoadStoredImpulseAsync(id, repo, jsonOptions.Value.SerializerOptions, ct);
            if (stored is null)
                return Results.NotFound();

            try
            {
                var (ir, left, available) = stored.Value;
                var settings = request.ToSettings(windowMode);
                return Results.Ok(await Task.Run(
                    () => CumulativeSpectralDecay.Compute(ir, left.PeakIndex, left.SampleRateHz, available, settings, new MathNetBackend()),
                    ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/measurements/{id:long}/decay/burst", async (
            long id, ImpulseBurstDecayRequest request, MeasurementRepository repo,
            IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Resolution, BurstDecayResolution.PreferTime, out BurstDecayResolution resolution, out var resolutionError))
                return Results.BadRequest(resolutionError);

            var stored = await LoadStoredImpulseAsync(id, repo, jsonOptions.Value.SerializerOptions, ct);
            if (stored is null)
                return Results.NotFound();

            try
            {
                var (ir, left, available) = stored.Value;
                var settings = request.ToSettings(resolution);
                return Results.Ok(await Task.Run(
                    () => BurstDecay.Compute(ir, left.PeakIndex, left.SampleRateHz, available, settings),
                    ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }

    /// <summary>
    /// A stored result's left impulse as doubles, with how far past the peak a decay analysis may
    /// read; null when the result does not exist or holds no impulse.
    /// </summary>
    private static async Task<(double[] Ir, StoredImpulseTrace Left, int AvailablePostSamples)?> LoadStoredImpulseAsync(
        long id, MeasurementRepository repo, JsonSerializerOptions serializerOptions, CancellationToken ct)
    {
        var stored = await repo.GetImpulseResultAsync(id, ct);
        var left = stored?.Impulses.FirstOrDefault(i => i.Kind == "left") ?? stored?.Impulses.FirstOrDefault();
        if (left is null || left.Ir.Length == 0)
            return null;

        var detail = await repo.GetResultAsync(id, ct);
        ImpulseResponseParameters? parameters = null;
        if (detail is not null)
        {
            try
            {
                parameters = JsonSerializer.Deserialize<ImpulseResponseParameters>(detail.ParamsJson, serializerOptions);
            }
            catch (JsonException)
            {
                // A row whose settings this build cannot read: bound the analysis by the row alone.
            }
        }

        int available = parameters is null
            ? Math.Max(0, left.Ir.Length - left.PeakIndex - 1)
            : ImpulseResponseRunner.StoredAvailablePostSamples(left, parameters);
        return (Array.ConvertAll(left.Ir, v => (double)v), left, available);
    }
}

/// <summary>A stored impulse response as its display trace; null when the result holds no impulse.</summary>
public sealed record StoredImpulseTraceResponse(ImpulseTrace? Impulse);

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

/// <summary>
/// Wire shape for a cumulative spectral decay. Every field is an analysis setting, so a run is
/// never needed to change one — but the same warning applies as to
/// <see cref="ImpulseResponseStartRequest.ToMeasurement"/>: <see cref="ToSettings"/> maps field by
/// field, and a field left out of it is a default rather than an error. The surface then comes back
/// looking perfectly reasonable and answering a question nobody asked. Guarded by
/// <c>ImpulseDecayRequestTests</c>.
/// </summary>
public sealed record ImpulseCsdRequest(
    int FftLength,
    int BlockShiftSamples,
    int BlockCount,
    string? WindowMode,
    double RiseTimeMs,
    double LowHz,
    double HighHz,
    int DisplayColumns)
{
    public CsdSettings ToSettings(CsdWindowMode windowMode) =>
        new(FftLength, BlockShiftSamples, BlockCount, windowMode, RiseTimeMs, LowHz, HighHz, DisplayColumns);
}

/// <summary>Wire shape for a burst decay. See <see cref="ImpulseCsdRequest"/> on the mapping.</summary>
public sealed record ImpulseBurstDecayRequest(
    double LowHz,
    double HighHz,
    int MaxPeriods,
    int PointsPerPeriod,
    string? Resolution,
    int FrequencyColumns)
{
    public BurstDecaySettings ToSettings(BurstDecayResolution resolution) =>
        new(LowHz, HighHz, MaxPeriods, PointsPerPeriod, resolution, FrequencyColumns);
}

public sealed record SaveImpulseResponseRequest(long MeasurementTypeId, string Name, string? Notes);

public sealed record LoopbackReferencesResponse(IReadOnlyList<LoopbackReferenceInfo> References);
