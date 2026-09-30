using System.Numerics;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Measures an amplifier's output impedance and damping factor by the load-step method: the DUT's
/// gain is read with a stepped sine at a fixed output level into two different loads, and the
/// source impedance behind the two is solved at every point (<see cref="OutputImpedanceMath.Solve"/>).
/// The tutorial's <i>PWR Output Impedance</i>, with a second load in place of its open circuit by
/// default — see <see cref="OutputImpedanceRequest"/> for why.
///
/// <para><b>Two passes with a pause the user ends.</b> The first load's pass runs first — the level
/// target (1 W into 8 Ω) is defined into it and the probe finds the generator level there — then
/// the generator goes silent, the phase becomes <see cref="FrequencyResponsePhase.WaitingForLoadChange"/>
/// and the run blocks until <see cref="Continue"/> (the CONTINUE button) or a cancel. The
/// acquisition stays up and the input range stays put throughout, which is the point of the next
/// paragraph. Nothing else in the app pauses for the user; a calibration is a separate run.</para>
///
/// <para><b>One input range for both passes.</b> A damping factor of 286 is a gain difference of
/// 0.03 dB between the passes, so a range step between them — ~0.1 dB of absolute gain error on
/// the QA403 — would read as three times the whole answer. The range is therefore chosen once, in
/// the first pass, from the probed level plus <see cref="OpenHeadroomDb"/> (a lighter or absent
/// load raises the output by at most a factor of two for any DF ≥ 1), and held; autorange step-down
/// is suppressed and only step-up stays live as the clip backstop. If it fires, the points measured
/// either side of it are named in <c>Warning</c> rather than quietly trusted.</para>
///
/// <para><b>Magnitude or complex.</b> Unreferenced, each point is the tone's averaged level on the
/// left input and the ratio is one of magnitudes — the QA40x's figure. Referenced, the right output
/// is looped to the right input, each gain also carries <c>arg(L/R)</c> and the ratio is complex;
/// only then does a reactive Zout (an output coil) come out at its true size, since a reactance in
/// quadrature with the load barely moves the level — see <see cref="OutputImpedanceRequest"/>.</para>
/// </summary>
public sealed class OutputImpedanceRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<OutputImpedanceRunner> logger) : IMeasurementRun
{
    /// <summary>
    /// Headroom added to the first pass's level when the input range is chosen, so the second pass —
    /// louder by the very gain ratio being measured when its load is lighter or absent — reads at the
    /// same range. 6 dB is a factor of two, i.e. a damping factor of 1; anything a load-step
    /// measurement is used on is far above that.
    /// </summary>
    public const double OpenHeadroomDb = 6.0;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TaskCompletionSource? _continue;

    private FrequencyResponsePhase _phase = FrequencyResponsePhase.Idle;
    private int _passesCompleted;
    private int _pointsMeasured;
    private int _pointCount;
    private int _averageCount;
    private double _generatorAmpDbv;
    private double? _inputRangeDbv;
    private double _binHz;
    private double _sampleRateHz;
    private int _fftSize;
    private double _loadOhms;
    private double? _secondLoadOhms;
    private double _nominalOhms;
    private bool _rightChannelIsReference;
    private string? _error;
    private string? _warning;
    private ImpedanceTrace? _trace;
    private FrequencyResponseTrace? _firstGainTrace;
    private FrequencyResponseTrace? _secondGainTrace;
    private double? _medianOhms;
    private double? _zoutAt1kOhms;
    private double? _dampingFactorAt1k;
    private double? _firstGainAt1kDb;
    private double? _secondGainAt1kDb;
    private OutputImpedanceRequest? _lastRequest;

    /// <summary>The request the most recent run used — saving needs the as-run configuration.</summary>
    public OutputImpedanceRequest? LastRequest => _lastRequest;

    /// <summary>The two gain passes, for the save path — stored beside the Zout curve so a result keeps what it was derived from. Null until the run has them.</summary>
    public (FrequencyResponseTrace? First, FrequencyResponseTrace? Second) GainTraces
    {
        get
        {
            lock (_stateLock)
                return (_firstGainTrace, _secondGainTrace);
        }
    }

