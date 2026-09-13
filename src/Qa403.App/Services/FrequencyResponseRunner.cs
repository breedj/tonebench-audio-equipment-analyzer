using System.Numerics;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;
using Qa403.Dsp.Fft;

namespace Qa403.App.Services;

/// <summary>
/// Measures a DUT's frequency response with an exponential chirp: play one periodic sweep, capture
/// whole periods of it, and deconvolve.
///
/// <para><b>Why this needs no trigger.</b> The QA403 streams continuously and cannot be told to
/// start a capture at a particular sample. The stimulus is therefore made exactly periodic with a
/// period of <see cref="FrequencyResponseRequest.FftSize"/> frames
/// (<see cref="EchoChirpSettings.RepeatFrames"/>), so every analysis record holds exactly one whole
/// sweep, circularly rotated by an unknown amount — and a circular rotation leaves a magnitude
/// spectrum untouched. The response is correct wherever the record landed. Only phase carries the
/// rotation, and taking it out is the same step that takes out real DUT delay, so it is wanted
/// anyway.</para>
///
/// <para>That also fixes the window: <see cref="WindowType.Rectangular"/>. A record holding a whole
/// number of periods transforms exactly with no taper, and a taper would break the periodicity the
/// whole scheme rests on. This is the one measurement in the app for which Hann is the wrong
/// answer.</para>
///
/// <para>Server-side for the same reasons as <see cref="ThdSweepRunner"/>: a stimulus change carries
/// the flush and in-flight-DAC-discard rules with it, and a run survives a browser reload.</para>
/// </summary>
public sealed class FrequencyResponseRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    ILogger<FrequencyResponseRunner> logger)
{
    /// <summary>Columns sent to the chart per trace. Roughly 2x a wide display, per the project's decimation rule.</summary>
    public const int DisplayColumns = 1600;

    /// <summary>Where the output-referred level probe starts, and the ceiling it will not exceed. Both from the previous app.</summary>
    private const double ProbeStartDbv = -40.0;

    private const double MaxGeneratorDbv = 18.0;

    private const double MinGeneratorDbv = -60.0;

    /// <summary>How close the probe has to get before it stops correcting.</summary>
    private const double ProbeToleranceDb = 0.5;

    private const int MaxProbeIterations = 4;

    /// <summary>FFT size the probe runs at. Short on purpose: the probe needs one level, not resolution, and at the full size each pass would cost seconds.</summary>
    private const int ProbeFftSize = 16384;

    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Ranges closer than this count as the same one — the ladder is 6 dB apart, so this only absorbs float noise.</summary>
    private const double RangeEpsilonDb = 0.01;

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
    private double? _latencySamples;
    private string? _error;

    /// <summary>
    /// Reported so the display panel can drop the Right trace selector for a run whose right input
    /// is a yardstick rather than a measurement. Nothing about this measurement's own behaviour
    /// depends on it — the phase kind stays at its PerTrace default, which is what this runner has
    /// always done.
    /// </summary>
    private bool _rightChannelIsReference;
    private List<FrequencyResponseTrace> _traces = [];
    private FrequencyResponseRequest? _lastRequest;

    /// <summary>
    /// The request the most recent run used. Saving needs the as-run configuration, not whatever the
    /// panel happens to show by the time Save is pressed.
    /// </summary>
    public FrequencyResponseRequest? LastRequest => _lastRequest;

    /// <summary>
    /// Every phase in which the device is busy on this measurement's behalf. Both probes count:
    /// this is what rejects a second start, keeps STOP enabled, and tells a caller when the run is
    /// over — a probe missing from here reads as "finished" the moment it begins.
    /// </summary>
    public bool Running => _phase
        is FrequencyResponsePhase.DeterminingLevel
        or FrequencyResponsePhase.DeterminingRange
        or FrequencyResponsePhase.Measuring;

