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
    /// <summary>Where the level probe starts, and the level below which it gives up. Both from the previous app.</summary>
    private const double ProbeStartDbv = -40.0;

    /// <summary>Most passes the probe will make. Each is a full capture, so this is a real time cost.</summary>
    private const int MaxProbeIterations = 5;

    /// <summary>How close to the requested level the probe has to get before it stops.</summary>
    private const double ProbeToleranceDb = 0.5;

    private const double ProbeFrequencyHz = 1000.0;

    /// <summary>Longest record either probe will use. Both want a level, not resolution, and at the sweep's own sizes each pass would cost seconds.</summary>
    private const int MaxProbeFftSize = 16384;

    /// <summary>
    /// Below this the probe is not measuring a quiet DUT, it is measuring nothing.
    ///
    /// <para>The analyzer's own residual is about −161 dBV per root hertz at its most sensitive
    /// input range, which integrates to far more than this across any band, so a reading under
    /// −120 dBV means the signal path is broken — nothing connected, the DUT unpowered, the wrong
    /// channel — and not that the DUT is very quiet. Worth a hard stop rather than a correction,
    /// because the correction is what makes it dangerous: an output-referred probe that reads
    /// silence computes an enormous shortfall and winds the generator all the way to the DAC's
    /// ceiling, which is full scale into whatever <em>is</em> connected.</para>
    /// </summary>
    private const double ProbeSilenceDbv = -120.0;

    /// <summary>
    /// Fresh signal between two averages, as a fraction of one record — Welch's 50 %, exactly as
    /// <see cref="ThdSweepRunner"/> uses it. Here as there, an average that adds nothing is time
    /// added to every single point.
    /// </summary>
    private const double AverageHopFraction = 0.5;

    /// <summary>How often <see cref="WaitForCaptureAsync"/> re-checks. Short against the 20 Hz pipeline tick.</summary>
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Ranges closer than this count as the same one — the ladder is 6 dB apart, so this only absorbs float noise.</summary>
    private const double RangeEpsilonDb = 0.01;

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
                _generatorAmpDbv = request.SolvesGeneratorLevel ? ProbeStartDbv : request.LevelDbv;
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
        session.AutoRangeEnabled != false && session.Capabilities.InputRangesDbv.Count > 0;

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

            pipeline.Options = BuildOptions(savedOptions, request);

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
                    await ApplyInputRangeAsync(rangeRatchetDbv, ct);
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

                var snapshots = await CaptureWithStimulusAsync(
                    ToneConfig(step.FrequencyHz, generatorAmpDbv, request), request, step.FftSize, ct);

                long dropsDuring = session.DroppedRxChunks - dropsBefore;
                if (dropsDuring > 0)
                {
                    logger.LogWarning(
                        "Stepped response point {Index}/{Total} at {FreqHz:0.##} Hz ({FftSize} points) captured across "
                            + "{Drops} dropped RX chunks — its record is spliced and its level is not trustworthy",
                        i + 1, steps.Length, step.FrequencyHz, step.FftSize, dropsDuring);
                }

                double inputRangeAfter = session.InputRangeDbv;
                if (Math.Abs(inputRangeAfter - inputRangeBefore) > RangeEpsilonDb)
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
                double pointPeakDbv = MeasuredPeakDbv(snapshots);
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

    /// <summary>
    /// The points this run will measure, in order: a log-spaced grid, each frequency snapped onto a
    /// coherent bin of the size it will be analyzed at, de-duplicated where the spacing is finer
    /// than the bins.
    /// </summary>
    private static SteppedStepPlan[] BuildSteps(SteppedResponseRequest request)
    {
        double sampleRateHz = request.EffectiveSampleRateHz;
        double maxHz = request.MaxFrequencyHz;
        var desired = SweepPoints.LogPerOctave(request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave);

        SteppedStepPlan[] steps = request.AdaptiveResolution
            ? [.. SweepPoints.SnapAndDedupeAdaptive(desired, sampleRateHz, request.FftSizeAt, maxHz)
                .Select(s => new SteppedStepPlan(s.FrequencyHz, BinOf(s.FrequencyHz, s.FftSize, sampleRateHz), s.FftSize))]
            : [.. SweepPoints.SnapAndDedupe(desired, sampleRateHz, request.FftSize, maxHz)
                .Select(t => new SteppedStepPlan(t.FrequencyHz, t.Bin, request.FftSize))];

        if (steps.Length < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"A response needs at least two distinct points below {maxHz:0} Hz; this grid produced {steps.Length}.");
        }

        return steps;
    }

    /// <summary>
    /// The bin a snapped tone sits on. Exact by construction — <see cref="SweepPoints"/> already
    /// rounded the frequency onto a bin centre for this size — and it is deliberately this rather
    /// than the peak the analyzer located: a peak search can lock onto mains hum or a DUT's own
    /// oscillation, and reading the level off the wrong bin is invisible in the result.
    /// </summary>
    private static int BinOf(double freqHz, int fftSize, double sampleRateHz) =>
        (int)Math.Round(freqHz * fftSize / sampleRateHz);

    /// <summary>
    /// The pipeline settings for a stepped-response capture. Two differ from
    /// <c>ThdSweepRunner.BuildSweepOptions</c> and both are load-bearing:
    /// <see cref="SpectrumPipelineOptions.RetainComplexSpectrum"/>, because the phase is read off the
    /// complex bin, and both channels always processed, because the gain trace is a ratio between
    /// them and the two have to come from the same tick.
    ///
    /// <para><c>Averaging</c> follows the same rule the sweep uses: a single average is "no
    /// averaging", not "Linear with count 1". <see cref="SpectrumAverager"/> treats count ≤ 1 as
    /// <see cref="AveragingMode.Off"/> and leaves its Count at 0, so asking for Linear would leave
    /// the single-shot completion check false forever and every point would time out waiting for a
    /// capture that can never complete.</para>
    /// </summary>
    private static SpectrumPipelineOptions BuildOptions(SpectrumPipelineOptions current, SteppedResponseRequest request) =>
        current with
        {
            FftSize = request.FftSize,
            Window = request.Window,
            Averaging = request.AverageCount > 1 ? AveragingMode.Linear : AveragingMode.Off,
            AverageCount = request.AverageCount,
            ProcessLeft = true,
            ProcessRight = true,
            SingleShot = true,
            RetainComplexSpectrum = true,
            // A runner measures unweighted whatever the FFT screen has selected: a sweep's noise
            // and THD+N figures are defined over the plain band, and the weighting is restored
            // with the rest of the saved options afterwards.
            Weighting = WeightingSpec.None,

            // Nothing here reads a preview, and at these FFT sizes it is not free.
            CapturePreview = false,

            AverageHopFraction = AverageHopFraction,

            // The meters are only read for their peak (the ranging predictor), but the peak comes
            // with a located fundamental, and the default 200 Hz search floor exists to keep mains
            // hum from being mistaken for one. A run starting at 20 Hz would otherwise fail to
            // locate its own tone for the whole bottom decade and lose its range predictor with it.
            FundamentalMinSearchHz = Math.Min(current.FundamentalMinSearchHz, request.StartFreqHz / 2.0),
            HarmonicMaxFreqHz = request.EffectiveSampleRateHz / 2.0,
        };

    /// <summary>
    /// The generator amplitude to measure at. Direct for <see cref="ThdSweepLevelMode.GeneratorVoltage"/>;
    /// for the output-referred modes it is solved by probing at 1 kHz: measure what comes back,
    /// correct the generator by the shortfall, repeat.
    ///
    /// <para>Reads the <b>left</b> channel only, unlike <c>ThdSweepRunner</c>'s equivalent which
    /// takes the loudest of the two. Here the right input is (usually) the reference, carrying the
    /// stimulus at the generator's own level — taking the maximum would lock the probe onto that
    /// reference and solve for the generator level rather than for the DUT's output, silently
    /// turning "1 W into 8 Ω" into "the generator at 2.83 V".</para>
    ///
    /// <para>The input range is set from the <b>target</b> before the first probe pass, not after
    /// it. That ordering is load-bearing: the probe measures the DUT's gain from its own captures,
    /// so a leftover range too sensitive for the target clips them, the probe reads the loss as
    /// missing gain and pushes the generator up, which clips harder still — measured on the chirp
    /// path as a 10.7 dB overshoot against a plain loopback.</para>
    /// </summary>
    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        SteppedResponseRequest request, CancellationToken ct)
    {
        // Nothing was measured, so there is nothing to seed the range predictor with — the first
        // point measures at whatever range is set and seeds it itself.
        if (!request.SolvesGeneratorLevel)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        SetPhase(FrequencyResponsePhase.DeterminingLevel);
        BroadcastProgress(0, MaxProbeIterations, FrequencyResponsePhase.DeterminingLevel);

        double wantedOutDbv = request.LevelDbv;
        double maxGenDbv = session.Capabilities.OutputRangesDbv.Count > 0
            ? session.Capabilities.OutputRangesDbv.Max()
            : 18.0;

        if (session.AutoRangeEnabled != false)
            await SetInputRangeForAsync(wantedOutDbv, ct);

        int probeFftSize = request.FftSizeAt(ProbeFrequencyHz);
        var probeTone = CoherentFrequency.Nearest(ProbeFrequencyHz, request.EffectiveSampleRateHz, probeFftSize);
        double genDbv = ProbeStartDbv;

        // Tracked as a pair: the peak, and the generator level it was measured at. The loop can exit
        // having just applied an unmeasured correction, and a peak paired with the wrong generator
        // level is a constant offset on every prediction the run then makes.
        double peakDbv = double.NegativeInfinity;
        double peakAtGenDbv = genDbv;

        for (int i = 0; i < MaxProbeIterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            SetGeneratorAmpDbv(genDbv);
            BroadcastProgress(i, MaxProbeIterations, FrequencyResponsePhase.DeterminingLevel);

            var snapshots = await CaptureWithStimulusAsync(
                ToneConfig(probeTone.FrequencyHz, genDbv, request), request, probeFftSize, ct);

            peakDbv = MeasuredPeakDbv(snapshots);
            peakAtGenDbv = genDbv;

            double measuredDbv = ResponseCaptureSupport.LevelAt(snapshots, StreamChannel.Left, probeTone.Bin);
            if (double.IsNaN(measuredDbv) || measuredDbv < ProbeSilenceDbv)
            {
                throw new InvalidOperationException(
                    $"No signal came back on the left input at {genDbv:0.##} dBV generator while determining the drive level "
                        + "— check that the DUT is connected and powered, and that its output reaches the left input.");
            }

            double errorDb = wantedOutDbv - measuredDbv;

            // Measured at the level asked for: genDbv is right and has been verified, not just
            // calculated. Stop here rather than applying one more unmeasured correction.
            if (Math.Abs(errorDb) <= ProbeToleranceDb)
                break;

            // Already at the DAC's ceiling and still short — another pass would only re-clamp.
            if (genDbv >= maxGenDbv && errorDb > 0.0)
                break;

            genDbv += errorDb;

            if (genDbv <= ProbeStartDbv)
            {
                throw new InvalidOperationException(
                    $"Could not reach the requested output level — the generator would need to go below {ProbeStartDbv:0} dBV. "
                        + "Check that the DUT is connected and powered.");
            }

            genDbv = Math.Min(genDbv, maxGenDbv);
        }

        SetGeneratorAmpDbv(genDbv);
        return (genDbv, peakDbv, peakAtGenDbv);
    }

    /// <summary>
    /// Measures the input range this DUT needs before the sweep starts, for the directly-specified
    /// generator level where nothing else has measured it: park on the least sensitive range, play
    /// one tone at the real generator level, capture, and pick the range from the peak. Returns that
    /// peak, so it also seeds the per-point predictor.
    ///
    /// <para>This is <see cref="FrequencyResponseRunner"/>'s own input-range probe, ported for the
    /// same reason it exists there: without it the run starts on whatever range the previous
    /// measurement left behind and lets the device's own ranger walk to the right one. That works --
    /// an autorange step announces itself in-band and the point re-captures -- but every step costs a
    /// ~110 ms hole and a re-captured record, and with the predictor unseeded the first point cannot
    /// suppress step-down either, so the ranger hunts freely through the start of the sweep. Arriving
    /// on the right range costs one short capture instead.</para>
    ///
    /// <para>Least sensitive range first, and for the same reason as there: an unknown DUT can be
    /// doing anything, and a probe that clips reads a peak that is a property of the range rather
    /// than of the signal -- which would then choose that same range again and stay wrong.</para>
    ///
    /// <para>The probe tone is 1 kHz <b>clamped into the swept band</b>. Ranging at a frequency the
    /// sweep never visits would range for a level the run never sees, which on a DUT with any
    /// response at all is a different level.</para>
    /// </summary>
    private async Task<double> ProbeInputRangeAsync(
        SteppedResponseRequest request, double generatorAmpDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;
        if (ranges.Count == 0)
            return double.NegativeInfinity;

        SetPhase(FrequencyResponsePhase.DeterminingRange);
        BroadcastProgress(0, 1, FrequencyResponsePhase.DeterminingRange);

        double probeFreqHz = Math.Clamp(
            ProbeFrequencyHz, request.StartFreqHz, Math.Min(request.EndFreqHz, request.MaxFrequencyHz));
        int probeFftSize = Math.Min(request.FftSizeAt(probeFreqHz), MaxProbeFftSize);
        var probeTone = CoherentFrequency.Nearest(probeFreqHz, request.EffectiveSampleRateHz, probeFftSize);

        await ApplyInputRangeAsync(ranges.Max(), ct);

        var snapshots = await CaptureWithStimulusAsync(
            ToneConfig(probeTone.FrequencyHz, generatorAmpDbv, request), request, probeFftSize, ct);

        double peakDbv = MeasuredPeakDbv(snapshots);
        if (double.IsNegativeInfinity(peakDbv))
        {
            // Nothing came back. Leave the range where the probe parked it -- the widest, which is
            // the safe place to start a sweep of a DUT we know nothing about -- and let the first
            // point seed the predictor the way it used to.
            logger.LogWarning(
                "The input-range probe measured nothing at {FreqHz:0.##} Hz; leaving the range at {RangeDbv:0.#} dBV "
                    + "and letting the first sweep point find it",
                probeTone.FrequencyHz, session.InputRangeDbv);
            return double.NegativeInfinity;
        }

        double selected = InputRangeSelector.For(ranges, peakDbv);
        logger.LogInformation(
            "Input-range probe measured a peak of {PeakDbv:0.##} dBV at {FreqHz:0.##} Hz / {GenDbv:0.##} dBV generator; "
                + "input range {RangeDbv:0.#} dBV",
            peakDbv, probeTone.FrequencyHz, generatorAmpDbv, selected);

        await ApplyInputRangeAsync(selected, ct);
        return peakDbv;
    }

    /// <summary>
    /// Programs the smallest input range that still leaves <see cref="InputRangeSelector.HeadroomDb"/>
    /// above <paramref name="expectedLevelDbv"/>, out of whatever this device actually offers. One
    /// ladder decides every input range in this app — see <see cref="InputRangeSelector"/>.
    /// </summary>
    private async Task SetInputRangeForAsync(double expectedLevelDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;
        if (ranges.Count == 0)
            return;

        await ApplyInputRangeAsync(InputRangeSelector.For(ranges, expectedLevelDbv), ct);
    }

    /// <summary>
    /// Goes through <see cref="DeviceSessionService.SetInputRangeAsync"/> so a mid-stream change is
    /// applied on the transfer pump rather than written to EP1 with EP2 transfers in flight.
    /// </summary>
    private async Task ApplyInputRangeAsync(double rangeDbv, CancellationToken ct)
    {
        if (Math.Abs(rangeDbv - session.InputRangeDbv) <= RangeEpsilonDb)
            return;

        await session.SetInputRangeAsync(rangeDbv, ct);
    }

    /// <summary>
    /// Applies a stimulus and returns the analysis of a record that holds <em>only</em> that
    /// stimulus.
    ///
    /// <para><b>The record armed by the stimulus change is thrown away, not measured</b> — the same
    /// rule, for the same reason, as <see cref="FrequencyResponseRunner.MeasureOnceAsync"/>.
    /// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/> discards the audio already queued
    /// for the DAC, but that is the <i>play</i> latency, not the round trip back through the DUT and
    /// the ADC, so some pre-change audio still reaches the front of the next record. A record that is
    /// part old tone and part new one reads the new tone's bin low by whatever fraction of the record
    /// the old tone occupied.</para>
    ///
    /// <para><see cref="ThdSweepRunner"/> tolerates exactly this, and is right to: THD is a ratio of
    /// harmonics to the fundamental <i>within one record</i>, so a level error common to both cancels.
    /// This measurement reads an absolute level, so nothing cancels.</para>
    ///
    /// <para>The residue is a fixed number of frames, so the damage scales as 1/<paramref name="fftSize"/>
    /// — which is why it showed up as "spikes only with adaptive resolution on". A fixed 192 kHz /
    /// 131072 run gives every point a 683 ms record and the residue is a per-cent of it; adaptive
    /// sizing takes the top of the band down to 4096 points (21 ms at 192 kHz), where the same
    /// residue is most of the record. Measured on the simulator before this: 6.0 dB low at 996 Hz /
    /// 8192, and a total miss (−205 dBV) at 2836 Hz / 4096, on a plain loopback.</para>
    ///
    /// <para>Waiting one whole record is the only bound available that does not depend on a latency
    /// figure the app never measures. It costs one extra record per point.</para>
    /// </summary>
    private async Task<SpectrumSnapshot[]> CaptureWithStimulusAsync(
        GeneratorConfig generator, SteppedResponseRequest request, int fftSize, CancellationToken ct)
    {
        // Strictly before ConfigureGeneratorAsync, which is what flushes. Changing FftSize makes the
        // acquisition thread allocate replacement ring buffers, and doing that after the flush would
        // discard the flush's own state — the buffers it emptied are not the buffers the next record
        // fills.
        if (pipeline.Options.FftSize != fftSize)
            pipeline.Options = pipeline.Options with { FftSize = fftSize };

        // The throwaway is one *record*, not one averaged capture: all it has to do is carry the ring
        // past the samples that still hold the previous tone, and averaging it would pay the whole
        // average count twice per point. Averaging off makes SingleShotComplete fire on the first
        // completed record instead.
        var measurementOptions = pipeline.Options;
        pipeline.Options = measurementOptions with { Averaging = AveragingMode.Off, AverageCount = 1 };

        // Arms the throwaway. Do NOT add a pipeline.Flush(0) straight after this call:
        // SampleRingBuffer.Reset(0) would clear the pending in-flight discard counter, and the step
        // would land back inside the very record the flush existed to protect.
        await session.ConfigureGeneratorAsync(generator, ct);
        await WaitForCaptureAsync(request, fftSize, ct);

        // Restored before the flush, which is what resets the averagers — the measured capture has
        // to start its averaging from empty, and from the settings the run asked for.
        pipeline.Options = measurementOptions;

        // Buffers emptied, nothing discarded: the stimulus has not changed since the throwaway
        // above, so there is no pre-change audio left in flight and a non-zero discard would throw
        // away good samples for nothing.
        pipeline.Flush(0);
        await WaitForCaptureAsync(request, fftSize, ct);

        return pipeline.LatestSnapshots();
    }

    private async Task WaitForCaptureAsync(SteppedResponseRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = CaptureTimeout(request, fftSize);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"A stepped response point did not complete within {timeout.TotalSeconds:0.0} s. The device may have stopped streaming.");
            }

            await Task.Delay(CapturePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long one point may take: the time to refill the FFT window after the flush (including the
    /// discarded in-flight audio), plus the averages, times three for slack. Generous on purpose —
    /// this exists to notice a wedged device, not to police timing.
    /// </summary>
    private TimeSpan CaptureTimeout(SteppedResponseRequest request, int fftSize) =>
        TimeSpan.FromSeconds((PointSeconds(request, fftSize, session.CaptureDelayMs / 1000.0) * 3.0) + 2.0);

    /// <summary>
    /// How long one point is expected to take: the in-flight audio the flush discards, the throwaway
    /// record that follows the stimulus change (see <see cref="CaptureWithStimulusAsync"/>), one
    /// record to refill the window after its flush, then <c>AverageCount</c> averages at
    /// <see cref="AverageHopFraction"/> — <c>1 + (n-1)·hop</c> records rather than <c>n</c>.
    /// </summary>
    private static double PointSeconds(SteppedResponseRequest request, int fftSize, double settleSeconds)
    {
        // ~65 ms at every sample rate (Qa40xUsbDevice.StimulusLatencyFrames); 100 ms is a
        // deliberately loose stand-in rather than widening DeviceSessionService's surface for it.
        // The settle time is the user's own number (DeviceSessionService.CaptureDelayMs), discarded
        // on top of it at every stimulus change.
        const double StimulusLatencySeconds = 0.1;

        double recordSeconds = fftSize / request.EffectiveSampleRateHz;
        double averageSeconds = (1.0 + ((Math.Max(1, request.AverageCount) - 1) * AverageHopFraction)) * recordSeconds;
        return (2.0 * recordSeconds) + StimulusLatencySeconds + settleSeconds + averageSeconds;
    }

    /// <summary>
    /// What the whole run should take, for the UI's estimate. The level probe is not counted: it is
    /// at most <see cref="MaxProbeIterations"/> short captures and usually two.
    /// </summary>
    private double EstimateSeconds(SteppedResponseRequest request, SteppedStepPlan[] steps) =>
        steps.Sum(s => PointSeconds(request, s.FftSize, session.CaptureDelayMs / 1000.0));

    private static GeneratorConfig ToneConfig(double freqHz, double ampDbv, SteppedResponseRequest request)
    {
        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(freqHz, ampDbv)]) { Kind = GeneratorKind.Tones };
        return new GeneratorConfig(
            request.GenerateLeft ? tone : ChannelGeneratorConfig.Off,
            request.GenerateRight ? tone : ChannelGeneratorConfig.Off)
        {
            // The frequency is already coherent; snapping again is idempotent and keeps the device's
            // reported tone identical to the one this run recorded.
            SnapToBin = true,
        };
    }

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
    /// The largest time-domain peak any channel saw, in the same "Pk dBV" (peak volts / √2)
    /// convention <c>Qa40xAutoRanger</c> compares against — one input range serves both channels, so
    /// the louder one decides. Negative infinity when nothing was measured.
    /// </summary>
    private static double MeasuredPeakDbv(SpectrumSnapshot[] snapshots)
    {
        double best = double.NegativeInfinity;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is { } meters)
                best = Math.Max(best, meters.PeakDbv);
        }

        return best;
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
            hub.Broadcast(StreamFrame.EncodeFrequencyResponse(trace, stepped: true));
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
            BuildTrace(request, FrequencyResponseTraceKind.Left, freqHz, leftDb, noPhase),
            BuildTrace(request, FrequencyResponseTraceKind.Right, freqHz, rightDb, noPhase),
            BuildTrace(request, FrequencyResponseTraceKind.Gain, freqHz, gainDb, phaseDeg),
        ];
    }

    /// <summary>
    /// One curve. Nothing is decimated and nothing is smoothed: every sample here <i>is</i> a
    /// measurement, so the min and the max of a "column" are the same number, and there is no
    /// fractional-octave smoothing to apply to a grid already coarser than the narrowest smoothing
    /// the chirp offers. Coherence is 1.0 for the same reason — one deterministic capture per point
    /// has no cross-record variance to report, and reporting a fabricated figure would be worse than
    /// reporting none.
    /// </summary>
    private static FrequencyResponseTrace BuildTrace(
        SteppedResponseRequest request,
        FrequencyResponseTraceKind kind,
        double[] freqHz,
        double[] magnitudeDb,
        double[] phaseDeg)
    {
        var peak = BandwidthAnalyzer.FindPeak(magnitudeDb, freqHz);

        // Same reference and the same two questions as the chirp path -- see
        // ResponseCaptureSupport.BuildTrace. A stepped sweep is the measurement most likely to be
        // pointed at an amplifier's low end, where a peak reference is at its most misleading.
        var reference = BandwidthAnalyzer.FindReference(magnitudeDb, freqHz, request.BandwidthReferenceSpec);
        var oneDb = BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 1.0, reference, symmetric: true);
        var threeDb = BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 3.0, reference, symmetric: false);

        int n = freqHz.Length;
        var freq = new float[n];
        var mag = new float[n];
        var phase = new float[n];
        var coherence = new float[n];

        for (int i = 0; i < n; i++)
        {
            freq[i] = (float)freqHz[i];
            mag[i] = (float)magnitudeDb[i];
            phase[i] = (float)phaseDeg[i];
            coherence[i] = 1.0f;
        }

        // The same array for min and max: the chart draws the max as the line and the min as a band
        // beneath it, and here there is nothing between them to draw.
        return new FrequencyResponseTrace(
            kind,
            freqHz[0],
            freqHz[^1],
            freq,
            mag,
            (float[])mag.Clone(),
            phase,
            coherence,
            BandwidthReading.From(oneDb),
            BandwidthReading.From(threeDb),
            peak.LevelDb,
            peak.FreqHz,
            reference.LevelDb,
            reference.Mode,
            reference.FellBackToPeak);
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

        hub.Broadcast(StreamFrame.EncodeFrequencyResponseProgress(done, total, phase, generatorAmpDbv, stepped: true));
    }

    /// <summary>One planned point: the coherent tone to play, the bin it lands on, and the size it is coherent for.</summary>
    private readonly record struct SteppedStepPlan(double FrequencyHz, int Bin, int FftSize);

    /// <summary>One measured point, before it becomes three parallel curves.</summary>
    private readonly record struct SteppedPoint(
        double FreqHz, double LeftDbv, double RightDbv, double GainDb, double PhaseDeg);
}
