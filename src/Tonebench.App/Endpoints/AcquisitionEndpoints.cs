using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

public static class AcquisitionEndpoints
{
    public static void MapAcquisitionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/acquisition");

        group.MapPost("/start", async (AcquisitionStartRequest request, DeviceSessionService session, SpectrumPipeline pipeline, CancellationToken ct) =>
        {
            try
            {
                // Backs both Run and Single: SingleShot is a one-off capture mode, not a sticky
                // setting, so it is whatever this start asks for — Run always free-runs even if a
                // previous Single left the pipeline holding. Set *before* the start, because the
                // start's own flushes (Start()'s bare one, then the capture delay's) are what arm
                // the capture: they clear the held flags and the averagers, and the settle discard
                // is the last flush to land. Single used to be armed by a separate call after this
                // one that did a bare Flush(), and SampleRingBuffer.Reset(0) zeroed the settle
                // discard that had just been booked — so a Single's first record started at the
                // tone step while Run's started a capture delay later, which on a tube amp's
                // 12 Hz high-pass ringing read as a bump around 50 Hz that no averaging removed.
                pipeline.Options = pipeline.Options with { SingleShot = request.SingleShot ?? false };

                var config = new AcquisitionConfig(request.SampleRateHz, request.ChannelCount, request.BlockFrameCount ?? 4096);
                await session.StartAcquisitionAsync(config, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/stop", async (DeviceSessionService session) =>
        {
            await session.StopAcquisitionAsync();
            return Results.Ok(session.GetStatus());
        });

        // Every running measurement, cancelled and unwound, before whatever the client does next --
        // switching measurements, most of all. A sweep left running restarts the generator and the
        // stream for its next point right after a plain /stop, and collides with the measurement the
        // user has moved on to (see IMeasurementRun). The session's own acquisition is left to /stop.
        group.MapPost("/cancel-runs", async (DeviceSessionService session, IEnumerable<IMeasurementRun> runs) =>
        {
            foreach (var run in runs)
            {
                if (!ReferenceEquals(run, session) && run.Running)
                    await run.CancelAsync();
            }

            return Results.Ok(session.GetStatus());
        });

        group.MapPost("/spectrum-config", async (
            SpectrumConfigRequest request, SpectrumPipeline pipeline, DeviceSessionService session, CancellationToken ct) =>
        {
            var current = pipeline.Options;

            int fftSize = request.FftSize ?? current.FftSize;
            if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
                return Results.BadRequest("FftSize must be a power of two.");

            if (!EnumParsing.TryParse<WindowType>(request.Window, current.Window, out var window, out var windowError))
                return Results.BadRequest(windowError);

            if (!EnumParsing.TryParse<AveragingMode>(request.Averaging, current.Averaging, out var averaging, out var averagingError))
                return Results.BadRequest(averagingError);

            double measurementLoHz = request.MeasurementLoFreqHz ?? current.MeasurementLoFreqHz;
            double measurementHiHz = request.MeasurementHiFreqHz ?? current.MeasurementHiFreqHz;
            if (measurementLoHz < 0.0 || measurementHiHz <= measurementLoHz)
                return Results.BadRequest("MeasurementHiFreqHz must be greater than MeasurementLoFreqHz, and neither may be negative.");

            // Null leaves the IMD spec alone (patch semantics like every other field); Enabled=false clears it.
            IntermodulationSpec? intermodulation = current.Intermodulation;
            if (request.Intermodulation is { } imd)
            {
                if (!imd.Enabled)
                {
                    intermodulation = null;
                }
                else
                {
                    if (!EnumParsing.TryParse<IntermodulationMethod>(imd.Method, IntermodulationMethod.Smpte, out var method, out var methodError))
                        return Results.BadRequest(methodError);
                    int maxOrder = imd.MaxOrder ?? IntermodulationPresets.DefaultMaxOrder(method);
                    if (maxOrder < 2 || maxOrder > 12)
                        return Results.BadRequest("Intermodulation MaxOrder must be between 2 and 12.");
                    if (!(imd.ToneLowHz > 0.0) || !(imd.ToneHighHz > imd.ToneLowHz))
                        return Results.BadRequest("Intermodulation tones must satisfy 0 < ToneLowHz < ToneHighHz.");
                    if (imd.MaxProductFreqHz is { } maxProduct && !(maxProduct > 0.0))
                        return Results.BadRequest("Intermodulation MaxProductFreqHz must be positive when given.");

                    intermodulation = new IntermodulationSpec(imd.ToneLowHz, imd.ToneHighHz, method, maxOrder, imd.MaxProductFreqHz);
                }
            }

            if (!TryMergeWeighting(current.Weighting, request.Weighting, out var weighting, out var weightingError))
                return Results.BadRequest(weightingError);

            pipeline.Options = current with
            {
                Intermodulation = intermodulation,
                Weighting = weighting,
                MeasurementLoFreqHz = measurementLoHz,
                MeasurementHiFreqHz = measurementHiHz,
                FftSize = fftSize,
                Window = window,
                Averaging = averaging,
                AverageCount = request.AverageCount ?? current.AverageCount,
                TargetFrameRateHz = request.TargetFrameRateHz ?? current.TargetFrameRateHz,
                OtherPeaksCount = request.OtherPeaksCount ?? current.OtherPeaksCount,
                ProcessLeft = request.ProcessLeft ?? current.ProcessLeft,
                ProcessRight = request.ProcessRight ?? current.ProcessRight,
                ScopeResidual = request.ScopeResidual ?? current.ScopeResidual,
            };

            // Which frequency is coherent depends on the FFT size, so a size change has to re-snap
            // the generator or the tone silently drifts back off-bin.
            if (fftSize != current.FftSize)
                await session.ReapplyGeneratorAsync(ct);

            return Results.Ok(pipeline.Options);
        });

        group.MapGet("/spectrum-config", (SpectrumPipeline pipeline) => Results.Ok(pipeline.Options));

        // The weighting in effect as a curve, for the chart's "Weighting curve" overlay. Sampled here
        // rather than ported to the client: the curves and the user tables live server-side, and one
        // source of truth is the whole point of a curve drawn next to the trace it weights.
        group.MapGet("/weighting-curve", (
            SpectrumPipeline pipeline, UserWeightingSetting userWeighting, double? loHz, double? hiHz, int? points) =>
        {
            double lo = loHz ?? WeightingCurveResponse.DefaultLoHz;
            double hi = hiHz ?? WeightingCurveResponse.DefaultHiHz;
            int count = Math.Clamp(points ?? WeightingCurveResponse.DefaultPoints, 2, 4096);
            if (!(lo > 0.0) || !(hi > lo))
                return Results.BadRequest("loHz must be positive and hiHz greater than loHz.");

            return Results.Ok(WeightingCurveResponse.Sample(pipeline.Options.Weighting, userWeighting, lo, hi, count));
        });

        // Backs the "Clear" button next to Save measurement result: discards the retained (stopped)
        // spectrum so the chart goes back to "Waiting for data..." and Save measurement result has
        // nothing stale left to save. Meaningful only while stopped -- the client disables the
        // button while acquiring, same as Save measurement result itself.
        group.MapPost("/clear", (SpectrumPipeline pipeline) =>
        {
            pipeline.Flush();
            return Results.Ok();
        });
    }

    /// <summary>
    /// Patch semantics for the weighting, like every other field of the config request: a null
    /// <paramref name="patch"/> keeps <paramref name="current"/>, and within it a null member keeps
    /// that member. Static and internal so a test can pin it without an HTTP round trip.
    /// </summary>
    internal static bool TryMergeWeighting(WeightingSpec current, WeightingConfigRequest? patch, out WeightingSpec merged, out string error)
    {
        merged = current;
        error = "";
        if (patch is null)
            return true;

        if (!EnumParsing.TryParse<WeightingStandard>(patch.Standard, current.Standard, out var standard, out error))
            return false;

        merged = new WeightingSpec(standard, patch.User1 ?? current.User1, patch.User2 ?? current.User2);
        return true;
    }
}

/// <param name="SingleShot">
/// True for the FFT screen's Single button: capture the configured averages, then hold (see
/// <see cref="SpectrumPipelineOptions.SingleShot"/>). Null or false free-runs.
/// </param>
public sealed record AcquisitionStartRequest(double SampleRateHz, int ChannelCount, int? BlockFrameCount, bool? SingleShot = null);

public sealed record SpectrumConfigRequest(
    int? FftSize, string? Window, string? Averaging, int? AverageCount, int? TargetFrameRateHz, int? OtherPeaksCount,
    bool? ProcessLeft, bool? ProcessRight,
    /// <summary>Measurement bandwidth for THD+N/SNR/noise — see <see cref="SpectrumPipelineOptions.MeasurementLoFreqHz"/>.</summary>
    double? MeasurementLoFreqHz = null, double? MeasurementHiFreqHz = null,
    /// <summary>Null leaves the current IMD spec alone; see <see cref="IntermodulationConfigRequest"/>.</summary>
    IntermodulationConfigRequest? Intermodulation = null,
    /// <summary>Null leaves the weighting alone; see <see cref="WeightingConfigRequest"/>.</summary>
    WeightingConfigRequest? Weighting = null,
    /// <summary>Send the scope tail with its fundamental removed as well — see <see cref="SpectrumPipelineOptions.ScopeResidual"/>. Null leaves it alone.</summary>
    bool? ScopeResidual = null);

/// <summary>
/// The combined weighting curve, log-spaced from <see cref="DefaultLoHz"/> to <see cref="DefaultHiHz"/>
/// by default. <paramref name="Weighted"/> false means nothing is selected (or only empty user slots)
/// and the arrays are empty. A gain of minus infinity (A, C and 468 at DC) is clamped to
/// <see cref="FloorDb"/> so the document stays JSON.
/// </summary>
public sealed record WeightingCurveResponse(bool Weighted, double[] FreqHz, double[] GainDb)
{
    public const double DefaultLoHz = 5.0;
    public const double DefaultHiHz = 100_000.0;
    public const int DefaultPoints = 400;
    public const double FloorDb = -200.0;

    public static WeightingCurveResponse Sample(WeightingSpec spec, UserWeightingSetting? userWeighting, double loHz, double hiHz, int points)
    {
        var curve = SpectrumPipeline.BuildCurve(spec, userWeighting);
        if (curve is null)
            return new WeightingCurveResponse(false, [], []);

        var freqHz = new double[points];
        var gainDb = new double[points];
        double logLo = Math.Log10(loHz);
        double step = (Math.Log10(hiHz) - logLo) / (points - 1);
        for (int i = 0; i < points; i++)
        {
            freqHz[i] = Math.Pow(10.0, logLo + (i * step));
            double db = curve.GainDb(freqHz[i]);
            gainDb[i] = double.IsFinite(db) ? Math.Max(db, FloorDb) : FloorDb;
        }

        return new WeightingCurveResponse(true, freqHz, gainDb);
    }
}

/// <summary>
/// The noise weighting patch — see <see cref="SpectrumPipelineOptions.Weighting"/>. Every member is
/// optional and a null one keeps the current value: <paramref name="Standard"/> is a
/// <see cref="WeightingStandard"/> name, <paramref name="User1"/>/<paramref name="User2"/> whether
/// the user tables are in effect.
/// </summary>
public sealed record WeightingConfigRequest(string? Standard = null, bool? User1 = null, bool? User2 = null);

/// <summary>
/// The two-tone intermodulation spec the FFT screen derives from its staged stimulus — see
/// <see cref="SpectrumPipelineOptions.Intermodulation"/>. <paramref name="Enabled"/> false clears
/// the spec (the other fields are then ignored); <paramref name="Method"/> is an
/// <see cref="IntermodulationMethod"/> name, <paramref name="MaxOrder"/> defaults to the method's
/// own, and a null <paramref name="MaxProductFreqHz"/> means the measurement band's upper edge.
/// </summary>
public sealed record IntermodulationConfigRequest(
    bool Enabled, double ToneLowHz = 0.0, double ToneHighHz = 0.0, string? Method = null, int? MaxOrder = null, double? MaxProductFreqHz = null);