    /// <summary>
    /// Every phase in which the device is busy on this measurement's behalf — including the wait for
    /// the load change, during which the run still owns the session, the pipeline options and the
    /// input range.
    /// </summary>
    public bool Running => _phase
        is FrequencyResponsePhase.DeterminingLevel
        or FrequencyResponsePhase.DeterminingRange
        or FrequencyResponsePhase.Sweeping
        or FrequencyResponsePhase.WaitingForLoadChange;

    public OutputImpedanceStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new OutputImpedanceStatus(
                    Running, _phase, _passesCompleted, _pointsMeasured, _pointCount,
                    _generatorAmpDbv, _inputRangeDbv, _binHz, _sampleRateHz, _fftSize,
                    _loadOhms, _secondLoadOhms, _nominalOhms, _rightChannelIsReference,
                    _error, _warning, _trace,
                    _medianOhms, _zoutAt1kOhms, _dampingFactorAt1k, _firstGainAt1kDb, _secondGainAt1kDb);
            }
        }
    }

    /// <exception cref="InvalidOperationException">A run is already going, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not something that can be measured.</exception>
    public async Task<OutputImpedanceStarted> StartAsync(OutputImpedanceRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("An output-impedance measurement is already running.");

            var steps = BuildSteps(request);

            lock (_stateLock)
            {
                _phase = request.SolvesGeneratorLevel
                    ? FrequencyResponsePhase.DeterminingLevel
                    : WillOwnInputRange()
                        ? FrequencyResponsePhase.DeterminingRange
                        : FrequencyResponsePhase.Sweeping;
                _passesCompleted = 0;
                _pointsMeasured = 0;
                _pointCount = steps.Length * OutputImpedanceRequest.PassCount;
                _averageCount = request.AverageCount;
                _generatorAmpDbv = request.SolvesGeneratorLevel ? SteppedCaptureSupport.ProbeStartDbv : request.LevelDbv;
                _inputRangeDbv = null;
                _sampleRateHz = request.EffectiveSampleRateHz;
                _fftSize = steps[0].FftSize;
                _binHz = request.EffectiveSampleRateHz / steps[0].FftSize;
                _loadOhms = request.LoadOhms;
                _secondLoadOhms = request.SecondLoadOhms;
                _nominalOhms = request.NominalOhms;
                _rightChannelIsReference = request.RightChannelIsReference;
                _error = null;
                _warning = null;
                _trace = null;
                _firstGainTrace = null;
                _secondGainTrace = null;
                _medianOhms = null;
                _zoutAt1kOhms = null;
                _dampingFactorAt1k = null;
                _firstGainAt1kDb = null;
                _secondGainAt1kDb = null;
                _continue = null;
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new OutputImpedanceStarted(
                OutputImpedanceRequest.PassCount,
                steps.Length,
                [.. steps.Select(s => s.FrequencyHz)],
                request.EffectiveSampleRateHz,
                request.AverageCount,
                OutputImpedanceRequest.PassCount * EstimateSeconds(request, steps));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resumes a run that is waiting for the load to be changed — the CONTINUE button.
    /// </summary>
    /// <exception cref="InvalidOperationException">The measurement is not waiting.</exception>
    public void Continue()
    {
        TaskCompletionSource? waiter;
        lock (_stateLock)
        {
            if (_phase != FrequencyResponsePhase.WaitingForLoadChange || _continue is null)
                throw new InvalidOperationException("The measurement is not waiting for a load change.");
            waiter = _continue;
        }

        waiter.TrySetResult();
    }

    /// <summary>Stops a running measurement and waits for it to unwind. A run stopped before the open pass has no Zout to keep.</summary>
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
    public OutputImpedanceStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before clearing.");

            _trace = null;
            _firstGainTrace = null;
            _secondGainTrace = null;
            _phase = FrequencyResponsePhase.Idle;
            _passesCompleted = 0;
            _pointsMeasured = 0;
            _pointCount = 0;
            _averageCount = 0;
            _generatorAmpDbv = 0.0;
            _inputRangeDbv = null;
            _error = null;
            _warning = null;
            _medianOhms = null;
            _zoutAt1kOhms = null;
            _dampingFactorAt1k = null;
            _firstGainAt1kDb = null;
            _secondGainAt1kDb = null;

            return Status;
        }
    }

    private bool WillOwnInputRange() =>
        session.RunnerOwnsInputRange;

    private async Task RunAsync(OutputImpedanceRequest request, SteppedStepPlan[] steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;

        var terminalPhase = FrequencyResponsePhase.Failed;
        string? terminalError = null;

        var first = new List<GainPoint>(steps.Length);
        var second = new List<GainPoint>(steps.Length);

        try
        {
            logger.LogInformation(
                "Output impedance starting: {StartHz:0.##}-{EndHz:0.##} Hz, {StepsPerOctave}/octave, {Points} points per pass, "
                    + "{SampleRateHz:0} Hz, adaptive {Adaptive} ({FftDescription}), {Averages} averages, "
                    + "level {LevelValue:0.##} {LevelUnit} ({LevelMode}), loads {LoadOhms:0.##} ohm then {SecondLoad}, nominal {NominalOhms:0.##} ohm, "
                    + "{Method}",
                request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, steps.Length,
                request.EffectiveSampleRateHz, request.AdaptiveResolution,
                request.AdaptiveResolution
                    ? $"{steps.Max(s => s.FftSize)}-{steps.Min(s => s.FftSize)} points"
                    : $"fixed {request.FftSize} points",
                request.AverageCount, request.LevelValue, request.LevelUnit, request.LevelMode,
                request.LoadOhms, request.SecondLoadOhms is { } sl ? $"{sl:0.##} ohm" : "open", request.NominalOhms,
                request.RightChannelIsReference ? "complex (right channel is the reference)" : "magnitude only");

            pipeline.Options = SteppedCaptureSupport.BuildSteppedOptions(
                savedOptions, request.FftSize, request.Window, request.AverageCount, request.StartFreqHz, request.EffectiveSampleRateHz);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.EffectiveSampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            bool ownsRange = WillOwnInputRange();

            // The level is solved in the first pass, where the target is defined, and reused for the
            // second — the ratio between the passes is the measurement, so the drive must not move.
            var (generatorAmpDbv, probedPeakDbv, _) = await ResolveGeneratorAmplitudeAsync(request, ct);
            SetGeneratorAmpDbv(generatorAmpDbv);

            if (ownsRange)
            {
                if (double.IsNegativeInfinity(probedPeakDbv))
                    probedPeakDbv = await ProbeInputRangeAsync(request, generatorAmpDbv, ct);

                if (!double.IsNegativeInfinity(probedPeakDbv))
                {
                    double rangeDbv = InputRangeSelector.For(session.Capabilities.InputRangesDbv, probedPeakDbv + OpenHeadroomDb);
                    await SteppedCaptureSupport.ApplyInputRangeAsync(session, rangeDbv, ct);

                    if (session.AutoRangeStepDownEnabled is true)
                    {
                        session.SetAutoRangeStepDownEnabled(false);
                        stepDownSuppressed = true;
                    }

                    lock (_stateLock)
                        _inputRangeDbv = session.InputRangeDbv;

                    logger.LogInformation(
                        "Output impedance: input range {RangeDbv:0.#} dBV chosen from the first pass's level {PeakDbv:0.##} dBV "
                            + "plus {HeadroomDb} dB of headroom for the second, and held for both passes (step-down suppressed)",
                        session.InputRangeDbv, probedPeakDbv, OpenHeadroomDb);
                }
            }

            SetPhase(FrequencyResponsePhase.Sweeping);
            await MeasurePassAsync(request, steps, generatorAmpDbv, first, "first-load", pointBase: 0, publishZout: null, ct);

            lock (_stateLock)
                _passesCompleted = 1;

            // The pause. Silence first — the user is about to swap the load on a live amplifier
            // output — and keep everything else exactly as it is: the stream, the options, the range.
            await session.ConfigureGeneratorAsync(GeneratorConfig.Off, ct);
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
                _continue = waiter;
            SetPhase(FrequencyResponsePhase.WaitingForLoadChange);
            BroadcastProgress(steps.Length, _pointCount, FrequencyResponsePhase.WaitingForLoadChange);
            logger.LogInformation("Output impedance: first pass done ({Points} points); waiting for the load to be changed", first.Count);

            await waiter.Task.WaitAsync(ct);

            lock (_stateLock)
                _continue = null;

            SetPhase(FrequencyResponsePhase.Sweeping);
            await MeasurePassAsync(
                request, steps, generatorAmpDbv, second, "second-load", pointBase: steps.Length,
                publishZout: () => PublishZout(request, first, second, final: false), ct);

            lock (_stateLock)
                _passesCompleted = 2;

            PublishZout(request, first, second, final: true);
            BroadcastProgress(_pointCount, _pointCount, FrequencyResponsePhase.Sweeping);
            terminalPhase = FrequencyResponsePhase.Finished;
        }
        catch (OperationCanceledException)
        {
            terminalPhase = FrequencyResponsePhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Output impedance measurement failed");
            terminalPhase = FrequencyResponsePhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            lock (_stateLock)
                _continue = null;

            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the output-impedance run");
            }

            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the output-impedance run");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the output-impedance run started");
                }
            }

            pipeline.Options = savedOptions;

            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_pointsMeasured, _pointCount, terminalPhase);
        }
    }

    /// <summary>One pass over the grid at a fixed generator level, at whatever input range is set.</summary>
    private async Task MeasurePassAsync(
        OutputImpedanceRequest request,
        SteppedStepPlan[] steps,
        double generatorAmpDbv,
        List<GainPoint> points,
        string passLabel,
        int pointBase,
        Action? publishZout,
        CancellationToken ct)
    {
        var unwrapper = new PhaseUnwrapper();

        for (int i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = steps[i];
            SetProgress(pointBase + i);
            BroadcastProgress(pointBase + i, _pointCount, FrequencyResponsePhase.Sweeping);

            long dropsBefore = session.DroppedRxChunks;
            double inputRangeBefore = session.InputRangeDbv;

            var snapshots = await SteppedCaptureSupport.CaptureWithStimulusAsync(
                session, pipeline,
                SteppedCaptureSupport.ToneConfig(step.FrequencyHz, generatorAmpDbv, left: true, right: request.RightChannelIsReference),
                step.FftSize, CaptureTimeout(request, step.FftSize), ct);

            long dropsDuring = session.DroppedRxChunks - dropsBefore;
            if (dropsDuring > 0)
            {
                logger.LogWarning(
                    "Output impedance {Pass} point {Index}/{Total} at {FreqHz:0.##} Hz ({FftSize} points) captured across "
                        + "{Drops} dropped RX chunks — its record is spliced and its level is not trustworthy",
                    passLabel, i + 1, steps.Length, step.FrequencyHz, step.FftSize, dropsDuring);
            }

            double inputRangeAfter = session.InputRangeDbv;
            if (Math.Abs(inputRangeAfter - inputRangeBefore) > SteppedCaptureSupport.RangeEpsilonDb)
            {
                // Only the step-up backstop can do this, and it means the two passes no longer share
                // a range from here on — which is what the whole measurement rests on.
                logger.LogWarning(
                    "Output impedance {Pass} point {Index}/{Total} at {FreqHz:0.##} Hz: the input range moved "
                        + "{FromDbv:0.#} -> {ToDbv:0.#} dBV during the capture; the passes no longer share one range",
                    passLabel, i + 1, steps.Length, step.FrequencyHz, inputRangeBefore, inputRangeAfter);
            }

            points.Add(ReadPoint(step, snapshots, generatorAmpDbv, request, unwrapper, inputRangeAfter));
            SetProgress(pointBase + i + 1);
            publishZout?.Invoke();
        }

        logger.LogInformation(
            "Output impedance {Pass} pass measured: {Points} points at {GenDbv:0.##} dBV generator, range {RangeDbv:0.#} dBV",
            passLabel, points.Count, generatorAmpDbv, session.InputRangeDbv);
    }

    private static SteppedStepPlan[] BuildSteps(OutputImpedanceRequest request) =>
        SteppedCaptureSupport.BuildSteps(
            request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, request.EffectiveSampleRateHz,
            request.MaxFrequencyHz, request.AdaptiveResolution, request.FftSize, request.FftSizeAt);

    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        OutputImpedanceRequest request, CancellationToken ct)
    {
        if (!request.SolvesGeneratorLevel)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        int probeFftSize = request.FftSizeAt(SteppedCaptureSupport.ProbeFrequencyHz);
        var result = await SteppedCaptureSupport.ProbeGeneratorLevelAsync(
            session, pipeline, StreamChannel.Left, request.LevelDbv, request.EffectiveSampleRateHz, probeFftSize,
            generateLeft: true, generateRight: request.RightChannelIsReference, CaptureTimeout(request, probeFftSize),
            (phase, done, total, genDbv) =>
            {
                SetGeneratorAmpDbv(genDbv);
                BroadcastProgress(done, total, phase);
            },
            ct);

        SetGeneratorAmpDbv(result.GenDbv);
        return result;
    }

    private async Task<double> ProbeInputRangeAsync(OutputImpedanceRequest request, double generatorAmpDbv, CancellationToken ct)
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
            SteppedCaptureSupport.ToneConfig(probeTone.FrequencyHz, generatorAmpDbv, left: true, right: request.RightChannelIsReference),
            probeTone.FrequencyHz, generatorAmpDbv, probeFftSize, CaptureTimeout(request, probeFftSize), logger, ct);
    }

    private TimeSpan CaptureTimeout(OutputImpedanceRequest request, int fftSize) =>
        SteppedCaptureSupport.CaptureTimeout(fftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0);

    private double EstimateSeconds(OutputImpedanceRequest request, SteppedStepPlan[] steps) =>
        steps.Sum(s => SteppedCaptureSupport.PointSeconds(s.FftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0));

    /// <summary>
    /// One measured point: the DUT's gain at the commanded bin. The magnitude is the averaged level on
    /// the left input (relative to the right one when that is the reference, else to the generator);
    /// the phase, when the right channel carries the stimulus, is arg(L/R) of the last record,
    /// unwrapped across the pass — exactly <see cref="SteppedResponseRunner"/>'s reading.
    /// </summary>
    private static GainPoint ReadPoint(
        SteppedStepPlan step,
        SpectrumSnapshot[] snapshots,
        double generatorAmpDbv,
        OutputImpedanceRequest request,
        PhaseUnwrapper unwrapper,
        double inputRangeDbv)
    {
        double leftDbv = ResponseCaptureSupport.LevelAt(snapshots, StreamChannel.Left, step.Bin);
        if (double.IsNaN(leftDbv))
            throw new InvalidOperationException($"An output-impedance point at {step.FrequencyHz:0.##} Hz needs the left channel's spectrum, and the pipeline returned none.");

        double gainDb;
        double phaseDeg = 0.0;
        if (request.RightChannelIsReference)
        {
            double rightDbv = ResponseCaptureSupport.LevelAt(snapshots, StreamChannel.Right, step.Bin);
            if (double.IsNaN(rightDbv))
                throw new InvalidOperationException($"An output-impedance point at {step.FrequencyHz:0.##} Hz needs the right channel's spectrum, and the pipeline returned none.");

            gainDb = leftDbv - rightDbv;

            var left = ResponseCaptureSupport.ComplexAt(snapshots, StreamChannel.Left, step.Bin);
            var right = ResponseCaptureSupport.ComplexAt(snapshots, StreamChannel.Right, step.Bin);
            if (left is { } l && right is { } r && r != Complex.Zero)
                phaseDeg = unwrapper.NextFromRadians((l / r).Phase);
        }
        else
        {
            gainDb = leftDbv - generatorAmpDbv;
        }

        return new GainPoint(step.FrequencyHz, gainDb, phaseDeg, leftDbv, inputRangeDbv);
    }

    /// <summary>
    /// Zout from the pairs measured so far, published as an impedance curve; on the final call the
    /// gain traces and the readings are recorded too.
    /// </summary>
    private void PublishZout(OutputImpedanceRequest request, List<GainPoint> first, List<GainPoint> second, bool final)
    {
        int n = Math.Min(first.Count, second.Count);
        // BandwidthAnalyzer's peak search needs two samples.
        if (n < 2)
            return;

        var freqHz = new double[n];
        var zout = new Complex[n];
        var coherence = new double[n];
        var rangeMoved = new List<string>();

        for (int i = 0; i < n; i++)
        {
            var a = first[i];
            var b = second[i];
            freqHz[i] = a.FreqHz;
            coherence[i] = 1.0;

            double magnitudeRatio = Math.Pow(10.0, (b.GainDb - a.GainDb) / 20.0);
            if (request.RightChannelIsReference)
            {
                // Complex ratio of two gains that are both relative to the same reference tap, so the
                // record's arbitrary start point has cancelled out of each and what is left is the DUT.
                var ratio = Complex.FromPolarCoordinates(magnitudeRatio, (b.PhaseDeg - a.PhaseDeg) * Math.PI / 180.0);
                zout[i] = OutputImpedanceMath.Solve(ratio, request.LoadOhms, request.SecondLoadOhms);
            }
            else
            {
                // A ratio of magnitudes assumes Zout in phase with the loads; a reactive Zout reads low.
                // Floored at zero: noise can put the two readings a hair the wrong way round, and a
                // negative resistance is not something this method can report.
                var z = OutputImpedanceMath.Solve(new Complex(magnitudeRatio, 0.0), request.LoadOhms, request.SecondLoadOhms);
                zout[i] = new Complex(Math.Max(0.0, z.Real), 0.0);
            }

            if (Math.Abs(a.InputRangeDbv - b.InputRangeDbv) > SteppedCaptureSupport.RangeEpsilonDb)
                rangeMoved.Add($"{a.FreqHz:0.##} Hz ({a.InputRangeDbv:0.#} vs {b.InputRangeDbv:0.#} dBV)");
        }

        var trace = ImpedanceTraceBuilder.BuildPoints(ImpedanceTraceKind.Impedance, freqHz, zout, coherence);

        string? warning = rangeMoved.Count == 0
            ? null
            : "The input range differed between the two passes at " + string.Join(", ", rangeMoved)
                + " — those points compare levels read at different ranges, and their Zout is not trustworthy.";

        lock (_stateLock)
        {
            _trace = trace;
            _warning = warning;

            if (final)
            {
                _firstGainTrace = BuildGainTrace(FrequencyResponseTraceKind.GainLoad1, first);
                _secondGainTrace = BuildGainTrace(FrequencyResponseTraceKind.GainLoad2, second);

                double median = ImpedanceMath.MedianOhms(zout, 0, n - 1);
                _medianOhms = double.IsNaN(median) ? null : median;

                int at1k = NearestInLogFrequency(freqHz, 1000.0);
                double z1k = zout[at1k].Magnitude;
                _zoutAt1kOhms = z1k;
                _dampingFactorAt1k = z1k > 0.0 ? request.NominalOhms / z1k : null;
                _firstGainAt1kDb = first[at1k].GainDb;
                _secondGainAt1kDb = second[at1k].GainDb;
            }
        }

        hub.Broadcast(StreamFrame.EncodeImpedance(trace, StreamFlags.OutputImpedance));
    }

    private static FrequencyResponseTrace BuildGainTrace(FrequencyResponseTraceKind kind, List<GainPoint> points)
    {
        var freqHz = new double[points.Count];
        var gainDb = new double[points.Count];
        var phaseDeg = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            freqHz[i] = points[i].FreqHz;
            gainDb[i] = points[i].GainDb;
            phaseDeg[i] = points[i].PhaseDeg;
        }

        return SteppedCaptureSupport.BuildPointTrace(kind, freqHz, gainDb, phaseDeg, reference: null);
    }

    /// <summary>The index of the point nearest <paramref name="targetHz"/> in log frequency — the reading a specification quotes.</summary>
    internal static int NearestInLogFrequency(double[] freqHz, double targetHz)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < freqHz.Length; i++)
        {
            if (!(freqHz[i] > 0.0))
                continue;
            double distance = Math.Abs(Math.Log(freqHz[i]) - Math.Log(targetHz));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
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

        hub.Broadcast(StreamFrame.EncodeImpedanceProgress(done, total, phase, generatorAmpDbv, StreamFlags.OutputImpedance));
    }

    /// <summary>One measured point of one pass: the DUT's gain, and the range it was read at.</summary>
    private readonly record struct GainPoint(double FreqHz, double GainDb, double PhaseDeg, double LevelDbv, double InputRangeDbv);
}
