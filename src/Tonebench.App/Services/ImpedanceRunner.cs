using System.Numerics;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Services;

/// <summary>
/// Measures a one-port impedance through a series reference resistor: play a periodic chirp, capture
/// both input channels from the <i>same</i> record, and invert the voltage divider they form.
///
/// <para><b>Why the two channels must come from one record.</b> <c>Z = R·H/(1 − H)</c> is a complex
/// ratio, and the chirp has no trigger — each record starts at an arbitrary point in the sweep. That
/// rotation is common to both channels and cancels in the ratio, which is what lets this measurement
/// report phase at all. It also means the estimator runs with <c>removeLatency: false</c>: with a
/// measured reference there is no rotation left to find, so the peak search would be fitting noise,
/// and it refines to a <i>fraction</i> of a sample. Half a sample is 75 degrees at 20 kHz on a 48 kHz
/// record, reported as DUT phase. See <see cref="FrequencyResponseEstimator"/>'s constructor.</para>
///
/// <para><b>The stepped-sine stimulus plays one coherent tone per point instead</b>, and everything
/// above still holds: the two channels come from one record, so the tone's arbitrary phase is
/// common-mode and cancels. What it buys is level. A chirp spreads its energy over thousands of bins,
/// so each bin sits far below the sweep's own amplitude; a tone puts the whole drive on the one bin
/// being read, which is the difference between a usable and an unusable reading wherever
/// <c>V_ref - V_dut</c> gets small -- a high-impedance DUT, the bottom of the band, and a shorted
/// standard.</para>
///
/// <para><b>The pink-noise stimulus excites the whole band at once like the chirp</b>, and the same
/// two-channels-from-one-record argument carries it: a noise record has no phase reference of its
/// own, and does not need one. What it changes is the analysis. Nothing about noise is periodic in
/// the record, so the window is <b>Hann</b> and there is no deconvolved impulse response to gate;
/// and because successive records are independent draws rather than repeats of one deterministic
/// sweep, averaging keeps improving the estimate instead of merely confirming it -- which is why a
/// noise pass publishes a curve after <i>every</i> record rather than only at the end.</para>
///
/// <para><b>Calibration runs go through this same runner</b> with a
/// <see cref="ImpedanceCalTarget"/> set. A standard is captured exactly like a measurement and then
/// <i>not</i> corrected: the open standard is the channel mismatch, so normalising it against a
/// previous open would report the difference between two calibrations as an impedance.</para>
///
/// <para>A calibration may use a <i>different</i> stimulus from the measurement it will correct --
/// see <see cref="ImpedanceRequest.CalibrationStimulus"/>. Nothing here needs to care, because
/// <see cref="ImpedanceRequest.EffectiveStimulus"/> is what every derived property and every branch
/// below reads.</para>
///
/// <para>The runner holds no calibration of its own. A captured standard is returned on the status
/// and stored by the browser in the measurement type's <c>params_json</c>, so a fixture calibration
/// lives in exactly one place and survives a restart.</para>
/// </summary>
public sealed class ImpedanceRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<ImpedanceRunner> logger) : IMeasurementRun
{
    /// <summary>Columns sent to the chart per trace, matching the frequency response's.</summary>
    public const int DisplayColumns = 1600;

    /// <summary>FFT size the input-range probe runs at. Short on purpose: it needs one level, not resolution.</summary>
    private const int ProbeFftSize = 16384;

    /// <summary>Where the stepped run's range probe plays its tone, clamped into the swept band.</summary>
    private const double ProbeToneHz = 1000.0;

    /// <summary>Ranges closer than this count as the same one — the ladder is 6 dB apart, so this only absorbs float noise.</summary>
    private const double RangeEpsilonDb = 0.01;

    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly IFftBackend _fft = new FftFlatBackend();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private FrequencyResponsePhase _phase = FrequencyResponsePhase.Idle;
    private bool _continuous;
    private int _passesCompleted;
    private int _averagesTaken;
    private int _averageCount;
    private double _generatorAmpDbv;
    private double _binHz;
    private double _sampleRateHz;
    private int _fftSize;
    private double _referenceOhms;
    private bool _rightChannelIsReference;
    private ImpedanceCalTarget _calTarget;
    private ImpedanceStimulus _stimulus;
    private int? _pointsMeasured;
    private int? _pointCount;
    private ImpedanceCalibration? _capturedCalibration;
    private double? _medianOhms;
    private string? _warning;

    /// <summary>
    /// A warning that holds for the whole run rather than being re-derived per pass -- today only
    /// <see cref="StoredStandardWarning"/>, which is a property of the request, not of a capture.
    /// </summary>
    private string? _standingWarning;
    private double? _driveCurrentAmps;
    private string? _error;
    private List<ImpedanceTrace> _traces = [];
    private ImpedanceRequest? _lastRequest;

    /// <summary>
    /// The runs of the measurements switched away from — see <see cref="SwitchOwner"/>, and
    /// <see cref="ThdSweepRunner.SwitchOwner"/> for the whole reasoning. Four, the same bound every
    /// one of these uses.
    /// </summary>
    private readonly RunParking<ParkedRun> _parking = new(capacity: 4);

    /// <summary>The request the most recent run used. Saving needs the as-run configuration, not whatever the panel shows by then.</summary>
    public ImpedanceRequest? LastRequest => _lastRequest;

    /// <summary>
    /// Every phase in which the device is busy on this measurement's behalf. The range probe and a
    /// calibration capture both count: this is what rejects a second start, keeps STOP enabled and
    /// tells a caller the run is over, so a phase missing from here reads as "finished" the moment it
    /// begins.
    /// </summary>
    public bool Running => _phase
        is FrequencyResponsePhase.DeterminingRange
        or FrequencyResponsePhase.Measuring
        or FrequencyResponsePhase.Sweeping
        or FrequencyResponsePhase.Calibrating;

