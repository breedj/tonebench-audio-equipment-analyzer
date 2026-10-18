using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

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
    ILogger<ThdSweepRunner> logger) : IMeasurementRun
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
    /// How close two input ranges have to be to count as the same one. The ladder's rungs are 6 dB
    /// apart, so this is only guarding against a float comparison — but it has to be a real
    /// tolerance rather than equality, because a write that changes nothing still quiesces the pump.
    /// </summary>
    private const double RangeEpsilonDb = 0.01;

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
    private string? _stopReason;
    private List<ThdSweepPoint> _points = [];
    private ThdMethod _thdMethod = ThdMethod.Conventional;
    private ThdSweepRequest? _lastRequest;

    /// <summary>
    /// The sweeps of the measurements switched away from, so clicking one away and back does not
    /// throw a run away — see <see cref="SwitchOwner"/>. Four rather than all of them, the same
    /// bound and the same reasoning as the retained spectrum's: a sweep's points carry a decimated
    /// FFT and oscilloscope preview each, so a long run is megabytes and a project with thirty
    /// measurements would hold sweeps nobody has asked to see. The fifth measurement back shows an
    /// empty chart on return, which is what every one of them did before this existed.
    /// </summary>
    private readonly RunParking<ParkedSweep> _parking = new(capacity: 4);

    /// <summary>
    /// The frequency-normalized method's working state: every point's located harmonics, kept
    /// past <see cref="BuildPoints"/>, and each channel's fundamental response as measured so far.
    /// Only the sweep task touches these — they are not under <see cref="_stateLock"/>, since
    /// nothing else reads them and <see cref="_points"/> is what carries the result out.
    /// </summary>
    private readonly List<RawPoint> _rawPoints = [];
    private readonly FundamentalResponse[] _fundamentalResponse = [new(), new()];

    /// <summary>
    /// The request the most recent sweep ran with, or null if none has run. Saving a result needs
    /// it: the as-run parameters must be the ones the points were actually measured under, not
    /// whatever the config panel happens to show by the time someone presses Save.
    /// </summary>
    public ThdSweepRequest? LastRequest => _lastRequest;

    public bool Running => _phase is ThdSweepPhase.DeterminingLevel or ThdSweepPhase.DeterminingRange
        or ThdSweepPhase.NoiseFloor or ThdSweepPhase.Sweeping;

    public ThdSweepStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new ThdSweepStatus(
                    Running, _phase, _index, _total, _currentFreqHz, _generatorAmpDbv, _error, [.. _points],
                    _axis, _currentAmplitudeVolt, _thdMethod, _stopReason);
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
                _stopReason = null;
                _points = [];
                _thdMethod = request.ThdMethod;
            }

            _rawPoints.Clear();
            foreach (var response in _fundamentalResponse)
                response.Clear();

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
            _stopReason = null;
            _thdMethod = ThdMethod.Conventional;

            return Status;
        }
    }

    /// <summary>
    /// Points the runner at the measurement now on screen: the sweep it holds is parked under the
    /// measurement it belongs to, and whatever that one had parked becomes the live sweep. Returns
    /// the status the caller should now show.
    ///
    /// <para>See <see cref="RunParking{TState}"/> for why the server has to know this at all —
    /// in short, <b>Save sweep result</b> reads <see cref="Status"/>, so the points on screen and
    /// the points that would be saved have to be the same measurement's. Called when a sweep
    /// screen is <i>arrived at</i>, not on every switch: nothing but a sweep writes this runner, so
    /// leaving the owner alone while the user is on an unrelated measurement is what keeps a
    /// reload mid-sweep re-attaching exactly as it did before this existed.</para>
    ///
    /// <para>Ignored while a sweep is running, rather than throwing: a measurement switch is
    /// refused client-side while anything runs, and a park that raced the writer appending to
    /// <see cref="_points"/> would be worse than a no-op.</para>
    /// </summary>
    public ThdSweepStatus SwitchOwner(string? owner)
    {
        lock (_stateLock)
        {
            if (Running)
                return Status;

            // Null means "nothing worth parking" -- an empty runner, or one just cleared. See
            // RunParking.Switch: that drops the outgoing owner's entry rather than leaving a stale
            // one, so a Clear cannot be undone by switching away and back.
            var current = _points.Count == 0
                ? null
                : new ParkedSweep(
                    _phase, _axis, _index, _total, _currentFreqHz, _currentAmplitudeVolt,
                    _generatorAmpDbv, _error, _stopReason, _points, _thdMethod, _lastRequest);

            var outcome = _parking.Switch(owner, current);
            if (!outcome.Changed)
                return Status;

            if (outcome.Adopted is { } parked)
            {
                _phase = parked.Phase;
                _axis = parked.Axis;
                _index = parked.Index;
                _total = parked.Total;
                _currentFreqHz = parked.CurrentFreqHz;
                _currentAmplitudeVolt = parked.CurrentAmplitudeVolt;
                _generatorAmpDbv = parked.GeneratorAmpDbv;
                _error = parked.Error;
                _stopReason = parked.StopReason;
                _points = parked.Points;
                _thdMethod = parked.ThdMethod;
                _lastRequest = parked.LastRequest;
            }
            else if (outcome.Cleared)
            {
                ResetLiveState();
            }

            return Status;
        }
    }

    /// <summary>The measurement the sweep on hand belongs to, or null — see <see cref="SwitchOwner"/>.</summary>
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
        // A fresh list, never the parked one emptied: the park holds this very reference,
        // and AddPoint mutates in place.
        _points = [];
        _phase = ThdSweepPhase.Idle;
        _index = 0;
        _total = 0;
        _currentFreqHz = 0;
        _currentAmplitudeVolt = 0;
        _generatorAmpDbv = 0;
        _error = null;
        _stopReason = null;
        _thdMethod = ThdMethod.Conventional;
        _lastRequest = null;
    }

    /// <summary>Everything a measurement switch has to carry — <see cref="Status"/>'s own fields plus the as-run request.</summary>
    private sealed record ParkedSweep(
        ThdSweepPhase Phase, SweepAxis Axis, int Index, int Total, double CurrentFreqHz,
        double CurrentAmplitudeVolt, double GeneratorAmpDbv, string? Error, string? StopReason,
        List<ThdSweepPoint> Points, ThdMethod ThdMethod, ThdSweepRequest? LastRequest);

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
            // Manual range control means manual: with the Auto checkbox off the user has picked a
            // range deliberately, and a sweep quietly reprogramming it would be the same class of
            // surprise autoranging used to be before that checkbox existed.
            bool predictionOwnsRange = session.RunnerOwnsInputRange;

            // Never steps back down within one sweep on the amplitude axis -- see
            // ApplyPredictedInputRangeAsync.
            double rangeRatchetDbv = double.NegativeInfinity;

            // Suppressing step-down is what makes a programmed range stay programmed; it is deferred
            // until there is something to predict *from*, so a sweep with no probe still gets a
            // freely-ranging first point to seed itself off instead of being pinned to whatever
            // range happened to be left over from the last measurement. An output-referred run has
            // a probe, so there it is taken over up front instead -- see the call below.
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
                // Step-down is handed over before the first probe rather than after, because both
                // probes program a range of their own and the ranger would walk straight back off
                // it. Measured on a QA403 in loopback, 0 dBV target: with step-down live the level
                // probe's climb from -40 dBV dragged the range 0 -> 6 -> 12 -> 18 while the
                // prediction said 6, and the range probe's own 42 dBV would be walked down a rung
                // per second -- which is inside the probe's record at any FFT size past 65536
                // points, so its capture would be flushed and retaken for no gain in the answer.
                (generatorAmpDbv, predictorPeakDbv, predictorGenDbv) =
                    await ResolveGeneratorAmplitudeAsync(request, TakeOverRangingFromStepDown, ct);
            }

            SetGeneratorAmpDbv(generatorAmpDbv);

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

                ThdSweepPoint? overThreshold = null;
                foreach (var point in BuildPoints(step.FrequencyHz, stepAmpDbv, request, snapshots, noiseVrms))
                {
                    AddPoint(point);
                    hub.Broadcast(StreamFrame.EncodeSweepPoint(point, (uint)i));
                    if (request.StopThdPercent is { } stopThd && double.IsFinite(point.ThdPercent) && point.ThdPercent > stopThd
                        && (overThreshold is null || point.ThdPercent > overThreshold.ThdPercent))
                    {
                        overThreshold = point;
                    }
                }

                if (request.ThdMethod == ThdMethod.FrequencyNormalized)
                    NormalizeNewlyReferencedPoints(step.FrequencyHz, snapshots, i);

                // The point that crossed the threshold stays -- it is the one that says where the
                // DUT gave up -- and the sweep ends here as Finished, not as a phase of its own.
                if (overThreshold is { } stopPoint)
                {
                    string channelName = stopPoint.Channel == 0 ? "L" : "R";
                    lock (_stateLock)
                    {
                        _stopReason =
                            $"Stopped at {DbvToVolt(stepAmpDbv):0.###} Vrms: THD {stopPoint.ThdPercent:0.###} % on {channelName} exceeded {request.StopThdPercent:0.###} %.";
                    }
                    break;
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
        double maxHz = request.MaxSweepFreqHz;
        double levelDbv = request.LevelDbv;

        // The requested grid, then -- for the frequency-normalized method -- the same per-octave
        // step continued past the end, so that the fundamental exists at 2f..5f of every requested
        // point. Continued, not re-spaced: LogPerOctave over the widened span would move every
        // requested point (see SweepPoints.ExtendPerOctave). The extension points are measured
        // like any other and reported like any other; only their own normalized figures stay null,
        // since nothing above them was measured.
        double[] requested = SweepPoints.LogPerOctave(request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave);
        double[] desired = request.ReferenceEndFreqHz > request.EndFreqHz
            ? [.. requested, .. SweepPoints.ExtendPerOctave(request.EndFreqHz, request.ReferenceEndFreqHz, request.StepsPerOctave)]
            : requested;

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
            // The band this sweep's own noise figures are read over, rather than whatever the
            // live FFT screen left behind -- see ThdSweepRequest.MeasurementLoFreqHz.
            MeasurementLoFreqHz = request.MeasurementLoFreqHz,
            MeasurementHiFreqHz = request.MeasurementHiFreqHz,
            // A runner measures unweighted whatever the FFT screen has selected: a sweep's noise
            // and THD+N figures are defined over the plain band, and the weighting is restored
            // with the rest of the saved options afterwards.
            Weighting = WeightingSpec.None,
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
    /// The generator amplitude to sweep with, and the peak/level pair that seeds the per-point range
    /// predictor. Direct for <see cref="ThdSweepLevelMode.GeneratorVoltage"/>; for the
    /// output-referred modes the level is solved by probing at 1 kHz, exactly as the previous app
    /// did — measure what comes back, correct the generator by the shortfall, repeat.
    ///
    /// <para>The two halves sit in this order for the reason
    /// <see cref="FrequencyResponseRunner"/>'s do, and the ordering is load-bearing rather than
    /// tidy. An output-referred target <i>is</i> the level the input stage must accommodate, so the
    /// range can be predicted from it and applied before the first tone is voiced; without that the
    /// level probe fights its own clipping, reading a compressed tone as missing gain. But the
    /// prediction is only where the probe <em>starts</em>: the range the sweep runs on is
    /// <see cref="ProbeInputRangeAsync"/>'s, measured from the least sensitive range, because a
    /// prediction assumes the DUT's peak sits at its 1 kHz level and that is true of a flat DUT and
    /// of nothing else. A probe that measures wider than the prediction is saying the level probe's
    /// own captures were clipped, so the level is settled again on the range the DUT actually needs
    /// and ranged once more — a flat DUT never enters that branch and pays nothing for it.</para>
    /// </summary>
    /// <param name="takeOverRanging">
    /// Hands step-down over to this runner — called before the first range is programmed, since
    /// both probes program one and a live step-down walks the range off it again.
    /// </param>
    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ResolveGeneratorAmplitudeAsync(
        ThdSweepRequest request, Action takeOverRanging, CancellationToken ct)
    {
        // Nothing was measured, so there is nothing to seed the range predictor with — the first
        // sweep point measures at whatever range is set and seeds it itself.
        if (request.LevelMode == ThdSweepLevelMode.GeneratorVoltage)
            return (request.LevelDbv, double.NegativeInfinity, request.LevelDbv);

        // The probe is its own capture at its own frequency, so under adaptive resolution it gets
        // the size 1 kHz would be swept at -- and must be snapped against that same size, or the
        // probe tone leaks and the level it solves for is read off a smeared fundamental.
        int probeFftSize = request.FftSizeAt(ProbeFrequencyHz);
        double probeToneHz = CoherentFrequency.Nearest(ProbeFrequencyHz, request.EffectiveSampleRateHz, probeFftSize).FrequencyHz;

        // Manual range control means manual: with the Auto checkbox off the user has picked a range
        // deliberately, and neither the prediction nor the probe may reprogram it -- the same gate
        // the per-point prediction in RunAsync is behind, and the same one the frequency-response
        // runner puts its own probe behind.
        bool ownsInputRange = session.RunnerOwnsInputRange;

        // The target is the DUT's level; the range ladder is the analyzer's, so an external input
        // gain is put back first. No single measured channel here, so the wider of the two decides.
        double predictedRangeDbv = InputRangeSelector.For(
            session.Capabilities.InputRangesDbv, session.ToAnalyzerDbv(request.LevelDbv));

        if (ownsInputRange)
        {
            takeOverRanging();
            await ApplyInputRangeAsync(predictedRangeDbv, ct);
        }

        var settled = await SettleGeneratorLevelAsync(request, probeToneHz, probeFftSize, ProbeStartDbv, ct);

        if (!ownsInputRange)
            return settled;

        var probe = await ProbeInputRangeAsync(request, probeToneHz, probeFftSize, settled.GenDbv, ct);

        // Nothing readable at the least sensitive range, so there is no measured peak to range from.
        // The prediction is then the honest answer — it is the range the level the probe just
        // settled on was measured at — and InputRangeSelector would otherwise read an infinitely
        // quiet peak as "the most sensitive rung will do".
        if (double.IsNegativeInfinity(probe.PeakDbv))
        {
            logger.LogWarning(
                "The input-range probe measured no signal at the {WidestDbv:0.#} dBV range; keeping the {Predicted:0.#} dBV "
                    + "range predicted from the target level",
                session.Capabilities.InputRangesDbv.Max(), predictedRangeDbv);

            await ApplyInputRangeAsync(predictedRangeDbv, ct);
            return settled;
        }

        if (probe.RangeDbv > predictedRangeDbv + RangeEpsilonDb)
        {
            logger.LogInformation(
                "The DUT's peak needs a {Measured:0.#} dBV input range where its target level predicted {Predicted:0.#} dBV; "
                    + "settling the generator level again on the wider range",
                probe.RangeDbv, predictedRangeDbv);

            await ApplyInputRangeAsync(probe.RangeDbv, ct);
            settled = await SettleGeneratorLevelAsync(request, probeToneHz, probeFftSize, settled.GenDbv, ct);
            probe = await ProbeInputRangeAsync(request, probeToneHz, probeFftSize, settled.GenDbv, ct);
        }

        await ApplyInputRangeAsync(probe.RangeDbv, ct);

        // The seed is the *probe's* peak, not the level probe's last one: it was measured on the
        // least sensitive range, so it is a property of the signal rather than of whatever range
        // happened to be in force, and the generator level it was measured at is known exactly
        // rather than being the level a loop that ran out of iterations returned.
        return (settled.GenDbv, probe.PeakDbv, settled.GenDbv);
    }

    /// <summary>
    /// Corrects the generator level toward the output-referred target, from
    /// <paramref name="startDbv"/>, and returns the level it settled on together with the peak that
    /// level was measured at.
    /// </summary>
    private async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> SettleGeneratorLevelAsync(
        ThdSweepRequest request, double probeToneHz, int probeFftSize, double startDbv, CancellationToken ct)
    {
        SetPhase(ThdSweepPhase.DeterminingLevel);
        BroadcastProgress(0, 0, ProbeFrequencyHz, ThdSweepPhase.DeterminingLevel);

        double wantedOutDbv = request.LevelDbv;
        double maxGenDbv = session.Capabilities.OutputRangesDbv.Count > 0
            ? session.Capabilities.OutputRangesDbv.Max()
            : 18.0;

        double genDbv = startDbv;

        // Tracked as a pair: the peak, and the generator level it was measured at. The loop can exit
        // having just applied an unmeasured correction, and a peak paired with the wrong generator
        // level is a constant offset on every prediction the sweep then makes.
        double peakDbv = double.NegativeInfinity;
        double peakAtGenDbv = genDbv;

        for (int i = 0; i < MaxProbeIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            var snapshots = await CaptureWithStimulusAsync(
                ToneConfig(probeToneHz, genDbv, request), request, probeFftSize, ct);

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
                    $"Could not hold the requested output level — this DUT needs less than {ProbeStartDbv:0} dBV of drive to reach it, "
                        + "which is below the generator level this probe will go to. Lower the target level, or insert attenuation ahead of the DUT.");
            }

            genDbv = Math.Min(genDbv, maxGenDbv);
            SetGeneratorAmpDbv(genDbv);
        }

        return (genDbv, peakDbv, peakAtGenDbv);
    }

    /// <summary>
    /// Measures the input range this DUT's output needs, at a generator level already settled on,
    /// and returns it with the peak it was measured from.
    ///
    /// <para><b>Least sensitive range first</b>, which is the whole reason this exists rather than
    /// trusting the prediction: a probe that clips reads a peak that is a property of the range
    /// rather than of the signal, and would then choose that same range again and stay wrong — one
    /// rung per pass, which is how the frequency-response runner's RIAA case took four passes to
    /// draw a curve. Starting at the top cannot clip, so one capture lands on the answer.</para>
    ///
    /// <para>Reuses the level probe's own record length and snapped tone rather than taking a short
    /// record of its own: a peak needs no resolution, but this way the capture is the same one more
    /// iteration of the probe loop would have cost and the ring buffers are not resized twice for
    /// it.</para>
    /// </summary>
    private async Task<(double RangeDbv, double PeakDbv)> ProbeInputRangeAsync(
        ThdSweepRequest request, double probeToneHz, int probeFftSize, double generatorAmpDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;

        SetPhase(ThdSweepPhase.DeterminingRange);
        BroadcastProgress(0, 0, ProbeFrequencyHz, ThdSweepPhase.DeterminingRange);

        await ApplyInputRangeAsync(ranges.Max(), ct);

        var snapshots = await CaptureWithStimulusAsync(
            ToneConfig(probeToneHz, generatorAmpDbv, request), request, probeFftSize, ct);

        double peakDbv = MeasuredPeakDbv(snapshots);
        double selected = double.IsNegativeInfinity(peakDbv) ? ranges.Max() : SelectInputRange(ranges, peakDbv);

        logger.LogInformation(
            "Input-range probe measured a peak of {PeakDbv:0.##} dBV at {GenDbv:0.##} dBV generator; input range {RangeDbv:0.#} dBV",
            peakDbv, generatorAmpDbv, selected);

        return (selected, peakDbv);
    }

    /// <summary>
    /// Programs <paramref name="rangeDbv"/>, which is one of the rungs
    /// <see cref="DeviceCapabilities.InputRangesDbv"/> offers — see <see cref="SelectInputRange"/>
    /// for how one is chosen, and why the ladder is read off the device rather than ported from the
    /// previous app's hard-coded 0/6/12/18/24/30/36/42.
    ///
    /// Goes through <see cref="DeviceSessionService.SetInputRangeAsync"/> so a mid-stream change is
    /// applied on the transfer pump rather than written to EP1 with EP2 transfers in flight. A
    /// no-op when the range is already there, because a write that changes nothing still quiesces
    /// the pump and punches a ~110 ms hole in the stream.
    /// </summary>
    private async Task ApplyInputRangeAsync(double rangeDbv, CancellationToken ct)
    {
        if (Math.Abs(rangeDbv - session.InputRangeDbv) <= RangeEpsilonDb)
            return;

        await session.SetInputRangeAsync(rangeDbv, ct);
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

        if (Math.Abs(selected - session.InputRangeDbv) > RangeEpsilonDb)
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

    private Task WaitForCaptureAsync(ThdSweepRequest request, int fftSize, CancellationToken ct) =>
        SweepCaptureSupport.WaitForSingleShotAsync(pipeline, CaptureTimeout(request, fftSize), ct);

    /// <summary>How long one point may take — see <see cref="SweepCaptureSupport.CaptureTimeout"/>; the settle time is the session's.</summary>
    private TimeSpan CaptureTimeout(ThdSweepRequest request, int fftSize) =>
        SweepCaptureSupport.CaptureTimeout(fftSize, request.EffectiveSampleRateHz, request.AverageCount, AverageHopFraction, session.CaptureDelayMs, session.StreamSettleMs);

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

    private static double MeasuredFundamentalDbv(SpectrumSnapshot[] snapshots) => SweepCaptureSupport.MeasuredFundamentalDbv(snapshots);

    private static double MeasuredPeakDbv(SpectrumSnapshot[] snapshots) => SweepCaptureSupport.MeasuredPeakDbv(snapshots);

    private static IEnumerable<ThdSweepPoint> BuildPoints(
        double freqHz, double generatorAmpDbv, ThdSweepRequest request, SpectrumSnapshot[] snapshots, double[] noiseVrms)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is not { } meters)
                continue;

            int channel = snapshot.Channel == StreamChannel.Left ? 0 : 1;

            yield return SweepCaptureSupport.PointFromMeters(
                freqHz,
                channel,
                meters,
                generatorAmpDbv,
                request.LoadOhms,
                noiseVrms[channel],
                snapshot.PreviewFft,
                snapshot.PreviewScope);
        }
    }

    private static double MeanBandBinVrms(SpectrumSnapshot snapshot, double loHz, double hiHz) =>
        SweepCaptureSupport.MeanBandBinVrms(snapshot, loHz, hiHz);

    private void AddPoint(ThdSweepPoint point)
    {
        lock (_stateLock)
            _points.Add(point);
    }

    /// <summary>
    /// The frequency-normalized method's second half, run after every point. Records the point's
    /// own harmonics and fundamental, then fills in the normalized figures of every point that
    /// <em>this</em> point completed the references for.
    ///
    /// <para>The normalization of a point at f needs the fundamental at 2f..Nf, which the sweep only
    /// reaches later, so the figures cannot be computed when the point is measured. They are
    /// filled in progressively instead: after the point at F is added, every earlier point whose
    /// n·f now lies inside the measured response gets its order-n figure — and never revised again,
    /// because interpolation reads the two neighbours of n·f, which are already there once the
    /// grid has passed it. So each order of each point is broadcast at most once more, with the
    /// point's <em>original</em> index, and the client replaces it in place (useSweep.ingestPoint
    /// keys on channel, frequency and level). A point is left alone until something about it
    /// changed, which bounds the re-broadcasts to a handful per new point rather than the whole
    /// list.</para>
    /// </summary>
    private void NormalizeNewlyReferencedPoints(double freqHz, SpectrumSnapshot[] snapshots, int pointIndex)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is not { } meters)
                continue;

            int channel = snapshot.Channel == StreamChannel.Left ? 0 : 1;
            _rawPoints.Add(new RawPoint(pointIndex, channel, freqHz, meters.Harmonics));
            _fundamentalResponse[channel].Add(freqHz, Math.Pow(10.0, meters.FundamentalDbv / 20.0));
        }

        foreach (var raw in _rawPoints)
        {
            var normalized = FrequencyNormalizedDistortion.Compute(raw.FreqHz, raw.Harmonics, _fundamentalResponse[raw.Channel]);

            ThdSweepPoint updated;
            lock (_stateLock)
            {
                int slot = _points.FindIndex(p => p.Channel == raw.Channel && p.FreqHz == raw.FreqHz);
                if (slot < 0)
                    continue;

                var current = _points[slot];
                var candidate = current with
                {
                    NormalizedThdPercent = Percent(normalized.Thd),
                    NormalizedHarmonicPercent = [.. normalized.HarmonicRatio.Select(Percent)],
                    NormalizedD6PlusPercent = Percent(normalized.D6Plus),
                };

                if (SameNormalizedFigures(current, candidate))
                    continue;

                _points[slot] = candidate;
                updated = candidate;
            }

            hub.Broadcast(StreamFrame.EncodeSweepPoint(updated, (uint)raw.Index));
        }
    }

    private static double? Percent(double? ratio) => ratio is { } r ? 100.0 * r : null;

    private static bool SameNormalizedFigures(ThdSweepPoint a, ThdSweepPoint b)
    {
        if (a.NormalizedThdPercent != b.NormalizedThdPercent || a.NormalizedD6PlusPercent != b.NormalizedD6PlusPercent)
            return false;

        var ha = a.NormalizedHarmonicPercent;
        var hb = b.NormalizedHarmonicPercent;
        if (ha is null || hb is null)
            return ha is null && hb is null;
        if (ha.Count != hb.Count)
            return false;

        for (int i = 0; i < ha.Count; i++)
        {
            if (ha[i] != hb[i])
                return false;
        }

        return true;
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

    private static double DbvToVolt(double dbv) => SweepCaptureSupport.DbvToVolt(dbv);

    private static double VoltToDbv(double volt) => SweepCaptureSupport.VoltToDbv(volt);

    private void SetGeneratorAmpDbv(double ampDbv)
    {
        lock (_stateLock)
            _generatorAmpDbv = ampDbv;
    }

    private void BroadcastProgress(int index, int total, double currentX, ThdSweepPhase phase) =>
        hub.Broadcast(StreamFrame.EncodeSweepProgress(index, total, currentX, phase));

    /// <summary>
    /// One planned sweep point: the tone to play, the level to play it at, and the FFT size to
    /// analyze it with. <see cref="Tonebench.Dsp.SweepStep"/> is the frequency-axis half of this and
    /// stays in the DSP layer where the grid math lives; the level is a device concern, so the
    /// combined plan is the runner's own type.
    /// </summary>
    private readonly record struct SweepStepPlan(double FrequencyHz, int FftSize, double GeneratorDbv);

    /// <summary>
    /// One measured point's located harmonics, kept for the frequency-normalized method — the
    /// <see cref="ThdSweepPoint"/> only carries them relative to the fundamental at f, and the
    /// normalization needs their absolute amplitudes against a fundamental measured later.
    /// </summary>
    private readonly record struct RawPoint(int Index, int Channel, double FreqHz, Harmonic[] Harmonics);
}
