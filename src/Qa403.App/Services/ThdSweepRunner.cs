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

    /// <summary>Headroom left above the expected level when picking an input range, in dB. From the previous app's <c>DetermineAttenuation</c>.</summary>
    private const double InputRangeHeadroomDb = 5.0;

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
    private int _index;
    private int _total;
    private double _currentFreqHz;
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
                    Running, _phase, _index, _total, _currentFreqHz, _generatorAmpDbv, _error, [.. _points]);
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

            double sampleRateHz = request.EffectiveSampleRateHz;
            double maxHz = MaxFrequencyNyquistFraction * sampleRateHz / 2.0;
            var desired = SweepPoints.LogPerOctave(request.StartFreqHz, request.EndFreqHz, request.StepsPerOctave);

            // Both paths produce the same shape; the fixed one just reports one size for every
            // point, so the runner below never has to branch on the mode again.
            var steps = request.AdaptiveResolution
                ? SweepPoints.SnapAndDedupeAdaptive(desired, sampleRateHz, request.FftSizeAt, maxHz)
                : [.. SweepPoints.SnapAndDedupe(desired, sampleRateHz, request.FftSize, maxHz)
                    .Select(t => new SweepStep(t.FrequencyHz, request.FftSize))];

            if (steps.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(request), $"No sweep frequency falls below {maxHz:0} Hz at this sample rate.");

            lock (_stateLock)
            {
                _phase = ThdSweepPhase.DeterminingLevel;
                _index = 0;
                _total = steps.Length;
                _currentFreqHz = 0.0;
                _generatorAmpDbv = request.LevelMode == ThdSweepLevelMode.GeneratorVoltage ? request.LevelDbv : ProbeStartDbv;
                _error = null;
                _points = [];
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _sweepTask = Task.Run(() => RunAsync(request, steps, cts.Token), CancellationToken.None);

            return new ThdSweepStarted(steps.Length, [.. steps.Select(s => s.FrequencyHz)]);
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

    private async Task RunAsync(ThdSweepRequest request, SweepStep[] steps, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;

        // Set only once everything below has been unwound -- see the end of the finally block.
        var terminalPhase = ThdSweepPhase.Failed;
        string? terminalError = null;

        try
        {
            pipeline.Options = BuildSweepOptions(savedOptions, request);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.EffectiveSampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            double generatorAmpDbv = await ResolveGeneratorAmplitudeAsync(request, ct);
            SetGeneratorAmpDbv(generatorAmpDbv);

            // Distinct sizes only -- fixed mode has one, adaptive has one per size band the grid
            // actually reaches, which is at most a handful.
            var fftSizes = steps.Select(s => s.FftSize).Distinct().OrderBy(n => n).ToArray();
            var noiseByFftSize = await MeasureNoiseFloorAsync(request, fftSizes, ct);

            SetPhase(ThdSweepPhase.Sweeping);

            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var step = steps[i];
                SetProgress(i, step.FrequencyHz);
                BroadcastProgress(i, steps.Length, step.FrequencyHz, ThdSweepPhase.Sweeping);

                var snapshots = await CaptureWithStimulusAsync(
                    ToneConfig(step.FrequencyHz, generatorAmpDbv, request), request, step.FftSize, ct);

                var noiseVrms = noiseByFftSize.TryGetValue(step.FftSize, out var forSize) ? forSize : new double[2];
                foreach (var point in BuildPoints(step.FrequencyHz, generatorAmpDbv, request, snapshots, noiseVrms))
                {
                    AddPoint(point);
                    hub.Broadcast(StreamFrame.EncodeSweepPoint(point, (uint)i));
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
            FundamentalMinSearchHz = Math.Min(current.FundamentalMinSearchHz, request.StartFreqHz / 2.0),
        };

    /// <summary>
    /// The generator amplitude to sweep with. Direct for <see cref="ThdSweepLevelMode.GeneratorVoltage"/>;
    /// for the output-referred modes it is solved by probing at 1 kHz, exactly as the previous app
    /// did: measure what comes back, correct the generator by the shortfall, repeat.
    /// </summary>
    private async Task<double> ResolveGeneratorAmplitudeAsync(ThdSweepRequest request, CancellationToken ct)
    {
        if (request.LevelMode == ThdSweepLevelMode.GeneratorVoltage)
            return request.LevelDbv;

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

        for (int i = 0; i < MaxProbeIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            var snapshots = await CaptureWithStimulusAsync(
                ToneConfig(probeTone.FrequencyHz, genDbv, request), request, probeFftSize, ct);

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

        return genDbv;
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

        double wanted = expectedLevelDbv + InputRangeHeadroomDb;
        double selected = ranges.Where(r => r >= wanted).DefaultIfEmpty(ranges.Max()).Min();

        if (Math.Abs(selected - session.InputRangeDbv) > 0.01)
            await session.SetInputRangeAsync(selected, ct);
    }

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
    /// </summary>
    private async Task<Dictionary<int, double[]>> MeasureNoiseFloorAsync(
        ThdSweepRequest request, IReadOnlyList<int> fftSizes, CancellationToken ct)
    {
        var byFftSize = new Dictionary<int, double[]>();
        if (!request.MeasureNoiseFloor)
        {
            foreach (int fftSize in fftSizes)
                byFftSize[fftSize] = new double[2];
            return byFftSize;
        }

        SetPhase(ThdSweepPhase.NoiseFloor);
        BroadcastProgress(0, 0, 0.0, ThdSweepPhase.NoiseFloor);

        // One capture per distinct FFT size the sweep will use, not one for the whole sweep. The
        // figure is a *per-bin* level (see this method's own remarks) and therefore scales with bin
        // width -- about 3 dB per doubling -- so under adaptive resolution a single measurement
        // would be wrong by that much for every point analyzed at a different size, and the noise
        // trace would step at each size boundary. Fixed mode has exactly one size, so this collapses
        // back to the single capture it always did.
        foreach (int fftSize in fftSizes)
            byFftSize[fftSize] = await MeasureNoiseFloorAtAsync(request, fftSize, ct);

        return byFftSize;
    }

    private async Task<double[]> MeasureNoiseFloorAtAsync(ThdSweepRequest request, int fftSize, CancellationToken ct)
    {
        var noiseVrms = new double[2];

        var snapshots = await CaptureWithStimulusAsync(GeneratorConfig.Off, request, fftSize, ct);
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

        double recordSeconds = fftSize / request.EffectiveSampleRateHz;
        double fillSeconds = recordSeconds + StimulusLatencySeconds;

        // Averages are now paced by fresh signal, not by the display tick, so the budget has to be
        // too: AverageCount averages at AverageHopFraction cost (1 + (n-1)*hop) records. Timing this
        // against the old tick-rate figure would abort a large-FFT point long before it could
        // legitimately finish.
        double averageSeconds = (1.0 + ((Math.Max(1, request.AverageCount) - 1) * AverageHopFraction)) * recordSeconds;
        return TimeSpan.FromSeconds(((fillSeconds + averageSeconds) * 3.0) + 2.0);
    }

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

    private void SetProgress(int index, double currentFreqHz)
    {
        lock (_stateLock)
        {
            _index = index;
            _currentFreqHz = currentFreqHz;
        }
    }

    private void SetGeneratorAmpDbv(double ampDbv)
    {
        lock (_stateLock)
            _generatorAmpDbv = ampDbv;
    }

    private void BroadcastProgress(int index, int total, double currentFreqHz, ThdSweepPhase phase) =>
        hub.Broadcast(StreamFrame.EncodeSweepProgress(index, total, currentFreqHz, phase));
}
