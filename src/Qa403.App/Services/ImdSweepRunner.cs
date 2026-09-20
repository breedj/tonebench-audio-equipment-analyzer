using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Drives an IMD-vs-amplitude sweep: a two-tone pair (SMPTE, DIN, CCIF or a typed one) played at
/// each of a log-spaced ladder of generator levels, with the intermodulation recorded per step.
///
/// <para>Its own runner rather than a third axis on <see cref="ThdSweepRunner"/>: every consumer
/// of that runner's request, point and status records is pinned (the wire-mapping test, the frame
/// layout tests, the CSV columns, the client store keyed on its two axes), and an IMD point is not
/// a THD point with different numbers — it has two tones, no fundamental, no THD+N. The capture
/// mechanics the two share sit in <see cref="SweepCaptureSupport"/>; the control flow here is the
/// THD amplitude sweep's, step for step, and deliberately reads like it.</para>
///
/// <para>The measurement itself is the pipeline's: <see cref="SpectrumPipelineOptions.Intermodulation"/>
/// is set for the run, and each point reads <see cref="SpectrumMeters.Intermodulation"/> off the
/// single-shot snapshot — the same object the FFT screen's IMD tiles show, so a sweep point and a
/// spot reading at the same level are the same computation.</para>
/// </summary>
public sealed class ImdSweepRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<ImdSweepRunner> logger) : IMeasurementRun
{
    /// <summary>Welch's 50 % — see <see cref="SpectrumPipelineOptions.AverageHopFraction"/> and the THD sweep's own remarks.</summary>
    private const double AverageHopFraction = 0.5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _sweepTask;

    private ThdSweepPhase _phase = ThdSweepPhase.Idle;
    private int _index;
    private int _total;
    private double _currentLevelVolt;
    private string? _error;
    private List<ImdSweepPoint> _points = [];
    private IntermodulationMethod _method = IntermodulationMethod.Smpte;
    private int _maxOrder = IntermodulationPresets.SmpteDefaultMaxOrder;
    private ImdSweepRequest? _lastRequest;

    /// <summary>The request the most recent sweep ran with — a save's as-run parameters, not whatever the panel shows by then.</summary>
    public ImdSweepRequest? LastRequest => _lastRequest;

    public bool Running => _phase is ThdSweepPhase.DeterminingLevel or ThdSweepPhase.NoiseFloor or ThdSweepPhase.Sweeping;

    public ImdSweepStatus Status
    {
        get
        {
            lock (_stateLock)
                return new ImdSweepStatus(Running, _phase, _index, _total, _currentLevelVolt, _error, [.. _points], _method, _maxOrder);
        }
    }

    /// <exception cref="InvalidOperationException">A sweep is already running, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not something that can be swept.</exception>
    public async Task<ImdSweepStarted> StartAsync(ImdSweepRequest request, CancellationToken ct)
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
                _index = 0;
                _total = steps.Length;
                _currentLevelVolt = 0.0;
                _error = null;
                _points = [];
                _method = request.Method;
                _maxOrder = request.MaxOrder;
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _sweepTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new ImdSweepStarted(steps.Length, [.. steps.Select(s => s.LevelVolt)]);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks a running sweep to stop and waits for it to unwind; the points already measured are kept.</summary>
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

    /// <summary>Discards the sweep on screen and returns the emptied status — server-side, since the runner is the source of truth (see <see cref="ThdSweepRunner.Clear"/>).</summary>
    /// <exception cref="InvalidOperationException">A sweep is running.</exception>
    public ImdSweepStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the sweep to finish (or cancel it) before clearing.");

            _points = [];
            _phase = ThdSweepPhase.Idle;
            _index = 0;
            _total = 0;
            _currentLevelVolt = 0.0;
            _error = null;

