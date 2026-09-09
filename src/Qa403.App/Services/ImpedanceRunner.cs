using System.Numerics;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;
using Qa403.Dsp.Fft;

namespace Qa403.App.Services;

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
/// <para><b>Calibration runs go through this same runner</b> with a
/// <see cref="ImpedanceCalTarget"/> set. A standard is captured exactly like a measurement and then
/// <i>not</i> corrected: the open standard is the channel mismatch, so normalising it against a
/// previous open would report the difference between two calibrations as an impedance.</para>
///
/// <para>The runner holds no calibration of its own. A captured standard is returned on the status
/// and stored by the browser in the measurement type's <c>params_json</c>, so a fixture calibration
/// lives in exactly one place and survives a restart.</para>
/// </summary>
public sealed class ImpedanceRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<ImpedanceRunner> logger)
{
    /// <summary>Columns sent to the chart per trace, matching the frequency response's.</summary>
    public const int DisplayColumns = 1600;

    /// <summary>FFT size the input-range probe runs at. Short on purpose: it needs one level, not resolution.</summary>
    private const int ProbeFftSize = 16384;

    /// <summary>Ranges closer than this count as the same one — the ladder is 6 dB apart, so this only absorbs float noise.</summary>
    private const double RangeEpsilonDb = 0.01;

    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly IFftBackend _fft = new MathNetBackend();

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
    private ImpedanceCalibration? _capturedCalibration;
    private double? _medianOhms;
    private double? _driveCurrentAmps;
    private string? _error;
    private List<ImpedanceTrace> _traces = [];
    private ImpedanceRequest? _lastRequest;

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
                    _capturedCalibration, _medianOhms, _driveCurrentAmps);
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("An impedance measurement is already running.");

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
                _capturedCalibration = null;
                _medianOhms = null;
                _driveCurrentAmps = null;
                _error = null;
                _traces = [];
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, cts.Token), CancellationToken.None);

            return new ImpedanceStarted(
                request.SampleRateHz, request.FftSize, request.BinHz, request.AverageCount,
                request.FftSize / request.SampleRateHz, request.Continuous, request.CalTarget);
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
            _capturedCalibration = null;
            _medianOhms = null;
            _driveCurrentAmps = null;
            _error = null;

            return Status;
        }
    }

    private async Task RunAsync(ImpedanceRequest request, CancellationToken ct)
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
                "Impedance starting: {What}, {ReferenceOhms:0.###} ohm reference, reference channel {RefChannel}, "
                    + "span {SpanHz:0} Hz ({SampleRateHz:0} Hz sample rate), {FftSize} points ({BinHz:0.00} Hz per bin), "
                    + "{Averages} averages, generator {GenDbv:0.##} dBV, smoothing {Smoothing}, cable {CableMode}",
                request.CalTarget == ImpedanceCalTarget.None ? "measuring a DUT" : $"capturing the {request.CalTarget} standard",
                request.ReferenceOhms, request.RightChannelIsReference ? "right" : "left",
                request.FrequencySpanHz, request.SampleRateHz, request.FftSize, request.BinHz,
                request.AverageCount, request.GeneratorDbv,
                request.SmoothingDenominator == 0 ? "none" : $"1/{request.SmoothingDenominator} octave",
                request.CableMode);

            pipeline.Options = BuildOptions(savedOptions, request);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.SampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // Manual range control means manual, exactly as the chirp runner treats it.
            ownsInputRange = session.AutoRangeEnabled != false && session.Capabilities.InputRangesDbv.Count > 0;

            if (ownsInputRange && session.AutoRangeStepDownEnabled is true)
            {
                session.SetAutoRangeStepDownEnabled(false);
                stepDownSuppressed = true;
            }

            double generatorAmpDbv = request.GeneratorDbv;

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

            var generator = ResponseCaptureSupport.BuildChirpGenerator(
                generatorAmpDbv, request.ChirpStartHz, request.ChirpEndHz, request.FftSize);

            // Configured once for the whole run: in continuous mode the stimulus never changes, so
            // re-applying it would flush a queue depth of good audio on every repeat for nothing.
            bool generatorConfigured = false;

            do
            {
                ct.ThrowIfCancellationRequested();

                var pass = await MeasureOnceAsync(request, generator, generatorConfigured, ct);
                generatorConfigured = true;
                passes++;

                if (request.Continuous && ownsInputRange)
                    await ApplyInputRangeAsync(InputRangeSelector.For(session.Capabilities.InputRangesDbv, pass.PeakDbv), ct);

                lock (_stateLock)
                {
                    _traces = pass.Traces;
                    _passesCompleted = passes;
                    _capturedCalibration = pass.Calibration;
                    _medianOhms = pass.MedianOhms;
                    _driveCurrentAmps = pass.DriveCurrentAmps;
                }

                foreach (var trace in pass.Traces)
                    hub.Broadcast(StreamFrame.EncodeImpedance(trace));

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
        double? DriveCurrentAmps);

    /// <summary>
    /// One complete measurement: <see cref="ImpedanceRequest.AverageCount"/> records folded into a
    /// fresh estimate, then solved and turned into a curve.
    ///
    /// <para>The estimator is created per pass rather than reused, which makes a continuous run a
    /// sequence of independent measurements instead of an ever-lengthening average.</para>
    /// </summary>
    private async Task<PassResult> MeasureOnceAsync(
        ImpedanceRequest request,
        GeneratorConfig generator,
        bool generatorAlreadyConfigured,
        CancellationToken ct)
    {
        // Optional here, unlike the frequency response, where it is always on. Gating removes the
        // harmonic packets a swept measurement of a distorting DUT picks up, at a factor of two in
        // resolution -- and a passive impedance usually distorts far too little to be worth paying
        // that, which is why this is a choice.
        ImpulseGate? gate = request.ApplySweepGate
            ? ImpulseGate.ForSweep(request.FftSize, request.ChirpStartHz, request.ChirpEndHz)
            : null;

        var estimator = new FrequencyResponseEstimator(request.FftSize, _fft, gate, removeLatency: false);

        lock (_stateLock)
            _averagesTaken = 0;

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
                _averagesTaken = record + 1;

            BroadcastProgress(record + 1, request.AverageCount, phase);
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
                peakDbv, calibration, MedianOhms: null, DriveCurrentAmps: null);
        }

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
            peakDbv, null, medianOhms, driveCurrentAmps);
    }

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
        await session.ConfigureGeneratorAsync(
            ResponseCaptureSupport.BuildChirpGenerator(
                generatorAmpDbv, request.ChirpStartHz, request.ChirpEndHz, probeFftSize), ct);
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
    /// Pipeline settings for a chirp capture, identical to the frequency response's and load-bearing
    /// for the same reasons: a rectangular window because the stimulus is periodic in the record, and
    /// the retained complex spectrum because this measurement is built on phase.
    /// </summary>
    private static SpectrumPipelineOptions BuildOptions(SpectrumPipelineOptions current, ImpedanceRequest request) =>
        current with
        {
            FftSize = request.FftSize,
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
        };

    private async Task WaitForCaptureAsync(ImpedanceRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = ResponseCaptureSupport.CaptureTimeout(request.SampleRateHz, fftSize);
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

        hub.Broadcast(StreamFrame.EncodeImpedanceProgress(taken, total, phase, generatorAmpDbv));
    }

    private void SetPhase(FrequencyResponsePhase phase, string? error = null)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _error = error;
        }
    }
}
