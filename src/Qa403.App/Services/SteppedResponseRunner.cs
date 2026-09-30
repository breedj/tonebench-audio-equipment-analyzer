using System.Numerics;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Measures a DUT's frequency response one tone at a time: step a sine across the band, read the
/// level at each point, and report the same Left/Right/Gain curves the chirp measurement produces.
///
/// <para><b>Why it is not the chirp.</b> See <see cref="SteppedResponseRequest"/> — a chirp excites
/// the whole band at once, which a transformer-coupled DUT does not answer the same way, and the
/// stepped sine is the reference that question is settled against. The price is time: minutes rather
/// than seconds, and a handful of points per octave rather than thousands of bins.</para>
///
/// <para><b>Modelled on <see cref="ThdSweepRunner"/>, not on <see cref="FrequencyResponseRunner"/>.</b>
/// The acquisition half is a stepped sweep — the same grid, the same 1 kHz level probe, the same
/// feed-forward input ranging, and the same rule that a stimulus change carries a flush and an
/// in-flight-DAC discard with it. Only the <i>result</i> is a frequency response, which is why it
/// emits <see cref="FrequencyResponseTrace"/> and saves through the same <c>response_trace</c>
/// storage.</para>
///
/// <para>Server-side for the same reasons both of those are: the flush rules live in
/// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/>, and a run survives a browser reload
/// because the UI re-attaches through <see cref="Status"/>.</para>
/// </summary>
public sealed class SteppedResponseRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<SteppedResponseRunner> logger) : IMeasurementRun
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private FrequencyResponsePhase _phase = FrequencyResponsePhase.Idle;
    private int _pointsMeasured;
    private int _pointCount;
    private int _averageCount;
    private double _generatorAmpDbv;
    private double _binHz;
    private double _sampleRateHz;
    private int _fftSize;
    private string? _error;
    private ResponsePhaseKind _phaseKind = ResponsePhaseKind.None;
    private bool _rightChannelIsReference;
    private List<FrequencyResponseTrace> _traces = [];
    private SteppedResponseRequest? _lastRequest;

    /// <summary>
    /// The points behind <see cref="_traces"/> and the level they were measured at, kept so the
    /// bandwidth reference can be changed after the run without re-measuring — the same reason
    /// <c>FrequencyResponseRunner</c> keeps its estimates. Replaced per point, dropped by <see cref="Clear"/>.
    /// </summary>
    private (double GeneratorAmpDbv, List<SteppedPoint> Points)? _lastPoints;

    /// <summary>
    /// A bandwidth reference applied after the run started; the points still to come publish with
    /// it. Cleared by <see cref="StartAsync"/>, whose request carries the panel's current value.
    /// </summary>
    private BandwidthReferenceSpec? _bandwidthOverride;

    /// <summary>
    /// The request the most recent run used. Saving needs the as-run configuration, not whatever the
    /// panel happens to show by the time Save is pressed.
    /// </summary>
    public SteppedResponseRequest? LastRequest => _lastRequest;

    /// <summary>
    /// Every phase in which the device is busy on this measurement's behalf. This is what rejects a
    /// second start, keeps STOP enabled, and tells a caller when the run is over — a phase missing
    /// from here reads as "finished" the moment it begins, which is how the chirp runner's own
    /// input-range probe broke every runner test at once when it was added.
    /// </summary>
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
                    Running, _phase, Continuous: false, PassesCompleted: 0,
                    // A stepped run's progress is points, not records — see the dedicated fields
                    // below. These two stay at the run's own average count so a reattaching client
                    // reads "3 averages per point" rather than a point counter in their place.
                    AveragesTaken: _averageCount, AverageCount: _averageCount,
                    _generatorAmpDbv, _binHz, _sampleRateHz, _fftSize,
                    LatencySamples: null, _error, [.. _traces],
                    PointsMeasured: _pointsMeasured, PointCount: _pointCount,
                    PhaseKind: _phaseKind, RightChannelIsReference: _rightChannelIsReference);
            }
        }
    }

    /// <summary>
    /// Validates the request, works out the frequency grid, and starts the run on a background task.
    /// Returns as soon as the grid is known — the run itself takes minutes, and the level probe
    /// alone is several captures, so neither belongs inside an HTTP request.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is already going, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not something that can be measured.</exception>
    public async Task<SteppedResponseStarted> StartAsync(SteppedResponseRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("A stepped response measurement is already running.");

            var steps = BuildSteps(request);

            lock (_stateLock)
            {
                // The phase the run will actually start in, decided here rather than left to the
                // background task to correct a moment later: a client polling status the instant
                // after start would otherwise be told the sweep was under way while a probe was
                // still running, and the progress line would read "Point 1 of 82" for several
                // seconds before the first point existed.
                _phase = request.SolvesGeneratorLevel
                    ? FrequencyResponsePhase.DeterminingLevel
                    : WillOwnInputRange()
                        ? FrequencyResponsePhase.DeterminingRange
                        : FrequencyResponsePhase.Sweeping;
                _pointsMeasured = 0;
                _pointCount = steps.Length;
                _averageCount = request.AverageCount;
                _generatorAmpDbv = request.SolvesGeneratorLevel ? SteppedCaptureSupport.ProbeStartDbv : request.LevelDbv;
                _sampleRateHz = request.EffectiveSampleRateHz;
                _fftSize = steps[0].FftSize;
                _binHz = request.EffectiveSampleRateHz / steps[0].FftSize;
                _error = null;
                _phaseKind = request.PhaseKind;
                _rightChannelIsReference = request.RightChannelIsReference;
                _traces = [];
                _lastPoints = null;
                _bandwidthOverride = null;
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new SteppedResponseStarted(
                steps.Length,
                [.. steps.Select(s => s.FrequencyHz)],
                request.EffectiveSampleRateHz,
                request.AverageCount,
                EstimateSeconds(request, steps));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks a running measurement to stop and waits for it to unwind. The points already measured
    /// are kept — a sweep abandoned halfway still measured the part it covered, the same way
    /// <see cref="ThdSweepRunner.CancelAsync"/> keeps its points.
    /// </summary>
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
    /// Discards the curves on screen and returns the emptied status. Server-side for the same reason
    /// as <see cref="ThdSweepRunner.Clear"/>: the runner is the source of truth and a client-only
    /// clear reappears on the next status poll.
    /// </summary>
    /// <exception cref="InvalidOperationException">A measurement is running.</exception>
    public FrequencyResponseStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before clearing.");

            _traces = [];
            _lastPoints = null;
            _phase = FrequencyResponsePhase.Idle;
            _pointsMeasured = 0;
            _pointCount = 0;
            _averageCount = 0;
            _generatorAmpDbv = 0.0;
            _error = null;

            return Status;
        }
    }

    /// <summary>
    /// Re-finds the bandwidth corners of the traces on screen against a new reference, without
    /// re-measuring — see <see cref="FrequencyResponseRunner.ApplyBandwidthReference"/>, whose
    /// reasoning this shares. While a sweep is running the points still to come publish with the
    /// new reference; the ones already on screen are rebuilt with them.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The band is not one a median can be taken over.</exception>
    public FrequencyResponseStatus ApplyBandwidthReference(BandwidthReferenceSpec spec)
    {
        spec.Validate();

        SteppedResponseRequest? request;
        (double GeneratorAmpDbv, List<SteppedPoint> Points)? points;
        lock (_stateLock)
        {
            _bandwidthOverride = spec;
            if (_lastRequest is not null)
                _lastRequest = WithBandwidthReference(_lastRequest, spec);
            request = _lastRequest;
            points = Running ? null : _lastPoints;
        }

        if (request is not null && points is { } p)
            PublishTraces(request, p.GeneratorAmpDbv, p.Points);

        return Status;
    }

    private static SteppedResponseRequest WithBandwidthReference(SteppedResponseRequest request, BandwidthReferenceSpec spec) =>
        request with
        {
            BandwidthReference = spec.Mode,
            BandwidthReferenceLowHz = spec.LowHz,
            BandwidthReferenceHighHz = spec.HighHz,
        };

    /// <summary>The run's request with any reference applied since it started — what a point publishes its traces with.</summary>
    private SteppedResponseRequest EffectiveRequest(SteppedResponseRequest request)
    {
        lock (_stateLock)
            return _bandwidthOverride is { } spec ? WithBandwidthReference(request, spec) : request;
    }

    /// <summary>
    /// Whether this run gets to program the input range. Manual range control means manual: with the
    /// Auto checkbox off the user has picked a range deliberately, and a measurement quietly
    /// reprogramming it would be the same class of surprise autoranging used to be before that
    /// checkbox existed.
    /// </summary>
    private bool WillOwnInputRange() =>
        session.RunnerOwnsInputRange;

    private async Task RunAsync(SteppedResponseRequest request, SteppedStepPlan[] steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;

        // Set only once everything below has been unwound — see the end of the finally block.
        var terminalPhase = FrequencyResponsePhase.Failed;
        string? terminalError = null;

        var points = new List<SteppedPoint>(steps.Length);

        try
        {
            logger.LogInformation(
                "Stepped response starting: {StartHz:0.##}-{EndHz:0.##} Hz, {StepsPerOctave}/octave, {Points} points, "
                    + "{SampleRateHz:0} Hz, adaptive {Adaptive} ({FftDescription}), {Averages} averages, "
                    + "level {LevelValue:0.##} {LevelUnit} ({LevelMode}), right channel is reference: {RightRef}",
                request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, steps.Length,
                request.EffectiveSampleRateHz, request.AdaptiveResolution,
                request.AdaptiveResolution
                    ? $"{steps.Max(s => s.FftSize)}-{steps.Min(s => s.FftSize)} points"
                    : $"fixed {request.FftSize} points",
                request.AverageCount, request.LevelValue, request.LevelUnit, request.LevelMode,
                request.RightChannelIsReference);

            pipeline.Options = SteppedCaptureSupport.BuildSteppedOptions(
                savedOptions, request.FftSize, request.Window, request.AverageCount, request.StartFreqHz, request.EffectiveSampleRateHz);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.EffectiveSampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            bool predictionOwnsRange = WillOwnInputRange();

            // The probe's own last reading doubles as the seed for the range prediction below: it is
            // a measurement of this DUT's output at a known generator level, which is exactly what
            // the predictor is. Its generator level comes back with it rather than being assumed —
            // a probe that runs out of iterations returns a level it has not measured at, and
            // pairing that with the earlier peak would offset every prediction.
            var (generatorAmpDbv, predictorPeakDbv, predictorGenDbv) = await ResolveGeneratorAmplitudeAsync(request, ct);
            SetGeneratorAmpDbv(generatorAmpDbv);

            // Suppressing step-down is what makes a programmed range stay programmed; deferred until
            // there is something to predict *from*, so a run with no probe still gets a freely-ranging
            // first point to seed itself off instead of being pinned to a leftover range.
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

            // Nothing has measured the DUT yet, which is the case whenever the generator level is
            // given directly and no level probe runs. Measure one tone for it rather than letting the
            // first sweep point discover the range -- see ProbeInputRangeAsync.
            if (predictionOwnsRange && double.IsNegativeInfinity(predictorPeakDbv))
            {
                predictorPeakDbv = await ProbeInputRangeAsync(request, generatorAmpDbv, ct);
                predictorGenDbv = generatorAmpDbv;
            }

            if (!double.IsNegativeInfinity(predictorPeakDbv))
                TakeOverRangingFromStepDown();

            // Never more sensitive than a range already used in this run -- see the per-point
            // prediction below. Seeded from whatever a probe settled on, so the sweep starts at the
            // range that was measured for and can only ever leave it upward.
            double rangeRatchetDbv = predictionOwnsRange && !double.IsNegativeInfinity(predictorPeakDbv)
                ? session.InputRangeDbv
                : double.NegativeInfinity;

            SetPhase(FrequencyResponsePhase.Sweeping);

            var unwrapper = new PhaseUnwrapper();

            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var step = steps[i];
                SetProgress(i);
                BroadcastProgress(i, steps.Length, FrequencyResponsePhase.Sweeping);

                // Feed-forward ranging: the previous point measured this DUT's output, and while it
                // is linear the level at the next frequency is close to it. Programming the range
                // here puts the change between points, where it rides along with the generator
                // change's own flush — rather than leaving it to be discovered mid-record by the
                // ranger, which costs a ~110 ms hole and a re-captured point.
                //
                // Monotone: the range may step up when the DUT turns out louder than predicted, and
                // never back down within one run.
                //
                // Deliberately unlike ThdSweepRunner's frequency axis, which follows the measured
                // level in both directions -- the trade-off is genuinely different here and the
                // reasoning does not transfer. A THD sweep measures harmonics 80-100 dB below the
                // fundamental, so a range one step too coarse costs real resolution and is worth a
                // change per point to avoid. This measures the *tone*, 20-30 dB above any floor,
                // where sensitivity is a non-issue -- and it compares levels **across frequency**, so
                // what a range change costs here is comparability: whatever the attenuator's
                // range-to-range calibration error is, a change mid-sweep writes it straight into the
                // response curve as a step, at exactly the frequency it happened. A slightly higher
                // floor everywhere is a far better trade than a discontinuity in the middle of the
                // trace.
                //
                // It also removes the last source of hunting, since the step-down half of the
                // device's own ranger is already suppressed (see TakeOverRangingFromStepDown) and
                // this was the only remaining thing that could lower the range.
                if (predictionOwnsRange && !double.IsNegativeInfinity(predictorPeakDbv))
                {
                    double predicted = InputRangeSelector.For(session.Capabilities.InputRangesDbv, predictorPeakDbv);
                    rangeRatchetDbv = Math.Max(predicted, rangeRatchetDbv);
                    await SteppedCaptureSupport.ApplyInputRangeAsync(session, rangeRatchetDbv, ct);
                }

                // Bracketed around the capture, not sampled at the end. A hole in the sample stream
                // splices whatever FFT record spans it, so the question that matters is which point's
                // record it landed in — and a total read afterwards cannot say.
                long dropsBefore = session.DroppedRxChunks;

                // Not the same reasoning as the drop counter: an autorange step is *handled*. It
                // announces itself in-band on the first block after the step
                // (PcmBlock.StartsAfterDiscontinuity) and DeviceSessionService.PumpAsync answers with
                // a bare flush, so this point re-captures at the new range rather than analysing
                // across the ~110 ms hole. Logged because it costs a record of time and moves the
                // analyzer's own residual -- and because a run full of these is the symptom of a
                // predictor that is not tracking, which is worth being able to see afterwards.
                double inputRangeBefore = session.InputRangeDbv;

                var snapshots = await SteppedCaptureSupport.CaptureWithStimulusAsync(
                    session, pipeline, ToneConfig(step.FrequencyHz, generatorAmpDbv, request), step.FftSize, CaptureTimeout(request, step.FftSize), ct);

                long dropsDuring = session.DroppedRxChunks - dropsBefore;
                if (dropsDuring > 0)
                {
                    logger.LogWarning(
                        "Stepped response point {Index}/{Total} at {FreqHz:0.##} Hz ({FftSize} points) captured across "
                            + "{Drops} dropped RX chunks — its record is spliced and its level is not trustworthy",
                        i + 1, steps.Length, step.FrequencyHz, step.FftSize, dropsDuring);
                }

                double inputRangeAfter = session.InputRangeDbv;
                if (Math.Abs(inputRangeAfter - inputRangeBefore) > SteppedCaptureSupport.RangeEpsilonDb)
                {
                    logger.LogInformation(
                        "Stepped response point {Index}/{Total} at {FreqHz:0.##} Hz: the input range moved "
                            + "{FromDbv:0.#} -> {ToDbv:0.#} dBV during the capture, so the point was re-measured "
                            + "at the new range",
                        i + 1, steps.Length, step.FrequencyHz, inputRangeBefore, inputRangeAfter);

                    // The clip backstop firing is the hardware saying the prediction was low. The
                    // ratchet has to learn from it, or the next point predicts its way straight back
                    // down onto a range this DUT has already overrun -- and then the backstop fires
                    // again, which is hunting with extra steps.
                    rangeRatchetDbv = Math.Max(rangeRatchetDbv, inputRangeAfter);
                }

                points.Add(ReadPoint(step, snapshots, generatorAmpDbv, request, unwrapper));

                // The predictor is the measured peak, not the level: it is the same
                // peak-to-RMS-equivalent "Pk dBV" quantity Qa40xAutoRanger evaluates, so prediction
                // and the backstop that catches its mistakes agree on what "too hot" means. Both
                // channels, because one input range serves both and the louder one decides.
                double pointPeakDbv = SweepCaptureSupport.MeasuredPeakDbv(snapshots);
                if (!double.IsNegativeInfinity(pointPeakDbv))
                {
                    predictorPeakDbv = pointPeakDbv;
                    predictorGenDbv = generatorAmpDbv;
                    TakeOverRangingFromStepDown();
                }

                PublishTraces(request, generatorAmpDbv, points);
                SetProgress(i + 1);
            }

            BroadcastProgress(steps.Length, steps.Length, FrequencyResponsePhase.Sweeping);
            terminalPhase = FrequencyResponsePhase.Finished;

            logger.LogInformation(
                "Stepped response measured: {Points} points at {GenDbv:0.##} dBV generator, predictor last saw "
                    + "{PeakDbv:0.##} dBV at {PredictorGenDbv:0.##} dBV",
                points.Count, generatorAmpDbv, predictorPeakDbv, predictorGenDbv);
        }
        catch (OperationCanceledException)
        {
            // The points already measured stay on screen; a run stopped halfway still measured the
            // part it covered, and the phase describes the data rather than the manner of stopping.
            terminalPhase = FrequencyResponsePhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stepped response failed");
            terminalPhase = FrequencyResponsePhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            // Silence the generator before anything else — a run that threw must not leave a tone
            // playing into someone's amplifier. CancellationToken.None because this must run even
            // (especially) when the run was cancelled.
            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the stepped response run");
            }

            // Before the acquisition stops, and unconditionally: the suppression is scoped to this
            // run, and leaving it set would hand the live view a ranger that can only ever get less
            // sensitive — indistinguishable from a stuck range, and only fixable by a restart.
            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the stepped response run");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the stepped response run started");
                }
            }

            pipeline.Options = savedOptions;

            // Last, once everything above has unwound: `Running` is derived from the phase, so this
            // is what makes "no longer running" also mean "and has finished cleaning up".
            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_pointsMeasured, _pointCount, terminalPhase);
        }
    }

    /// <summary>The run's grid — see <see cref="SteppedCaptureSupport.BuildSteps"/>.</summary>
    private static SteppedStepPlan[] BuildSteps(SteppedResponseRequest request) =>
        SteppedCaptureSupport.BuildSteps(
            request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, request.EffectiveSampleRateHz,
            request.MaxFrequencyHz, request.AdaptiveResolution, request.FftSize, request.FftSizeAt);

    /// <summary>
    /// The generator amplitude to measure at. Direct for <see cref="ThdSweepLevelMode.GeneratorVoltage"/>;
    /// for the output-referred modes it is solved by probing at 1 kHz — see
    /// <see cref="SteppedCaptureSupport.ProbeGeneratorLevelAsync"/>, which reads the <b>left</b>
    /// channel here: the right input is (usually) the reference, carrying the stimulus at the
    /// generator's own level, and solving against it would turn "1 W into 8 Ω" into "the generator
    /// at 2.83 V". The crosstalk runner passes its aggressor channel instead.
    /// </summary>
    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        SteppedResponseRequest request, CancellationToken ct)
    {
        // Nothing was measured, so there is nothing to seed the range predictor with — the first
        // point measures at whatever range is set and seeds it itself.
        if (!request.SolvesGeneratorLevel)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        int probeFftSize = request.FftSizeAt(SteppedCaptureSupport.ProbeFrequencyHz);
        var result = await SteppedCaptureSupport.ProbeGeneratorLevelAsync(
            session, pipeline, StreamChannel.Left, request.LevelDbv, request.EffectiveSampleRateHz, probeFftSize,
            request.GenerateLeft, request.GenerateRight, CaptureTimeout(request, probeFftSize),
            (phase, done, total, genDbv) =>
            {
                SetGeneratorAmpDbv(genDbv);
                BroadcastProgress(done, total, phase);
            },
            ct);

        SetGeneratorAmpDbv(result.GenDbv);
        return result;
    }

    /// <summary>
    /// Measures the input range this DUT needs before the sweep starts, for the directly-specified
    /// generator level where nothing else has measured it — see
    /// <see cref="SteppedCaptureSupport.ProbeInputRangeAsync"/>. The probe tone is 1 kHz
    /// <b>clamped into the swept band</b>: ranging at a frequency the sweep never visits would range
    /// for a level the run never sees, which on a DUT with any response at all is a different level.
    /// </summary>
    private async Task<double> ProbeInputRangeAsync(
        SteppedResponseRequest request, double generatorAmpDbv, CancellationToken ct)
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
            session, pipeline, ToneConfig(probeTone.FrequencyHz, generatorAmpDbv, request), probeTone.FrequencyHz,
            generatorAmpDbv, probeFftSize, CaptureTimeout(request, probeFftSize), logger, ct);
    }

    /// <summary>How long one point may take — see <see cref="SteppedCaptureSupport.CaptureTimeout"/>.</summary>
    private TimeSpan CaptureTimeout(SteppedResponseRequest request, int fftSize) =>
        SteppedCaptureSupport.CaptureTimeout(fftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0);

    /// <summary>
    /// What the whole run should take, for the UI's estimate. The level probe is not counted: it is
    /// at most <see cref="SteppedCaptureSupport.MaxProbeIterations"/> short captures and usually two.
    /// </summary>
    private double EstimateSeconds(SteppedResponseRequest request, SteppedStepPlan[] steps) =>
        steps.Sum(s => SteppedCaptureSupport.PointSeconds(s.FftSize, request.EffectiveSampleRateHz, request.AverageCount, session.CaptureDelayMs / 1000.0));

    private static GeneratorConfig ToneConfig(double freqHz, double ampDbv, SteppedResponseRequest request) =>
        SteppedCaptureSupport.ToneConfig(freqHz, ampDbv, request.GenerateLeft, request.GenerateRight);

    /// <summary>
    /// One measured point, read off both channels of a single capture.
    ///
    /// <para><b>Level</b> comes from <see cref="SpectrumSnapshot.Dbv"/> at the commanded coherent
    /// bin, which is the <i>averaged</i> amplitude spectrum in the same peak/√2 convention every dBV
    /// in this app uses. Reading one bin is exact rather than approximate: for a tone that is
    /// coherent in the record, <c>A = 2|X_k|/S1</c> recovers the whole amplitude from the centre bin
    /// — the golden test the whole DSP layer is pinned to.</para>
    ///
    /// <para><b>Phase</b> comes from the retained complex spectrum, which is the <i>last</i> record's
    /// rather than an average — the pipeline's averager works on magnitudes and would throw the phase
    /// away entirely. That is fine and is the only option: a coherent tone's phase in one record is
    /// already low-variance. What matters is that both channels come from the same tick, which
    /// <see cref="SpectrumPipeline"/> guarantees by snapshotting every channel under one lock; the
    /// window cancels in the difference because both channels get the same one.</para>
    /// </summary>
    private static SteppedPoint ReadPoint(
        SteppedStepPlan step,
        SpectrumSnapshot[] snapshots,
        double generatorAmpDbv,
        SteppedResponseRequest request,
        PhaseUnwrapper unwrapper)
    {
        double leftDbv = ResponseCaptureSupport.LevelAt(snapshots, StreamChannel.Left, step.Bin);
        double rightDbv = ResponseCaptureSupport.LevelAt(snapshots, StreamChannel.Right, step.Bin);

        if (double.IsNaN(leftDbv) || double.IsNaN(rightDbv))
        {
            throw new InvalidOperationException(
                $"A stepped response point at {step.FrequencyHz:0.##} Hz needs both channels' spectra, and the pipeline returned "
                    + $"{(double.IsNaN(leftDbv) ? "no left" : "left")} and {(double.IsNaN(rightDbv) ? "no right" : "right")}.");
        }

        // Gain against the measured reference when there is one, otherwise against the level the
        // generator was commanded to — the same two cases the chirp measurement has, and the same
        // meaning for each.
        double gainDb = request.RightChannelIsReference ? leftDbv - rightDbv : leftDbv - generatorAmpDbv;

        // arg(L/R) whenever the right channel carries the stimulus — the computation is identical
        // whether that channel is a reference or a second measured path, and what the reference
        // switch changes is only what the number means (see SteppedResponseRequest.PhaseKind).
        //
        // With no stimulus on it there is nothing to compare against, and a single channel's own
        // angle is no answer: with no capture trigger it is wherever the untriggered record happened
        // to land in the tone, uniform over a period and different every point.
        //
        // Unwrapped across the sweep so a DUT that accumulates several turns reads as a continuous
        // curve rather than a sawtooth.
        double phaseDeg = 0.0;
        if (request.GenerateRight)
        {
            var left = ResponseCaptureSupport.ComplexAt(snapshots, StreamChannel.Left, step.Bin);
            var right = ResponseCaptureSupport.ComplexAt(snapshots, StreamChannel.Right, step.Bin);
            if (left is { } l && right is { } r && r != Complex.Zero)
                phaseDeg = unwrapper.NextFromRadians((l / r).Phase);
        }

        return new SteppedPoint(step.FrequencyHz, leftDbv, rightDbv, gainDb, phaseDeg);
    }

    /// <summary>
    /// Rebuilds the three curves from everything measured so far and pushes them out.
    ///
    /// <para>Per point rather than once at the end, and that is not a nicety: a fifty-point run is
    /// minutes long, and a chart that stays empty until it finishes is indistinguishable from a run
    /// that has hung. The client keys traces by kind, so each set simply replaces the last.</para>
    /// </summary>
    private void PublishTraces(SteppedResponseRequest request, double generatorAmpDbv, List<SteppedPoint> points)
    {
        // BandwidthAnalyzer needs two samples to have a slope to walk down.
        if (points.Count < 2)
            return;

        var traces = BuildTraces(EffectiveRequest(request), points);

        lock (_stateLock)
        {
            _traces = traces;
            // A copy: the run keeps appending to its own list, and a rebuild must see a fixed set.
            _lastPoints = (generatorAmpDbv, [.. points]);
        }

        foreach (var trace in traces)
            hub.Broadcast(StreamFrame.EncodeFrequencyResponse(trace, StreamFlags.SteppedResponse));
    }

    /// <summary>
    /// The Left / Right / Gain triple, in the same shape and the same order the chirp measurement
    /// produces — which is what lets both save through <c>response_trace</c> and draw on one chart.
    /// </summary>
    private static List<FrequencyResponseTrace> BuildTraces(SteppedResponseRequest request, List<SteppedPoint> points)
    {
        var freqHz = new double[points.Count];
        var leftDb = new double[points.Count];
        var rightDb = new double[points.Count];
        var gainDb = new double[points.Count];
        var phaseDeg = new double[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            freqHz[i] = points[i].FreqHz;
            leftDb[i] = points[i].LeftDbv;
            rightDb[i] = points[i].RightDbv;
            gainDb[i] = points[i].GainDb;
            phaseDeg[i] = points[i].PhaseDeg;
        }

        // Phase belongs to the gain trace alone, because it is the only one that is a *relationship
        // between the two inputs* — arg(L/R). A channel's own trace has none to report, and writing
        // this one onto it would claim a measurement that was not made. Note the gain trace carries
        // it even when its magnitude is referenced to the commanded generator level instead of to the
        // right input: there is one phase curve in this measurement and this is where it lives.
        var noPhase = new double[points.Count];

        return
        [
            SteppedCaptureSupport.BuildPointTrace(FrequencyResponseTraceKind.Left, freqHz, leftDb, noPhase, request.BandwidthReferenceSpec),
            SteppedCaptureSupport.BuildPointTrace(FrequencyResponseTraceKind.Right, freqHz, rightDb, noPhase, request.BandwidthReferenceSpec),
            SteppedCaptureSupport.BuildPointTrace(FrequencyResponseTraceKind.Gain, freqHz, gainDb, phaseDeg, request.BandwidthReferenceSpec),
        ];
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

        hub.Broadcast(StreamFrame.EncodeFrequencyResponseProgress(done, total, phase, generatorAmpDbv, StreamFlags.SteppedResponse));
    }

    /// <summary>One measured point, before it becomes three parallel curves.</summary>
    private readonly record struct SteppedPoint(
        double FreqHz, double LeftDbv, double RightDbv, double GainDb, double PhaseDeg);
}
