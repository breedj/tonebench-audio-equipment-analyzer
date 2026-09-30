using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Measures crosstalk between a stereo DUT's channels: drive one channel (the aggressor) with a
/// stepped sine at a fixed level, read the tone at its bin on <em>both</em> inputs at every point,
/// and report victim − aggressor in dB against frequency. The tutorial's <i>AMP Crosstalk</i>.
///
/// <para><b>A stepped response with one output muted.</b> The acquisition is exactly
/// <see cref="SteppedResponseRunner"/>'s — the same grid, the same level probe, the same feed-forward
/// input ranging and the same capture that outruns the stimulus change — through the helpers both
/// share in <see cref="SteppedCaptureSupport"/>. What differs is the result: one trace per pass,
/// under its own <see cref="FrequencyResponseTraceKind"/>, with no phase (a crosstalk path's phase is
/// not a figure anyone quotes) and no bandwidth corners (there is no passband to find them against).
/// <see cref="CrosstalkDirection.Both"/> runs the two directions as two passes of one run so the two
/// curves land on one chart and one saved result.</para>
///
/// <para>Two things about the level are deliberate. The level probe reads the <b>aggressor</b>
/// input — the victim is 60–90 dB down and would trip the probe's silence guard — and in the
/// output-referred modes it is solved <em>per pass</em>, since the two channels of an amplifier need
/// not have the same gain. And the input range follows the aggressor: one range serves both inputs
/// and the louder one decides, so the victim's reading sits deep in the range — which is why a
/// crosstalk figure below the analyzer's own floor at that range reads as the floor, not as the DUT.</para>
///
/// <para>Its status is a <see cref="FrequencyResponseStatus"/>, because the client's response state
/// already has every field a stepped run needs and the frames are the response frames with their
/// own origin flag (<see cref="StreamFlags.Crosstalk"/>); <c>PassesCompleted</c> counts passes and the
/// point counters run over every pass, so the progress line stays monotone through a two-pass run.</para>
/// </summary>
public sealed class CrosstalkRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<CrosstalkRunner> logger) : IMeasurementRun
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private FrequencyResponsePhase _phase = FrequencyResponsePhase.Idle;
    private int _passesCompleted;
    private int _pointsMeasured;
    private int _pointCount;
    private int _averageCount;
    private double _generatorAmpDbv;
    private double _binHz;
    private double _sampleRateHz;
    private int _fftSize;
    private string? _error;
    private List<FrequencyResponseTrace> _traces = [];
    private Dictionary<FrequencyResponseTraceKind, double> _generatorAmpDbvByKind = [];
    private CrosstalkRequest? _lastRequest;

    /// <summary>The request the most recent run used — saving needs the as-run configuration.</summary>
    public CrosstalkRequest? LastRequest => _lastRequest;

    /// <summary>
    /// The generator level each pass ran at, by the trace it produced. In the output-referred modes
    /// the two passes solve their own, and a saved result records both.
    /// </summary>
    public IReadOnlyDictionary<FrequencyResponseTraceKind, double> GeneratorAmpDbvByKind
    {
        get
        {
            lock (_stateLock)
                return new Dictionary<FrequencyResponseTraceKind, double>(_generatorAmpDbvByKind);
        }
    }

    /// <summary>Every phase in which the device is busy on this measurement's behalf — see <see cref="SteppedResponseRunner.Running"/>.</summary>
    public bool Running => _phase
        is FrequencyResponsePhase.DeterminingLevel
        or FrequencyResponsePhase.DeterminingRange
        or FrequencyResponsePhase.Sweeping;

    public FrequencyResponseStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new FrequencyResponseStatus(
                    Running, _phase, Continuous: false, PassesCompleted: _passesCompleted,
                    AveragesTaken: _averageCount, AverageCount: _averageCount,
                    _generatorAmpDbv, _binHz, _sampleRateHz, _fftSize,
                    LatencySamples: null, _error, [.. _traces],
                    PointsMeasured: _pointsMeasured, PointCount: _pointCount,
                    PhaseKind: ResponsePhaseKind.None, RightChannelIsReference: false);
            }
        }
    }

    /// <exception cref="InvalidOperationException">A run is already going, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not something that can be measured.</exception>
    public async Task<CrosstalkStarted> StartAsync(CrosstalkRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("A crosstalk measurement is already running.");

            var steps = BuildSteps(request);
            var passes = request.Passes;

            lock (_stateLock)
            {
                // The phase the run will actually start in — see SteppedResponseRunner.StartAsync.
                _phase = request.SolvesGeneratorLevel
                    ? FrequencyResponsePhase.DeterminingLevel
                    : WillOwnInputRange()
                        ? FrequencyResponsePhase.DeterminingRange
                        : FrequencyResponsePhase.Sweeping;
                _passesCompleted = 0;
                _pointsMeasured = 0;
                _pointCount = steps.Length * passes.Count;
                _averageCount = request.AverageCount;
                _generatorAmpDbv = request.SolvesGeneratorLevel ? SteppedCaptureSupport.ProbeStartDbv : request.LevelDbv;
                _sampleRateHz = request.EffectiveSampleRateHz;
                _fftSize = steps[0].FftSize;
                _binHz = request.EffectiveSampleRateHz / steps[0].FftSize;
                _error = null;
                _traces = [];
                _generatorAmpDbvByKind = [];
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new CrosstalkStarted(
                passes.Count,
                steps.Length,
                [.. steps.Select(s => s.FrequencyHz)],
                request.EffectiveSampleRateHz,
                request.AverageCount,
                passes.Count * EstimateSeconds(request, steps));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops a running measurement and waits for it to unwind. Finished passes and the points of the partial one are kept.</summary>
    public async Task CancelAsync()
    {
        var cts = _cts;
        var task = _runTask;
        if (cts is null)
            return;

        await cts.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
    }

    /// <exception cref="InvalidOperationException">A measurement is running.</exception>
    public FrequencyResponseStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before clearing.");

            _traces = [];
            _generatorAmpDbvByKind = [];
            _phase = FrequencyResponsePhase.Idle;
            _passesCompleted = 0;
            _pointsMeasured = 0;
            _pointCount = 0;
            _averageCount = 0;
            _generatorAmpDbv = 0.0;
            _error = null;

            return Status;
        }
    }

    private bool WillOwnInputRange() =>
        session.RunnerOwnsInputRange;

    private async Task RunAsync(CrosstalkRequest request, SteppedStepPlan[] steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;

        var terminalPhase = FrequencyResponsePhase.Failed;
        string? terminalError = null;

        var passes = request.Passes;
        // Every pass's points, in pass order; finished passes stay when a later one is cancelled.
        var pointsByPass = new List<(CrosstalkPass Pass, List<CrosstalkPoint> Points)>(passes.Count);

        try
        {
            logger.LogInformation(
                "Crosstalk starting: {Direction}, {StartHz:0.##}-{EndHz:0.##} Hz, {StepsPerOctave}/octave, {Points} points per pass, "
                    + "{SampleRateHz:0} Hz, adaptive {Adaptive} ({FftDescription}), {Averages} averages, "
                    + "level {LevelValue:0.##} {LevelUnit} ({LevelMode})",
                request.Direction, request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, steps.Length,
                request.EffectiveSampleRateHz, request.AdaptiveResolution,
                request.AdaptiveResolution
                    ? $"{steps.Max(s => s.FftSize)}-{steps.Min(s => s.FftSize)} points"
                    : $"fixed {request.FftSize} points",
                request.AverageCount, request.LevelValue, request.LevelUnit, request.LevelMode);

            pipeline.Options = SteppedCaptureSupport.BuildSteppedOptions(
                savedOptions, request.FftSize, request.Window, request.AverageCount, request.StartFreqHz, request.EffectiveSampleRateHz);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.EffectiveSampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            bool predictionOwnsRange = WillOwnInputRange();

            void TakeOverRangingFromStepDown()
            {
                if (stepDownSuppressed || !predictionOwnsRange || session.AutoRangeStepDownEnabled is not true)
                    return;

                session.SetAutoRangeStepDownEnabled(false);
                stepDownSuppressed = true;
                logger.LogInformation(
                    "Input range is now predicted per point from the measured level; autorange step-down "
                        + "suppressed for the rest of the run (step-up stays live as the clip backstop)");
            }

            for (int passIndex = 0; passIndex < passes.Count; passIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var pass = passes[passIndex];
                var points = new List<CrosstalkPoint>(steps.Length);
                pointsByPass.Add((pass, points));
                int pointBase = passIndex * steps.Length;

                // The level is solved per pass: the aggressor is a different amplifier channel each
                // time, and nothing says the two have the same gain. Its last reading seeds the
                // range predictor, as on the stepped response.
                var (generatorAmpDbv, predictorPeakDbv, predictorGenDbv) = await ResolveGeneratorAmplitudeAsync(request, pass, ct);
                SetGeneratorAmpDbv(generatorAmpDbv);
                lock (_stateLock)
                    _generatorAmpDbvByKind[pass.Kind] = generatorAmpDbv;

                if (predictionOwnsRange && double.IsNegativeInfinity(predictorPeakDbv))
                {
                    predictorPeakDbv = await ProbeInputRangeAsync(request, pass, generatorAmpDbv, ct);
                    predictorGenDbv = generatorAmpDbv;
                }

                if (!double.IsNegativeInfinity(predictorPeakDbv))
                    TakeOverRangingFromStepDown();

                // Never more sensitive than a range already used in this pass. Re-seeded per pass:
                // the second pass drives the other channel, whose level is its own.
                double rangeRatchetDbv = predictionOwnsRange && !double.IsNegativeInfinity(predictorPeakDbv)
                    ? session.InputRangeDbv
                    : double.NegativeInfinity;

                SetPhase(FrequencyResponsePhase.Sweeping);

                for (int i = 0; i < steps.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var step = steps[i];
                    SetProgress(pointBase + i);
                    BroadcastProgress(pointBase + i, _pointCount, FrequencyResponsePhase.Sweeping);

                    // Feed-forward, monotone ranging — see SteppedResponseRunner.RunAsync for why the
                    // range never steps back down within a pass. The predictor is the aggressor's
                    // peak, because one range serves both inputs and the louder one decides.
                    if (predictionOwnsRange && !double.IsNegativeInfinity(predictorPeakDbv))
                    {
                        double predicted = InputRangeSelector.For(session.Capabilities.InputRangesDbv, predictorPeakDbv);
                        rangeRatchetDbv = Math.Max(predicted, rangeRatchetDbv);
                        await SteppedCaptureSupport.ApplyInputRangeAsync(session, rangeRatchetDbv, ct);
                    }

                    long dropsBefore = session.DroppedRxChunks;
                    double inputRangeBefore = session.InputRangeDbv;

                    var snapshots = await SteppedCaptureSupport.CaptureWithStimulusAsync(
                        session, pipeline,
                        SteppedCaptureSupport.ToneConfig(step.FrequencyHz, generatorAmpDbv, pass.GenerateLeft, pass.GenerateRight),
                        step.FftSize, CaptureTimeout(request, step.FftSize), ct);

                    long dropsDuring = session.DroppedRxChunks - dropsBefore;
                    if (dropsDuring > 0)
                    {
                        logger.LogWarning(
                            "Crosstalk {Pass} point {Index}/{Total} at {FreqHz:0.##} Hz ({FftSize} points) captured across "
                                + "{Drops} dropped RX chunks — its record is spliced and its level is not trustworthy",
                            pass.Label, i + 1, steps.Length, step.FrequencyHz, step.FftSize, dropsDuring);
                    }

                    double inputRangeAfter = session.InputRangeDbv;
                    if (Math.Abs(inputRangeAfter - inputRangeBefore) > SteppedCaptureSupport.RangeEpsilonDb)
                    {
                        logger.LogInformation(
                            "Crosstalk {Pass} point {Index}/{Total} at {FreqHz:0.##} Hz: the input range moved "
                                + "{FromDbv:0.#} -> {ToDbv:0.#} dBV during the capture, so the point was re-measured at the new range",
                            pass.Label, i + 1, steps.Length, step.FrequencyHz, inputRangeBefore, inputRangeAfter);
                        rangeRatchetDbv = Math.Max(rangeRatchetDbv, inputRangeAfter);
                    }

                    points.Add(ReadPoint(step, snapshots, pass));

                    double pointPeakDbv = SweepCaptureSupport.MeasuredPeakDbv(snapshots);
                    if (!double.IsNegativeInfinity(pointPeakDbv))
                    {
                        predictorPeakDbv = pointPeakDbv;
                        predictorGenDbv = generatorAmpDbv;
                        TakeOverRangingFromStepDown();
                    }

                    PublishTraces(pointsByPass);
                    SetProgress(pointBase + i + 1);
                }

                lock (_stateLock)
                    _passesCompleted = passIndex + 1;

                logger.LogInformation(
                    "Crosstalk {Pass} measured: {Points} points at {GenDbv:0.##} dBV generator, predictor last saw "
                        + "{PeakDbv:0.##} dBV at {PredictorGenDbv:0.##} dBV",
                    pass.Label, points.Count, generatorAmpDbv, predictorPeakDbv, predictorGenDbv);
            }

            BroadcastProgress(_pointCount, _pointCount, FrequencyResponsePhase.Sweeping);
            terminalPhase = FrequencyResponsePhase.Finished;
        }
        catch (OperationCanceledException)
        {
            terminalPhase = FrequencyResponsePhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Crosstalk measurement failed");
            terminalPhase = FrequencyResponsePhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            // The same unwind as the stepped response, in the same order and for the same reasons:
            // silence first, restore step-down, stop what this run started, restore the options,
            // and only then declare the run over.
            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the crosstalk run");
            }

            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the crosstalk run");
                }
            }

            if (startedAcquisition)
            {
                try
                {
                    await session.StopAcquisitionAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not stop the acquisition the crosstalk run started");
                }
            }

            pipeline.Options = savedOptions;

            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_pointsMeasured, _pointCount, terminalPhase);
        }
    }

    private static SteppedStepPlan[] BuildSteps(CrosstalkRequest request) =>
        SteppedCaptureSupport.BuildSteps(
            request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, request.EffectiveSampleRateHz,
            request.MaxFrequencyHz, request.AdaptiveResolution, request.FftSize, request.FftSizeAt);

    /// <summary>
    /// The generator amplitude for one pass: direct, or solved by probing the <b>aggressor</b> input
    /// at 1 kHz — the channel that is driven, whichever it is. See <see cref="SteppedCaptureSupport.ProbeGeneratorLevelAsync"/>.
    /// </summary>
    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        CrosstalkRequest request, CrosstalkPass pass, CancellationToken ct)
    {
        if (!request.SolvesGeneratorLevel)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        int probeFftSize = request.FftSizeAt(SteppedCaptureSupport.ProbeFrequencyHz);
        var result = await SteppedCaptureSupport.ProbeGeneratorLevelAsync(
            session, pipeline, pass.Aggressor, request.LevelDbv, request.EffectiveSampleRateHz, probeFftSize,
            pass.GenerateLeft, pass.GenerateRight, CaptureTimeout(request, probeFftSize),
            (phase, done, total, genDbv) =>
            {
                SetGeneratorAmpDbv(genDbv);
                BroadcastProgress(done, total, phase);
            },
            ct);

        SetGeneratorAmpDbv(result.GenDbv);
        return result;
    }

    private async Task<double> ProbeInputRangeAsync(CrosstalkRequest request, CrosstalkPass pass, double generatorAmpDbv, CancellationToken ct)
    {
        if (session.Capabilities.InputRangesDbv.Count == 0)
            return double.NegativeInfinity;

        SetPhase(FrequencyResponsePhase.DeterminingRange);
        BroadcastProgress(0, 1, FrequencyResponsePhase.DeterminingRange);

        double probeFreqHz = Math.Clamp(
            SteppedCaptureSupport.ProbeFrequencyHz, request.StartFreqHz, Math.Min(request.EndFreqHz, request.MaxFrequencyHz));
        int probeFftSize = Math.Min(request.FftSizeAt(probeFreqHz), SteppedCaptureSupport.MaxProbeFftSize);
        var probeTone = CoherentFrequency.Nearest(probeFreqHz, request.EffectiveSampleRateHz, probeFftSize);

        return await SteppedCaptureSupport.ProbeInputRangeAsync(
            session, pipeline,
            SteppedCaptureSupport.ToneConfig(probeTone.FrequencyHz, generatorAmpDbv, pass.GenerateLeft, pass.GenerateRight),
            probeTone.FrequencyHz, generatorAmpDbv, probeFftSize, CaptureTimeout(request, probeFftSize), logger, ct);
    }

    private TimeSpan CaptureTimeout(CrosstalkRequest request, int fftSize) =>
        SteppedCaptureSupport.CaptureTimeout(fftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0);

    private double EstimateSeconds(CrosstalkRequest request, SteppedStepPlan[] steps) =>
        steps.Sum(s => SteppedCaptureSupport.PointSeconds(s.FftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0));

    /// <summary>
    /// One measured point: the tone's level on both inputs at the commanded coherent bin (the
    /// averaged amplitude spectrum, in the app's peak/√2 dBV convention), and their difference.
    /// </summary>
    private static CrosstalkPoint ReadPoint(SteppedStepPlan step, SpectrumSnapshot[] snapshots, CrosstalkPass pass)
    {
        double aggressorDbv = ResponseCaptureSupport.LevelAt(snapshots, pass.Aggressor, step.Bin);
        double victimDbv = ResponseCaptureSupport.LevelAt(snapshots, pass.Victim, step.Bin);

        if (double.IsNaN(aggressorDbv) || double.IsNaN(victimDbv))
        {
            throw new InvalidOperationException(
                $"A crosstalk point at {step.FrequencyHz:0.##} Hz needs both channels' spectra, and the pipeline returned "
                    + $"{(double.IsNaN(aggressorDbv) ? "no aggressor" : "aggressor")} and {(double.IsNaN(victimDbv) ? "no victim" : "victim")}.");
        }

        // A victim channel carrying exact digital silence — the simulator with Noise off — reads
        // thousands of dB down, which is not a measurement of anything and would put the chart's
        // autofit and the CSV somewhere absurd. −200 dB is below what any converter could report,
        // so clamping there loses nothing real; a real analyzer's floor is ~100 dB above it.
        double crosstalkDb = Math.Max(FloorDb, victimDbv - aggressorDbv);
        return new CrosstalkPoint(step.FrequencyHz, aggressorDbv, victimDbv, crosstalkDb);
    }

    /// <summary>The lowest crosstalk a point can report; see <see cref="ReadPoint"/>.</summary>
    public const double FloorDb = -200;

    /// <summary>Rebuilds every pass's trace from what has been measured and pushes them out — per point, so a minutes-long run is visibly alive.</summary>
    private void PublishTraces(List<(CrosstalkPass Pass, List<CrosstalkPoint> Points)> pointsByPass)
    {
        var traces = new List<FrequencyResponseTrace>(pointsByPass.Count);
        foreach (var (pass, points) in pointsByPass)
        {
            // BandwidthAnalyzer's peak search needs two samples.
            if (points.Count < 2)
                continue;

            var freqHz = new double[points.Count];
            var crosstalkDb = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                freqHz[i] = points[i].FreqHz;
                crosstalkDb[i] = points[i].CrosstalkDb;
            }

            traces.Add(SteppedCaptureSupport.BuildPointTrace(pass.Kind, freqHz, crosstalkDb, new double[points.Count], reference: null));
        }

        if (traces.Count == 0)
            return;

        lock (_stateLock)
            _traces = traces;

        foreach (var trace in traces)
            hub.Broadcast(StreamFrame.EncodeFrequencyResponse(trace, StreamFlags.Crosstalk));
    }

    private void SetPhase(FrequencyResponsePhase phase, string? error = null)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _error = error;
        }
    }

    private void SetProgress(int pointsMeasured)
    {
        lock (_stateLock)
            _pointsMeasured = pointsMeasured;
    }

    private void SetGeneratorAmpDbv(double dbv)
    {
        lock (_stateLock)
            _generatorAmpDbv = dbv;
    }

    private void BroadcastProgress(int done, int total, FrequencyResponsePhase phase)
    {
        double generatorAmpDbv;
        lock (_stateLock)
            generatorAmpDbv = _generatorAmpDbv;

        hub.Broadcast(StreamFrame.EncodeFrequencyResponseProgress(done, total, phase, generatorAmpDbv, StreamFlags.Crosstalk));
    }

    /// <summary>One measured point of one pass.</summary>
    private readonly record struct CrosstalkPoint(double FreqHz, double AggressorDbv, double VictimDbv, double CrosstalkDb);
}
