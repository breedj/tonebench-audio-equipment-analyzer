using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Drives a stepped THD-vs-frequency sweep: for each coherent frequency, set the generator, wait
/// for the analysis to settle, and record the harmonics.
///
/// Server-side rather than client-side on purpose. Every step is a stimulus change, and a stimulus
/// change has two hard rules attached (flush the ring buffers, and discard the audio still queued
/// for the DAC when the flush happened — see <see cref="SpectrumPipeline.Flush"/>). Both already
/// live inside <see cref="DeviceSessionService.ConfigureGeneratorAsync"/>; driving the loop from the
/// browser would mean re-implementing them across the network, once per client. It also means a
/// sweep survives a browser reload — the UI re-attaches through <see cref="Status"/>.
///
/// Kept out of the endpoint file for the same reason as <see cref="MeasurementSaveService"/>: it is
/// testable against <c>SimulatedDevice</c> without spinning up a host.
/// </summary>
public sealed class ThdSweepRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<ThdSweepRunner> logger)
{
    /// <summary>Where the level probe starts, and the level below which it gives up. Both from the previous app.</summary>
    private const double ProbeStartDbv = -40.0;

    /// <summary>
    /// Most passes the probe will make. Each is a full capture, so this is a real time cost — but
    /// it exits as soon as it is within <see cref="ProbeToleranceDb"/>, and for a roughly-linear DUT
    /// that is the second pass.
    /// </summary>
    private const int MaxProbeIterations = 5;

    /// <summary>
    /// How close to the requested level the probe has to get before it stops.
    ///
    /// Convergence with an early exit, rather than a fixed number of corrections, is what makes the
    /// result trustworthy: the loop used to apply its last correction and return without ever
    /// measuring the outcome, so one bad reading on the final pass — a record that happened to span
    /// the stimulus change — became the amplitude the whole sweep then ran at, with nothing to catch
    /// it. Now a bad reading only costs another pass.
    /// </summary>
    private const double ProbeToleranceDb = 0.5;

    private const double ProbeFrequencyHz = 1000.0;

    /// <summary>
    /// Fresh signal between two averages, as a fraction of one record — see
    /// <see cref="SpectrumPipelineOptions.AverageHopFraction"/>. Welch's 50 %: eight averages cost
    /// 4.5 records rather than 8, and keep about 90 % of the variance reduction of fully
    /// independent ones. The sweep sets this and the live view does not, because here an average
    /// that adds nothing is time added to every single point.
    /// </summary>
    private const double AverageHopFraction = 0.5;

    /// <summary>Headroom left above the expected level when picking an input range, in dB — see <see cref="InputRangeSelector.HeadroomDb"/>, which owns the ladder this and the device's own ranger share.</summary>
    private const double InputRangeHeadroomDb = InputRangeSelector.HeadroomDb;

    /// <summary>
    /// Fraction of Nyquist the highest sweep point may reach. A tone at Nyquist itself has no
    /// harmonics at all and lands in the one bin <see cref="PsdSpectrum"/> gives half weight.
    /// </summary>
    private const double MaxFrequencyNyquistFraction = 0.98;

    /// <summary>How often <see cref="WaitForCaptureAsync"/> re-checks. Short against the 20 Hz pipeline tick, so it costs a poll or two per average.</summary>
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _sweepTask;

    private ThdSweepPhase _phase = ThdSweepPhase.Idle;
    private SweepAxis _axis = SweepAxis.Frequency;
    private int _index;
    private int _total;
    private double _currentFreqHz;
    private double _currentAmplitudeVolt;
    private double _generatorAmpDbv;
    private string? _error;
    private List<ThdSweepPoint> _points = [];
    private ThdSweepRequest? _lastRequest;

    /// <summary>
    /// The request the most recent sweep ran with, or null if none has run. Saving a result needs
    /// it: the as-run parameters must be the ones the points were actually measured under, not
    /// whatever the config panel happens to show by the time someone presses Save.
    /// </summary>
    public ThdSweepRequest? LastRequest => _lastRequest;

    public bool Running => _phase is ThdSweepPhase.DeterminingLevel or ThdSweepPhase.NoiseFloor or ThdSweepPhase.Sweeping;

