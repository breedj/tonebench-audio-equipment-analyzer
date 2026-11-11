using System.Text.Json;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

public static class FrequencyResponseEndpoints
{
    public static void MapFrequencyResponseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/frequency-response");

        group.MapPost("/start", async (
            FrequencyResponseStartRequest request, FrequencyResponseRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.LevelMode, ThdSweepLevelMode.GeneratorVoltage, out ThdSweepLevelMode levelMode, out var modeError))
                return Results.BadRequest(modeError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Dbv, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(
                    request.BandwidthReference, BandwidthReferenceMode.BandMedian,
                    out BandwidthReferenceMode bandwidthReference, out var referenceError))
            {
                return Results.BadRequest(referenceError);
            }

            if (!EnumParsing.TryParse(
                    request.Stimulus, ResponseStimulus.Chirp, out ResponseStimulus stimulus, out var stimulusError))
            {
                return Results.BadRequest(stimulusError);
            }

            var measurement = request.ToMeasurement(levelMode, levelUnit, bandwidthReference, stimulus);

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
                // /api/sweep/*/start and /api/acquisition/start.
                return Results.Conflict(ex.Message);
            }
        }).AddEndpointFilter<MeasurementRunGuard>();

        group.MapPost("/cancel", async (FrequencyResponseRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        // Carries the finished traces, not just a progress counter, so a browser that reloaded
        // mid-measurement can redraw what has been measured rather than waiting for a rerun.
        group.MapGet("/status", (FrequencyResponseRunner runner) => Results.Ok(runner.Status));

        // The bandwidth reference is a reading of the response rather than a parameter of the
        // stimulus, so it can be changed after the run: the runner re-finds the corners on the
        // full-resolution estimates it kept and pushes the rebuilt traces out. See
        // FrequencyResponseRunner.ApplyBandwidthReference.
        group.MapPut("/bandwidth-reference", (BandwidthReferenceRequest request, FrequencyResponseRunner runner) =>
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

        group.MapPost("/clear", (FrequencyResponseRunner runner) =>
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
        // FrequencyResponseRunner.SwitchOwner, and RunParking for the reasoning. Posted on arrival
        // at a chirp screen only: nothing but a chirp writes this runner, so the owner can be left
        // alone while the user is elsewhere. The answer is the status to redraw, so the switch and
        // the redraw are one round trip.
        group.MapPost("/owner", (RunOwnerRequest request, FrequencyResponseRunner runner) =>
            Results.Ok(runner.SwitchOwner(request.Normalized)));

        group.MapPost("/save", async (
            SaveFrequencyResponseRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveFrequencyResponseAsync(
                    request.MeasurementTypeId, request.Name, request.Notes, request.ChartAnnotations, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Reads a stored response back as traces, for redrawing a saved result or overlaying it.
        app.MapGet("/api/measurements/{id:long}/response-traces", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            var traces = await repo.GetResponseTracesAsync(id, ct);
            return traces.Count == 0 ? Results.NotFound() : Results.Ok(new StoredResponseTracesResponse(traces));
        });

        // The per-point captures behind a stored stepped-sine response, for the preview charts
        // beside the chart. An empty list rather than a 404 for a result that has none: a chirp
        // stores no points, and so does a stepped run saved before these were recorded — neither is
        // a missing result, and the client checks every response overlay through this one route.
        app.MapGet("/api/measurements/{id:long}/response-previews", async (
            long id, MeasurementRepository repo, CancellationToken ct) =>
            Results.Ok(new StoredResponsePreviewsResponse(await repo.GetResponsePreviewsAsync(id, ct))));
    }
}

/// <summary>
/// Wire shape for starting a frequency-response measurement. Enums arrive as strings, matching the
/// rest of the API — see <see cref="EnumParsing"/>.
/// </summary>
/// <param name="FrequencySpanHz">Nyquist, i.e. half the sample rate: 24000, 48000 or 96000.</param>
/// <param name="ResolutionMultiplier">1, 2 or 4 — the FFT size multiplier giving 1.46, 0.73 or 0.37 Hz bins.</param>
/// <param name="LevelMode">GeneratorVoltage ("Input voltage"), OutputVoltage or OutputPower.</param>
/// <param name="SmoothingDenominator">0 (none), 69, 48, 24, 12, 6 or 3 — the N in 1/N octave.</param>
/// <summary>
/// The wire shape of <c>PUT /api/frequency-response/bandwidth-reference</c> and its stepped twin:
/// the same three fields the start requests carry, applied to the traces already on screen.
/// </summary>
/// <param name="BandwidthReference">BandMedian or Peak.</param>
public sealed record BandwidthReferenceRequest(
    string? BandwidthReference,
    double BandwidthReferenceLowHz,
    double BandwidthReferenceHighHz);

public sealed record FrequencyResponseStartRequest(
    double FrequencySpanHz,
    int ResolutionMultiplier,
    string? LevelMode,
    double LevelValue,
    string? LevelUnit,
    bool RightChannelIsReference,
    int SmoothingDenominator,
    int AverageCount,
    /// <summary>RUN sets this and keeps measuring until stopped; SINGLE leaves it false.</summary>
    bool Continuous = false,
    /// <summary>
    /// Where the sweep starts, in Hz. Defaulted rather than required so an older client still works
    /// — but note that this mapping is written out field by field, which is exactly how this
    /// parameter was silently dropped the first time: the UI sent it, the runner defaulted it, and
    /// three "different" runs came back byte-identical. A field added to
    /// <see cref="FrequencyResponseRequest"/> has to be added here too.
    /// </summary>
    double ChirpStartHz = FrequencyResponseRequest.DefaultChirpStartHz,
    /// <summary>
    /// The load a watts target is defined against, in ohms. Only read by the OutputPower mode;
    /// defaulted so a client that never sends it keeps working.
    /// </summary>
    double LoadOhms = 8.0,
    /// <summary>
    /// "BandMedian" (the default) or "Peak" — what the corners are measured down from. See
    /// <see cref="BandwidthReferenceSpec"/>; the peak is right only for a DUT whose response is
    /// itself a peak.
    /// </summary>
    string? BandwidthReference = null,
    double BandwidthReferenceLowHz = BandwidthReferenceSpec.DefaultLowHz,
    double BandwidthReferenceHighHz = BandwidthReferenceSpec.DefaultHighHz,
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
    /// <summary>Clock offset in ppm from the drift test; 0 leaves the measurement uncorrected.</summary>
    double ClockDriftPpm = 0.0)
{
    /// <summary>
    /// The measurement this request asks for. A method rather than inline in the endpoint so it can
    /// be tested: the mapping is written out field by field, and a field left out of it is dropped
    /// in silence — the run simply uses the default and returns a perfectly plausible measurement of
    /// something else. That is exactly what happened when <see cref="ChirpStartHz"/> was added:
    /// three runs at 2, 5 and 8 Hz came back identical to 0.05 dB, and only the right channel still
    /// reading the generator below the supposed start frequency gave it away.
    /// </summary>
    public FrequencyResponseRequest ToMeasurement(
        ThdSweepLevelMode levelMode,
        ThdSweepLevelUnit levelUnit,
        BandwidthReferenceMode bandwidthReference,
        ResponseStimulus stimulus) =>
        new(FrequencySpanHz,
            ResolutionMultiplier,
            levelMode,
            LevelValue,
            levelUnit,
            RightChannelIsReference,
            SmoothingDenominator,
            AverageCount,
            Continuous,
            ChirpStartHz,
            LoadOhms,
            bandwidthReference,
            BandwidthReferenceLowHz,
            BandwidthReferenceHighHz,
            stimulus,
            NoiseCutoffHz,
            ClockDriftPpm);
}

/// <param name="ChartAnnotations">
/// The notes on this measurement's charts at the moment of the save, which the result keeps as its
/// own frozen copy -- see <see cref="Tonebench.Data.FftSpectrumParameters.ChartAnnotations"/>.
/// An empty list is a result with none; null is a caller that sent none at all, which stores
/// nothing rather than reaching for the measurement type's.
/// </param>
public sealed record SaveFrequencyResponseRequest(long MeasurementTypeId, string Name, string? Notes,
    IReadOnlyList<JsonElement>? ChartAnnotations = null);

public sealed record StoredResponseTracesResponse(IReadOnlyList<StoredResponseTrace> Traces);

/// <summary>A stored stepped-sine response's per-point captures — see <see cref="MeasurementRepository.GetResponsePreviewsAsync"/>.</summary>
public sealed record StoredResponsePreviewsResponse(IReadOnlyList<StoredResponsePreview> Points);
