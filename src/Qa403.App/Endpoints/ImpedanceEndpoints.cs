using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Endpoints;

public static class ImpedanceEndpoints
{
    public static void MapImpedanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/impedance");

        group.MapPost("/start", async (
            ImpedanceStartRequest request, ImpedanceRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Method, ImpedanceMethod.ReferenceResistor, out ImpedanceMethod method, out var methodError))
                return Results.BadRequest(methodError);

            if (!EnumParsing.TryParse(request.Stimulus, ImpedanceStimulus.Chirp, out ImpedanceStimulus stimulus, out var stimulusError))
                return Results.BadRequest(stimulusError);

            if (!EnumParsing.TryParse(request.LevelUnit, ThdSweepLevelUnit.Dbv, out ThdSweepLevelUnit levelUnit, out var unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.CalTarget, ImpedanceCalTarget.None, out ImpedanceCalTarget calTarget, out var calError))
                return Results.BadRequest(calError);

            if (!EnumParsing.TryParse(request.CableMode, ImpedanceCableMode.None, out ImpedanceCableMode cableMode, out var cableError))
                return Results.BadRequest(cableError);

            if (!EnumParsing.TryParse(
                    request.CalibrationStimulus, ImpedanceStimulus.Chirp,
                    out ImpedanceStimulus calibrationStimulus, out var calStimulusError))
                return Results.BadRequest(calStimulusError);