    public ThdSweepStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new ThdSweepStatus(
                    Running, _phase, _index, _total, _currentFreqHz, _generatorAmpDbv, _error, [.. _points],
                    _axis, _currentAmplitudeVolt);
            }
        }
    }

    /// <summary>
    /// Validates the request, works out the frequency grid, and starts the sweep on a background
    /// task. Returns as soon as the grid is known — the sweep itself takes minutes, and the level
    /// probe alone is several captures, so neither belongs inside an HTTP request.
    /// </summary>
    /// <exception cref="InvalidOperationException">A sweep is already running, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not something that can be swept.</exception>
    public async Task<ThdSweepStarted> StartAsync(ThdSweepRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("A sweep is already running.");

            var steps = BuildSteps(request);

            lock (_stateLock)
            {
                _phase = ThdSweepPhase.DeterminingLevel;
                _axis = request.Axis;
                _index = 0;
                _total = steps.Length;
                _currentFreqHz = 0.0;
                _currentAmplitudeVolt = 0.0;
                _generatorAmpDbv = request.Axis == SweepAxis.Amplitude
                    ? steps[0].GeneratorDbv
                    : request.LevelMode == ThdSweepLevelMode.GeneratorVoltage ? request.LevelDbv : ProbeStartDbv;
                _error = null;
                _points = [];
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _sweepTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new ThdSweepStarted(
                steps.Length,
                [.. steps.Select(s => s.FrequencyHz)],
                request.Axis == SweepAxis.Amplitude ? [.. steps.Select(s => DbvToVolt(s.GeneratorDbv))] : []);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks a running sweep to stop and waits for it to unwind. The points already measured are
    /// kept — a sweep abandoned halfway is still a measurement of the part it covered, and the
    /// previous app kept them too.
    /// </summary>
    public async Task CancelAsync()
    {
        var cts = _cts;
        var task = _sweepTask;
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
    /// Discards the sweep on screen and returns the emptied status.
    /// </summary>
    /// <remarks>
    /// Server-side, because the runner is the source of truth and the browser is a mirror: a
    /// client-only clear comes straight back on the next <c>GET /api/sweep/status</c>, which is
    /// what a reload, a second tab, and every switch back to the sweep screen all perform.
    /// <para>
    /// The whole progress block is reset with the points, not just the list — leaving
    /// <c>Finished · 50 points</c> next to an empty chart states something that is no longer true.
    /// <see cref="LastRequest"/> deliberately survives: it is the as-run configuration of a sweep
    /// that is gone, and <see cref="MeasurementSaveService.SaveSweepAsync"/> is already gated on
    /// the points being non-empty, so it can never be saved against.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A sweep is running — clearing would race the writer still appending to the list.</exception>
    public ThdSweepStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the sweep to finish (or cancel it) before clearing.");

            _points = [];
            _phase = ThdSweepPhase.Idle;
            _index = 0;
            _total = 0;
            _currentFreqHz = 0;
            _currentAmplitudeVolt = 0;
            _generatorAmpDbv = 0;
            _error = null;

            return Status;
        }
    }

    private async Task RunAsync(ThdSweepRequest request, SweepStepPlan[] steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;

        // Set only once everything below has been unwound -- see the end of the finally block.
        var terminalPhase = ThdSweepPhase.Failed;
        string? terminalError = null;

        try
        {
            // The as-run configuration, once, before anything else. Nothing recorded it before, so a
            // log showing dropped chunks during a sweep could not answer the first question anyone
            // asks of it — which resolution mode and FFT size were actually in play.
            if (request.Axis == SweepAxis.Amplitude)
            {
                logger.LogInformation(
                    "THD-vs-amplitude sweep starting: {StartVolt:0.####}-{EndVolt:0.####} V at {FreqHz:0.##} Hz, "
                        + "{StepsPerOctave}/octave, {Points} points, {SampleRateHz:0} Hz, fixed {FftSize} points, "
                        + "{Averages} averages, load {LoadOhms:0.##} Ohm",
                    request.StartAmplitudeVolt, request.EndAmplitudeVolt,
                    steps.Length > 0 ? steps[0].FrequencyHz : request.FixedFreqHz,
                    request.StepsPerOctave, steps.Length, request.SampleRateHz, request.FftSize,
                    request.AverageCount, request.LoadOhms);
            }
            else
            {
                logger.LogInformation(
                    "THD sweep starting: {StartHz:0.##}-{EndHz:0.##} Hz, {StepsPerOctave}/octave, {Points} points, "
                        + "{SampleRateHz:0} Hz, adaptive {Adaptive} ({FftDescription}), {Averages} averages, "
                        + "level {LevelValue:0.##} {LevelUnit} ({LevelMode}), noise floor first: {NoiseFloor}",
                    request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave, steps.Length,
                    request.EffectiveSampleRateHz, request.AdaptiveResolution,
                    request.AdaptiveResolution
                        ? $"{steps.Max(s => s.FftSize)}-{steps.Min(s => s.FftSize)} points"
                        : $"fixed {request.FftSize} points",
                    request.AverageCount, request.LevelValue, request.LevelUnit, request.LevelMode,
                    request.MeasureNoiseFloor);
            }

            pipeline.Options = BuildSweepOptions(savedOptions, request);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.EffectiveSampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // The level probe means nothing when the level is the thing being stepped -- there is no
            // single amplitude to solve for -- so it is skipped explicitly rather than left to lean
            // on its own early return. The noise-floor pass is axis-independent and runs on both.
            //
            // The probe's own last reading doubles as the seed for the range prediction below: it is
            // a measurement of this DUT's output at a known generator level, which is exactly what
            // the predictor is. Its generator level comes back with it rather than being assumed to
            // be the returned one -- a probe that runs out of iterations returns a level it has not
            // measured at, and pairing that with the earlier peak would offset every prediction.
            double generatorAmpDbv;
            double predictorPeakDbv = double.NegativeInfinity;
            double predictorGenDbv;

            if (request.Axis == SweepAxis.Amplitude)
            {
                generatorAmpDbv = VoltToDbv(request.StartAmplitudeVolt);
                predictorGenDbv = generatorAmpDbv;
            }
            else
            {
                (generatorAmpDbv, predictorPeakDbv, predictorGenDbv) = await ResolveGeneratorAmplitudeAsync(request, ct);
            }

            SetGeneratorAmpDbv(generatorAmpDbv);

            // Manual range control means manual: with the Auto checkbox off the user has picked a
            // range deliberately, and a sweep quietly reprogramming it would be the same class of
            // surprise autoranging used to be before that checkbox existed.
            bool predictionOwnsRange = session.AutoRangeEnabled != false;

            // Never steps back down within one sweep on the amplitude axis -- see
            // ApplyPredictedInputRangeAsync.
            double rangeRatchetDbv = double.NegativeInfinity;

            // Suppressing step-down is what makes a programmed range stay programmed; it is deferred
            // until there is something to predict *from*, so a sweep with no probe still gets a
            // freely-ranging first point to seed itself off instead of being pinned to whatever
            // range happened to be left over from the last measurement.
            void TakeOverRangingFromStepDown()
            {
                if (stepDownSuppressed || !predictionOwnsRange || session.AutoRangeStepDownEnabled is not true)
                    return;

                session.SetAutoRangeStepDownEnabled(false);
                stepDownSuppressed = true;
                logger.LogInformation(
                    "Input range is now predicted per point from the measured level; autorange step-down "
                        + "suppressed for the rest of the sweep (step-up stays live as the clip backstop)");
            }

            if (!double.IsNegativeInfinity(predictorPeakDbv))
                TakeOverRangingFromStepDown();

            // Deliberately after the suppression above: with step-down live, a generator-off pass is
            // the one thing guaranteed to walk the range down to the most sensitive setting, so the
            // floor would be measured on a range no sweep point then runs at.
            var noiseVrms = await MeasureNoiseFloorAsync(request, ct);

            SetPhase(ThdSweepPhase.Sweeping);

            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var step = steps[i];

                // The level is the sweep-wide one on the frequency axis and the swept variable on the
                // amplitude axis; the status readout follows whichever it is.
                double stepAmpDbv = request.Axis == SweepAxis.Amplitude ? step.GeneratorDbv : generatorAmpDbv;
                if (request.Axis == SweepAxis.Amplitude)
                    SetGeneratorAmpDbv(stepAmpDbv);

                SetProgress(i, step.FrequencyHz, DbvToVolt(stepAmpDbv));
                BroadcastProgress(i, steps.Length, ProgressX(request, step.FrequencyHz, stepAmpDbv), ThdSweepPhase.Sweeping);

                // Feed-forward ranging. What this point will measure is already known well enough to
                // range for: the previous point measured this DUT's output, and while it is linear a
                // generator step of x dB moves that output by x dB. Programming the range from that
                // *here* puts the change between points, where it rides along with the generator
                // change's own flush and costs nothing -- rather than leaving it to be discovered
                // mid-record by the ranger, which costs a ~110 ms hole and a re-captured point.
                //
                // Where linearity fails it fails safe: past clipping the real output is lower than
                // predicted, so the range is coarser than ideal -- a slightly higher floor, never a
                // clipped record. The opposite direction (a DUT louder than predicted -- a gain peak
                // between two frequencies, an amp going unstable) is what the ranger's step-up half
                // is still there for.
                if (predictionOwnsRange && !double.IsNegativeInfinity(predictorPeakDbv))
                {
                    rangeRatchetDbv = await ApplyPredictedInputRangeAsync(
                        predictorPeakDbv + (stepAmpDbv - predictorGenDbv), request.Axis, rangeRatchetDbv, ct);
                }

                // Bracketed around the capture, not sampled after the run. A hole in the sample stream
                // splices whatever FFT record spans it, so the question that matters is which point's
                // record it landed in — and a total read at the end cannot say.
                long dropsBefore = session.DroppedRxChunks;

                // Not the same reasoning as the drop counter above: an autorange step is *handled*.
                // It announces itself in-band on the first block after the step
                // (PcmBlock.StartsAfterDiscontinuity) and DeviceSessionService.PumpAsync answers with
                // a bare SpectrumPipeline.Flush, which empties the analysis window and clears the
                // single-shot hold -- so this point re-captures from scratch at the new range rather
                // than analysing a record spliced across the ~110 ms hole the quiesce punched. This
                // is logged because it costs a record of time and moves the analyzer's own residual
                // (0 dBV range: -161 dB re 1 V/sqrt(Hz); 24 dBV: -126), which is worth being able to
                // attribute afterwards -- not because the reading is suspect.
                double inputRangeBefore = session.InputRangeDbv;

                var snapshots = await CaptureWithStimulusAsync(
                    ToneConfig(step.FrequencyHz, stepAmpDbv, request), request, step.FftSize, ct);

                long dropsDuring = session.DroppedRxChunks - dropsBefore;
                if (dropsDuring > 0)
                {
                    logger.LogWarning(
                        "Sweep point {Index}/{Total} at {FreqHz:0.##} Hz ({FftSize} points) captured across "
                            + "{Drops} dropped RX chunks — its record is spliced and its THD is not trustworthy",
                        i + 1, steps.Length, step.FrequencyHz, step.FftSize, dropsDuring);
                }

                // The predictor is the measured peak, not the fundamental: it is the same
                // peak-to-RMS-equivalent "Pk dBV" quantity Qa40xAutoRanger evaluates, so prediction
                // and the backstop that catches its mistakes agree on what "too hot" means.
                double pointPeakDbv = MeasuredPeakDbv(snapshots);
                if (!double.IsNegativeInfinity(pointPeakDbv))
                {
                    predictorPeakDbv = pointPeakDbv;
                    predictorGenDbv = stepAmpDbv;
                    TakeOverRangingFromStepDown();
                }

                double inputRangeAfter = session.InputRangeDbv;
                if (Math.Abs(inputRangeAfter - inputRangeBefore) > 0.01)
                {
                    logger.LogInformation(
                        "Sweep point {Index}/{Total} at {GenDbv:0.##} dBV: autoranging stepped the input range "
                            + "{FromDbv:0.#} -> {ToDbv:0.#} dBV during the capture, so the point was re-measured at "
                            + "the new range. Its residual is that range's, not the one the noise-floor pass used",
                        i + 1, steps.Length, stepAmpDbv, inputRangeBefore, inputRangeAfter);

                    // With prediction running this is the backstop firing, i.e. the hardware saying
                    // the prediction was low. The ratchet has to learn from it, or the next point
                    // predicts its way straight back down onto a range this DUT has already overrun.
                    if (request.Axis == SweepAxis.Amplitude)
                        rangeRatchetDbv = Math.Max(rangeRatchetDbv, inputRangeAfter);
                }

                foreach (var point in BuildPoints(step.FrequencyHz, stepAmpDbv, request, snapshots, noiseVrms))
                {
                    AddPoint(point);
                    hub.Broadcast(StreamFrame.EncodeSweepPoint(point, (uint)i));
                }
            }

            SetProgress(steps.Length, 0.0, 0.0);
            terminalPhase = ThdSweepPhase.Finished;
        }
        catch (OperationCanceledException)
        {
            terminalPhase = ThdSweepPhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "THD sweep failed");
            terminalPhase = ThdSweepPhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            // Silence the generator before anything else — a sweep that threw must not leave a tone
            // playing into someone's amplifier. CancellationToken.None because this must run even
            // (especially) when the sweep was cancelled.
            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the sweep");
            }

            // Before the acquisition stops, and unconditionally: the suppression is scoped to this
            // sweep, and leaving it set would hand the live view a ranger that can only ever get
            // less sensitive — indistinguishable from a stuck range, and only fixable by a restart.
            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the sweep");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the sweep started");
                }
            }

            pipeline.Options = savedOptions;

            // The terminal phase lands last, after the generator is silent, the acquisition this
            // sweep started is stopped and the pipeline options are back. `Running` is derived from
            // the phase, so this is what makes "the sweep is no longer running" mean "and it has
            // finished cleaning up" -- announcing Finished first left a window where a caller could
            // save, or read pipeline.Options, and see the sweep's own state instead of its own.
            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_index, _total, 0.0, terminalPhase);
        }
    }

    /// <summary>
    /// The points a sweep will measure, in order — one frequency and one generator level each,
    /// whichever of the two is the axis. Both axes produce the same shape so the loop in
    /// <see cref="RunAsync"/> never has to branch on the mode again.
    /// </summary>
    private static SweepStepPlan[] BuildSteps(ThdSweepRequest request) =>
        request.Axis == SweepAxis.Amplitude ? BuildAmplitudeSteps(request) : BuildFrequencySteps(request);

    private static SweepStepPlan[] BuildFrequencySteps(ThdSweepRequest request)
    {
        double sampleRateHz = request.EffectiveSampleRateHz;
        double maxHz = MaxFrequencyNyquistFraction * sampleRateHz / 2.0;
        var desired = SweepPoints.LogPerOctave(request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave);
        double levelDbv = request.LevelDbv;

        SweepStepPlan[] steps = request.AdaptiveResolution
            ? [.. SweepPoints.SnapAndDedupeAdaptive(desired, sampleRateHz, request.FftSizeAt, maxHz)
                .Select(s => new SweepStepPlan(s.FrequencyHz, s.FftSize, levelDbv))]
            : [.. SweepPoints.SnapAndDedupe(desired, sampleRateHz, request.FftSize, maxHz)
                .Select(t => new SweepStepPlan(t.FrequencyHz, request.FftSize, levelDbv))];

        if (steps.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(request), $"No sweep frequency falls below {maxHz:0} Hz at this sample rate.");

        return steps;
    }

    /// <summary>
    /// One tone, snapped once, played at each of a log-spaced ladder of generator amplitudes.
    ///
    /// There is no snap-and-dedupe pass here, and no adaptive sizing: the frequency never moves, so
    /// coherence is settled before the sweep starts, and two adjacent amplitudes are never "the same
    /// point" the way two frequencies rounding into one bin are.
    /// <see cref="SweepPoints.LogPerOctave"/> is pure log spacing over any positive quantity — a
    /// voltage octave is a doubling — which is exactly what the previous app's
    /// <c>GetLineairSpacedLogarithmicValuesPerOctave</c> did for this screen.
    /// </summary>
    private static SweepStepPlan[] BuildAmplitudeSteps(ThdSweepRequest request)
    {
        var tone = CoherentFrequency.Nearest(request.FixedFreqHz, request.SampleRateHz, request.FftSize);
        var volts = SweepPoints.LogPerOctave(request.StartAmplitudeVolt, request.EndAmplitudeVolt, request.StepsPerOctave);

        return [.. volts.Select(v => new SweepStepPlan(tone.FrequencyHz, request.FftSize, VoltToDbv(v)))];
    }

    /// <summary>
    /// The sweep's analysis settings. Two of these differ from anything the live view would set and
    /// are the difference between a working sweep and a broken one:
    ///
    /// <list type="bullet">
    /// <item><c>HarmonicMaxFreqHz = Nyquist</c> — otherwise every harmonic of a point above ~7 kHz
    /// is outside the 20 kHz measurement band and THD reads 0. See that option's own remarks.</item>
    /// <item><c>FundamentalMinSearchHz</c> lowered below the sweep's start — it defaults to 200 Hz so
    /// mains hum is never mistaken for the fundamental, but a sweep starting at 20 Hz would then
    /// fail to locate its own tone for the whole bottom decade.</item>
    /// </list>
    ///
    /// <c>SingleShot</c> is on for the entire sweep: each point's arming flush is the one
    /// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/> already performs.
    ///
    /// <c>CapturePreview</c> is also always on here — see its own remarks on why the sweep is the
    /// one caller that needs it, and the live FFT screen never does.
    /// </summary>
    private static SpectrumPipelineOptions BuildSweepOptions(SpectrumPipelineOptions current, ThdSweepRequest request) =>
        current with
        {
            FftSize = request.FftSize,
            Window = request.Window,

            // A single average is "no averaging", not "Linear with count 1". SpectrumAverager treats
            // count <= 1 as Off and leaves its Count at 0, so asking for Linear here would leave
            // ProcessChannel's `averagesTaken >= AverageCount` false forever and every point would
            // time out waiting for a capture that can never complete.
            Averaging = request.AverageCount > 1 ? AveragingMode.Linear : AveragingMode.Off,
            AverageCount = request.AverageCount,
            ProcessLeft = request.ProcessLeft,
            ProcessRight = request.ProcessRight,
            SingleShot = true,
            CapturePreview = true,
            HarmonicMaxFreqHz = request.EffectiveSampleRateHz / 2.0,

            // Averaging paced by fresh signal rather than by the display tick. Without this the
            // sweep paid AverageCount/TargetFrameRateHz per point for averages that overlapped by
            // ~93 % at a large FFT size -- see SpectrumPipelineOptions.AverageHopFraction. Welch's
            // 50 % is the standard trade: eight averages cost 4.5 records instead of 8 and keep
            // about 90 % of the variance reduction of fully independent ones.
            AverageHopFraction = AverageHopFraction,
            FundamentalMinSearchHz = Math.Min(current.FundamentalMinSearchHz, request.SweepToneFreqHz / 2.0),
        };

    /// <summary>
    /// The generator amplitude to sweep with. Direct for <see cref="ThdSweepLevelMode.GeneratorVoltage"/>;
    /// for the output-referred modes it is solved by probing at 1 kHz, exactly as the previous app
    /// did: measure what comes back, correct the generator by the shortfall, repeat.
    /// </summary>
    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        ThdSweepRequest request, CancellationToken ct)
    {
        // Nothing was measured, so there is nothing to seed the range predictor with — the first
        // sweep point measures at whatever range is set and seeds it itself.
        if (request.LevelMode == ThdSweepLevelMode.GeneratorVoltage)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        SetPhase(ThdSweepPhase.DeterminingLevel);
        BroadcastProgress(0, 0, ProbeFrequencyHz, ThdSweepPhase.DeterminingLevel);

        double wantedOutDbv = request.LevelDbv;
        double maxGenDbv = session.Capabilities.OutputRangesDbv.Count > 0
            ? session.Capabilities.OutputRangesDbv.Max()
            : 18.0;

        // The target output level is known before the first probe, so the input range can be set for
        // it up front. Without this the probe fights its own clipping: at a range below the target,
        // the ADC compresses the tone, the measured fundamental comes back low, and the correction
        // pushes the generator *higher* — straight to full scale, driving a real DUT far harder than
        // asked. Only the output-referred modes can do this; with a fixed generator amplitude the
        // DUT's output is unknown until something has been measured, so the user's own range (or the
        // device's autoranging) stands.
        await SetInputRangeForAsync(wantedOutDbv, ct);

        // The probe is its own capture at its own frequency, so under adaptive resolution it gets
        // the size 1 kHz would be swept at -- and must be snapped against that same size, or the
        // probe tone leaks and the level it solves for is read off a smeared fundamental.
        int probeFftSize = request.FftSizeAt(ProbeFrequencyHz);
        var probeTone = CoherentFrequency.Nearest(ProbeFrequencyHz, request.EffectiveSampleRateHz, probeFftSize);
        double genDbv = ProbeStartDbv;

        // Tracked as a pair: the peak, and the generator level it was measured at. The loop can exit
        // having just applied an unmeasured correction, and a peak paired with the wrong generator
        // level is a constant offset on every prediction the sweep then makes.
        double peakDbv = double.NegativeInfinity;
        double peakAtGenDbv = genDbv;

        for (int i = 0; i < MaxProbeIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            var snapshots = await CaptureWithStimulusAsync(
                ToneConfig(probeTone.FrequencyHz, genDbv, request), request, probeFftSize, ct);

            peakDbv = MeasuredPeakDbv(snapshots);
            peakAtGenDbv = genDbv;

            double measuredDbv = MeasuredFundamentalDbv(snapshots);
            if (double.IsNegativeInfinity(measuredDbv))
            {
                throw new InvalidOperationException(
                    "No signal came back from the DUT while determining the drive level — check that it is connected and powered.");
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
                    $"Could not reach the requested output level — the generator would need to go below {ProbeStartDbv:0} dBV. Check that the DUT is connected and powered.");
            }

            genDbv = Math.Min(genDbv, maxGenDbv);
            SetGeneratorAmpDbv(genDbv);
        }

        return (genDbv, peakDbv, peakAtGenDbv);
    }

    /// <summary>
    /// Programs the smallest input range that still leaves <see cref="InputRangeHeadroomDb"/> above
    /// <paramref name="expectedLevelDbv"/>, out of whatever this device actually offers.
    ///
    /// Deliberately built from <see cref="DeviceCapabilities.InputRangesDbv"/> rather than porting
    /// the previous app's hard-coded 0/6/12/18/24/30/36/42 ladder — that list is a QA402-era
    /// constant, and the client already carries its own copy in <c>web/src/attenuation.ts</c>; a
    /// third one in C# would be the copy that goes stale. The 5 dB of headroom is from that same
    /// reference: enough that the tone's own peaks stay off the clip point, which matters far more
    /// for a distortion measurement than for a level one.
    ///
    /// Goes through <see cref="DeviceSessionService.SetInputRangeAsync"/> so a mid-stream change is
    /// applied on the transfer pump rather than written to EP1 with EP2 transfers in flight.
    /// </summary>
    private async Task SetInputRangeForAsync(double expectedLevelDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;
        if (ranges.Count == 0)
            return;

        double selected = SelectInputRange(ranges, expectedLevelDbv);
        if (Math.Abs(selected - session.InputRangeDbv) > 0.01)
            await session.SetInputRangeAsync(selected, ct);
    }

    /// <summary>
    /// Programs the range for a level this sweep has predicted rather than measured, and returns the
    /// ratchet floor to carry into the next point.
    ///
    /// <para>On the amplitude axis the selection is monotone — never a more sensitive range than one
    /// already used in this sweep, which is the previous WinForms app's
    /// <c>newAttenuation = Math.Max(predicted, newAttenuation)</c>
    /// (<c>frmThdAmplitude.cs</c>). An ascending amplitude ladder needs the range to move one way
    /// only, and pinning that removes the last source of hunting: without it a point that reads low
    /// (a compressing DUT, a mis-seeded predictor) drops the range, and the next point's step-up
    /// backstop puts it straight back, at the cost of a hole in the stream each way.</para>
    ///
    /// <para>Deliberately not applied on the frequency axis. Gain there is not monotone — a
    /// transformer resonance or an HF peak is a bump in the middle of the sweep — so a ratchet would
    /// hold every later point at the range one peak needed and give away sensitivity across the rest
    /// of the response. Following the previous point's measurement is what tracks that curve.</para>
    /// </summary>
    private async Task<double> ApplyPredictedInputRangeAsync(
        double predictedPeakDbv, SweepAxis axis, double ratchetFloorDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;
        if (ranges.Count == 0)
            return ratchetFloorDbv;

        double selected = SelectInputRange(ranges, predictedPeakDbv);

        if (axis == SweepAxis.Amplitude)
        {
            selected = Math.Max(selected, ratchetFloorDbv);
            ratchetFloorDbv = selected;
        }

        if (Math.Abs(selected - session.InputRangeDbv) > 0.01)
        {
            logger.LogDebug(
                "Predicted peak {PredictedDbv:0.##} dBV for the next point — input range {FromDbv:0.#} -> {ToDbv:0.#} dBV",
                predictedPeakDbv, session.InputRangeDbv, selected);

            await session.SetInputRangeAsync(selected, ct);
        }

        return ratchetFloorDbv;
    }

    /// <summary>
    /// The lowest range in <paramref name="ranges"/> that still leaves
    /// <see cref="InputRangeHeadroomDb"/> above <paramref name="expectedLevelDbv"/>, or the largest
    /// range offered when nothing fits. Same ladder and same headroom the device's own ranger uses,
    /// which is what lets the two disagree only about *when*, never about *where*.
    /// </summary>
    private static double SelectInputRange(IReadOnlyList<double> ranges, double expectedLevelDbv) =>
        InputRangeSelector.For(ranges, expectedLevelDbv);

    /// <summary>
    /// The generator-off residual, per channel: the <em>mean level of one bin</em> across the
    /// measurement band. Measured once at the start of a sweep — the previous app did the same, and
    /// the noise floor of an analyzer plus DUT does not depend on which tone is playing.
    ///
    /// Per bin, not band-integrated, and that choice is the whole point of this method. The two
    /// figures differ by √(bins in the band) — about 35 dB at 20 Hz–20 kHz — so the band-integrated
    /// version this used to return plotted the noise trace an order of magnitude *above* the THD
    /// traces it shares an axis with, and nowhere near the previous app's reading for the same
    /// hardware. That app took <c>fft.Skip(fundamentalBin + 1).Average()</c>
    /// (<c>frmThdFrequency.cs</c>), i.e. the arithmetic mean of the per-bin amplitudes, and this
    /// matches it so the two are comparable.
    ///
    /// Two consequences worth knowing, both inherent to a per-bin figure rather than bugs:
    /// it scales with bin width, so the same hardware reads ~3 dB lower at each doubling of FFT
    /// size; and it is not the noise term in THD+N, which does integrate the band. If you want
    /// "how many volts of noise are there", read THD+N, not this.
    ///
    /// <para><b>Always captured at <see cref="SweepResolution.BoundsFor"/>'s ceiling, never at the
    /// size a given point is swept at.</b> That bin-width dependence above is why. Under adaptive
    /// resolution the FFT shrinks as frequency rises, so measuring this per point makes every point
    /// internally consistent and the *trace* meaningless: measured on the simulator, the noise floor
    /// climbed 16.8 dB from 20 Hz to 20 kHz in 3.4 dB steps, one at each FFT-size halving, purely
    /// from the resolution change and with the real noise constant. On a DUT whose THD sits near the
    /// residual that crosses the noise trace above the distortion traces part-way up the sweep.
    /// Since the generator is off, the size is free to choose, and pinning it to the ceiling costs
    /// nothing: the ceiling is a record-*time* bound, so it yields the same 1.465 Hz bin at every
    /// supported sample rate, which makes an adaptive sweep's noise trace identical to a fixed one's
    /// with no scaling factor and no white-noise assumption.</para>
    /// </summary>
    private async Task<double[]> MeasureNoiseFloorAsync(ThdSweepRequest request, CancellationToken ct)
    {
        var noiseVrms = new double[2];
        if (!request.MeasureNoiseFloor)
            return noiseVrms;

        SetPhase(ThdSweepPhase.NoiseFloor);
        BroadcastProgress(0, 0, 0.0, ThdSweepPhase.NoiseFloor);

        var snapshots = await CaptureWithStimulusAsync(GeneratorConfig.Off, request, NoiseFloorFftSize(request), ct);
        foreach (var snapshot in snapshots)
        {
            int ch = snapshot.Channel == StreamChannel.Left ? 0 : 1;

            // The meters' own noise figure is SNR-derived and needs a fundamental to reference; with
            // the generator off there isn't one, so read the retained trace directly.
            noiseVrms[ch] = MeanBandBinVrms(snapshot, pipeline.Options.MeasurementLoFreqHz, pipeline.Options.MeasurementHiFreqHz);
        }

        return noiseVrms;
    }

    /// <summary>
    /// Applies a stimulus and returns the analysis of the first fully-averaged record after it.
    ///
    /// The ordering here is the whole point of the class. <see cref="DeviceSessionService.ConfigureGeneratorAsync"/>
    /// flushes the ring buffers *and* discards the audio still queued for the DAC, so with
    /// <c>SingleShot</c> already on, that one call is also the arming of this capture. Do NOT add a
    /// <c>pipeline.Flush(0)</c> after it: <c>SampleRingBuffer.Reset(0)</c> would clear the pending
    /// in-flight discard counter, the window would refill with pre-change audio, and the step would
    /// land back inside the very record the flush existed to protect.
    /// </summary>
    private async Task<SpectrumSnapshot[]> CaptureWithStimulusAsync(
        GeneratorConfig generator, ThdSweepRequest request, int fftSize, CancellationToken ct)
    {
        // Strictly before ConfigureGeneratorAsync, which is what flushes. Changing FftSize makes the
        // acquisition thread allocate replacement ring buffers, and doing that *after* the flush
        // would discard the flush's own state -- the buffers it emptied are not the buffers the next
        // record fills. (The stimulus-latency discard survives either order now, because
        // EnsureRingBuffers carries it across, but the emptying itself still has to come last.)
        if (pipeline.Options.FftSize != fftSize)
            pipeline.Options = pipeline.Options with { FftSize = fftSize };

        await session.ConfigureGeneratorAsync(generator, ct);
        await WaitForCaptureAsync(request, fftSize, ct);
        return pipeline.LatestSnapshots();
    }

    private async Task WaitForCaptureAsync(ThdSweepRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = CaptureTimeout(request, fftSize);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"A sweep point did not complete within {timeout.TotalSeconds:0.0} s. The device may have stopped streaming.");
            }

            await Task.Delay(CapturePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long one point may take: the time to refill the FFT window after the flush (including
    /// the discarded in-flight audio), plus one pipeline tick per average, times three for slack.
    /// Generous on purpose — this exists to notice a wedged device, not to police timing.
    /// </summary>
    private TimeSpan CaptureTimeout(ThdSweepRequest request, int fftSize)
    {
        // The stimulus latency the flush discards is ~65 ms at every sample rate (see
        // Qa40xUsbDevice.StimulusLatencyFrames); 100 ms is a deliberately loose stand-in rather than
        // widening DeviceSessionService's surface for a timeout estimate.
        const double StimulusLatencySeconds = 0.1;

        // The settle time the session discards on top of the latency at every stimulus change --
        // a user's number, so it has to be in the budget or a long settle reads as a wedged device.
        double settleSeconds = session.CaptureDelayMs / 1000.0;

        double recordSeconds = fftSize / request.EffectiveSampleRateHz;
        double fillSeconds = recordSeconds + StimulusLatencySeconds + settleSeconds;

        // Averages are now paced by fresh signal, not by the display tick, so the budget has to be
        // too: AverageCount averages at AverageHopFraction cost (1 + (n-1)*hop) records. Timing this
        // against the old tick-rate figure would abort a large-FFT point long before it could
        // legitimately finish.
        double averageSeconds = (1.0 + ((Math.Max(1, request.AverageCount) - 1) * AverageHopFraction)) * recordSeconds;
        return TimeSpan.FromSeconds(((fillSeconds + averageSeconds) * 3.0) + 2.0);
    }

    /// <summary>
    /// The FFT size the noise floor is captured at — the largest the sweep's sample rate allows, in
    /// both modes. See <see cref="MeasureNoiseFloorAsync"/> for why it is deliberately not the size
    /// any particular point is swept at.
    /// </summary>
    private static int NoiseFloorFftSize(ThdSweepRequest request) =>
        request.AdaptiveResolution
            ? SweepResolution.BoundsFor(request.EffectiveSampleRateHz).Ceiling
            : request.FftSize;

    private static GeneratorConfig ToneConfig(double freqHz, double ampDbv, ThdSweepRequest request)
    {
        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(freqHz, ampDbv)]) { Kind = GeneratorKind.Tones };
        return new GeneratorConfig(
            request.ProcessLeft ? tone : ChannelGeneratorConfig.Off,
            request.ProcessRight ? tone : ChannelGeneratorConfig.Off)
        {
            // The frequency is already coherent; snapping again is idempotent and keeps the device's
            // reported tone identical to the one this sweep recorded.
            SnapToBin = true,
        };
    }

    private static double MeasuredFundamentalDbv(SpectrumSnapshot[] snapshots)
    {
        double best = double.NegativeInfinity;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is { } meters)
                best = Math.Max(best, meters.FundamentalDbv);
        }

        return best;
    }

    /// <summary>
    /// The largest time-domain peak level any measured channel saw, in the same "Pk dBV"
    /// (peak volts / sqrt(2)) convention <c>Qa40xAutoRanger</c> compares against — one input range
    /// serves both channels, so the louder one decides. Negative infinity when nothing was measured.
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

    private static IEnumerable<ThdSweepPoint> BuildPoints(
        double freqHz, double generatorAmpDbv, ThdSweepRequest request, SpectrumSnapshot[] snapshots, double[] noiseVrms)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is not { } meters)
                continue;

            int channel = snapshot.Channel == StreamChannel.Left ? 0 : 1;
            var harmonics = meters.Harmonics;

            double fundamentalVrms = Math.Pow(10.0, meters.FundamentalDbv / 20.0);

            var harmonicPercent = new List<double>(4);
            for (int order = 2; order <= 5; order++)
            {
                var match = harmonics.FirstOrDefault(h => h.Order == order);
                if (match.Order != order)
                    break;

                harmonicPercent.Add(100.0 * Math.Pow(10.0, match.Dbc / 20.0));
            }

            double d6Plus = harmonics.Length > 0 && harmonics[0].Order == 1
                ? 100.0 * SpectrumMetrics.PartialThd(harmonics, 6)
                : 0.0;

            double noisePercent = fundamentalVrms > 0.0 ? 100.0 * noiseVrms[channel] / fundamentalVrms : 0.0;
            double powerWatt = request.LoadOhms > 0.0 ? fundamentalVrms * fundamentalVrms / request.LoadOhms : 0.0;

            yield return new ThdSweepPoint(
                freqHz,
                channel,
                meters.FundamentalFreqHz,
                meters.FundamentalDbv,
                meters.FundamentalDbv - generatorAmpDbv,
                powerWatt,
                meters.ThdPercent,
                meters.ThdNPercent,
                meters.SnrDb,
                harmonicPercent,
                d6Plus,
                noisePercent,
                generatorAmpDbv,
                snapshot.PreviewFft,
                snapshot.PreviewScope);
        }
    }

    /// <summary>
    /// Mean per-bin Vrms over [loHz, hiHz] of a retained dBV trace. The snapshot carries dBV per
    /// bin, so convert back to volts first.
    ///
    /// The mean of the amplitudes, matching the previous app, not the RMS of them: for
    /// Rayleigh-distributed noise bins those differ by 1 dB, which is not worth a difference from
    /// the reading this is meant to be comparable with.
    /// </summary>
    private static double MeanBandBinVrms(SpectrumSnapshot snapshot, double loHz, double hiHz)
    {
        int loBin = Math.Max(1, (int)Math.Round(loHz / snapshot.BinSizeHz));
        int hiBin = Math.Min(snapshot.Dbv.Length - 1, (int)Math.Round(hiHz / snapshot.BinSizeHz));
        if (hiBin < loBin)
            return 0.0;

        double sum = 0.0;
        for (int k = loBin; k <= hiBin; k++)
            sum += Math.Pow(10.0, snapshot.Dbv[k] / 20.0);

        return sum / (hiBin - loBin + 1);
    }

    private void AddPoint(ThdSweepPoint point)
    {
        lock (_stateLock)
            _points.Add(point);
    }

    private void SetPhase(ThdSweepPhase phase, string? error = null)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _error = error;
        }
    }

    private void SetProgress(int index, double currentFreqHz, double currentAmplitudeVolt)
    {
        lock (_stateLock)
        {
            _index = index;
            _currentFreqHz = currentFreqHz;
            _currentAmplitudeVolt = currentAmplitudeVolt;
        }
    }

    /// <summary>
    /// The x coordinate a progress frame carries: Hz on the frequency axis, generator volts on the
    /// amplitude one. The frame has one x slot (see <see cref="StreamFrame.EncodeSweepProgress"/>)
    /// and the client already knows which axis it started.
    /// </summary>
    private static double ProgressX(ThdSweepRequest request, double freqHz, double ampDbv) =>
        request.Axis == SweepAxis.Amplitude ? DbvToVolt(ampDbv) : freqHz;

    private static double DbvToVolt(double dbv) => Math.Pow(10.0, dbv / 20.0);

    private static double VoltToDbv(double volt) => 20.0 * Math.Log10(Math.Max(volt, double.Epsilon));

    private void SetGeneratorAmpDbv(double ampDbv)
    {
        lock (_stateLock)
            _generatorAmpDbv = ampDbv;
    }

    private void BroadcastProgress(int index, int total, double currentX, ThdSweepPhase phase) =>
        hub.Broadcast(StreamFrame.EncodeSweepProgress(index, total, currentX, phase));

    /// <summary>
    /// One planned sweep point: the tone to play, the level to play it at, and the FFT size to
    /// analyze it with. <see cref="Qa403.Dsp.SweepStep"/> is the frequency-axis half of this and
    /// stays in the DSP layer where the grid math lives; the level is a device concern, so the
    /// combined plan is the runner's own type.
    /// </summary>
    private readonly record struct SweepStepPlan(double FrequencyHz, int FftSize, double GeneratorDbv);
}