            return Status;
        }
    }

    private async Task RunAsync(ImdSweepRequest request, Step[] steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;

        var terminalPhase = ThdSweepPhase.Failed;
        string? terminalError = null;

        try
        {
            logger.LogInformation(
                "IMD-vs-amplitude sweep starting: {Preset} {Method} {LowHz:0.##} + {HighHz:0.##} Hz ({Ratio:0.##}:1), orders to {MaxOrder}, "
                    + "{StartVolt:0.####}-{EndVolt:0.####} V sine-equivalent, {StepsPerOctave}/octave, {Points} points, "
                    + "{SampleRateHz:0} Hz, {FftSize} points, {Averages} averages, load {LoadOhms:0.##} Ohm",
                request.Preset, request.Method, steps[0].LowHz, steps[0].HighHz, request.LowToHighRatio, request.MaxOrder,
                request.StartLevelVolt, request.EndLevelVolt, request.StepsPerOctave, steps.Length,
                request.SampleRateHz, request.FftSize, request.AverageCount, request.LoadOhms);

            pipeline.Options = BuildOptions(savedOptions, request, steps[0]);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.SampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // No level probe: the level is the swept variable. The first point seeds the range
            // predictor itself, exactly as the THD amplitude sweep does.
            double predictorPeakDbv = double.NegativeInfinity;
            double predictorGenDbv = steps[0].SineEquivalentDbv;

            bool predictionOwnsRange = session.AutoRangeEnabled != false;

            // Monotone on an ascending ladder — never a more sensitive range than one already used
            // (see ThdSweepRunner.ApplyPredictedInputRangeAsync's remarks).
            double rangeRatchetDbv = double.NegativeInfinity;

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

            var noiseVrms = await MeasureNoiseFloorAsync(request, ct);

            SetPhase(ThdSweepPhase.Sweeping);

            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var step = steps[i];
                SetProgress(i, step.LevelVolt);
                BroadcastProgress(i, steps.Length, step.LevelVolt, ThdSweepPhase.Sweeping);

                // Feed-forward ranging from the previous point's measured peak — see the THD runner.
                // The pair's peak moves with the sine-equivalent level exactly as a single tone's
                // would, which is the whole point of stepping in that unit.
                if (predictionOwnsRange && !double.IsNegativeInfinity(predictorPeakDbv))
                {
                    var ranges = session.Capabilities.InputRangesDbv;
                    if (ranges.Count > 0)
                    {
                        double selected = Math.Max(
                            InputRangeSelector.For(ranges, predictorPeakDbv + (step.SineEquivalentDbv - predictorGenDbv)),
                            rangeRatchetDbv);
                        rangeRatchetDbv = selected;
                        if (Math.Abs(selected - session.InputRangeDbv) > 0.01)
                            await session.SetInputRangeAsync(selected, ct);
                    }
                }

                long dropsBefore = session.DroppedRxChunks;
                double inputRangeBefore = session.InputRangeDbv;

                var snapshots = await CaptureWithStimulusAsync(TwoToneConfig(step, request), request, ct);

                long dropsDuring = session.DroppedRxChunks - dropsBefore;
                if (dropsDuring > 0)
                {
                    logger.LogWarning(
                        "IMD sweep point {Index}/{Total} at {LevelVolt:0.####} V captured across {Drops} dropped RX chunks "
                            + "— its record is spliced and its IMD is not trustworthy",
                        i + 1, steps.Length, step.LevelVolt, dropsDuring);
                }

                double pointPeakDbv = SweepCaptureSupport.MeasuredPeakDbv(snapshots);
                if (!double.IsNegativeInfinity(pointPeakDbv))
                {
                    predictorPeakDbv = pointPeakDbv;
                    predictorGenDbv = step.SineEquivalentDbv;
                    TakeOverRangingFromStepDown();
                }

                double inputRangeAfter = session.InputRangeDbv;
                if (Math.Abs(inputRangeAfter - inputRangeBefore) > 0.01)
                {
                    logger.LogInformation(
                        "IMD sweep point {Index}/{Total} at {LevelVolt:0.####} V: autoranging stepped the input range "
                            + "{FromDbv:0.#} -> {ToDbv:0.#} dBV during the capture, so the point was re-measured at the new range",
                        i + 1, steps.Length, step.LevelVolt, inputRangeBefore, inputRangeAfter);
                    rangeRatchetDbv = Math.Max(rangeRatchetDbv, inputRangeAfter);
                }

                foreach (var point in BuildPoints(step, request, snapshots, noiseVrms))
                {
                    AddPoint(point);
                    hub.Broadcast(StreamFrame.EncodeImdSweepPoint(point, (uint)i));
                }
            }

            SetProgress(steps.Length, 0.0);
            terminalPhase = ThdSweepPhase.Finished;
        }
        catch (OperationCanceledException)
        {
            terminalPhase = ThdSweepPhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IMD sweep failed");
            terminalPhase = ThdSweepPhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            // The THD runner's teardown, in its order: silence first (CancellationToken.None — this
            // must run when cancelled), restore the ranger, stop what this run started, hand the
            // pipeline options back, and only then announce the terminal phase.
            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the IMD sweep");
            }

            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the IMD sweep");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the IMD sweep started");
                }
            }

            pipeline.Options = savedOptions;

            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_index, _total, 0.0, terminalPhase);
        }
    }

    /// <summary>
    /// The ladder: both tones snapped once to coherent bins, played at each of a log-spaced set of
    /// sine-equivalent levels. No snap-and-dedupe pass — the frequencies never move, so coherence
    /// is settled before the sweep starts.
    /// </summary>
    private static Step[] BuildSteps(ImdSweepRequest request)
    {
        double lowHz = CoherentFrequency.Nearest(request.ToneLowHz, request.SampleRateHz, request.FftSize).FrequencyHz;
        double highHz = CoherentFrequency.Nearest(request.ToneHighHz, request.SampleRateHz, request.FftSize).FrequencyHz;
        var volts = SweepPoints.LogPerOctave(request.StartLevelVolt, request.EndLevelVolt, request.StepsPerOctave);

        return [.. volts.Select(v =>
        {
            double sineEqDbv = SweepCaptureSupport.VoltToDbv(v);
            var (lowDbv, highDbv) = request.ToneLevelsDbv(sineEqDbv);
            return new Step(lowHz, highHz, v, sineEqDbv, lowDbv, highDbv);
        })];
    }

    /// <summary>
    /// The sweep's analysis settings — the THD amplitude sweep's, plus the intermodulation spec on
    /// the snapped tones. <c>FundamentalMinSearchHz</c> is lowered under the low tone so the meters
    /// still find *a* fundamental (the pipeline returns no meters at all otherwise, and the IMD
    /// result rides on the meters record); which tone it picks is irrelevant here.
    /// </summary>
    private static SpectrumPipelineOptions BuildOptions(SpectrumPipelineOptions current, ImdSweepRequest request, Step first) =>
        current with
        {
            FftSize = request.FftSize,
            Window = ImdSweepRequest.Window,
            Averaging = request.AverageCount > 1 ? AveragingMode.Linear : AveragingMode.Off,
            AverageCount = request.AverageCount,
            ProcessLeft = request.ProcessLeft,
            ProcessRight = request.ProcessRight,
            SingleShot = true,
            CapturePreview = true,
            HarmonicMaxFreqHz = request.SampleRateHz / 2.0,
            AverageHopFraction = AverageHopFraction,
            FundamentalMinSearchHz = Math.Min(current.FundamentalMinSearchHz, first.LowHz / 2.0),
            Intermodulation = new IntermodulationSpec(first.LowHz, first.HighHz, request.Method, request.MaxOrder, request.MaxProductFreqHz),
        };

    private async Task<double[]> MeasureNoiseFloorAsync(ImdSweepRequest request, CancellationToken ct)
    {
        var noiseVrms = new double[2];
        if (!request.MeasureNoiseFloor)
            return noiseVrms;

        SetPhase(ThdSweepPhase.NoiseFloor);
        BroadcastProgress(0, 0, 0.0, ThdSweepPhase.NoiseFloor);

        var snapshots = await CaptureWithStimulusAsync(GeneratorConfig.Off, request, ct);
        foreach (var snapshot in snapshots)
        {
            int ch = snapshot.Channel == StreamChannel.Left ? 0 : 1;
            noiseVrms[ch] = SweepCaptureSupport.MeanBandBinVrms(snapshot, pipeline.Options.MeasurementLoFreqHz, pipeline.Options.MeasurementHiFreqHz);
        }

        return noiseVrms;
    }

    /// <summary>
    /// Applies a stimulus and returns the analysis of the first fully-averaged record after it.
    /// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/> flushes and discards the in-flight
    /// audio, and with <c>SingleShot</c> on that is the arming of this capture — do NOT add a
    /// <c>pipeline.Flush(0)</c> after it (see <see cref="ThdSweepRunner"/>'s own copy of this remark).
    /// </summary>
    private async Task<SpectrumSnapshot[]> CaptureWithStimulusAsync(GeneratorConfig generator, ImdSweepRequest request, CancellationToken ct)
    {
        // The FFT size is set once in BuildOptions and never changes across the run, so unlike the
        // THD runner there is nothing to re-apply before the flush here.
        await session.ConfigureGeneratorAsync(generator, ct);
        await SweepCaptureSupport.WaitForSingleShotAsync(
            pipeline,
            SweepCaptureSupport.CaptureTimeout(request.FftSize, request.SampleRateHz, request.AverageCount, AverageHopFraction, session.CaptureDelayMs),
            ct);
        return pipeline.LatestSnapshots();
    }

    private static GeneratorConfig TwoToneConfig(Step step, ImdSweepRequest request)
    {
        var pair = new ChannelGeneratorConfig(true, [new GeneratorTone(step.LowHz, step.LowDbv), new GeneratorTone(step.HighHz, step.HighDbv)])
        {
            Kind = GeneratorKind.Tones,
        };
        return new GeneratorConfig(
            request.ProcessLeft ? pair : ChannelGeneratorConfig.Off,
            request.ProcessRight ? pair : ChannelGeneratorConfig.Off)
        {
            // Already coherent; snapping again is idempotent and keeps the device's reported tones
            // identical to the ones the spec was built on.
            SnapToBin = true,
        };
    }

    private static IEnumerable<ImdSweepPoint> BuildPoints(Step step, ImdSweepRequest request, SpectrumSnapshot[] snapshots, double[] noiseVrms)
    {
        foreach (var snapshot in snapshots)
        {
            // No meters, or tones the analyzer could not resolve: no point, rather than a NaN one.
            if (snapshot.Meters?.Intermodulation is not { } imd)
                continue;

            int channel = snapshot.Channel == StreamChannel.Left ? 0 : 1;

            double sineEqVrms = imd.SineEquivalentVrms;
            double noisePercent = sineEqVrms > 0.0 ? 100.0 * noiseVrms[channel] / sineEqVrms : 0.0;
            double powerWatt = request.LoadOhms > 0.0 ? sineEqVrms * sineEqVrms / request.LoadOhms : 0.0;

            // Contiguous from order 2: the chart's per-order traces are positional, and a gap would
            // shift every later order onto the wrong trace.
            var orderPercent = new List<double>();
            for (int order = 2; ; order++)
            {
                var match = imd.PerOrder.FirstOrDefault(o => o.Order == order);
                if (match.Order != order)
                    break;
                orderPercent.Add(match.Ratio * 100.0);
            }

            yield return new ImdSweepPoint(
                channel,
                step.SineEquivalentDbv,
                imd.ToneLowDbv,
                imd.ToneHighDbv,
                imd.SineEquivalentDbv,
                imd.CombinedRmsDbv,
                imd.SineEquivalentDbv - step.SineEquivalentDbv,
                powerWatt,
                imd.ImdPercent,
                orderPercent,
                noisePercent,
                imd.ToneLowFreqHz,
                imd.ToneHighFreqHz,
                snapshot.PreviewFft,
                snapshot.PreviewScope);
        }
    }

    private void AddPoint(ImdSweepPoint point)
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

    private void SetProgress(int index, double currentLevelVolt)
    {
        lock (_stateLock)
        {
            _index = index;
            _currentLevelVolt = currentLevelVolt;
        }
    }

    /// <summary>Progress frames carry the <see cref="StreamFlags.IntermodulationSweep"/> flag so the client routes them to this sweep's store, not the THD one's.</summary>
    private void BroadcastProgress(int index, int total, double currentLevelVolt, ThdSweepPhase phase) =>
        hub.Broadcast(StreamFrame.EncodeSweepProgress(index, total, currentLevelVolt, phase, StreamFlags.IntermodulationSweep));

    /// <summary>One planned point: the snapped pair and the levels to play it at.</summary>
    private readonly record struct Step(double LowHz, double HighHz, double LevelVolt, double SineEquivalentDbv, double LowDbv, double HighDbv);
}