            var measurement = request.ToMeasurement(method, stimulus, levelUnit, calTarget, cableMode, calibrationStimulus);

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
                // Already running, or no device — same convention as every other runner's start.
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/cancel", async (ImpedanceRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        // Carries the finished traces and any captured calibration, so a browser that reloaded
        // mid-measurement can redraw rather than rerun.
        group.MapGet("/status", (ImpedanceRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (ImpedanceRunner runner) =>
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
            SaveImpedanceRequest request, MeasurementSaveService saveService, CancellationToken ct) =>
        {
            try
            {
                long id = await saveService.SaveImpedanceAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Reading a stored impedance back needs no endpoint of its own: the curves live in the same
        // response_trace rows the frequency response uses, so GET /api/measurements/{id}/response-traces
        // returns them. Only the `kind` strings differ.
    }
}

/// <summary>
/// Wire shape for starting an impedance measurement. Enums arrive as strings, matching the rest of
/// the API — see <see cref="EnumParsing"/>.
/// </summary>
/// <param name="Method">ReferenceResistor. TwoLoad is reserved and rejected for now.</param>
/// <param name="Stimulus">Chirp. SteppedSine and PinkNoise are reserved and rejected for now.</param>
/// <param name="ReferenceOhms">The series reference resistor.</param>
/// <param name="RightChannelIsReference">True when the right channel taps the generator side. The simulator needs this; a bench fixture wired left-as-reference does not.</param>
/// <param name="FrequencySpanHz">Nyquist, i.e. half the sample rate: 24000, 48000 or 96000.</param>
/// <param name="ResolutionMultiplier">1, 2 or 4 — the FFT size multiplier giving 1.46, 0.73 or 0.37 Hz bins.</param>
/// <param name="LevelUnit">Dbv, Volt or MilliVolt. There is no watts option: a one-port has no output power to aim at.</param>
/// <param name="SmoothingDenominator">0 (none), 69, 48, 24, 12, 6 or 3 — the N in 1/N octave.</param>
/// <param name="CalTarget">None to measure a DUT, or Open/Short to capture that standard.</param>
/// <param name="CableMode">None, Measured (subtract <paramref name="ShortCal"/>) or Fixed (subtract the typed-in values).</param>
public sealed record ImpedanceStartRequest(
    string? Method,
    string? Stimulus,
    double ReferenceOhms,
    bool RightChannelIsReference,
    double FrequencySpanHz,
    int ResolutionMultiplier,
    double LevelValue,
    string? LevelUnit,
    int SmoothingDenominator,
    int AverageCount,
    /// <summary>RUN sets this and keeps measuring until stopped; SINGLE leaves it false. Rejected for a calibration run.</summary>
    bool Continuous = false,
    double ChirpStartHz = FrequencyResponseRequest.DefaultChirpStartHz,
    string? CalTarget = null,
    string? CableMode = null,
    double FixedCableOhms = 0.0,
    double FixedCableMillihenry = 0.0,
    double? AnalyzerInputOhms = null,
    bool ApplySweepGate = false,
    /// <summary>
    /// The stored open standard, sent back by the browser from the measurement type's params_json.
    /// The server holds no calibration of its own, so these travel with every start.
    /// </summary>
    ImpedanceCalibration? OpenCal = null,
    ImpedanceCalibration? ShortCal = null,
    /// <summary>
    /// The drive level a calibration run uses, in the same unit as <paramref name="LevelValue"/>.
    /// Louder than the measurement's by default — see <see cref="ImpedanceRequest.DefaultCalibrationLevelDbv"/>.
    /// </summary>
    double CalibrationLevelValue = ImpedanceRequest.DefaultCalibrationLevelDbv,
    /// <summary>
    /// What the Open/Short captures play, independently of <paramref name="Stimulus"/>. Null keeps
    /// the chirp, which is what a fixture usually wants: a standard is a curve in frequency, so a
    /// chirp-captured one corrects a stepped measurement unchanged and in a fraction of the time.
    /// </summary>
    string? CalibrationStimulus = null,
    double StartFreqHz = 20.0,
    double EndFreqHz = 20000.0,
    int StepsPerOctave = 5,
    bool AdaptiveResolution = true,
    int SteppedFftSize = SteppedResponseRequest.DefaultFftSize,
    double SteppedSampleRateHz = SteppedResponseRequest.DefaultSampleRateHz)
{
    /// <summary>
    /// The measurement this request asks for.
    ///
    /// <para>A method rather than inline in the endpoint so it can be tested, and the reason is worth
    /// restating: <b>the mapping is written out field by field, and a field left out of it is not an
    /// error — it is a default.</b> The run then succeeds and returns a perfectly plausible
    /// measurement of settings nobody asked for. That is exactly how <c>ChirpStartHz</c> was lost
    /// from the frequency response: three runs at 2, 5 and 8 Hz came back identical to 0.05 dB, and
    /// only the right channel still reading the generator below the supposed start frequency gave it
    /// away. A field added to <see cref="ImpedanceRequest"/> has to be added here too, and
    /// <c>BothRecordsStillHaveTheFieldsTheMappingTestChecks</c> is what makes forgetting fail
    /// loudly.</para>
    /// </summary>
    public ImpedanceRequest ToMeasurement(
        ImpedanceMethod method,
        ImpedanceStimulus stimulus,
        ThdSweepLevelUnit levelUnit,
        ImpedanceCalTarget calTarget,
        ImpedanceCableMode cableMode,
        ImpedanceStimulus calibrationStimulus) =>
        new(method,
            stimulus,
            ReferenceOhms,
            RightChannelIsReference,
            FrequencySpanHz,
            ResolutionMultiplier,
            LevelValue,
            levelUnit,
            SmoothingDenominator,
            AverageCount,
            Continuous,
            ChirpStartHz,
            calTarget,
            cableMode,
            FixedCableOhms,
            FixedCableMillihenry,
            AnalyzerInputOhms,
            ApplySweepGate,
            OpenCal,
            ShortCal,
            calibrationStimulus,
            StartFreqHz,
            EndFreqHz,
            StepsPerOctave,
            AdaptiveResolution,
            SteppedFftSize,
            SteppedSampleRateHz,
            CalibrationLevelValue);
}

public sealed record SaveImpedanceRequest(long MeasurementTypeId, string Name, string? Notes);
