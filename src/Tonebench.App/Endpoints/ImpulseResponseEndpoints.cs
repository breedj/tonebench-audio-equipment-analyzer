using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Endpoints;

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

            if (!EnumParsing.TryParse(
                    request.Stimulus, ResponseStimulus.Chirp, out ResponseStimulus stimulus, out var stimulusError))
            {
                return Results.BadRequest(stimulusError);
            }

            var measurement = request.ToMeasurement(levelMode, levelUnit, referenceMode, gateWindow, stimulus);

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

        // Points the runner at the measurement now on screen, parking the one being left — see
        // ImpulseResponseRunner.SwitchOwner, and POST /api/frequency-response/owner for what this
        // family of endpoints is and when the client posts to one.
        group.MapPost("/owner", (RunOwnerRequest request, ImpulseResponseRunner runner) =>
            Results.Ok(runner.SwitchOwner(request.Normalized)));

        group.MapPost("/save", async (
            SaveImpulseResponseRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveImpulseResponseAsync(
                    request.MeasurementTypeId, request.Name, request.Notes, request.ChartAnnotations, ct);
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
                return Results.Ok(await runner.ComputeCumulativeSpectralDecayAsync(
                    request.ToSettings(windowMode, runner.Status.SampleRateHz), ct));
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
                var (ir, left, available, availableBefore) = stored.Value;
                // This record's own rate, not the live runner's — see ImpulseCsdRequest.SpanMs.
                var settings = request.ToSettings(windowMode, left.SampleRateHz);
                return Results.Ok(await Task.Run(
                    () => CumulativeSpectralDecay.Compute(
                        ir, left.PeakIndex, left.SampleRateHz, available, settings, new FftFlatBackend(), availableBefore),
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
                var (ir, left, available, _) = stored.Value;
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
    private static async Task<(double[] Ir, StoredImpulseTrace Left, int AvailablePostSamples, int AvailablePreSamples)?> LoadStoredImpulseAsync(
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

        // And the same in front of it, which bounds a cumulative spectral decay's head start.
        int availableBefore = parameters is null
            ? Math.Max(0, left.PeakIndex)
            : ImpulseResponseRunner.StoredAvailablePreSamples(left, parameters);

        return (Array.ConvertAll(left.Ir, v => (double)v), left, available, availableBefore);
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
    /// <summary>
    /// What to play -- "Chirp" (the default, and what every build before this could play) or one of
    /// the periodic-noise kinds: "PinkNoise", "WhiteNoise", "SpeechMale2011", "SpeechFemale2011",
    /// "SpeechMale2020". Parsed by the endpoint, which answers a name it does not know rather than
    /// quietly sweeping instead.
    /// </summary>
    string? Stimulus = null,
    /// <summary>
    /// Where a pink or speech sequence stops rising towards DC, in Hz. Read by neither the chirp nor
    /// the white sequence, which has no slope for a corner to start.
    /// </summary>
    double NoiseCutoffHz = 5.0,
    /// <summary>The load a watts target is defined against, in ohms. Only read by the OutputPower mode.</summary>
    double LoadOhms = 8.0,
    /// <summary>Clock offset in ppm from the drift test; 0 leaves the measurement uncorrected.</summary>
    double ClockDriftPpm = 0.0)
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
        WindowType gateWindow,
        ResponseStimulus stimulus) =>
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
            LoadOhms,
            stimulus,
            NoiseCutoffHz,
            ClockDriftPpm);
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
    /// <summary>The surface's whole depth in samples — see <see cref="CsdSettings.SpanSamples"/>.</summary>
    int SpanSamples,
    int BlockCount,
    string? WindowMode,
    double RiseTimeMs,
    double LowHz,
    double HighHz,
    int DisplayColumns,
    /// <summary>How far ahead of the impulse the first slice sits — see <see cref="CsdSettings.StartBeforePeakMs"/>.</summary>
    double StartBeforePeakMs = 0.0,
    /// <summary>
    /// The surface's whole depth as a <em>time</em>, which is what the panel actually sets, and
    /// which wins over <see cref="SpanSamples"/> whenever it is positive.
    /// </summary>
    /// <remarks>
    /// <para>A depth in samples is a different measurement at every sample rate — the reason the
    /// panel asks for milliseconds at all — so the conversion needs the rate of the record being
    /// analysed, and only the server knows that per measurement. The client used to convert with
    /// the <em>live runner's</em> rate, which is <c>0</c> until something has been measured: a
    /// stored result opened on a fresh start therefore asked for a span of one sample, every slice
    /// landed on the same sample, and the surface came back flat. Pressing Run fixed it by giving
    /// the runner a rate, which is exactly how it was reported on 2026-10-26.</para>
    /// <para>It was wrong a second way that no amount of client arithmetic could fix: one request
    /// serves every stacked result, so two results taken at different rates shared one sample
    /// count and only one of them could be right.</para>
    /// </remarks>
    double SpanMs = 0.0)
{
    public CsdSettings ToSettings(CsdWindowMode windowMode, double sampleRateHz) =>
        new(FftLength, SpanSamplesAt(sampleRateHz), BlockCount, windowMode, RiseTimeMs, LowHz, HighHz, DisplayColumns, StartBeforePeakMs);

    /// <summary>The depth in samples at this record's own rate, preferring the time where given.</summary>
    public int SpanSamplesAt(double sampleRateHz)
    {
        if (SpanMs <= 0 || !(sampleRateHz > 0))
            return SpanSamples;
        var samples = (int)Math.Round(SpanMs / 1000.0 * sampleRateHz);
        return Math.Max(1, samples);
    }
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

/// <param name="ChartAnnotations">
/// The notes on this measurement's charts at the moment of the save, which the result keeps as its
/// own frozen copy -- see <see cref="Tonebench.Data.FftSpectrumParameters.ChartAnnotations"/>.
/// An empty list is a result with none; null is a caller that sent none at all, which stores
/// nothing rather than reaching for the measurement type's.
/// </param>
public sealed record SaveImpulseResponseRequest(long MeasurementTypeId, string Name, string? Notes,
    IReadOnlyList<JsonElement>? ChartAnnotations = null);

public sealed record LoopbackReferencesResponse(IReadOnlyList<LoopbackReferenceInfo> References);