    public ImpedanceStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new ImpedanceStatus(
                    Running, _phase, _continuous, _passesCompleted, _averagesTaken, _averageCount,
                    _generatorAmpDbv, _binHz, _sampleRateHz, _fftSize, _referenceOhms,
                    _rightChannelIsReference, _calTarget, _error, [.. _traces],
                    _capturedCalibration, _medianOhms, _driveCurrentAmps,
                    _stimulus, _pointsMeasured, _pointCount, _warning);
            }
        }
    }

    /// <summary>
    /// Validates and starts a run in the background, returning as soon as its shape is known. A run
    /// is several full records long, so it does not belong inside an HTTP request.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is already going, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not measurable.</exception>
    public async Task<ImpedanceStarted> StartAsync(ImpedanceRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        // Raised to the analyzer's own floor if it has one -- see StimulusFloor. Before anything
        // derives from the start frequency, so the gate, the stimulus and the saved parameters all
        // describe the sweep that was actually played.
        (double clampedStartHz, string? floorWarning) =
            StimulusFloor.Clamp(request.ChirpStartHz, session.MinStimulusFrequencyHz);

        if (floorWarning is not null)
        {
            request = request with { ChirpStartHz = clampedStartHz };
            logger.LogInformation(
                "{Measurement}: chirp start raised to the analyzer's floor of {Floor:0.#} Hz", "Impedance", clampedStartHz);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("An impedance measurement is already running.");

            // Built here rather than in the background task so the reply can say how many points
            // there are and how long they will take, and so an unmeasurable grid is a 400 rather than
            // a run that fails a moment later.
            var steps = request.IsStepped ? BuildSteps(request) : null;

            lock (_stateLock)
            {
                _phase = FrequencyResponsePhase.DeterminingRange;
                _continuous = request.Continuous;
                _passesCompleted = 0;
                _averagesTaken = 0;
                _averageCount = request.AverageCount;
                _generatorAmpDbv = request.GeneratorDbv;
                _binHz = request.BinHz;
                _sampleRateHz = request.SampleRateHz;
                _fftSize = request.FftSize;
                _referenceOhms = request.ReferenceOhms;
                _rightChannelIsReference = request.RightChannelIsReference;
                _calTarget = request.CalTarget;
                _stimulus = request.EffectiveStimulus;
                _pointsMeasured = steps is null ? null : 0;
                _pointCount = steps?.Length;
                _capturedCalibration = null;
                _medianOhms = null;
                _driveCurrentAmps = null;
                _standingWarning = StoredStandardWarning(request);
                _warning = _standingWarning;
                _error = null;
                _traces = [];
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new ImpedanceStarted(
                request.SampleRateHz, request.FftSize, request.BinHz, request.AverageCount,
                request.FftSize / request.SampleRateHz, request.Continuous, request.CalTarget,
                request.EffectiveStimulus,
                steps?.Length,
                steps is null ? null : [.. steps.Select(step => step.FrequencyHz)],
                steps is null ? null : EstimateSeconds(request, steps));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks a running measurement to stop and waits for it to unwind.</summary>
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

    /// <summary>
    /// Discards the traces on screen and returns the emptied status. Server-side because the runner
    /// is the source of truth: a client-only clear reappears on the next status poll.
    /// </summary>
    /// <exception cref="InvalidOperationException">A measurement is running.</exception>
    public ImpedanceStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before clearing.");

            _traces = [];
            _phase = FrequencyResponsePhase.Idle;
            _continuous = false;
            _passesCompleted = 0;
            _averagesTaken = 0;
            _averageCount = 0;
            _generatorAmpDbv = 0.0;
            _pointsMeasured = null;
            _pointCount = null;
            _capturedCalibration = null;
            _medianOhms = null;
            _driveCurrentAmps = null;
            _standingWarning = null;
            _warning = null;
            _error = null;

            return Status;
        }
    }

    /// <summary>
    /// Points the runner at the measurement now on screen — the impedance screen's counterpart of
    /// <see cref="FrequencyResponseRunner.SwitchOwner"/>, following
    /// <see cref="ThdSweepRunner.SwitchOwner"/>'s six rules unchanged.
    ///
    /// <para>A calibration capture writes <see cref="_traces"/> too (one standard curve), so the
    /// emptiness test covers both kinds of run — and the captured standard is parked with it, or
    /// switching away between capturing an open and capturing a short would lose the first one.</para>
    /// </summary>
    public ImpedanceStatus SwitchOwner(string? owner)
    {
        lock (_stateLock)
        {
            if (Running)
                return Status;

            var current = _traces.Count == 0
                ? null
                : new ParkedRun(
                    _phase, _continuous, _passesCompleted, _averagesTaken, _averageCount,
                    _generatorAmpDbv, _binHz, _sampleRateHz, _fftSize, _referenceOhms,
                    _rightChannelIsReference, _calTarget, _stimulus, _pointsMeasured, _pointCount,
                    _capturedCalibration, _medianOhms, _warning, _standingWarning,
                    _driveCurrentAmps, _error, _traces, _lastRequest);

            var outcome = _parking.Switch(owner, current);
            if (!outcome.Changed)
                return Status;

            if (outcome.Adopted is { } parked)
            {
                _phase = parked.Phase;
                _continuous = parked.Continuous;
                _passesCompleted = parked.PassesCompleted;
                _averagesTaken = parked.AveragesTaken;
                _averageCount = parked.AverageCount;
                _generatorAmpDbv = parked.GeneratorAmpDbv;
                _binHz = parked.BinHz;
                _sampleRateHz = parked.SampleRateHz;
                _fftSize = parked.FftSize;
                _referenceOhms = parked.ReferenceOhms;
                _rightChannelIsReference = parked.RightChannelIsReference;
                _calTarget = parked.CalTarget;
                _stimulus = parked.Stimulus;
                _pointsMeasured = parked.PointsMeasured;
                _pointCount = parked.PointCount;
                _capturedCalibration = parked.CapturedCalibration;
                _medianOhms = parked.MedianOhms;
                _warning = parked.Warning;
                _standingWarning = parked.StandingWarning;
                _driveCurrentAmps = parked.DriveCurrentAmps;
                _error = parked.Error;
                _traces = parked.Traces;
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
        // A fresh list, never the parked one emptied: the park holds this very reference.
        _traces = [];
        _phase = FrequencyResponsePhase.Idle;
        _continuous = false;
        _passesCompleted = 0;
        _averagesTaken = 0;
        _averageCount = 0;
        _generatorAmpDbv = 0.0;
        _binHz = 0.0;
        _sampleRateHz = 0.0;
        _fftSize = 0;
        _referenceOhms = 0.0;
        _rightChannelIsReference = false;
        _calTarget = ImpedanceCalTarget.None;
        _stimulus = ImpedanceStimulus.Chirp;
        _pointsMeasured = null;
        _pointCount = null;
        _capturedCalibration = null;
        _medianOhms = null;
        _driveCurrentAmps = null;
        _standingWarning = null;
        _warning = null;
        _error = null;
        _lastRequest = null;
    }

    /// <summary>Everything a measurement switch has to carry — <see cref="Status"/>'s own fields plus the as-run request and the standing warning behind it.</summary>
    private sealed record ParkedRun(
        FrequencyResponsePhase Phase, bool Continuous, int PassesCompleted, int AveragesTaken,
        int AverageCount, double GeneratorAmpDbv, double BinHz, double SampleRateHz, int FftSize,
        double ReferenceOhms, bool RightChannelIsReference, ImpedanceCalTarget CalTarget,
        ImpedanceStimulus Stimulus, int? PointsMeasured, int? PointCount,
        ImpedanceCalibration? CapturedCalibration, double? MedianOhms, string? Warning,
        string? StandingWarning, double? DriveCurrentAmps, string? Error,
        List<ImpedanceTrace> Traces, ImpedanceRequest? LastRequest);

    private async Task RunAsync(ImpedanceRequest request, ImpedanceStepPlan[]? steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;
        bool ownsInputRange = false;

        var terminalPhase = FrequencyResponsePhase.Failed;
        string? terminalError = null;
        int passes = 0;

        try
        {
            logger.LogInformation(
                "Impedance starting: {What} by {Stimulus}, {ReferenceOhms:0.###} ohm reference, reference channel {RefChannel}, "
                    + "span {SpanHz:0} Hz ({SampleRateHz:0} Hz sample rate), {FftSize} points ({BinHz:0.00} Hz per bin), "
                    + "{Averages} averages, generator {GenDbv:0.##} dBV, smoothing {Smoothing}, cable {CableMode}",
                request.CalTarget == ImpedanceCalTarget.None ? "measuring a DUT" : $"capturing the {request.CalTarget} standard",
                request.EffectiveStimulus,
                request.ReferenceOhms, request.RightChannelIsReference ? "right" : "left",
                request.FrequencySpanHz, request.SampleRateHz, request.FftSize, request.BinHz,
                request.AverageCount, request.GeneratorDbv,
                request.SmoothingDenominator == 0 ? "none" : $"1/{request.SmoothingDenominator} octave",
                request.CableMode);

            pipeline.Options = steps is null
                ? BuildOptions(savedOptions, request)
                : BuildSteppedOptions(savedOptions, steps[0].FftSize);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.SampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // Manual range control means manual, exactly as the chirp runner treats it.
            ownsInputRange = session.RunnerOwnsInputRange;

            if (ownsInputRange && session.AutoRangeStepDownEnabled is true)
            {
                session.SetAutoRangeStepDownEnabled(false);
                stepDownSuppressed = true;
            }

            double generatorAmpDbv = request.GeneratorDbv;

            if (steps is not null)
            {
                passes = 1;
                await MeasureSteppedAsync(request, steps, generatorAmpDbv, ownsInputRange, ct);
                terminalPhase = FrequencyResponsePhase.Finished;
                return;
            }

            // Always measured, never predicted. The generator level is given, but what reaches the
            // input depends on the divider -- which is the unknown this measurement exists to find,
            // so there is nothing to predict a range from. Both channels share one attenuator, and
            // the reference side is the larger of the two, so the probe's peak is the one that
            // matters.
            if (ownsInputRange)
                await ApplyInputRangeAsync(await ProbeInputRangeAsync(request, generatorAmpDbv, ct), ct);

            SetPhase(request.IsCalibration
                ? FrequencyResponsePhase.Calibrating
                : FrequencyResponsePhase.Measuring);

            var generator = BuildStimulus(request, generatorAmpDbv, request.FftSize);

            // Configured once for the whole run: in continuous mode the stimulus never changes, so
            // re-applying it would flush a queue depth of good audio on every repeat for nothing.
            bool generatorConfigured = false;

            // A continuous noise run keeps one estimator for its whole life, as a sliding window of
            // AverageCount records: every record after the first pass replaces the oldest rather
            // than starting the average over. Records of noise are independent draws, so the
            // curve after record N+1 is as good as after record N -- and this path publishes after
            // *every* record, so restarting per pass put the single-record curve, the worst of the
            // run, on screen once every pass. A chirp's passes stay independent: its records are
            // repeats of one sweep, nothing is published mid-pass, and a fresh estimator per pass
            // is what keeps a continuous chirp run a sequence of like-for-like measurements.
            FrequencyResponseEstimator? slidingEstimator = request.Continuous && request.IsNoise
                ? CreateEstimator(request, windowRecords: request.AverageCount)
                : null;

            do
            {
                ct.ThrowIfCancellationRequested();

                var estimator = slidingEstimator ?? CreateEstimator(request, windowRecords: 0);
                var pass = await MeasureOnceAsync(request, generator, estimator, generatorConfigured, ct);
                generatorConfigured = true;
                passes++;

                if (request.Continuous && ownsInputRange)
                    await ApplyInputRangeAsync(InputRangeSelector.For(session.Capabilities.InputRangesDbv, pass.PeakDbv), ct);

                PublishPass(pass);

                lock (_stateLock)
                    _passesCompleted = passes;

                if (passes == 1)
                {
                    if (request.IsCalibration)
                    {
                        logger.LogInformation(
                            "Impedance {Target} standard captured: {Points} stored points over {LowHz:0.0}-{HighHz:0} Hz",
                            request.CalTarget, pass.Calibration?.FreqHz.Length ?? 0,
                            request.AnalysisLowHz, request.AnalysisHighHz);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Impedance measured: {Averages} records, median {MedianOhms:0.###} ohms, "
                                + "peak {PeakOhms:0.###} ohms at {PeakHz:0.0} Hz, drive current about {CurrentMa:0.##} mA{Continuing}",
                            request.AverageCount, pass.MedianOhms ?? double.NaN,
                            pass.Traces.Count > 0 ? ImpedanceMath.FromDbOhms(pass.Traces[0].PeakDbOhms) : 0.0,
                            pass.Traces.Count > 0 ? pass.Traces[0].PeakFreqHz : 0.0,
                            (pass.DriveCurrentAmps ?? double.NaN) * 1000.0,
                            request.Continuous ? " (repeating until stopped)" : "");
                    }
                }
            }
            while (request.Continuous && !ct.IsCancellationRequested);

            if (request.Continuous)
                logger.LogInformation("Impedance stopped after {Passes} passes", passes);

            terminalPhase = FrequencyResponsePhase.Finished;
        }
        catch (OperationCanceledException)
        {
            // The phase describes the data on screen, not the manner of stopping -- see the chirp
            // runner, which reasons this out at length.
            terminalPhase = request.Continuous && passes > 0
                ? FrequencyResponsePhase.Finished
                : FrequencyResponsePhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impedance measurement failed");
            terminalPhase = FrequencyResponsePhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            // Silence the generator first and unconditionally, with CancellationToken.None, because
            // this has to run especially when the run was cancelled.
            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the impedance run");
            }

            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the impedance run");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the impedance run started");
                }
            }

            pipeline.Options = savedOptions;

            lock (_stateLock)
                _continuous = false;

            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_averagesTaken, _averageCount, terminalPhase);
        }
    }

    /// <summary>What one pass produced.</summary>
    private readonly record struct PassResult(
        List<ImpedanceTrace> Traces,
        double PeakDbv,
        ImpedanceCalibration? Calibration,
        double? MedianOhms,
        double? DriveCurrentAmps,
        string? Warning = null);

    /// <summary>
    /// The estimator for one whole-spectrum pass. Created per pass for a chirp -- which makes a
    /// continuous run a sequence of independent measurements instead of an ever-lengthening
    /// average -- and once per run, windowed, for continuous noise; see the caller for why.
    /// </summary>
    private FrequencyResponseEstimator CreateEstimator(ImpedanceRequest request, int windowRecords)
    {
        // Optional here, unlike the frequency response, where it is always on. Gating removes the
        // harmonic packets a swept measurement of a distorting DUT picks up, at a factor of two in
        // resolution -- and a passive impedance usually distorts far too little to be worth paying
        // that, which is why this is a choice.
        //
        // Never for noise, whatever the request says: those packets exist because *sweep*
        // deconvolution folds each harmonic into its own impulse ahead of the linear one. A noise
        // estimate has no such structure, so a gate there would delete part of a perfectly good
        // impulse response and call it distortion.
        ImpulseGate? gate = request.ApplySweepGate && !request.IsNoise
            ? ImpulseGate.ForSweep(request.FftSize, request.ChirpStartHz, request.ChirpEndHz)
            : null;

        // The regulariser follows the stimulus. A chirp has decades with no energy in them and needs
        // the default to bound those; noise reaches every bin, where the same lambda only biases the
        // quiet top of the band low -- see FrequencyResponseEstimator.BroadbandRegularisationFraction.
        return new FrequencyResponseEstimator(
            request.FftSize, _fft, gate, removeLatency: false,
            regularisationFraction: request.IsNoise
                ? FrequencyResponseEstimator.BroadbandRegularisationFraction
                : FrequencyResponseEstimator.RegularisationFraction,
            windowRecords: windowRecords);
    }

    /// <summary>
    /// One complete measurement: <see cref="ImpedanceRequest.AverageCount"/> records folded into
    /// <paramref name="estimator"/>, then solved and turned into a curve. A fresh estimator gives
    /// an independent measurement; a windowed one carried over from the previous pass gives a
    /// sliding average that never restarts.
    /// </summary>
    private async Task<PassResult> MeasureOnceAsync(
        ImpedanceRequest request,
        GeneratorConfig generator,
        FrequencyResponseEstimator estimator,
        bool generatorAlreadyConfigured,
        CancellationToken ct)
    {
        // What the average holds right now: nothing for a fresh estimator, a full window for one
        // carried over -- and the latter stays full, so the status reads "N of N" from here on
        // rather than counting up from one again.
        lock (_stateLock)
            _averagesTaken = estimator.Records;

        double peakDbv = double.NaN;
        var phase = request.IsCalibration
            ? FrequencyResponsePhase.Calibrating
            : FrequencyResponsePhase.Measuring;

        // The record armed by the stimulus change is thrown away rather than measured. See
        // FrequencyResponseRunner.MeasureOnceAsync: ConfigureGeneratorAsync discards the audio queued
        // for the DAC, but not the round trip back through the fixture and the ADC, and a rectangular
        // window over a record that is part old chirp and part new one is a period of nothing.
        if (!generatorAlreadyConfigured)
        {
            await session.ConfigureGeneratorAsync(generator, ct);
            BroadcastProgress(0, request.AverageCount, phase);
            await WaitForCaptureAsync(request, request.FftSize, ct);
        }

        for (int record = 0; record < request.AverageCount; record++)
        {
            ct.ThrowIfCancellationRequested();

            // Buffers emptied, nothing discarded: the stimulus has not changed since the throwaway
            // record above, so a non-zero discard would throw away good samples for nothing.
            pipeline.Flush(0);

            await WaitForCaptureAsync(request, request.FftSize, ct);

            var snapshots = pipeline.LatestSnapshots();
            peakDbv = ResponseCaptureSupport.PeakDbv(snapshots);
            var (left, right) = ResponseCaptureSupport.RequireBothChannels(snapshots, request.FftSize, "impedance");

            // Both from the one capture, so the sweep's rotation is common to them and cancels.
            var (reference, dut) = request.RightChannelIsReference ? (right, left) : (left, right);
            estimator.Add(reference, dut);

            lock (_stateLock)
                _averagesTaken = estimator.Records;

            BroadcastProgress(estimator.Records, request.AverageCount, phase);

            // Published as it goes, for the noise stimulus only. Every record genuinely improves a
            // Welch estimate, so there is something new to look at each time -- where a chirp's
            // records are repeats of one deterministic sweep, and a partial average of those would
            // redraw the same curve at more expense. Complete() re-derives H from the accumulators
            // on every call, so this costs a solve and nothing else.
            if (request.IsNoise && !request.IsCalibration && record + 1 < request.AverageCount)
                PublishPass(BuildPass(request, estimator.Complete(), peakDbv));
        }

        var result = estimator.Complete();
        var h = ImpedanceTraceBuilder.ToComplex(result);

        if (request.IsCalibration)
        {
            // The standard is stored as the measured ratio, uncorrected, and shown the same way: an
            // open should read near 0 dB and a few degrees, a short far below it. Solving a standard
            // to ohms would be useless for the open, whose whole point is that Z is infinite.
            var calibration = ImpedanceCalibration.Capture(
                h, request.BinHz, request.AnalysisLowHz, request.AnalysisHighHz);

            var kind = request.CalTarget == ImpedanceCalTarget.Open
                ? ImpedanceTraceKind.OpenCal
                : ImpedanceTraceKind.ShortCal;

            return new PassResult(
                [ImpedanceTraceBuilder.Build(request, kind, h, result.Coherence, DisplayColumns)],
                peakDbv, calibration, MedianOhms: null, DriveCurrentAmps: null,
                Warning: MislabelledStandardWarning(request.CalTarget, calibration));
        }

        return BuildPass(request, result, peakDbv);
    }

    /// <summary>
    /// The solved half of a pass: invert the divider, take the band median, and build the curve the
    /// chart draws. Separate from the capture loop only so a noise run can call it after each record
    /// as well as at the end.
    /// </summary>
    private static PassResult BuildPass(ImpedanceRequest request, FrequencyResponseResult result, double peakDbv)
    {
        var h = ImpedanceTraceBuilder.ToComplex(result);
        var z = ImpedanceMath.Solve(h, request.SolveOptions());

        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / request.BinHz));
        int lastBin = Math.Min(z.Length - 1, (int)Math.Floor(request.AnalysisHighHz / request.BinHz));
        double median = ImpedanceMath.MedianOhms(z, firstBin, lastBin);
        double? medianOhms = double.IsNaN(median) ? null : median;

        // Approximate on purpose: a sweep's current varies with Z across the band, so one number can
        // only be the band-typical one. It is still the figure that says whether a driver was
        // measured gently enough to trust.
        double? driveCurrentAmps = medianOhms is { } m
            ? Math.Pow(10.0, request.GeneratorDbv / 20.0) / (request.ReferenceOhms + m)
            : null;

        return new PassResult(
            [ImpedanceTraceBuilder.Build(request, ImpedanceTraceKind.Impedance, z, result.Coherence, DisplayColumns)],
            peakDbv, null, medianOhms, driveCurrentAmps,
            SwappedChannelWarning(request, ImpedanceMath.NegativeRealFraction(z, firstBin, lastBin)));
    }

    /// <summary>
    /// The one setup mistake the numbers can prove: a negative real impedance over most of the band,
    /// which a passive one-port cannot present and swapped inputs always do. Measured against the
    /// simulator's 8 ohm fixture with the reference channel the wrong way round: a flat 108 ohm line
    /// at ±180 degrees, reported as "the scale is wrong". The threshold is a majority of bins rather
    /// than any, because a real measurement's phase does wander past ±90 in the noise at the edges
    /// of the band.
    /// </summary>
    /// <summary>
    /// A standard that is not the standard it was captured as -- the open taken with the fixture
    /// still shorted, or the short taken with it open.
    ///
    /// <para>Cheap, and unambiguous in a way <see cref="SwappedChannelWarning"/> is not: the two
    /// standards sit three orders of magnitude apart, so this is never a marginal call. It is
    /// reported the moment the capture finishes, because the alternative is discovering it from a
    /// measurement days later -- and the measurement does not look broken, it looks like a flat
    /// negative resistance, which reads as a wiring fault and sends the search to the bench.</para>
    /// </summary>
    private static string? MislabelledStandardWarning(ImpedanceCalTarget target, ImpedanceCalibration calibration)
    {
        if (!calibration.IsUsable)
            return null;

        string median = $"|H| = {calibration.MedianMagnitude:G3}";

        return (target, calibration.LooksLikeOpen) switch
        {
            (ImpedanceCalTarget.Open, false) =>
                $"This capture reads {median} across the band, which is a short, not an open — an open reads close to 1. "
                + "Take everything off the fixture terminals and capture it again.",
            (ImpedanceCalTarget.Short, true) =>
                $"This capture reads {median} across the band, which is an open, not a short — a short reads close to 0. "
                + "Short the fixture terminals and capture it again.",
            _ => null,
        };
    }

    /// <summary>
    /// The same test applied to the standards a measurement is about to be corrected with, which is
    /// the case that actually bites: a bad standard is captured once and then silently ruins every
    /// measurement made after it. Checked at run start so the warning is on screen before the first
    /// pass, and kept standing for the whole run -- unlike a per-pass warning, nothing later in the
    /// run re-derives it.
    /// </summary>
    private static string? StoredStandardWarning(ImpedanceRequest request)
    {
        if (request.IsCalibration)
            return null;

        if (request.OpenCal is { IsUsable: true, LooksLikeOpen: false } open)
            return $"The stored open standard reads |H| = {open.MedianMagnitude:G3}, which is a short, not an open. "
                + "Correcting with it inflates the measured ratio by about 60 dB, so the impedance comes out flat at "
                + "roughly minus the reference resistor whatever is connected. Capture the open again with nothing "
                + "across the fixture, or clear it.";

        if (request.CableMode == ImpedanceCableMode.Measured
            && request.ShortCal is { IsUsable: true, LooksLikeOpen: true } shortCal)
            return $"The stored short standard reads |H| = {shortCal.MedianMagnitude:G3}, which is an open, not a short. "
                + "The lead impedance it subtracts is meaningless. Capture it again with the fixture terminals shorted, "
                + "or set the cable correction to None.";

        return null;
    }

    /// <summary>Both warnings when both fired, since a bad standard also makes the impedance go negative.</summary>
    private static string? JoinWarnings(string? first, string? second) =>
        first is null ? second
        : second is null ? first
        : $"{first} {second}";

    private static string? SwappedChannelWarning(ImpedanceRequest request, double negativeRealFraction)
    {
        if (!(negativeRealFraction > 0.5))
            return null;

        string other = request.RightChannelIsReference ? "Left" : "Right";
        return $"The measured node is louder than the generator side over {negativeRealFraction:P0} of the band, "
            + "which a series-resistor fixture cannot do: the impedance comes out negative and the phase sits at ±180°. "
            + $"The reference channel is almost certainly the other input — try {other}.";
    }

    /// <summary>Puts a pass -- finished, or partway through a noise average -- on the status and on the wire.</summary>
    private void PublishPass(PassResult pass)
    {
        lock (_stateLock)
        {
            _traces = pass.Traces;
            _capturedCalibration = pass.Calibration;
            _medianOhms = pass.MedianOhms;
            _driveCurrentAmps = pass.DriveCurrentAmps;
            _warning = JoinWarnings(_standingWarning, pass.Warning);
        }

        foreach (var trace in pass.Traces)
            hub.Broadcast(StreamFrame.EncodeImpedance(trace, StreamFlags.None));
    }

    /// <summary>
    /// The generator config this run's stimulus needs. <paramref name="repeatFrames"/> is the
    /// stimulus's period, which has to equal the size it will be analysed at -- so the input-range
    /// probe, which analyses short, passes its own rather than the run's.
    ///
    /// <para>Both stimuli are periodic over it: the chirp has been since it existed, and the noise
    /// since 2026-10-21. One record, one whole period, either way.</para>
    /// </summary>
    private static GeneratorConfig BuildStimulus(ImpedanceRequest request, double amplitudeDbv, int repeatFrames) =>
        request.IsNoise
            ? ResponseCaptureSupport.BuildPinkNoiseGenerator(amplitudeDbv, repeatFrames, request.NoiseCutoffHz)
            : ResponseCaptureSupport.BuildChirpGenerator(
                amplitudeDbv, request.ChirpStartHz, request.ChirpEndHz, repeatFrames);

    /// <summary>
    /// One stepped-sine pass: every point measured in turn, published as it goes, then solved.
    ///
    /// <para><b>Published per point rather than once at the end</b>, and that is not a nicety -- a
    /// hundred-point run is minutes long, and a chart that stays empty until it finishes is
    /// indistinguishable from a run that has hung.</para>
    /// </summary>
    private async Task MeasureSteppedAsync(
        ImpedanceRequest request,
        ImpedanceStepPlan[] steps,
        double generatorAmpDbv,
        bool ownsInputRange,
        CancellationToken ct)
    {
        // Probed once and then left alone, unlike the stepped-sine *response*, whose range is
        // predicted per point. The difference is the circuit: V_ref is the generator's own output
        // across the reference resistor, so the loud channel's level barely moves across the sweep
        // and there is nothing to track. Step-up stays live as the clip backstop.
        if (ownsInputRange)
            await ApplyInputRangeAsync(await ProbeSteppedInputRangeAsync(request, steps, generatorAmpDbv, ct), ct);

        var phase = request.IsCalibration
            ? FrequencyResponsePhase.Calibrating
            : FrequencyResponsePhase.Sweeping;
        SetPhase(phase);

        var freqHz = new double[steps.Length];
        var h = new Complex[steps.Length];
        var coherence = new double[steps.Length];
        int measured = 0;

        // The loudest sample any point saw, against the one range the probe settled on. A run whose
        // worst peak sits far under that range was measured with headroom nobody used; one that
        // reaches it is the run whose points the clip backstop will have been moving.
        double worstPeakDbv = double.NegativeInfinity;

        for (int i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            BroadcastProgress(i, steps.Length, phase);

            var step = steps[i];
            double rangeBefore = session.InputRangeDbv;

            var (accumulator, pointPeakDbv) = await CaptureStepAsync(request, step, generatorAmpDbv, ct);

            freqHz[measured] = step.FrequencyHz;
            h[measured] = accumulator.H;
            coherence[measured] = accumulator.Coherence;
            measured++;
            if (!double.IsNaN(pointPeakDbv))
                worstPeakDbv = Math.Max(worstPeakDbv, pointPeakDbv);

            lock (_stateLock)
                _pointsMeasured = measured;

            // The hardware's own clip backstop moving the range is handled -- the step announces
            // itself in band and the affected record is re-captured -- but a run full of these lines
            // means the probe ranged for a level the sweep does not actually see.
            if (Math.Abs(session.InputRangeDbv - rangeBefore) > RangeEpsilonDb)
            {
                logger.LogInformation(
                    "Input range moved {From:0.#} -> {To:0.#} dBV during the {FreqHz:0.##} Hz point",
                    rangeBefore, session.InputRangeDbv, step.FrequencyHz);
            }

            // BandwidthAnalyzer needs two samples before there is a curve at all.
            if (measured >= 2)
            {
                var (partial, _) = BuildSteppedTrace(request, freqHz, h, coherence, measured);

                lock (_stateLock)
                    _traces = [partial];

                hub.Broadcast(StreamFrame.EncodeImpedance(partial, StreamFlags.None));
            }
        }

        var (trace, values) = BuildSteppedTrace(request, freqHz, h, coherence, measured);

        ImpedanceCalibration? calibration = null;
        double? medianOhms = null;
        double? driveCurrentAmps = null;
        string? warning = null;

        if (request.IsCalibration)
        {
            // Only at the end: a partial standard stored halfway through would be a calibration
            // covering part of the band, and the browser would keep it.
            calibration = ImpedanceCalibration.FromPoints(freqHz.AsSpan(0, measured), values);
            warning = MislabelledStandardWarning(request.CalTarget, calibration);
        }
        else
        {
            double median = ImpedanceMath.MedianOhms(values, 0, values.Length - 1);
            medianOhms = double.IsNaN(median) ? null : median;
            driveCurrentAmps = medianOhms is { } m
                ? Math.Pow(10.0, request.GeneratorDbv / 20.0) / (request.ReferenceOhms + m)
                : null;
            warning = SwappedChannelWarning(request, ImpedanceMath.NegativeRealFraction(values, 0, values.Length - 1));
        }

        lock (_stateLock)
        {
            _traces = [trace];
            _passesCompleted = 1;
            _capturedCalibration = calibration;
            _medianOhms = medianOhms;
            _driveCurrentAmps = driveCurrentAmps;
            _warning = JoinWarnings(_standingWarning, warning);
        }

        hub.Broadcast(StreamFrame.EncodeImpedance(trace, StreamFlags.None));
        BroadcastProgress(measured, steps.Length, phase);

        if (request.IsCalibration)
        {
            logger.LogInformation(
                "Impedance {Target} standard captured by stepped sine: {Points} points over {LowHz:0.0}-{HighHz:0} Hz, "
                    + "worst input peak {PeakDbv:0.##} dBV against a {RangeDbv:0.#} dBV range",
                request.CalTarget, measured, freqHz[0], freqHz[measured - 1],
                worstPeakDbv, session.InputRangeDbv);
        }
        else
        {
            logger.LogInformation(
                "Impedance measured by stepped sine: {Points} points, median {MedianOhms:0.###} ohms, "
                    + "peak {PeakOhms:0.###} ohms at {PeakHz:0.0} Hz, drive current about {CurrentMa:0.##} mA, "
                    + "worst input peak {PeakDbv:0.##} dBV against a {RangeDbv:0.#} dBV range",
                measured, medianOhms ?? double.NaN,
                ImpedanceMath.FromDbOhms(trace.PeakDbOhms), trace.PeakFreqHz,
                (driveCurrentAmps ?? double.NaN) * 1000.0,
                worstPeakDbv, session.InputRangeDbv);
        }
    }

    /// <summary>
    /// One point: the throwaway record the stimulus change arms, then
    /// <see cref="ImpedanceRequest.AverageCount"/> independent records folded into a cross-spectrum.
    ///
    /// <para><b>The throwaway is not optional</b>, and it is the same rule the chirp path applies.
    /// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/> discards the audio already queued
    /// for the DAC, but that is the <i>play</i> latency, not the round trip back through the fixture
    /// and the ADC -- so some of the previous tone still reaches the front of the next record, and a
    /// record that is part old tone and part new one reads the new tone's bin low. The residue is a
    /// fixed number of frames, so the damage scales as 1/N, which is exactly why it shows up as
    /// "spikes only with adaptive resolution on": adaptive sizing takes the top of the band to where
    /// the residue is most of the record.</para>
    ///
    /// <para>Every average is a <i>whole</i> record behind its own <c>Flush(0)</c> rather than an
    /// overlapped hop. That costs more time than the stepped-sine response's averaging and buys the
    /// thing this measurement wants: independent records, without which
    /// <see cref="CrossSpectrumAccumulator.Coherence"/> would report the same record's agreement with
    /// itself.</para>
    /// </summary>
    private async Task<(CrossSpectrumAccumulator Accumulator, double PeakDbv)> CaptureStepAsync(
        ImpedanceRequest request, ImpedanceStepPlan step, double generatorAmpDbv, CancellationToken ct)
    {
        // Strictly before ConfigureGeneratorAsync, which is what flushes: changing FftSize makes the
        // acquisition thread allocate replacement ring buffers, and doing that afterwards would
        // discard the flush's own state.
        if (pipeline.Options.FftSize != step.FftSize)
            pipeline.Options = pipeline.Options with { FftSize = step.FftSize };

        // Arms the throwaway. Do NOT add a pipeline.Flush(0) straight after this call:
        // SampleRingBuffer.Reset(0) clears the pending in-flight discard counter, and the step lands
        // back inside the very record the discard existed to protect.
        await session.ConfigureGeneratorAsync(ToneConfig(step.FrequencyHz, generatorAmpDbv), ct);
        await WaitForCaptureAsync(request, step.FftSize, ct);

        var accumulator = new CrossSpectrumAccumulator();
        double peakDbv = double.NaN;

        for (int record = 0; record < request.AverageCount; record++)
        {
            ct.ThrowIfCancellationRequested();

            pipeline.Flush(0);
            await WaitForCaptureAsync(request, step.FftSize, ct);

            var snapshots = pipeline.LatestSnapshots();
            peakDbv = ResponseCaptureSupport.PeakDbv(snapshots);

            var left = ResponseCaptureSupport.ComplexAt(snapshots, StreamChannel.Left, step.Bin);
            var right = ResponseCaptureSupport.ComplexAt(snapshots, StreamChannel.Right, step.Bin);

            if (left is not { } l || right is not { } r)
            {
                throw new InvalidOperationException(
                    $"An impedance point at {step.FrequencyHz:0.##} Hz needs both channels' spectra, and the pipeline "
                        + $"returned {(left is null ? "no left" : "left")} and {(right is null ? "no right" : "right")}.");
            }

            // Both from the one capture, so the tone's arbitrary phase is common-mode and cancels.
            var (reference, dut) = request.RightChannelIsReference ? (r, l) : (l, r);
            accumulator.Add(reference, dut);

            lock (_stateLock)
                _averagesTaken = record + 1;
        }

        return (accumulator, peakDbv);
    }

    /// <summary>
    /// The curve so far: the raw measured ratio for a calibration standard, the solved impedance for
    /// a measurement.
    /// </summary>
    private static (ImpedanceTrace Trace, Complex[] Values) BuildSteppedTrace(
        ImpedanceRequest request, double[] freqHz, Complex[] h, double[] coherence, int count)
    {
        var freq = freqHz[..count];
        var ratio = h[..count];
        var coh = coherence[..count];

        if (request.IsCalibration)
        {
            // Stored and shown as measured, uncorrected: the open standard *is* the channel mismatch,
            // so normalising it against a previous open would report the difference between two
            // calibrations as an impedance.
            var kind = request.CalTarget == ImpedanceCalTarget.Open
                ? ImpedanceTraceKind.OpenCal
                : ImpedanceTraceKind.ShortCal;

            return (ImpedanceTraceBuilder.BuildPoints(kind, freq, ratio, coh), ratio);
        }

        var z = ImpedanceMath.Solve(ratio, request.SolveOptions(freq));
        return (ImpedanceTraceBuilder.BuildPoints(ImpedanceTraceKind.Impedance, freq, z, coh), z);
    }

    /// <summary>
    /// The stepped counterpart of <see cref="ProbeInputRangeAsync"/>: park on the least sensitive
    /// range, play one tone at the real generator level, capture, and pick the range from the peak.
    ///
    /// <para>The tone is 1 kHz clamped into the swept band. Ranging at a frequency the sweep never
    /// visits ranges for a level the run never sees.</para>
    /// </summary>
    private async Task<double> ProbeSteppedInputRangeAsync(
        ImpedanceRequest request, ImpedanceStepPlan[] steps, double generatorAmpDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;

        SetPhase(FrequencyResponsePhase.DeterminingRange);
        BroadcastProgress(0, 1, FrequencyResponsePhase.DeterminingRange);

        double probeHz = Math.Clamp(ProbeToneHz, steps[0].FrequencyHz, steps[^1].FrequencyHz);
        int probeFftSize = Math.Min(ProbeFftSize, request.FftSizeAt(probeHz));
        var probeTone = CoherentFrequency.Nearest(probeHz, request.SampleRateHz, probeFftSize);

        await ApplyInputRangeAsync(ranges.Max(), ct);

        pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
        await session.ConfigureGeneratorAsync(ToneConfig(probeTone.FrequencyHz, generatorAmpDbv), ct);
        await WaitForCaptureAsync(request, probeFftSize, ct);

        double peakDbv = ResponseCaptureSupport.PeakDbv(pipeline.LatestSnapshots());

        double selected = InputRangeSelector.For(ranges, peakDbv);
        logger.LogInformation(
            "Impedance input-range probe measured a peak of {PeakDbv:0.##} dBV at {GenDbv:0.##} dBV generator "
                + "({ProbeHz:0.##} Hz tone); input range {RangeDbv:0.#} dBV",
            peakDbv, generatorAmpDbv, probeTone.FrequencyHz, selected);

        return selected;
    }

    /// <summary>
    /// The tone, on both generator channels. Both always: the reference channel taps the generator
    /// side of the resistor, and the fixture is driven from the same output either way.
    /// </summary>
    private static GeneratorConfig ToneConfig(double freqHz, double ampDbv)
    {
        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(freqHz, ampDbv)])
        {
            Kind = GeneratorKind.Tones,
        };

        // Already coherent; snapping again is idempotent and keeps the device's reported tone
        // identical to the one this run recorded.
        return new GeneratorConfig(tone, tone) { SnapToBin = true };
    }

    /// <summary>The measured grid: log-spaced, snapped onto coherent bins, de-duplicated.</summary>
    private static ImpedanceStepPlan[] BuildSteps(ImpedanceRequest request)
    {
        double sampleRateHz = request.SampleRateHz;
        double maxHz = request.MaxFrequencyHz;
        var desired = SweepPoints.LogPerOctave(request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave);

        ImpedanceStepPlan[] steps = request.AdaptiveResolution
            ? [.. SweepPoints.SnapAndDedupeAdaptive(desired, sampleRateHz, request.FftSizeAt, maxHz)
                .Select(step => new ImpedanceStepPlan(
                    step.FrequencyHz, BinOf(step.FrequencyHz, step.FftSize, sampleRateHz), step.FftSize))]
            : [.. SweepPoints.SnapAndDedupe(desired, sampleRateHz, request.SteppedFftSize, maxHz)
                .Select(tone => new ImpedanceStepPlan(tone.FrequencyHz, tone.Bin, request.SteppedFftSize))];

        if (steps.Length < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"An impedance sweep needs at least two distinct points below {maxHz:0} Hz; this grid produced {steps.Length}.");
        }

        return steps;
    }

    /// <summary>
    /// The bin a snapped tone sits on. Exact by construction, and deliberately this rather than the
    /// peak the analyzer located: a peak search can lock onto mains hum, and reading the level off
    /// the wrong bin is invisible in the result.
    /// </summary>
    private static int BinOf(double freqHz, int fftSize, double sampleRateHz) =>
        (int)Math.Round(freqHz * fftSize / sampleRateHz);

    /// <summary>
    /// How long one point is expected to take: the in-flight audio the flush discards, the throwaway
    /// record after the stimulus change, then one whole record per average.
    /// </summary>
    private static double PointSeconds(ImpedanceRequest request, int fftSize, double settleSeconds)
    {
        // ~65 ms at every sample rate (Qa40xUsbDevice.StimulusLatencyFrames); 100 ms is a deliberately
        // loose stand-in rather than widening DeviceSessionService's surface for it. The settle time
        // is the user's own number (DeviceSessionService.CaptureDelayMs), discarded on top of it.
        const double StimulusLatencySeconds = 0.1;

        double recordSeconds = fftSize / request.SampleRateHz;
        return ((1 + Math.Max(1, request.AverageCount)) * recordSeconds) + StimulusLatencySeconds + settleSeconds;
    }

    /// <summary>What the whole sweep should take, for the UI's estimate. The range probe is one short capture and is not counted.</summary>
    private double EstimateSeconds(ImpedanceRequest request, ImpedanceStepPlan[] steps) =>
        steps.Sum(step => PointSeconds(request, step.FftSize, session.CaptureDelayMs / 1000.0));

    /// <summary>
    /// Pipeline settings for a stepped-sine capture. Two differ from the chirp's
    /// <see cref="BuildOptions"/> and both are load-bearing: the window is <b>Hann</b>, because a
    /// stepped tone is coherent by bin snapping rather than by being periodic in the record and a
    /// rectangular window would leak every tone that is a hair off its bin; and the size is one
    /// point's rather than the run's, since adaptive resolution gives every point its own.
    ///
    /// <para>The window cancels in the ratio anyway -- both channels get the same one -- but it
    /// decides how much of the neighbouring noise lands in the bin being read, which is what the
    /// coherence figure is measuring.</para>
    /// </summary>
    private static SpectrumPipelineOptions BuildSteppedOptions(SpectrumPipelineOptions current, int fftSize) =>
        current with
        {
            FftSize = fftSize,
            Window = WindowType.Hann,

            // The runner averages in the complex domain itself. The pipeline's averager works on
            // magnitudes, which would throw away the phase this measurement is entirely about.
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

    /// <summary>
    /// Measures the input range the fixture needs: park on the least sensitive range, play the chirp
    /// at the real generator level, capture once, and pick the range from the peak.
    ///
    /// <para>Least sensitive first because a probe that clips reads a peak that is a property of the
    /// range rather than of the signal, which would then choose that same range again and stay
    /// wrong.</para>
    /// </summary>
    private async Task<double> ProbeInputRangeAsync(
        ImpedanceRequest request, double generatorAmpDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;

        SetPhase(FrequencyResponsePhase.DeterminingRange);
        BroadcastProgress(0, 1, FrequencyResponsePhase.DeterminingRange);

        int probeFftSize = Math.Min(ProbeFftSize, request.FftSize);

        await ApplyInputRangeAsync(ranges.Max(), ct);

        pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
        await session.ConfigureGeneratorAsync(BuildStimulus(request, generatorAmpDbv, probeFftSize), ct);
        await WaitForCaptureAsync(request, probeFftSize, ct);

        double peakDbv = ResponseCaptureSupport.PeakDbv(pipeline.LatestSnapshots());

        pipeline.Options = pipeline.Options with { FftSize = request.FftSize };

        double selected = InputRangeSelector.For(ranges, peakDbv);
        logger.LogInformation(
            "Impedance input-range probe measured a peak of {PeakDbv:0.##} dBV at {GenDbv:0.##} dBV generator; input range {RangeDbv:0.#} dBV",
            peakDbv, generatorAmpDbv, selected);

        return selected;
    }

    private async Task ApplyInputRangeAsync(double rangeDbv, CancellationToken ct)
    {
        if (Math.Abs(rangeDbv - session.InputRangeDbv) <= RangeEpsilonDb)
            return;

        await session.SetInputRangeAsync(rangeDbv, ct);
    }

    /// <summary>
    /// Pipeline settings for a whole-spectrum capture -- chirp or noise. Identical to the frequency
    /// response's and load-bearing for the same reasons, with one difference: the retained complex
    /// spectrum because this measurement is built on phase, and a window that follows the stimulus.
    ///
    /// <para><b>Rectangular for both.</b> The chirp is periodic in the record by construction, and
    /// since 2026-10-21 so is the noise -- its period is the record length, so a whole period
    /// transforms exactly and a taper would destroy the periodicity the measurement now rests on in
    /// either case. It used to be Hann for noise, which was right while that noise was a continuous
    /// random stream periodic in nothing: rectangular there splices an arbitrary discontinuity into
    /// every record. The window cancels in the ratio either way -- both channels get the same one --
    /// but it decides how much of the neighbouring floor lands in each bin, which is what the
    /// coherence figure is measuring.</para>
    /// </summary>
    private static SpectrumPipelineOptions BuildOptions(SpectrumPipelineOptions current, ImpedanceRequest request) =>
        current with
        {
            FftSize = request.FftSize,
            // Rectangular for both now: the noise is periodic over the record too, so a whole
            // period transforms exactly and a taper would only destroy that -- the chirp's rule,
            // applied to the stimulus that used to be the exception.
            Window = WindowType.Rectangular,

            // The runner averages in the complex domain itself. The pipeline's averager works on
            // magnitudes, which would throw away the phase this measurement is entirely about.
            Averaging = AveragingMode.Off,
            AverageCount = 1,
            AverageHopFraction = 0.0,

            // Both channels always: one is the reference and the other the measurement, and they
            // must come from the same instant.
            ProcessLeft = true,
            ProcessRight = true,
            SingleShot = true,
            RetainComplexSpectrum = true,
            CapturePreview = false,
            Weighting = WeightingSpec.None,
        };

    private async Task WaitForCaptureAsync(ImpedanceRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = ResponseCaptureSupport.CaptureTimeout(request.SampleRateHz, fftSize, session.CaptureDelayMs, session.StreamSettleMs);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"An impedance record did not complete within {timeout.TotalSeconds:0.0} s. "
                        + "The device may have stopped streaming.");
            }

            await Task.Delay(CapturePollInterval, ct).ConfigureAwait(false);
        }
    }

    private void BroadcastProgress(int taken, int total, FrequencyResponsePhase phase)
    {
        double generatorAmpDbv;
        lock (_stateLock)
            generatorAmpDbv = _generatorAmpDbv;

        hub.Broadcast(StreamFrame.EncodeImpedanceProgress(taken, total, phase, generatorAmpDbv, StreamFlags.None));
    }

    private void SetPhase(FrequencyResponsePhase phase, string? error = null)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _error = error;
        }
    }

    /// <summary>One planned point: the coherent tone to play, the bin it lands on, and the size it is coherent for.</summary>
    private readonly record struct ImpedanceStepPlan(double FrequencyHz, int Bin, int FftSize);
}
