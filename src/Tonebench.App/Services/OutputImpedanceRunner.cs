using System.Numerics;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Services;

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

    /// <summary>
    /// The generator level the broadband probe refuses to go below, in dBV. A two-sided limit like
    /// <see cref="SteppedCaptureSupport.ProbeStartDbv"/>'s: an amplifier whose gain puts the target
    /// under this needs less drive than the generator can usefully produce, and the message says so
    /// rather than reporting the opposite fault.
    /// </summary>
    private const double BroadbandMinGeneratorDbv = -60.0;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IFftBackend _fft = new FftFlatBackend();

    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TaskCompletionSource? _continue;

    private FrequencyResponsePhase _phase = FrequencyResponsePhase.Idle;
    private int _passesCompleted;
    private int? _pointsMeasured;
    private int? _pointCount;
    private int _averageCount;
    private int _averagesTaken;
    private ImpedanceStimulus _stimulus = ImpedanceStimulus.SteppedSine;
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

    /// <summary>
    /// The runs of the measurements switched away from — see <see cref="SwitchOwner"/>, and
    /// <see cref="ThdSweepRunner.SwitchOwner"/> for the whole reasoning. Four, the same bound every
    /// one of these uses.
    /// </summary>
    private readonly RunParking<ParkedRun> _parking = new(capacity: 4);

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
                    _medianOhms, _zoutAt1kOhms, _dampingFactorAt1k, _firstGainAt1kDb, _secondGainAt1kDb,
                    _stimulus, _averagesTaken, _averageCount);
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

            // A broadband run has no tone grid at all, and BuildSteps would read a start/end pair
            // the panel does not even show it.
            SteppedStepPlan[] steps = request.IsStepped ? BuildSteps(request) : [];

            lock (_stateLock)
            {
                _phase = request.SolvesGeneratorLevel
                    ? FrequencyResponsePhase.DeterminingLevel
                    : WillOwnInputRange()
                        ? FrequencyResponsePhase.DeterminingRange
                        : FrequencyResponsePhase.Sweeping;
                _passesCompleted = 0;
                // Null rather than zero on a broadband run: it has no grid, and the progress line
                // reads records instead. Zero would read as a stepped run that has measured nothing.
                _pointsMeasured = request.IsStepped ? 0 : null;
                _pointCount = request.IsStepped ? steps.Length * OutputImpedanceRequest.PassCount : null;
                _averageCount = request.AverageCount;
                _averagesTaken = 0;
                _stimulus = request.Stimulus;
                _generatorAmpDbv = request.SolvesGeneratorLevel ? SteppedCaptureSupport.ProbeStartDbv : request.LevelDbv;
                _inputRangeDbv = null;
                _sampleRateHz = request.EffectiveSampleRateHz;
                _fftSize = request.IsStepped ? steps[0].FftSize : request.BroadbandFftSize;
                _binHz = request.IsStepped ? request.EffectiveSampleRateHz / steps[0].FftSize : request.BroadbandBinHz;
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

    /// <summary>
    /// Points the runner at the measurement now on screen — the output-impedance screen's
    /// counterpart of <see cref="FrequencyResponseRunner.SwitchOwner"/>, following
    /// <see cref="ThdSweepRunner.SwitchOwner"/>'s six rules unchanged. The two gain passes are
    /// parked with the Zout curve, because
    /// <see cref="MeasurementSaveService.SaveOutputImpedanceAsync"/> stores them beside it so a
    /// result keeps what it was derived from.
    ///
    /// <para>Ignoring the switch while <see cref="Running"/> covers one case the other five do not:
    /// <see cref="FrequencyResponsePhase.WaitingForLoadChange"/> is a busy phase, so a run paused
    /// for the user to move the load cannot be parked out from under its own CONTINUE.</para>
    /// </summary>
    public OutputImpedanceStatus SwitchOwner(string? owner)
    {
        lock (_stateLock)
        {
            if (Running)
                return Status;

            var current = _trace is null
                ? null
                : new ParkedRun(
                    _phase, _passesCompleted, _pointsMeasured, _pointCount, _averageCount,
                    _stimulus, _averagesTaken,
                    _generatorAmpDbv, _inputRangeDbv, _binHz, _sampleRateHz, _fftSize, _loadOhms,
                    _secondLoadOhms, _nominalOhms, _rightChannelIsReference, _error, _warning,
                    _trace, _firstGainTrace, _secondGainTrace, _medianOhms, _zoutAt1kOhms,
                    _dampingFactorAt1k, _firstGainAt1kDb, _secondGainAt1kDb, _lastRequest);

            var outcome = _parking.Switch(owner, current);
            if (!outcome.Changed)
                return Status;

            if (outcome.Adopted is { } parked)
            {
                _phase = parked.Phase;
                _passesCompleted = parked.PassesCompleted;
                _pointsMeasured = parked.PointsMeasured;
                _pointCount = parked.PointCount;
                _averageCount = parked.AverageCount;
                _stimulus = parked.Stimulus;
                _averagesTaken = parked.AveragesTaken;
                _generatorAmpDbv = parked.GeneratorAmpDbv;
                _inputRangeDbv = parked.InputRangeDbv;
                _binHz = parked.BinHz;
                _sampleRateHz = parked.SampleRateHz;
                _fftSize = parked.FftSize;
                _loadOhms = parked.LoadOhms;
                _secondLoadOhms = parked.SecondLoadOhms;
                _nominalOhms = parked.NominalOhms;
                _rightChannelIsReference = parked.RightChannelIsReference;
                _error = parked.Error;
                _warning = parked.Warning;
                _trace = parked.Trace;
                _firstGainTrace = parked.FirstGainTrace;
                _secondGainTrace = parked.SecondGainTrace;
                _medianOhms = parked.MedianOhms;
                _zoutAt1kOhms = parked.ZoutAt1kOhms;
                _dampingFactorAt1k = parked.DampingFactorAt1k;
                _firstGainAt1kDb = parked.FirstGainAt1kDb;
                _secondGainAt1kDb = parked.SecondGainAt1kDb;
                _lastRequest = parked.LastRequest;
            }
            else if (outcome.Cleared)
            {
                ResetLiveState();
            }

            return Status;
        }
    }

    /// <summary>The measurement the run on hand belongs to, or null — see <see cref="SwitchOwner"/>.</summary>
    public string? Owner
    {
        get { lock (_stateLock) return _parking.Owner; }
    }

    /// <summary>
    /// Drops everything this runner holds for <paramref name="owner"/>, because that measurement
    /// has stopped existing — the trash's permanent delete and Empty trash. See
    /// <see cref="RunParking{TState}.Forget"/> for why the live slot is released too and not just
    /// the parked entry.
    ///
    /// <para>A run still going is left alone, owner and all. Nothing is parked under the live
    /// owner — <see cref="RunParking{TState}.Switch"/> takes the arriving entry out as it installs
    /// it — so there is nothing to drop, and pulling the state out from under the pass still
    /// writing it would be worse than an owner the next switch replaces anyway.</para>
    /// </summary>
    public void ForgetOwner(string owner)
    {
        lock (_stateLock)
        {
            if (Running && _parking.Owner == owner)
                return;

            if (_parking.Forget(owner))
                ResetLiveState();
        }
    }

    /// <summary>
    /// Empties the live slot: what <see cref="SwitchOwner"/> leaves behind for a measurement that
    /// has nothing parked, and what <see cref="ForgetOwner"/> leaves behind for one that has been
    /// destroyed. Everything the park carries is reset, which is a little more than
    /// <see cref="Clear"/> empties — a measurement that has never run must not read back the
    /// previous one's record length on its status line. Call under <c>_stateLock</c>.
    /// </summary>
    private void ResetLiveState()
    {
        _trace = null;
        _firstGainTrace = null;
        _secondGainTrace = null;
        _phase = FrequencyResponsePhase.Idle;
        _passesCompleted = 0;
        _pointsMeasured = 0;
        _pointCount = 0;
        _averageCount = 0;
        _averagesTaken = 0;
        _stimulus = ImpedanceStimulus.SteppedSine;
        _generatorAmpDbv = 0.0;
        _inputRangeDbv = null;
        _binHz = 0.0;
        _sampleRateHz = 0.0;
        _fftSize = 0;
        _loadOhms = 0.0;
        _secondLoadOhms = null;
        _nominalOhms = 0.0;
        _rightChannelIsReference = false;
        _error = null;
        _warning = null;
        _medianOhms = null;
        _zoutAt1kOhms = null;
        _dampingFactorAt1k = null;
        _firstGainAt1kDb = null;
        _secondGainAt1kDb = null;
        _lastRequest = null;
    }

    /// <summary>Everything a measurement switch has to carry — <see cref="Status"/>'s own fields plus the two gain passes and the as-run request the save path reads.</summary>
    private sealed record ParkedRun(
        FrequencyResponsePhase Phase, int PassesCompleted, int? PointsMeasured, int? PointCount,
        int AverageCount, ImpedanceStimulus Stimulus, int AveragesTaken,
        double GeneratorAmpDbv, double? InputRangeDbv, double BinHz,
        double SampleRateHz, int FftSize, double LoadOhms, double? SecondLoadOhms,
        double NominalOhms, bool RightChannelIsReference, string? Error, string? Warning,
        ImpedanceTrace? Trace, FrequencyResponseTrace? FirstGainTrace,
        FrequencyResponseTrace? SecondGainTrace, double? MedianOhms, double? ZoutAt1kOhms,
        double? DampingFactorAt1k, double? FirstGainAt1kDb, double? SecondGainAt1kDb,
        OutputImpedanceRequest? LastRequest);

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
        FrequencyResponseResult? firstBroadband = null;
        FrequencyResponseResult? secondBroadband = null;

        try
        {
            logger.LogInformation(
                "Output impedance starting: {Stimulus}, {Grid}, {SampleRateHz:0} Hz, {Averages} averages, "
                    + "level {LevelValue:0.##} {LevelUnit} ({LevelMode}), loads {LoadOhms:0.##} ohm then {SecondLoad}, "
                    + "nominal {NominalOhms:0.##} ohm, {Method}",
                request.Stimulus,
                request.IsStepped
                    ? $"{request.StartFreqHz:0.##}-{request.EndFreqHz:0.##} Hz, {request.StepsPerOctave}/octave, "
                        + $"{steps.Length} points per pass, adaptive {request.AdaptiveResolution} ("
                        + (request.AdaptiveResolution
                            ? $"{steps.Max(s => s.FftSize)}-{steps.Min(s => s.FftSize)} points)"
                            : $"fixed {request.FftSize} points)")
                    : $"{request.FrequencySpanHz:0.##} Hz span, {request.BroadbandFftSize} points "
                        + $"({request.BroadbandBinHz:0.###} Hz/bin), 1/{request.SmoothingDenominator} octave smoothing",
                request.EffectiveSampleRateHz,
                request.AverageCount, request.LevelValue, request.LevelUnit, request.LevelMode,
                request.LoadOhms, request.SecondLoadOhms is { } sl ? $"{sl:0.##} ohm" : "open", request.NominalOhms,
                request.RightChannelIsReference ? "complex (right channel is the reference)" : "magnitude only");

            pipeline.Options = request.IsStepped
                ? SteppedCaptureSupport.BuildSteppedOptions(
                    savedOptions, request.FftSize, request.Window, request.AverageCount, request.StartFreqHz, request.EffectiveSampleRateHz)
                : BuildBroadbandOptions(savedOptions, request);

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
            if (request.IsStepped)
                await MeasurePassAsync(request, steps, generatorAmpDbv, first, "first-load", pointBase: 0, publishZout: null, ct);
            else
                firstBroadband = await MeasureBroadbandPassAsync(request, generatorAmpDbv, "first-load", ct);

            lock (_stateLock)
                _passesCompleted = 1;

            // The pause. Silence first — the user is about to swap the load on a live amplifier
            // output — and keep everything else exactly as it is: the stream, the options, the range.
            await session.ConfigureGeneratorAsync(GeneratorConfig.Off, ct);
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
                _continue = waiter;
            SetPhase(FrequencyResponsePhase.WaitingForLoadChange);
            BroadcastProgress(
                request.IsStepped ? steps.Length : request.AverageCount,
                request.IsStepped ? _pointCount : request.AverageCount,
                FrequencyResponsePhase.WaitingForLoadChange);
            logger.LogInformation(
                "Output impedance: first pass done ({Measured}); waiting for the load to be changed",
                request.IsStepped ? $"{first.Count} points" : $"{request.AverageCount} records");

            await waiter.Task.WaitAsync(ct);

            lock (_stateLock)
                _continue = null;

            SetPhase(FrequencyResponsePhase.Sweeping);
            if (request.IsStepped)
            {
                await MeasurePassAsync(
                    request, steps, generatorAmpDbv, second, "second-load", pointBase: steps.Length,
                    publishZout: () => PublishZout(request, first, second, final: false), ct);
            }
            else
            {
                secondBroadband = await MeasureBroadbandPassAsync(request, generatorAmpDbv, "second-load", ct);
            }

            lock (_stateLock)
                _passesCompleted = 2;

            if (request.IsStepped)
            {
                PublishZout(request, first, second, final: true);
                BroadcastProgress(_pointCount, _pointCount, FrequencyResponsePhase.Sweeping);
            }
            else
            {
                PublishZoutBroadband(request, firstBroadband!, secondBroadband!);
                BroadcastProgress(request.AverageCount, request.AverageCount, FrequencyResponsePhase.Sweeping);
            }
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

    /// <summary>
    /// What the pipeline does for a chirp or noise run: one whole, rectangular-windowed record at a
    /// time with no averaging of its own. <see cref="ImpedanceRunner"/>'s own options, for the same
    /// reasons - the runner averages complex spectra itself, because the pipeline's averager works on
    /// magnitudes and would throw away the phase half the referenced method is about.
    /// </summary>
    private static SpectrumPipelineOptions BuildBroadbandOptions(SpectrumPipelineOptions current, OutputImpedanceRequest request) =>
        current with
        {
            FftSize = request.BroadbandFftSize,
            Window = WindowType.Rectangular,
            Averaging = AveragingMode.Off,
            AverageCount = 1,
            AverageHopFraction = 0.0,
            ProcessLeft = true,
            ProcessRight = true,
            SingleShot = true,
            RetainComplexSpectrum = true,
            CapturePreview = false,
            Weighting = WeightingSpec.None,
        };

    private static SteppedStepPlan[] BuildSteps(OutputImpedanceRequest request) =>
        SteppedCaptureSupport.BuildSteps(
            request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, request.EffectiveSampleRateHz,
            request.MaxFrequencyHz, request.AdaptiveResolution, request.FftSize, request.FftSizeAt);

    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        OutputImpedanceRequest request, CancellationToken ct)
    {
        if (!request.SolvesGeneratorLevel)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        // A broadband run probes with the stimulus it is about to play, over the band the target is
        // defined on. Its peak is deliberately *not* returned: the range is probed from the widest
        // setting afterwards, which is the frequency-response runner's documented order and the one
        // thing that stops a range left too sensitive by the previous measurement from clipping the
        // probe's own captures and reading as missing gain.
        if (!request.IsStepped)
        {
            double broadbandGenDbv = await ResolveBroadbandAmplitudeAsync(request, ct);
            return (broadbandGenDbv, double.NegativeInfinity, broadbandGenDbv);
        }

        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        int probeFftSize = request.FftSizeAt(SteppedCaptureSupport.ProbeFrequencyHz);
        var result = await SteppedCaptureSupport.ProbeGeneratorLevelAsync(
            session, pipeline, StreamChannel.Left, request.LevelDbv, request.LevelUnit, request.LoadOhms, request.EffectiveSampleRateHz, probeFftSize,
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

        if (!request.IsStepped)
            return await ProbeBroadbandInputRangeAsync(request, generatorAmpDbv, ct);

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
        SteppedCaptureSupport.CaptureTimeout(fftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0, session.StreamSettleMs);

    private double EstimateSeconds(OutputImpedanceRequest request, SteppedStepPlan[] steps)
    {
        if (request.IsStepped)
        {
            return steps.Sum(s => SteppedCaptureSupport.PointSeconds(
                s.FftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0));
        }

        // One throwaway record plus the averages, and a short probe - the impedance panel's own
        // arithmetic. Per pass; the caller multiplies by the two passes, and the load change in
        // between is the user's own time.
        double recordSeconds = request.BroadbandFftSize / request.EffectiveSampleRateHz;
        double discardSeconds = session.CaptureDelayMs / 1000.0 + 0.065;
        double probeSeconds = SteppedCaptureSupport.MaxProbeFftSize / request.EffectiveSampleRateHz + discardSeconds;
        return ((request.AverageCount + 1) * (recordSeconds + discardSeconds)) + probeSeconds;
    }

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

    // ----------------------------------------------------------------------------------------
    // The broadband path. One record per pass instead of a grid, with everything else about the
    // run - the held input range, the pause, the solve - exactly as it is for a stepped sweep.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// One pass's deconvolved gain against frequency, from <see cref="OutputImpedanceRequest.AverageCount"/>
    /// whole records of the chirp or the pink sequence.
    ///
    /// <para><b>The reference depends on the method, and only one of the two carries a phase.</b>
    /// Referenced, H = L/R comes from a single record, so the periodic stimulus's arbitrary
    /// rotation is common to both channels and cancels inside the pass - the rule
    /// <see cref="ImpedanceRunner"/> rests on, and why the estimator is built with
    /// <c>removeLatency: false</c>. Unreferenced, the left channel is deconvolved against the
    /// stimulus this run is playing, and each pass starts at its own arbitrary point in that
    /// stimulus, so the two passes' phases are not comparable and the ratio is taken on magnitudes
    /// alone - which is exactly what the stepped path already does without a reference.</para>
    /// </summary>
    private async Task<FrequencyResponseResult> MeasureBroadbandPassAsync(
        OutputImpedanceRequest request,
        double generatorAmpDbv,
        string passLabel,
        CancellationToken ct)
    {
        var acquisition = request.AsBroadbandAcquisition();
        int fftSize = acquisition.FftSize;

        // No sweep gate, as ImpedanceRunner's own measurement path takes none by default: gating
        // costs a factor of two in resolution to delete harmonic packets that this curve - smoothed
        // over a fractional octave and then divided by the other pass - is already insensitive to.
        var estimator = new FrequencyResponseEstimator(
            fftSize, _fft, gate: null, removeLatency: false,
            regularisationFraction: request.IsNoise
                ? FrequencyResponseEstimator.BroadbandRegularisationFraction
                : FrequencyResponseEstimator.RegularisationFraction);

        var syntheticReference = request.RightChannelIsReference
            ? null
            : ResponseCaptureSupport.BuildReferenceSpectrum(_fft, acquisition, generatorAmpDbv, fftSize);

        long dropsBefore = session.DroppedRxChunks;
        double inputRangeBefore = session.InputRangeDbv;

        await session.ConfigureGeneratorAsync(
            ResponseCaptureSupport.BuildGenerator(acquisition, generatorAmpDbv, fftSize), ct);

        lock (_stateLock)
            _averagesTaken = 0;
        BroadcastProgress(0, request.AverageCount, FrequencyResponsePhase.Sweeping);

        // The record armed by the stimulus change is thrown away rather than measured: the generator
        // configure discards the audio already queued for the DAC but not the round trip back
        // through the amplifier, and a rectangular window over a record that is part silence and
        // part stimulus is a period of nothing. FrequencyResponseRunner's rule, and it matters more
        // here than anywhere, since each pass gets exactly one chance at a clean set of records.
        await WaitForBroadbandCaptureAsync(request, fftSize, ct);

        for (int record = 0; record < request.AverageCount; record++)
        {
            ct.ThrowIfCancellationRequested();

            // Emptied but nothing discarded: the stimulus has not changed since the throwaway
            // record above, so a non-zero discard would throw away good samples for nothing.
            pipeline.Flush(0);
            await WaitForBroadbandCaptureAsync(request, fftSize, ct);

            var snapshots = pipeline.LatestSnapshots();
            var (left, right) = ResponseCaptureSupport.RequireBothChannels(snapshots, fftSize, "output-impedance");
            estimator.Add(syntheticReference ?? right, left);

            lock (_stateLock)
                _averagesTaken = estimator.Records;
            BroadcastProgress(estimator.Records, request.AverageCount, FrequencyResponsePhase.Sweeping);
        }

        long dropsDuring = session.DroppedRxChunks - dropsBefore;
        if (dropsDuring > 0)
        {
            logger.LogWarning(
                "Output impedance {Pass} pass captured across {Drops} dropped RX chunks - its records are "
                    + "spliced and its gain is not trustworthy",
                passLabel, dropsDuring);
        }

        double inputRangeAfter = session.InputRangeDbv;
        if (Math.Abs(inputRangeAfter - inputRangeBefore) > SteppedCaptureSupport.RangeEpsilonDb)
        {
            // Only the step-up backstop can do this, and it means the two passes no longer share one
            // range - which is the whole thing this measurement rests on.
            logger.LogWarning(
                "Output impedance {Pass} pass: the input range moved {FromDbv:0.#} -> {ToDbv:0.#} dBV during the "
                    + "capture; the passes no longer share one range",
                passLabel, inputRangeBefore, inputRangeAfter);
        }

        logger.LogInformation(
            "Output impedance {Pass} pass measured: {Records} records of {FftSize} points at {GenDbv:0.##} dBV generator, "
                + "range {RangeDbv:0.#} dBV",
            passLabel, estimator.Records, fftSize, generatorAmpDbv, session.InputRangeDbv);

        return estimator.Complete();
    }

    /// <summary>
    /// The generator level that puts the DUT's output at an output-referred target, measured with
    /// the broadband stimulus itself. <see cref="FrequencyResponseRunner"/>'s probe, one measurement
    /// along: a short record, the deconvolved gain at the bandwidth reference, correct, repeat.
    ///
    /// <para><b>Deconvolving is what makes this a level rather than a bin.</b> A chirp spreads its
    /// energy over thousands of bins, so any single bin of the raw spectrum sits far below the
    /// sweep's own amplitude; reading one and correcting toward the target drives the generator up
    /// by that whole difference, which measured 14 dB on the frequency-response screen before it was
    /// fixed there.</para>
    /// </summary>
    private async Task<double> ResolveBroadbandAmplitudeAsync(OutputImpedanceRequest request, CancellationToken ct)
    {
        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        var acquisition = request.AsBroadbandAcquisition();
        double target = request.LevelDbv;
        double amplitude = SteppedCaptureSupport.ProbeStartDbv;

        double maxGenDbv = session.Capabilities.OutputRangesDbv.Count > 0
            ? session.Capabilities.OutputRangesDbv.Max()
            : 18.0;

        // A short record: the probe wants a level, not resolution, and at the full size each pass
        // would cost seconds of a measurement the user is standing over with a load in their hand.
        int probeFftSize = Math.Min(SteppedCaptureSupport.MaxProbeFftSize, acquisition.FftSize);
        var savedFftSize = pipeline.Options.FftSize;
        bool ceilingAsked = false;

        try
        {
            for (int pass = 0; pass < SteppedCaptureSupport.MaxProbeIterations; pass++)
            {
                ct.ThrowIfCancellationRequested();
                SetGeneratorAmpDbv(amplitude);
                BroadcastProgress(pass, SteppedCaptureSupport.MaxProbeIterations, FrequencyResponsePhase.DeterminingLevel);

                pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
                await session.ConfigureGeneratorAsync(
                    ResponseCaptureSupport.BuildGenerator(acquisition, amplitude, probeFftSize), ct);
                await WaitForBroadbandCaptureAsync(request, probeFftSize, ct);

                var probeReference = ResponseCaptureSupport.BuildReferenceSpectrum(_fft, acquisition, amplitude, probeFftSize);
                var (probeLeft, _) = ResponseCaptureSupport.RequireBothChannels(pipeline.LatestSnapshots(), probeFftSize, "output-impedance");

                double gainDb = ResponseCaptureSupport.ProbeGainDb(probeReference, probeLeft, probeFftSize, acquisition, out bool fellBack);
                if (double.IsNaN(gainDb))
                {
                    throw new InvalidOperationException(
                        "The level probe measured no signal - check the amplifier is powered, connected and loaded as the measurement expects.");
                }

                if (fellBack && pass == 0)
                {
                    logger.LogWarning(
                        "The reference band {LowHz:0}-{HighHz:0} Hz holds no bin of the level probe's record; "
                            + "the output level is being set against the whole-band median instead",
                        acquisition.BandwidthReferenceLowHz, acquisition.BandwidthReferenceHighHz);
                }

                double measuredDbv = amplitude + gainDb;
                logger.LogInformation(
                    "Output impedance level probe iteration {Pass}: generator {Amplitude:0.##} dBV, gain {GainDb:0.##} dB, "
                        + "measured {MeasuredDbv:0.##} dBV against a target of {Target:0.##} dBV",
                    pass, amplitude, gainDb, measuredDbv, target);

                double shortfall = target - measuredDbv;
                if (Math.Abs(shortfall) <= SteppedCaptureSupport.ProbeToleranceDb)
                    break;

                var (corrected, outcome) = ResponseCaptureSupport.CorrectProbeAmplitude(
                    amplitude, shortfall, ceilingAsked, maxGenDbv, BroadbandMinGeneratorDbv);
                amplitude = corrected;

                if (outcome == ResponseCaptureSupport.ProbeCorrection.BelowFloor)
                {
                    throw new InvalidOperationException(
                        $"The requested output level of {target:0.##} dBV would need a generator level below "
                            + $"{BroadbandMinGeneratorDbv:0.##} dBV. The amplifier's gain looks too high to measure this way.");
                }

                if (outcome == ResponseCaptureSupport.ProbeCorrection.AcceptCeiling)
                {
                    // At the ceiling with nothing left to correct with, so the shortfall is a fact
                    // about the amplifier rather than about the probe. See OutputLevelReach.
                    if (!OutputLevelReach.Reached(target, measuredDbv, request.LevelUnit))
                    {
                        throw new InvalidOperationException(
                            OutputLevelReach.Message(target, measuredDbv, request.LevelUnit, request.LoadOhms, maxGenDbv));
                    }

                    logger.LogWarning(
                        "The requested output level of {Target:0.##} dBV needs more than the {Max:0.##} dBV the generator can "
                            + "produce; measuring at {Max:0.##} dBV instead",
                        target, maxGenDbv, maxGenDbv);
                    break;
                }

                ceilingAsked = outcome == ResponseCaptureSupport.ProbeCorrection.ConfirmCeiling;
            }
        }
        finally
        {
            pipeline.Options = pipeline.Options with { FftSize = savedFftSize };
        }

        SetGeneratorAmpDbv(amplitude);
        return amplitude;
    }

    /// <summary>
    /// The peak the input range is chosen from, measured from the <b>widest</b> rung with the
    /// stimulus playing - so a range left too sensitive by whatever ran before cannot clip the
    /// reading the whole run's range is then derived from.
    /// </summary>
    private async Task<double> ProbeBroadbandInputRangeAsync(
        OutputImpedanceRequest request, double generatorAmpDbv, CancellationToken ct)
    {
        var acquisition = request.AsBroadbandAcquisition();
        int probeFftSize = Math.Min(SteppedCaptureSupport.MaxProbeFftSize, acquisition.FftSize);
        var savedFftSize = pipeline.Options.FftSize;

        try
        {
            await SteppedCaptureSupport.ApplyInputRangeAsync(session, session.Capabilities.InputRangesDbv.Max(), ct);

            pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
            await session.ConfigureGeneratorAsync(
                ResponseCaptureSupport.BuildGenerator(acquisition, generatorAmpDbv, probeFftSize), ct);
            await WaitForBroadbandCaptureAsync(request, probeFftSize, ct);

            double peakDbv = ResponseCaptureSupport.PeakDbv(pipeline.LatestSnapshots());
            logger.LogInformation(
                "Output impedance input-range probe: peak {PeakDbv:0.##} dBV read from the {RangeDbv:0.#} dBV rung",
                peakDbv, session.InputRangeDbv);
            return peakDbv;
        }
        finally
        {
            pipeline.Options = pipeline.Options with { FftSize = savedFftSize };
        }
    }

    /// <summary>
    /// Zout from the two broadband passes: the gain <b>ratio</b> is averaged over the smoothing
    /// bands, and the solve happens once afterwards.
    ///
    /// <para><b>That order is the measurement, not a detail of it, and getting it the other way
    /// round is a wrong answer rather than a noisy one.</b> With 8 Ω then 4 Ω the ratio
    /// q = G₂/G₁ sits within a decibel of one — about 12 Ω of Z per unit of q, or a millidecibel
    /// per milliohm — so a single bin's noise is ohms, and |Z| is a <i>magnitude</i>: noise that
    /// moved q in any direction at all can only push it <i>up</i>. Smoothing |Z| afterwards averages
    /// numbers that are already biased, and an average of biased numbers is biased. Averaging q
    /// first instead removes the noise while it is still additive and zero mean, and one solve of a
    /// clean q is clean.</para>
    ///
    /// <para>Measured on Joost's EL84 push-pull at 1 W (2026-10-28), 1/3 octave, 10 records, where
    /// the stepped sine reads 0.894 Ω and all three stimuli agree on the underlying gain step to
    /// 1.4 %: solving per bin drew <b>1.24 Ω over 200 Hz – 1 kHz on the pink-noise run and 1.67 Ω
    /// at the bottom</b>, and re-solving the very same capture from its smoothed gains gave 0.87 and
    /// 1.02. <b>Coherence cannot catch it</b> — it read 0.99999 throughout, because each gain pass
    /// really was measured that well. It is their difference that is delicate, and nothing on the
    /// chart says so.</para>
    /// </summary>
    private void PublishZoutBroadband(
        OutputImpedanceRequest request,
        FrequencyResponseResult first,
        FrequencyResponseResult second)
    {
        var h1 = ImpedanceTraceBuilder.ToComplex(first);
        var h2 = ImpedanceTraceBuilder.ToComplex(second);

        int n = Math.Min(h1.Length, h2.Length);
        var ratio = new Complex[n];
        var coherence = new double[n];

        for (int k = 0; k < n; k++)
        {
            // The weaker of the two passes: a bin either of them measured badly is a bin the ratio
            // cannot be trusted at, and reporting the better one would flatter it.
            coherence[k] = Math.Min(first.Coherence[k], second.Coherence[k]);

            // A pass with nothing in a bin reads as unity, i.e. no impedance, which is what the
            // per-bin version returned there too - and a value rather than a hole, because this
            // array is about to be averaged over bands.
            ratio[k] = h1[k] == Complex.Zero ? Complex.One : h2[k] / h1[k];

            // Magnitudes only where there is no measured reference, exactly as the stepped path does
            // it: without one the two passes' phases are not comparable at all.
            if (!request.RightChannelIsReference)
                ratio[k] = new Complex(ratio[k].Magnitude, 0.0);
        }

        double binHz = request.BroadbandBinHz;

        // The readings and the curve are taken inside the analysed band and nowhere else. Outside it
        // the ratio is noise over nothing, and Z = R1*R2*(q - 1)/(R2 - R1*q) of a ratio wandering
        // near the denominator's zero is clamped at ImpedanceMath.MaxOhms - one such bin would be
        // the median, and a smoothing band reaching past the top would carry it inward.
        var band = ImpedanceTraceBuilder.AnalysisBand(binHz, request.AnalysisLowHz, request.AnalysisHighHz, n);
        int firstBin = band.FirstBin;
        int lastBin = band.LastBin;

        var smoothedRatio = FractionalOctaveSmoother.SmoothComplex(
            ratio, binHz, request.SmoothingDenominator, band);

        var zout = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            var z = OutputImpedanceMath.Solve(smoothedRatio[k], request.LoadOhms, request.SecondLoadOhms);

            // Floored at zero without a reference, as the stepped path does: a magnitude-only ratio
            // that lands the wrong side of unity is noise, not a negative resistance.
            zout[k] = request.RightChannelIsReference ? z : new Complex(Math.Max(0.0, z.Real), 0.0);
        }

        // Already averaged, so this must not smooth again - everything else about the trace is the
        // impedance screen's own, so the two measurements cannot disagree about one curve.
        var trace = ImpedanceTraceBuilder.BuildSmoothed(
            binHz, band, ImpedanceTraceKind.Impedance, zout, coherence, ImpedanceRunner.DisplayColumns);

        var acquisition = request.AsBroadbandAcquisition();

        int at1k = Math.Clamp((int)Math.Round(1000.0 / binHz), firstBin, lastBin);

        lock (_stateLock)
        {
            _trace = trace;
            _warning = null;

            _firstGainTrace = ResponseCaptureSupport.BuildTrace(
                acquisition, ImpedanceRunner.DisplayColumns, FrequencyResponseTraceKind.GainLoad1, first,
                offsetDb: 0.0, addBackDelayS: 0.0);
            _secondGainTrace = ResponseCaptureSupport.BuildTrace(
                acquisition, ImpedanceRunner.DisplayColumns, FrequencyResponseTraceKind.GainLoad2, second,
                offsetDb: 0.0, addBackDelayS: 0.0);

            double median = ImpedanceMath.MedianOhms(zout, firstBin, lastBin);
            _medianOhms = double.IsNaN(median) ? null : median;

            double z1k = zout[at1k].Magnitude;
            _zoutAt1kOhms = z1k;
            _dampingFactorAt1k = z1k > 0.0 ? request.NominalOhms / z1k : null;
            _firstGainAt1kDb = FrequencyResponseEstimator.ToDb(first.Magnitude[at1k]);
            _secondGainAt1kDb = FrequencyResponseEstimator.ToDb(second.Magnitude[at1k]);
        }

        hub.Broadcast(StreamFrame.EncodeImpedance(trace, StreamFlags.OutputImpedance));
    }

    private async Task WaitForBroadbandCaptureAsync(OutputImpedanceRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = ResponseCaptureSupport.CaptureTimeout(
            request.EffectiveSampleRateHz, fftSize, session.CaptureDelayMs, session.StreamSettleMs);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"An output-impedance record did not complete within {timeout.TotalSeconds:0.0} s. "
                        + "The device may have stopped streaming.");
            }

            await Task.Delay(CapturePollInterval, ct).ConfigureAwait(false);
        }
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

    private void BroadcastProgress(int? done, int? total, FrequencyResponsePhase phase)
    {
        double generatorAmpDbv;
        lock (_stateLock)
            generatorAmpDbv = _generatorAmpDbv;

        // The wire carries two ints; a broadband pass has no grid, so it sends its record counts
        // and the client tells the two apart by the status's own null point count.
        hub.Broadcast(StreamFrame.EncodeImpedanceProgress(done ?? 0, total ?? 0, phase, generatorAmpDbv, StreamFlags.OutputImpedance));
    }

    /// <summary>One measured point of one pass: the DUT's gain, and the range it was read at.</summary>
    private readonly record struct GainPoint(double FreqHz, double GainDb, double PhaseDeg, double LevelDbv, double InputRangeDbv);
}