    public FrequencyResponseStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new FrequencyResponseStatus(
                    Running, _phase, _continuous, _passesCompleted, _averagesTaken, _averageCount,
                    _generatorAmpDbv, _binHz, _sampleRateHz, _fftSize, _latencySamples, _error,
                    [.. _traces], PointsMeasured: 0, PointCount: 0,
                    PhaseKind: ResponsePhaseKind.PerTrace, RightChannelIsReference: _rightChannelIsReference);
            }
        }
    }

    /// <summary>
    /// Validates and starts a run in the background, returning as soon as its shape is known. A run
    /// is several full records long — at 0.37 Hz resolution one record alone is 2.7 s — so it does
    /// not belong inside an HTTP request.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is already going, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not measurable.</exception>
    public async Task<FrequencyResponseStarted> StartAsync(FrequencyResponseRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("A frequency-response measurement is already running.");

            lock (_stateLock)
            {
                _phase = request.SolvesGeneratorLevel
                    ? FrequencyResponsePhase.DeterminingLevel
                    : FrequencyResponsePhase.Measuring;
                _continuous = request.Continuous;
                _passesCompleted = 0;
                _averagesTaken = 0;
                _averageCount = request.AverageCount;
                _generatorAmpDbv = request.SolvesGeneratorLevel ? ProbeStartDbv : request.LevelDbv;
                _binHz = request.BinHz;
                _sampleRateHz = request.SampleRateHz;
                _fftSize = request.FftSize;
                _latencySamples = null;
                _error = null;
                _rightChannelIsReference = request.RightChannelIsReference;
                _traces = [];
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, cts.Token), CancellationToken.None);

            return new FrequencyResponseStarted(
                request.SampleRateHz, request.FftSize, request.BinHz, request.AverageCount,
                request.FftSize / request.SampleRateHz, request.Continuous);
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
    /// Discards the traces on screen and returns the emptied status. Server-side for the same reason
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
            _phase = FrequencyResponsePhase.Idle;
            _continuous = false;
            _passesCompleted = 0;
            _averagesTaken = 0;
            _averageCount = 0;
            _generatorAmpDbv = 0.0;
            _latencySamples = null;
            _error = null;

            return Status;
        }
    }

    private async Task RunAsync(FrequencyResponseRequest request, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;
        bool stepDownSuppressed = false;
        bool ownsInputRange = false;

        var terminalPhase = FrequencyResponsePhase.Failed;
        string? terminalError = null;

        // Read by the cancellation handler below, so it can tell "stopped with a complete
        // measurement on screen" from "abandoned before there was one".
        int passes = 0;

        try
        {
            logger.LogInformation(
                "Frequency response starting: span {SpanHz:0} Hz ({SampleRateHz:0} Hz sample rate), {FftSize} points "
                    + "({BinHz:0.00} Hz per bin, {RecordSeconds:0.000} s per record), {Averages} averages, "
                    + "level {LevelValue:0.###} {LevelUnit} ({LevelMode}), smoothing {Smoothing}, right channel is reference: {RightRef}",
                request.FrequencySpanHz, request.SampleRateHz, request.FftSize, request.BinHz,
                request.FftSize / request.SampleRateHz, request.AverageCount, request.LevelValue,
                request.LevelUnit, request.LevelMode,
                request.SmoothingDenominator == 0 ? "none" : $"1/{request.SmoothingDenominator} octave",
                request.RightChannelIsReference);

            pipeline.Options = BuildOptions(savedOptions, request);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.SampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // Manual range control means manual: with the Auto checkbox off the user has picked a
            // range deliberately, and a measurement quietly reprogramming it would be the same class
            // of surprise autoranging used to be before that checkbox existed.
            ownsInputRange = session.AutoRangeEnabled != false && session.Capabilities.InputRangesDbv.Count > 0;

            if (ownsInputRange && session.AutoRangeStepDownEnabled is true)
            {
                // Step-down would walk off whichever range is chosen below within a second or two.
                // Step-up stays live throughout as the clip backstop, exactly as the sweep runner
                // does it.
                session.SetAutoRangeStepDownEnabled(false);
                stepDownSuppressed = true;
            }

            // The two drive modes learn the level in opposite orders, so the range work has to sit
            // on opposite sides of the generator-level probe.
            //
            // Output voltage knows what the input stage must accommodate before it knows anything
            // about the generator — the target *is* that level — so the range is predicted from the
            // target and applied first. That ordering is load-bearing rather than tidy: the level
            // probe measures the DUT's gain from its own captures, so a leftover range too sensitive
            // for the target clips them, the probe reads the loss as missing gain and pushes the
            // generator up, which clips harder still. Measured against a plain loopback asking for
            // 6 dBV out: the generator settled at 16.74 dBV, a 10.7 dB overshoot, and the same run
            // from a wide starting range lands on 6.01 dBV.
            if (ownsInputRange && request.SolvesGeneratorLevel)
                await ApplyInputRangeAsync(InputRangeSelector.For(session.Capabilities.InputRangesDbv, request.LevelDbv), ct);

            double generatorAmpDbv = await ResolveGeneratorAmplitudeAsync(request, ct);
            SetGeneratorAmpDbv(generatorAmpDbv);

            // Input voltage is the other way round: the generator level is given and nothing is known
            // about what comes back, so it has to be measured — after the level is settled, because
            // the probe plays at exactly that level.
            if (ownsInputRange && !request.SolvesGeneratorLevel)
                await ApplyInputRangeAsync(await ProbeInputRangeAsync(request, generatorAmpDbv, ct), ct);

            SetPhase(FrequencyResponsePhase.Measuring);

            var reference = ResponseCaptureSupport.BuildReferenceSpectrum(_fft, request, generatorAmpDbv);
            var generator = ResponseCaptureSupport.BuildGenerator(request, generatorAmpDbv);

            // Configured once for the whole run, not once per pass. In continuous mode the stimulus
            // never changes between passes, so re-applying it would flush and discard a queue depth
            // of perfectly good audio on every repeat for nothing.
            bool generatorConfigured = false;

            do
            {
                ct.ThrowIfCancellationRequested();

                var (traces, peakDbv) = await MeasureOnceAsync(request, generatorAmpDbv, reference, generator, generatorConfigured, ct);
                generatorConfigured = true;
                passes++;

                // Re-range from what this pass actually saw, which is the previous app's
                // DetermineAttenuationFromLeftRightSeriesData at the bottom of its continuous loop.
                // Only in continuous mode: a single run has nothing left to spend a new range on,
                // and changing it after the fact would only cost the next measurement a settle.
                if (request.Continuous && ownsInputRange)
                    await ApplyInputRangeAsync(InputRangeSelector.For(session.Capabilities.InputRangesDbv, peakDbv), ct);

                lock (_stateLock)
                {
                    _traces = traces;
                    _passesCompleted = passes;
                }

                foreach (var trace in traces)
                    hub.Broadcast(StreamFrame.EncodeFrequencyResponse(trace));

                // Only the first pass is worth a log line in continuous mode; one per second for
                // minutes would bury everything else.
                if (passes == 1)
                {
                    logger.LogInformation(
                        "Frequency response measured: {Averages} records, {Delay}, "
                            + "peak {PeakDb:0.00} dB at {PeakHz:0.0} Hz{Continuing}",
                        request.AverageCount,
                        _latencySamples is { } delaySamples
                            ? $"DUT delay {delaySamples:0.00} samples"
                            : "no measured reference, so no delay",
                        traces.Count > 0 ? traces[0].PeakDb : 0.0,
                        traces.Count > 0 ? traces[0].PeakFreqHz : 0.0,
                        request.Continuous ? " (repeating until stopped)" : "");
                }
            }
            while (request.Continuous && !ct.IsCancellationRequested);

            if (request.Continuous)
                logger.LogInformation("Frequency response stopped after {Passes} passes", passes);

            terminalPhase = FrequencyResponsePhase.Finished;
        }
        catch (OperationCanceledException)
        {
            // The phase describes the data on screen, not the manner of stopping. Stopping a
            // continuous run is the ordinary way to end one, and the curves it leaves behind are the
            // last *complete* pass — a whole measurement, not a half-finished one. Only a run
            // cancelled before any pass finished has nothing to show, and that is a real Cancelled.
            terminalPhase = request.Continuous && passes > 0
                ? FrequencyResponsePhase.Finished
                : FrequencyResponsePhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Frequency response failed");
            terminalPhase = FrequencyResponsePhase.Failed;
            terminalError = ex.Message;
        }
        finally
        {
            // Silence the generator first and unconditionally — a run that threw must not leave a
            // sweep playing into someone's amplifier. CancellationToken.None because this has to run
            // especially when the run was cancelled.
            try
            {
                await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not silence the generator after the frequency-response run");
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
                    logger.LogWarning(ex, "Could not restore autorange step-down after the frequency-response run");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the frequency-response run started");
                }
            }

            pipeline.Options = savedOptions;

            // Last, once everything above has unwound: `Running` is derived from the phase, so this
            // is what makes "no longer running" also mean "and has finished cleaning up".
            lock (_stateLock)
                _continuous = false;

            SetPhase(terminalPhase, terminalError);
            BroadcastProgress(_averagesTaken, _averageCount, terminalPhase);
        }
    }

    /// <summary>
    /// One complete measurement: <see cref="FrequencyResponseRequest.AverageCount"/> records folded
    /// into a fresh estimate, then turned into curves.
    ///
    /// <para>The estimators are created per pass rather than reused, which is what makes a continuous
    /// run a sequence of independent measurements instead of an ever-lengthening average. A blend
    /// across minutes would stop responding to the DUT, which is the opposite of what watching a
    /// live trace is for.</para>
    /// </summary>
    private async Task<(List<FrequencyResponseTrace> Traces, double PeakDbv)> MeasureOnceAsync(
        FrequencyResponseRequest request,
        double generatorAmpDbv,
        Complex[] reference,
        GeneratorConfig generator,
        bool generatorAlreadyConfigured,
        CancellationToken ct)
    {
        // Gated, because the stimulus is a sweep: deconvolving one recovers the DUT's harmonic
        // packets alongside its impulse response, and left in they comb the magnitude over whatever
        // part of the band the DUT distorts in. See ImpulseGate.
        var gate = ImpulseGate.ForSweep(
            request.FftSize, request.ChirpStartHz, request.ChirpEndHz);

        var left = new FrequencyResponseEstimator(request.FftSize, _fft, gate);
        var right = new FrequencyResponseEstimator(request.FftSize, _fft, gate);

        // Only when the right input is the reference. Estimating L against R directly, rather than
        // dividing the two synthetic-reference results, is what gives the gain trace its own honest
        // coherence — a ratio of two estimates has no coherence of its own.
        var gain = request.RightChannelIsReference
            ? new FrequencyResponseEstimator(request.FftSize, _fft, gate)
            : null;

        lock (_stateLock)
            _averagesTaken = 0;

        double peakDbv = double.NaN;

        // The record armed by the stimulus change is thrown away rather than measured, and that
        // costs one record per run. ConfigureGeneratorAsync discards the audio already queued for
        // the DAC (StimulusLatencyFrames), but that is the *play* latency, not the round trip back
        // through the DUT and the ADC, so some pre-change audio still reaches the front of the first
        // record. Every other measurement in the app tolerates that; this one cannot, because it
        // analyses with a Rectangular window on the premise that the record holds exactly one period
        // of the stimulus. A record that is part old chirp and part new one is not a period of
        // anything, and rectangular leakage spreads the error across the whole spectrum instead of
        // degrading gracefully.
        //
        // The residue is a fixed number of frames, so the damage scales as 1/FftSize — which is how
        // it was found: at 0.37 Hz/bin (2.73 s records) nothing is visible, while at 1.46 Hz
        // (0.68 s) SINGLE produced a wrecked trace and RUN produced one wrecked pass followed by
        // clean ones, the second pass being the first armed by a bare Flush(0). Waiting a whole
        // record is the only bound available here that does not depend on a latency figure the app
        // does not measure.
        if (!generatorAlreadyConfigured)
        {
            await session.ConfigureGeneratorAsync(generator, ct);
            BroadcastProgress(0, request.AverageCount, FrequencyResponsePhase.Measuring);
            await WaitForCaptureAsync(request, request.FftSize, ct);
        }

        for (int record = 0; record < request.AverageCount; record++)
        {
            ct.ThrowIfCancellationRequested();

            // Buffers emptied, nothing discarded: the stimulus has not changed since the throwaway
            // record above, so there is no pre-change audio in flight and a non-zero discard would
            // throw away good samples for nothing.
            pipeline.Flush(0);

            await WaitForCaptureAsync(request, request.FftSize, ct);

            var snapshots = pipeline.LatestSnapshots();
            peakDbv = ResponseCaptureSupport.PeakDbv(snapshots);
            var (leftSpectrum, rightSpectrum) = ResponseCaptureSupport.RequireBothChannels(snapshots, request.FftSize, "frequency-response");

            // Discarded: against the synthesised chirp what comes back is the record's arbitrary
            // start point in the periodic stimulus, not a delay. Only the gain estimator's is real,
            // because both of its spectra come from the same capture and the rotation cancels.
            left.Add(reference, leftSpectrum);
            right.Add(reference, rightSpectrum);
            double? delaySamples = gain?.Add(rightSpectrum, leftSpectrum);

            lock (_stateLock)
            {
                _averagesTaken = record + 1;
                _latencySamples = delaySamples;
            }

            BroadcastProgress(record + 1, request.AverageCount, FrequencyResponsePhase.Measuring);
        }

        return (ResponseCaptureSupport.BuildTraces(
            request, generatorAmpDbv, DisplayColumns, left.Complete(), right.Complete(), gain?.Complete()), peakDbv);
    }

    /// <summary>
    /// Measures the input range this DUT needs, for the input-voltage mode where nothing is known
    /// about what comes back: park on the least sensitive range, play the chirp at the real
    /// generator level, capture once, and pick the range from the peak. This is the previous app's
    /// <c>DetermineAttenuationForGeneratorVoltageWithChirp</c>, which starts at
    /// <c>MAXIMUM_DEVICE_ATTENUATION</c> for the same reason — guessing low on an unknown DUT clips,
    /// and a clipped probe reads a peak that is a property of the range rather than of the signal.
    ///
    /// <para>Why this exists at all: without it the run starts at whatever range the previous
    /// measurement left behind and lets the device's own ranger walk there. That works, but every
    /// step it takes mid-record punches a ~110 ms hole in the sample stream (quiesce plus relay
    /// settle) and forces the record to be re-captured. Arriving on the right range costs one short
    /// capture instead.</para>
    /// </summary>
    private async Task<double> ProbeInputRangeAsync(
        FrequencyResponseRequest request, double generatorAmpDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;

        SetPhase(FrequencyResponsePhase.DeterminingRange);
        BroadcastProgress(0, 1, FrequencyResponsePhase.DeterminingRange);

        int probeFftSize = Math.Min(ProbeFftSize, request.FftSize);

        // Least sensitive range first. An unknown DUT can be doing anything, and a probe that clips
        // reads a peak that is a property of the range rather than of the signal — which would then
        // choose that same range again and stay wrong.
        await ApplyInputRangeAsync(ranges.Max(), ct);

        pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
        await session.ConfigureGeneratorAsync(ResponseCaptureSupport.BuildGenerator(request, generatorAmpDbv, probeFftSize), ct);
        await WaitForCaptureAsync(request, probeFftSize, ct);

        double peakDbv = ResponseCaptureSupport.PeakDbv(pipeline.LatestSnapshots());

        pipeline.Options = pipeline.Options with { FftSize = request.FftSize };

        double selected = InputRangeSelector.For(ranges, peakDbv);
        logger.LogInformation(
            "Input-range probe measured a peak of {PeakDbv:0.##} dBV at {GenDbv:0.##} dBV generator; input range {RangeDbv:0.#} dBV",
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
    /// Pipeline settings for a chirp capture. Three of these differ from every other measurement in
    /// the app and each is load-bearing — see the type remarks for the window, and
    /// <see cref="SpectrumPipelineOptions.RetainComplexSpectrum"/> for the phase.
    /// </summary>
    private static SpectrumPipelineOptions BuildOptions(SpectrumPipelineOptions current, FrequencyResponseRequest request) =>
        current with
        {
            FftSize = request.FftSize,

            // Exact for a stimulus that is periodic in the record; anything else is wrong here.
            Window = WindowType.Rectangular,

            // The runner averages the records itself, in the complex domain and after removing each
            // one's own rotation. The pipeline's averager works on magnitudes, which would throw
            // away the phase this measurement is built on -- and averaging magnitudes across records
            // would also make the coherence figure meaningless, since it needs the cross-spectra.
            Averaging = AveragingMode.Off,
            AverageCount = 1,
            AverageHopFraction = 0.0,

            // Both channels always: the right one is either the reference or a trace in its own
            // right, and the two must come from the same instant either way.
            ProcessLeft = true,
            ProcessRight = true,
            SingleShot = true,
            RetainComplexSpectrum = true,

            // Nothing here reads a preview, and at this FFT size it is not free.
            CapturePreview = false,
        };

    /// <summary>
    /// The generator level to measure at. For <see cref="ThdSweepLevelMode.GeneratorVoltage"/>
    /// ("Input voltage" in the UI) the entered level is the answer. For
    /// <see cref="ThdSweepLevelMode.OutputVoltage"/> it is solved by probing, exactly as the previous
    /// app did (<c>QaLibrary.DetermineGenAmplitudeByOutputAmplitudeWithChirp</c>): play a chirp,
    /// read the peak that comes back, correct the generator by the shortfall in dB, repeat.
    /// </summary>
    private async Task<double> ResolveGeneratorAmplitudeAsync(FrequencyResponseRequest request, CancellationToken ct)
    {
        if (!request.SolvesGeneratorLevel)
            return request.LevelDbv;

        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        double target = request.LevelDbv;
        double amplitude = ProbeStartDbv;

        // A short record: the probe wants a peak level, not resolution, and at the full size each
        // pass would cost seconds.
        int probeFftSize = Math.Min(ProbeFftSize, request.FftSize);

        bool ceilingAsked = false;

        for (int pass = 0; pass < MaxProbeIterations; pass++)
        {
            ct.ThrowIfCancellationRequested();
            SetGeneratorAmpDbv(amplitude);
            BroadcastProgress(pass, MaxProbeIterations, FrequencyResponsePhase.DeterminingLevel);

            pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
            await session.ConfigureGeneratorAsync(ResponseCaptureSupport.BuildGenerator(request, amplitude, probeFftSize), ct);
            await WaitForCaptureAsync(request, probeFftSize, ct);

            var probeReference = ResponseCaptureSupport.BuildReferenceSpectrum(_fft, request, amplitude, probeFftSize);
            var (probeLeft, _) = ResponseCaptureSupport.RequireBothChannels(pipeline.LatestSnapshots(), probeFftSize, "frequency-response");

            double gainDb = ResponseCaptureSupport.MedianGainDb(probeReference, probeLeft, probeFftSize, request);
            if (double.IsNaN(gainDb))
                throw new InvalidOperationException("The level probe measured no signal — check the DUT is connected and powered.");

            // What a sine at the generator's level would produce at the DUT's output. Deconvolving
            // is what makes this a *level* rather than a bin: a chirp spreads its energy over
            // thousands of bins, so any single bin of the raw spectrum sits far below the sweep's
            // own amplitude. Reading one and correcting toward the target drives the generator up
            // by that whole difference -- measured here as 14 dB too high before this changed.
            // (The previous app read a peak bin and was right to, because the level it read came
            // back from the QA40x application already deconvolved.)
            double measuredDbv = amplitude + gainDb;

            double shortfall = target - measuredDbv;
            if (Math.Abs(shortfall) <= ProbeToleranceDb)
                break;

            // A demand for more level than the generator has is not acted on the first time it
            // appears -- one capture that read as silence used to end the probe here, clamping to the
            // maximum and running the whole measurement at full output off a single bad record. See
            // ResponseCaptureSupport.CorrectProbeAmplitude.
            var (corrected, outcome) = ResponseCaptureSupport.CorrectProbeAmplitude(
                amplitude, shortfall, ceilingAsked, MaxGeneratorDbv, MinGeneratorDbv);
            amplitude = corrected;

            if (outcome == ResponseCaptureSupport.ProbeCorrection.BelowFloor)
            {
                throw new InvalidOperationException(
                    $"The requested output level of {target:0.##} dBV would need a generator level below "
                        + $"{MinGeneratorDbv:0.##} dBV. The DUT's gain looks too high to measure this way.");
            }

            if (outcome == ResponseCaptureSupport.ProbeCorrection.AcceptCeiling)
            {
                logger.LogWarning(
                    "The requested output level of {Target:0.##} dBV needs more than the {Max:0.##} dBV the generator can produce; "
                        + "measuring at {Max:0.##} dBV instead",
                    target, MaxGeneratorDbv, MaxGeneratorDbv);
                break;
            }

            // Set only when the ceiling was asked for, so a reading that comes back in range clears
            // it and a later, genuine ceiling still gets its own confirmation.
            ceilingAsked = outcome == ResponseCaptureSupport.ProbeCorrection.ConfirmCeiling;
        }

        // Back to the measurement size before the caller arms its first real record.
        pipeline.Options = pipeline.Options with { FftSize = request.FftSize };
        return amplitude;
    }

    private async Task WaitForCaptureAsync(FrequencyResponseRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = ResponseCaptureSupport.CaptureTimeout(request.SampleRateHz, fftSize, session.CaptureDelayMs);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"A frequency-response record did not complete within {timeout.TotalSeconds:0.0} s. "
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

        hub.Broadcast(StreamFrame.EncodeFrequencyResponseProgress(taken, total, phase, generatorAmpDbv));
    }

    private void SetPhase(FrequencyResponsePhase phase, string? error = null)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _error = error;
        }
    }

    private void SetGeneratorAmpDbv(double dbv)
    {
        lock (_stateLock)
            _generatorAmpDbv = dbv;
    }
}
