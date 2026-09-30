using System.Numerics;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Dsp;
using Qa403.Dsp.Fft;

namespace Qa403.App.Services;

/// <summary>
/// Measures a DUT's impulse response with an exponential chirp, and reads the things only an
/// impulse response can give: a gated (reflection-free) magnitude and phase, and the DUT's harmonic
/// distortion against frequency.
///
/// <para><b>It is the same acquisition as <see cref="FrequencyResponseRunner"/>.</b> Same periodic
/// sweep, same rectangular window, same level and range probes — everything shared and pure lives in
/// <see cref="ResponseCaptureSupport"/>. What differs is what happens to the estimate afterwards:
/// the frequency-response run reads magnitude and phase off H and throws the impulse response away,
/// while this one keeps it, gates it to a user-chosen span, and mines the stretch of record the
/// other measurement deliberately discards.</para>
///
/// <para><b>Two gates, and they are not the same gate.</b>
/// <see cref="ImpulseResponseRequest.SweepGate"/> is fixed by the sweep geometry and exists to
/// delete the harmonic packets; <see cref="ImpulseResponseRequest.GatePreMs"/> and
/// <see cref="ImpulseResponseRequest.GatePostMs"/> are the user's, and are about reflections and
/// frequency resolution. The second is always a subset of the first, which
/// <see cref="ImpulseResponseRequest.Validate"/> enforces. See <see cref="ImpulseWindow"/>.</para>
///
/// <para>The control flow below — and in particular the <c>finally</c> — is a deliberate copy of
/// <see cref="FrequencyResponseRunner"/>'s rather than a shared base, for the reasons set out in
/// <see cref="ResponseCaptureSupport"/>.</para>
/// </summary>
public sealed class ImpulseResponseRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    ILoopbackReferenceStore referenceStore,
    ILogger<ImpulseResponseRunner> logger) : IMeasurementRun
{
    /// <summary>Columns per magnitude/phase trace — the same as the frequency response's, because they are the same curves.</summary>
    public const int DisplayColumns = 1600;

    /// <summary>
    /// Columns per impulse trace. More than a spectrum gets: an impulse response is a waveform whose
    /// point is its single-sample excursions, and it is drawn on a linear axis where every column is
    /// the same width.
    /// </summary>
    public const int ImpulseDisplayColumns = 2000;

    /// <summary>Where the output-referred level probe starts, and the ceiling it will not exceed. Both from the previous app.</summary>
    private const double ProbeStartDbv = -40.0;

    private const double MaxGeneratorDbv = 18.0;

    private const double MinGeneratorDbv = -60.0;

    /// <summary>How close the probe has to get before it stops correcting.</summary>
    private const double ProbeToleranceDb = 0.5;

    private const int MaxProbeIterations = 4;

    /// <summary>FFT size the probe runs at. Short on purpose: the probe needs one level, not resolution.</summary>
    private const int ProbeFftSize = 16384;

    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Ranges closer than this count as the same one — the ladder is 6 dB apart, so this only absorbs float noise.</summary>
    private const double RangeEpsilonDb = 0.01;

    /// <summary>How far a loopback calibration's median in-band gain may sit from unity before it is refused.</summary>
    private const double ReferenceUnityToleranceDb = 3.0;

    /// <summary>And how coherent it has to be. A calibration is a straight wire; anything less is a mistake worth catching.</summary>
    private const double ReferenceMinCoherence = 0.99;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    /// <summary>For the reference spectra and the estimators, all at the analysis size.</summary>
    private readonly IFftBackend _fft = new FftFlatBackend();

    /// <summary>
    /// Its own backend for the packet extractor, which transforms at several shorter lengths. Both
    /// implementations cache one scratch array sized to the last call, so sharing would reallocate
    /// on every switch.
    /// </summary>
    private readonly IFftBackend _packetFft = new FftFlatBackend();

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
    private ImpulseReferenceMode _referenceMode;
    private string? _referenceName;
    private double _gatePreMs;
    private double _gatePostMs;
    private double _gateResolutionHz;

    private List<FrequencyResponseTrace> _traces = [];
    private List<ImpulseTrace> _impulses = [];
    private List<HarmonicCurve> _harmonics = [];
    private ImpulseResponseRequest? _lastRequest;

    /// <summary>
    /// The finished left-channel estimate and impulse response of the most recent pass, kept for the
    /// save path and for the loopback capture's sanity check. Not part of the status — the full
    /// arrays are up to a megabyte and are not a thing to poll.
    /// </summary>
    private FrequencyResponseResult? _lastLeftResult;

    private ImpulseResponseResult? _lastLeftImpulse;

    /// <summary>The request the most recent run used. Saving needs the as-run configuration, not whatever the panel shows now.</summary>
    public ImpulseResponseRequest? LastRequest => _lastRequest;

    /// <summary>The most recent pass's gated left impulse response, or null if nothing has been measured.</summary>
    public ImpulseResponseResult? LastImpulse => _lastLeftImpulse;

    /// <summary>
    /// A cumulative spectral decay read off the impulse response currently on screen.
    ///
    /// <para><b>Computed on demand rather than with the measurement</b>, and that is what makes the
    /// two decay views usable at all: every one of their settings is an analysis choice, not an
    /// acquisition one, so changing a transform length or a block count has to redraw from the
    /// record already captured instead of asking for another sweep. The same record answers as many
    /// different questions as are put to it.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing has been measured yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings do not describe a surface this record can support.</exception>
    public Task<DecaySurface> ComputeCumulativeSpectralDecayAsync(CsdSettings settings, CancellationToken ct)
    {
        var (impulse, request) = RequireLastImpulse();

        // Off the request thread: a long transform times a hundred blocks is not instant, and a
        // status poll has to stay answerable while it runs. Its own FFT backend for the same reason
        // — the backends cache one scratch array sized to the last call, so sharing `_fft` with a
        // measurement running concurrently would have the two overwrite each other's.
        return Task.Run(
            () => CumulativeSpectralDecay.Compute(
                impulse.Gated, impulse.PeakIndex, request.SampleRateHz,
                AvailablePostSamples(request), settings, new FftFlatBackend()),
            ct);
    }

    /// <summary>
    /// A burst decay read off the impulse response currently on screen. See
    /// <see cref="ComputeCumulativeSpectralDecayAsync"/> for why this is computed on demand.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing has been measured yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings do not describe a surface this record can support.</exception>
    public Task<DecaySurface> ComputeBurstDecayAsync(BurstDecaySettings settings, CancellationToken ct)
    {
        var (impulse, request) = RequireLastImpulse();

        // Rather more than not instant: a two-period wavelet at the bottom of the band is tens of
        // thousands of taps, and there is one convolution per analysis frequency. It parallelises
        // inside, which is the other reason it must not run on the request thread.
        return Task.Run(
            () => BurstDecay.Compute(
                impulse.Gated, impulse.PeakIndex, request.SampleRateHz,
                AvailablePostSamples(request), settings),
            ct);
    }

    /// <summary>
    /// How far past the impulse a <i>stored</i> record is the DUT rather than an edge — the same bound
    /// <see cref="AvailablePostSamples"/> applies to the live one, rebuilt from the run settings the
    /// result stores (the sweep gate is fixed by the sweep geometry, which those settings name), and
    /// further bounded by what was stored: the row keeps a window centred on the peak, and past its
    /// end there is nothing, which a decay analysis would read as a perfect decay.
    /// </summary>
    public static int StoredAvailablePostSamples(StoredImpulseTrace stored, ImpulseResponseParameters parameters)
    {
        double sampleRateHz = parameters.FrequencySpanHz * 2.0;
        int fftSize = (int)Math.Round(sampleRateHz / FrequencyResponseRequest.BaseBinHz) * Math.Max(1, parameters.ResolutionMultiplier);
        double chirpEndHz = FrequencyResponseRequest.ChirpEndNyquistFraction * sampleRateHz / 2.0;
        var gate = ImpulseGate.ForSweep(fftSize, Math.Max(parameters.ChirpStartHz, 0.1), chirpEndHz);
        int fromGate = Math.Max(0, gate.PostSamples - gate.TaperSamples);
        int fromRow = Math.Max(0, stored.Ir.Length - stored.PeakIndex - 1);
        return Math.Min(fromGate, fromRow);
    }

    /// <summary>The measured impulse and the settings that produced it, or a message saying there is none.</summary>
    private (ImpulseResponseResult Impulse, ImpulseResponseRequest Request) RequireLastImpulse()
    {
        ImpulseResponseResult? impulse;
        lock (_stateLock)
            impulse = _lastLeftImpulse;

        var request = _lastRequest;
        if (impulse is null || request is null)
            throw new InvalidOperationException("Nothing has been measured yet — run an impulse response first.");

        return (impulse, request);
    }

    /// <summary>
    /// How far past the impulse the record is the DUT rather than the sweep gate's own edge. The
    /// taper is excluded, not just the cut: a decay analysis reading across a raised-cosine edge
    /// measures the edge.
    /// </summary>
    private static int AvailablePostSamples(ImpulseResponseRequest request)
    {
        var gate = request.SweepGate;
        return Math.Max(0, gate.PostSamples - gate.TaperSamples);
    }

    /// <summary>
    /// Every phase in which this measurement is busy. <see cref="FrequencyResponsePhase.Analyzing"/>
    /// belongs here and in neither of the other runners' equivalents: the rule is that every phase a
    /// runner can <i>enter</i> must be in its own <c>Running</c>, or the run reads as finished the
    /// moment it begins. <c>RunnerPhaseCoverageTests</c> checks that mechanically.
    /// </summary>
    public bool Running => _phase
        is FrequencyResponsePhase.DeterminingLevel
        or FrequencyResponsePhase.DeterminingRange
        or FrequencyResponsePhase.Measuring
        or FrequencyResponsePhase.Analyzing;

    public ImpulseResponseStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return new ImpulseResponseStatus(
                    Running, _phase, _continuous, _passesCompleted, _averagesTaken, _averageCount,
                    _generatorAmpDbv, _binHz, _sampleRateHz, _fftSize, _latencySamples, _error,
                    [.. _traces], [.. _impulses], [.. _harmonics],
                    _referenceMode, _referenceName, _gateResolutionHz, _gatePreMs, _gatePostMs);
            }
        }
    }

    /// <summary>
    /// Validates and starts a run in the background, returning as soon as its shape is known.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is already going, or no device is connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The request is not measurable.</exception>
    public async Task<ImpulseResponseStarted> StartAsync(ImpulseResponseRequest request, CancellationToken ct)
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
                "{Measurement}: chirp start raised to the analyzer's floor of {Floor:0.#} Hz", "Impulse response", clampedStartHz);
        }

        // Resolved before anything is started, so a missing or mismatched calibration is a 400 on the
        // start request rather than a run that fails a minute in.
        LoopbackReference? loopback = null;
        string? loopbackName = null;
        if (request.ReferenceMode == ImpulseReferenceMode.StoredLoopback)
        {
            long id = request.ReferenceCalibrationId!.Value;
            var stored = await referenceStore.GetAsync(id, ct)
                ?? throw new ArgumentOutOfRangeException(
                    nameof(request), $"No loopback reference with id {id} exists.");

            stored.Reference.EnsureUsableAt(request.SampleRateHz);
            loopback = stored.Reference;
            loopbackName = stored.Info.Name;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("An impulse-response measurement is already running.");

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
                _referenceMode = request.ReferenceMode;
                _referenceName = loopbackName;
                _gatePreMs = request.GatePreMs;
                _gatePostMs = request.GatePostMs;
                _gateResolutionHz = request.GateResolutionHz;
                _traces = [];
                _impulses = [];
                _harmonics = [];
                _lastLeftResult = null;
                _lastLeftImpulse = null;
            }

            _lastRequest = request;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(request, loopback, cts.Token), CancellationToken.None);

            return new ImpulseResponseStarted(
                request.SampleRateHz, request.FftSize, request.BinHz, request.AverageCount,
                request.FftSize / request.SampleRateHz, request.Continuous, request.GateResolutionHz);
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

    /// <summary>Discards what is on screen and returns the emptied status.</summary>
    /// <exception cref="InvalidOperationException">A measurement is running.</exception>
    public ImpulseResponseStatus Clear()
    {
        lock (_stateLock)
        {
            if (Running)
                throw new InvalidOperationException("Wait for the measurement to finish (or cancel it) before clearing.");

            _traces = [];
            _impulses = [];
            _harmonics = [];
            _lastLeftResult = null;
            _lastLeftImpulse = null;
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

    /// <summary>
    /// Captures the analyzer's own impulse response, for later division out of a measurement.
    ///
    /// <para>Its own entry point rather than a mode of <see cref="StartAsync"/>: it must not replace
    /// the traces on screen, it is a calibration rather than a result, and it is the only path with
    /// a physical precondition — the generator output has to be patched straight back to the
    /// analyzer input. That precondition is also why it sanity-checks what it captured before
    /// storing it: a calibration taken by accident through the DUT would divide the DUT out of every
    /// subsequent measurement, silently and plausibly.</para>
    /// </summary>
    public async Task<LoopbackReferenceInfo> CaptureReferenceAsync(
        ImpulseReferenceCaptureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentOutOfRangeException(nameof(request), "A loopback reference needs a name.");

        // The floor matters more here than on an ordinary run: a reference captured below it has the
        // analyzer's own high-pass in it, and that then divides into every measurement corrected
        // with it -- silently, and as though it were the DUT's.
        (double referenceStartHz, _) =
            StimulusFloor.Clamp(request.ChirpStartHz, session.MinStimulusFrequencyHz);

        var measurement = new ImpulseResponseRequest(
            request.FrequencySpanHz,
            request.ResolutionMultiplier,
            ThdSweepLevelMode.GeneratorVoltage,
            request.GeneratorLevelDbv,
            ThdSweepLevelUnit.Dbv,
            SmoothingDenominator: 0,
            AverageCount: Math.Max(ImpulseReferenceCaptureRequest.MinAverageCount, request.AverageCount),
            ReferenceMode: ImpulseReferenceMode.None,
            ReferenceCalibrationId: null,
            GatePreMs: ImpulseResponseRequest.DefaultGatePreMs,
            GatePostMs: ImpulseResponseRequest.DefaultGatePostMs,
            GateWindow: WindowType.Hann,
            ExtractHarmonics: false,
            MaxHarmonicOrder: ImpulseResponseRequest.DefaultMaxHarmonicOrder,
            Continuous: false,
            ChirpStartHz: referenceStartHz);

        await StartAsync(measurement, ct);

        var runTask = _runTask;
        if (runTask is not null)
            await runTask.ConfigureAwait(false);

        FrequencyResponseResult? result;
        ImpulseResponseResult? impulse;
        FrequencyResponsePhase phase;
        string? error;
        lock (_stateLock)
        {
            result = _lastLeftResult;
            impulse = _lastLeftImpulse;
            phase = _phase;
            error = _error;
        }

        if (phase != FrequencyResponsePhase.Finished || result is null || impulse is null)
            throw new InvalidOperationException(error ?? "The loopback capture did not complete.");

        VerifyLooksLikeALoopback(measurement, result);

        var reference = LoopbackReference.Capture(impulse, measurement.SampleRateHz);

        logger.LogInformation(
            "Loopback reference '{Name}' captured at {SampleRateHz:0} Hz: {Samples} samples, "
                + "analyzer round trip {Delay:0.0} samples",
            request.Name, measurement.SampleRateHz, reference.Ir.Length, reference.RemovedDelaySamples);

        return await referenceStore.SaveAsync(
            request.Name,
            reference,
            measurement.ChirpStartHz,
            measurement.ChirpEndHz,
            measurement.FftSize,
            session.Info?.SerialNumber,
            session.Info?.IsSimulated ?? false,
            ct);
    }

    /// <summary>
    /// Refuses a calibration that is not a straight wire. The two checks catch the two ways this
    /// goes wrong in practice: a DUT still in the path (gain nowhere near unity) and nothing in the
    /// path at all or a bad connection (no coherence).
    /// </summary>
    private static void VerifyLooksLikeALoopback(ImpulseResponseRequest request, FrequencyResponseResult result)
    {
        double binHz = request.BinHz;
        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(result.Magnitude.Length - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));

        if (lastBin <= firstBin)
            throw new InvalidOperationException("The loopback capture measured no usable band.");

        var gains = new List<double>(lastBin - firstBin + 1);
        double worstCoherence = 1.0;
        for (int k = firstBin; k <= lastBin; k++)
        {
            gains.Add(FrequencyResponseEstimator.ToDb(result.Magnitude[k]));
            worstCoherence = Math.Min(worstCoherence, result.Coherence[k]);
        }

        gains.Sort();
        double medianDb = gains[gains.Count / 2];

        if (Math.Abs(medianDb) > ReferenceUnityToleranceDb)
        {
            throw new InvalidOperationException(
                $"That does not look like a loopback: the median gain was {medianDb:0.0} dB, not unity. "
                    + "Patch the generator output straight to the analyzer input, with the DUT out of the path.");
        }

        if (worstCoherence < ReferenceMinCoherence)
        {
            throw new InvalidOperationException(
                $"The loopback capture was too noisy to use as a calibration (coherence fell to {worstCoherence:0.000}). "
                    + "Check the connection and try again.");
        }
    }

    private async Task RunAsync(ImpulseResponseRequest request, LoopbackReference? loopback, CancellationToken ct)
    {
        var acquisition = request.AsAcquisition();
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
                "Impulse response starting: span {SpanHz:0} Hz ({SampleRateHz:0} Hz sample rate), {FftSize} points "
                    + "({BinHz:0.00} Hz per bin, {RecordSeconds:0.000} s per record), {Averages} averages, "
                    + "level {LevelValue:0.###} {LevelUnit} ({LevelMode}), reference {ReferenceMode}, "
                    + "gate -{GatePre:0.##}/+{GatePost:0.##} ms ({GateHz:0.#} Hz limit)",
                request.FrequencySpanHz, request.SampleRateHz, request.FftSize, request.BinHz,
                request.FftSize / request.SampleRateHz, request.AverageCount, request.LevelValue,
                request.LevelUnit, request.LevelMode, request.ReferenceMode,
                request.GatePreMs, request.GatePostMs, request.GateResolutionHz);

            pipeline.Options = BuildOptions(savedOptions, request);

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.SampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // Manual range control means manual: with the Auto checkbox off the user has picked a
            // range deliberately, and a measurement quietly reprogramming it would be the same class
            // of surprise autoranging used to be before that checkbox existed.
            ownsInputRange = session.RunnerOwnsInputRange;

            if (ownsInputRange && session.AutoRangeStepDownEnabled is true)
            {
                // Step-down would walk off whichever range is chosen below within a second or two.
                // Step-up stays live throughout as the clip backstop.
                session.SetAutoRangeStepDownEnabled(false);
                stepDownSuppressed = true;
            }

            // Output-referred modes range from the target before probing the level, input-referred
            // modes probe the range after. The ordering is load-bearing and the reasoning is
            // FrequencyResponseRunner's — a range left too sensitive for the target clips the level
            // probe's own captures, which reads as missing gain and drives the generator up.
            if (ownsInputRange && request.SolvesGeneratorLevel)
            {
                await ApplyInputRangeAsync(
                    InputRangeSelector.For(session.Capabilities.InputRangesDbv, session.ToAnalyzerDbv(request.LevelDbv, StreamChannel.Left)), ct);
            }

            double generatorAmpDbv = await ResolveGeneratorAmplitudeAsync(request, ct);
            SetGeneratorAmpDbv(generatorAmpDbv);

            if (ownsInputRange && !request.SolvesGeneratorLevel)
                await ApplyInputRangeAsync(await ProbeInputRangeAsync(request, generatorAmpDbv, ct), ct);

            SetPhase(FrequencyResponsePhase.Measuring);

            var reference = ResponseCaptureSupport.BuildReferenceSpectrum(_fft, acquisition, generatorAmpDbv);

            // The calibration is folded into the *reference* rather than divided out of the response
            // afterwards. Three things follow from that and all of them are wanted: H1 then yields
            // H_dut directly, the estimator's own regulariser bounds the corrected reference's
            // near-zero bins (a later division would need a second one), and the anti-distortion gate
            // is applied to an already-corrected impulse response rather than to one still carrying
            // the analyzer's response.
            if (loopback is not null)
            {
                var href = loopback.SpectrumFor(request.FftSize, _fft);
                for (int k = 0; k < reference.Length; k++)
                    reference[k] *= href[k];
            }

            var generator = ResponseCaptureSupport.BuildGenerator(acquisition, generatorAmpDbv);

            // Configured once for the whole run, not once per pass: in continuous mode the stimulus
            // never changes between passes, so re-applying it would flush and discard a queue depth
            // of good audio on every repeat for nothing.
            bool generatorConfigured = false;

            do
            {
                ct.ThrowIfCancellationRequested();

                var pass = await MeasureOnceAsync(request, generatorAmpDbv, reference, generator, generatorConfigured, ct);
                generatorConfigured = true;
                passes++;

                if (request.Continuous && ownsInputRange)
                    await ApplyInputRangeAsync(InputRangeSelector.For(session.Capabilities.InputRangesDbv, pass.PeakDbv), ct);

                lock (_stateLock)
                {
                    _traces = pass.Traces;
                    _impulses = pass.Impulses;
                    _harmonics = pass.Harmonics;
                    _lastLeftResult = pass.LeftResult;
                    _lastLeftImpulse = pass.LeftImpulse;
                    _passesCompleted = passes;
                }

                if (passes == 1)
                {
                    logger.LogInformation(
                        "Impulse response measured: {Averages} records, {Delay}, gate keeps {Samples} samples "
                            + "({GateHz:0.#} Hz resolution limit), {Harmonics} harmonic curves{Continuing}",
                        request.AverageCount,
                        _latencySamples is { } delaySamples
                            ? $"DUT delay {delaySamples:0.00} samples"
                            : "no measured reference, so no delay",
                        pass.Impulses.Count > 0 ? pass.Impulses[0].FullSampleCount : 0,
                        request.GateResolutionHz,
                        pass.Harmonics.Count,
                        request.Continuous ? " (repeating until stopped)" : "");
                }
            }
            while (request.Continuous && !ct.IsCancellationRequested);

            if (request.Continuous)
                logger.LogInformation("Impulse response stopped after {Passes} passes", passes);

            terminalPhase = FrequencyResponsePhase.Finished;
        }
        catch (OperationCanceledException)
        {
            // The phase describes the data on screen, not the manner of stopping: a stopped
            // continuous run leaves the last *complete* pass behind, which is a whole measurement.
            terminalPhase = request.Continuous && passes > 0
                ? FrequencyResponsePhase.Finished
                : FrequencyResponsePhase.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impulse response failed");
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
                logger.LogWarning(ex, "Could not silence the generator after the impulse-response run");
            }

            // Before the acquisition stops, and unconditionally: the suppression is scoped to this
            // run, and leaving it set would hand the live view a ranger that can only ever get less
            // sensitive.
            if (stepDownSuppressed)
            {
                try
                {
                    session.SetAutoRangeStepDownEnabled(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not restore autorange step-down after the impulse-response run");
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
                    logger.LogWarning(ex, "Could not stop the acquisition the impulse-response run started");
                }
            }

            pipeline.Options = savedOptions;

            // Last, once everything above has unwound: `Running` is derived from the phase, so this
            // is what makes "no longer running" also mean "and has finished cleaning up".
            lock (_stateLock)
                _continuous = false;

            SetPhase(terminalPhase, terminalError);
        }
    }

    /// <summary>One complete measurement: fold <c>AverageCount</c> records into a fresh estimate, then analyse it.</summary>
    private sealed record PassResult(
        List<FrequencyResponseTrace> Traces,
        List<ImpulseTrace> Impulses,
        List<HarmonicCurve> Harmonics,
        double PeakDbv,
        FrequencyResponseResult LeftResult,
        ImpulseResponseResult LeftImpulse);

    private async Task<PassResult> MeasureOnceAsync(
        ImpulseResponseRequest request,
        double generatorAmpDbv,
        Complex[] reference,
        GeneratorConfig generator,
        bool generatorAlreadyConfigured,
        CancellationToken ct)
    {
        var acquisition = request.AsAcquisition();
        var gate = request.SweepGate;

        var left = new FrequencyResponseEstimator(request.FftSize, _fft, gate);
        var right = new FrequencyResponseEstimator(request.FftSize, _fft, gate);

        // Only when the right input is the reference. Estimating L against R directly, rather than
        // dividing the two synthetic-reference results, is what gives the gain trace its own honest
        // coherence — a ratio of two estimates has no coherence of its own.
        var gain = request.ReferenceMode == ImpulseReferenceMode.RightChannel
            ? new FrequencyResponseEstimator(request.FftSize, _fft, gate)
            : null;

        lock (_stateLock)
            _averagesTaken = 0;

        double peakDbv = double.NaN;

        // The record armed by the stimulus change is thrown away rather than measured, and that
        // costs one record per run. ConfigureGeneratorAsync discards the audio already queued for
        // the DAC (StimulusLatencyFrames), but that is the *play* latency, not the round trip back
        // through the DUT and the ADC, so some pre-change audio still reaches the front of the first
        // record. This measurement cannot tolerate that: it analyses with a Rectangular window on
        // the premise that the record holds exactly one period of the stimulus, and a record that is
        // part old chirp and part new one is a period of nothing.
        if (!generatorAlreadyConfigured)
        {
            await session.ConfigureGeneratorAsync(generator, ct);
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
            var (leftSpectrum, rightSpectrum) =
                ResponseCaptureSupport.RequireBothChannels(snapshots, request.FftSize, "impulse-response");

            // Discarded for left and right: against the synthesised chirp what comes back is the
            // record's arbitrary start point in the periodic stimulus, not a delay. Only the gain
            // estimator's is real, because both of its spectra come from the same capture.
            left.Add(reference, leftSpectrum);
            right.Add(reference, rightSpectrum);
            double? delaySamples = gain?.Add(rightSpectrum, leftSpectrum);

            lock (_stateLock)
            {
                _averagesTaken = record + 1;
                _latencySamples = delaySamples;
            }
        }

        SetPhase(FrequencyResponsePhase.Analyzing);

        // Off the polling thread: gating, three transforms and up to four packet extractions at the
        // full analysis size is not instant, and the status endpoint has to stay answerable.
        return await Task.Run(() => Analyze(request, acquisition, generatorAmpDbv, left, right, gain, peakDbv), ct);
    }

    private PassResult Analyze(
        ImpulseResponseRequest request,
        FrequencyResponseRequest acquisition,
        double generatorAmpDbv,
        FrequencyResponseEstimator left,
        FrequencyResponseEstimator right,
        FrequencyResponseEstimator? gain,
        double peakDbv)
    {
        // Before Complete(), which gates its own working spectrum in place. CompleteImpulse is
        // immune to that by construction, but taking the impulse first keeps the dependency invisible
        // rather than load-bearing.
        var leftImpulse = left.CompleteImpulse();
        var gainImpulse = gain?.CompleteImpulse();

        // Completed exactly once each, here, and the results passed on. Complete() gates its working
        // spectrum in place and is not idempotent, so this measurement — which needs both the curves
        // and the left channel's magnitudes, as the denominator of every dBc figure — has to hold the
        // results rather than hand the estimators to two different consumers.
        var leftResult = left.Complete();
        var traces = ResponseCaptureSupport.BuildTraces(
            acquisition, generatorAmpDbv, DisplayColumns, leftResult, right.Complete(), gain?.Complete());

        var impulses = new List<ImpulseTrace>
        {
            BuildImpulseTrace(request, FrequencyResponseTraceKind.Left, leftImpulse),
        };

        if (gainImpulse is not null)
            impulses.Add(BuildImpulseTrace(request, FrequencyResponseTraceKind.Gain, gainImpulse));

        var harmonics = new List<HarmonicCurve>();
        if (request.ExtractHarmonics)
        {
            var extractor = new HarmonicPacketExtractor(
                request.FftSize, request.SampleRateHz, request.ChirpStartHz, request.ChirpEndHz, _packetFft);

            foreach (var packet in extractor.Extract(
                leftImpulse.Ungated, leftImpulse.PeakIndex, leftResult.Magnitude, request.MaxHarmonicOrder))
            {
                harmonics.Add(BuildHarmonicCurve(packet));
            }
        }

        return new PassResult(traces, impulses, harmonics, peakDbv, leftResult, leftImpulse);
    }

    private static ImpulseTrace BuildImpulseTrace(
        ImpulseResponseRequest request, FrequencyResponseTraceKind kind, ImpulseResponseResult impulse) =>
        BuildImpulseTrace(
            kind, impulse.Gated, impulse.PeakIndex,
            request.GatePreMs, request.GatePostMs, request.SampleRateHz, request.GateWindow);

    /// <summary>
    /// The display trace of an impulse response: gated about the peak with the user's window, then
    /// min/max decimated. Public and free of the runner's state so a <i>stored</i> result — which keeps
    /// the raw peak-centred IR, not this trace — is drawn back through exactly this code (see
    /// <c>GET /api/measurements/{id}/impulse-trace</c>), rather than a client-side imitation of the
    /// half-window gate.
    /// </summary>
    public static ImpulseTrace BuildImpulseTrace(
        FrequencyResponseTraceKind kind, ReadOnlySpan<double> ir, int peakIndex,
        double gatePreMs, double gatePostMs, double sampleRateHz, WindowType gateWindow)
    {
        var gated = ImpulseWindow.WindowIr(ir, peakIndex, gatePreMs, gatePostMs, sampleRateHz, gateWindow);

        int columns = Math.Min(ImpulseDisplayColumns, gated.Samples.Length);
        var minMax = new MinMax[columns];

        // Linear, not log: the x axis here is time, and log columns would crush the whole decay tail
        // into the last few pixels. Min/max rather than a mean for the usual reason, and it matters
        // more here than anywhere — a reflection can be one sample wide.
        SpectrumDecimator.DecimateLinear(gated.Samples, minMax);

        var timeS = new float[columns];
        var minVolts = new float[columns];
        var maxVolts = new float[columns];

        double peakVolts = 0.0;
        foreach (double sample in gated.Samples)
            peakVolts = Math.Max(peakVolts, Math.Abs(sample));

        for (int c = 0; c < columns; c++)
        {
            double centreSample = (c + 0.5) * gated.Samples.Length / columns;
            timeS[c] = (float)(gated.FirstSampleTimeS + (centreSample / sampleRateHz));
            minVolts[c] = (float)minMax[c].Min;
            maxVolts[c] = (float)minMax[c].Max;
        }

        return new ImpulseTrace(
            kind, timeS, minVolts, maxVolts, peakVolts, sampleRateHz,
            gated.Samples.Length, gatePreMs, gatePostMs, gated.ResolutionLimitHz);
    }

    /// <summary>
    /// One harmonic curve, thinned to something a chart can draw. The packet's own grid is linear in
    /// frequency while the chart's axis is logarithmic, so the columns are picked log-spaced across
    /// the curve's band and each carries the min and max of the source points that fell in it.
    /// </summary>
    private static HarmonicCurve BuildHarmonicCurve(HarmonicPacket packet)
    {
        int count = packet.FundamentalHz.Length;
        if (count == 0)
            return new HarmonicCurve(packet.Order, [], [], packet.UpperValidHz);

        int columns = Math.Min(DisplayColumns, count);

        var fundamentalHz = new float[columns];
        var levelDbc = new float[columns];

        double lowHz = packet.FundamentalHz[0];
        double highHz = packet.FundamentalHz[count - 1];
        double logLow = Math.Log(lowHz);
        double logSpan = Math.Log(highHz) - logLow;

        int source = 0;
        for (int c = 0; c < columns; c++)
        {
            double edgeHz = logSpan > 0.0
                ? Math.Exp(logLow + (logSpan * (c + 1) / columns))
                : highHz;

            int start = source;
            while (source < count - 1 && packet.FundamentalHz[source] < edgeHz)
                source++;

            // Never an empty column: at the sparse (low-frequency) end several columns can want the
            // same source point, and a column with nothing in it would draw as a gap.
            int end = Math.Max(source, start);

            double peak = packet.LevelDbc[start];
            for (int i = start; i <= end && i < count; i++)
                peak = Math.Max(peak, packet.LevelDbc[i]);

            int centre = Math.Min((start + end) / 2, count - 1);
            fundamentalHz[c] = (float)packet.FundamentalHz[centre];
            levelDbc[c] = (float)peak;
        }

        return new HarmonicCurve(packet.Order, fundamentalHz, levelDbc, packet.UpperValidHz);
    }

    /// <summary>
    /// Measures the input range this DUT needs, for the input-voltage mode where nothing is known
    /// about what comes back: park on the least sensitive range, play the chirp at the real
    /// generator level, capture once, and pick the range from the peak.
    /// </summary>
    private async Task<double> ProbeInputRangeAsync(
        ImpulseResponseRequest request, double generatorAmpDbv, CancellationToken ct)
    {
        var acquisition = request.AsAcquisition();
        var ranges = session.Capabilities.InputRangesDbv;

        SetPhase(FrequencyResponsePhase.DeterminingRange);

        int probeFftSize = Math.Min(ProbeFftSize, request.FftSize);

        // Least sensitive range first. An unknown DUT can be doing anything, and a probe that clips
        // reads a peak that is a property of the range rather than of the signal — which would then
        // choose that same range again and stay wrong.
        await ApplyInputRangeAsync(ranges.Max(), ct);

        pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
        await session.ConfigureGeneratorAsync(
            ResponseCaptureSupport.BuildGenerator(acquisition, generatorAmpDbv, probeFftSize), ct);
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
    /// Pipeline settings for a chirp capture. Deliberately its own copy of
    /// <c>FrequencyResponseRunner.BuildOptions</c> rather than a shared one: it is identical today
    /// and must be free to stop being so without acquiring a boolean that reads as a mode switch.
    /// </summary>
    private static SpectrumPipelineOptions BuildOptions(SpectrumPipelineOptions current, ImpulseResponseRequest request) =>
        current with
        {
            FftSize = request.FftSize,

            // Exact for a stimulus that is periodic in the record; anything else is wrong here.
            Window = WindowType.Rectangular,

            // The runner averages the records itself, in the complex domain and after removing each
            // one's own rotation. The pipeline's averager works on magnitudes, which would throw away
            // the phase this measurement is built on.
            Averaging = AveragingMode.Off,
            AverageCount = 1,
            AverageHopFraction = 0.0,

            ProcessLeft = true,
            ProcessRight = true,
            SingleShot = true,
            RetainComplexSpectrum = true,
            // A runner measures unweighted whatever the FFT screen has selected: a sweep's noise
            // and THD+N figures are defined over the plain band, and the weighting is restored
            // with the rest of the saved options afterwards.
            Weighting = WeightingSpec.None,

            // Nothing here reads a preview, and at this FFT size it is not free.
            CapturePreview = false,
        };

    /// <summary>The generator level to measure at — identical in intent and arithmetic to the frequency response's.</summary>
    private async Task<double> ResolveGeneratorAmplitudeAsync(ImpulseResponseRequest request, CancellationToken ct)
    {
        if (!request.SolvesGeneratorLevel)
            return request.LevelDbv;

        var acquisition = request.AsAcquisition();

        SetPhase(FrequencyResponsePhase.DeterminingLevel);

        double target = request.LevelDbv;
        double amplitude = ProbeStartDbv;

        int probeFftSize = Math.Min(ProbeFftSize, request.FftSize);

        bool ceilingAsked = false;

        for (int pass = 0; pass < MaxProbeIterations; pass++)
        {
            ct.ThrowIfCancellationRequested();
            SetGeneratorAmpDbv(amplitude);

            pipeline.Options = pipeline.Options with { FftSize = probeFftSize };
            await session.ConfigureGeneratorAsync(
                ResponseCaptureSupport.BuildGenerator(acquisition, amplitude, probeFftSize), ct);
            await WaitForCaptureAsync(request, probeFftSize, ct);

            var probeReference = ResponseCaptureSupport.BuildReferenceSpectrum(_fft, acquisition, amplitude, probeFftSize);
            var (probeLeft, _) = ResponseCaptureSupport.RequireBothChannels(
                pipeline.LatestSnapshots(), probeFftSize, "impulse-response");

            // At the acquisition's bandwidth reference, which for an impulse request is the default
            // passband (200 Hz - 2 kHz): "0 dBV out" means 0 dBV there, not the whole-band median.
            double gainDb = ResponseCaptureSupport.ProbeGainDb(probeReference, probeLeft, probeFftSize, acquisition, out _);
            if (double.IsNaN(gainDb))
                throw new InvalidOperationException("The level probe measured no signal — check the DUT is connected and powered.");

            // Deconvolved, not read off a bin: a chirp spreads its energy over thousands of bins, so
            // any single bin sits far below the sweep's own amplitude and correcting toward the
            // target from one drives the generator up by that whole difference.
            double measuredDbv = amplitude + gainDb;

            // Per iteration, because how the probe converged is otherwise invisible: it reports only
            // the level it settled on, so a run that reached the generator's ceiling gives no clue
            // whether the DUT really is that quiet or one capture read as silence.
            logger.LogInformation(
                "Level probe iteration {Pass}: generator {Amplitude:0.##} dBV, passband gain {GainDb:0.##} dB, "
                    + "measured {MeasuredDbv:0.##} dBV against a target of {Target:0.##} dBV",
                pass, amplitude, gainDb, measuredDbv, target);

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

        pipeline.Options = pipeline.Options with { FftSize = request.FftSize };
        return amplitude;
    }

    private async Task WaitForCaptureAsync(ImpulseResponseRequest request, int fftSize, CancellationToken ct)
    {
        var timeout = ResponseCaptureSupport.CaptureTimeout(request.SampleRateHz, fftSize, session.CaptureDelayMs);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"An impulse-response record did not complete within {timeout.TotalSeconds:0.0} s. "
                        + "The device may have stopped streaming.");
            }

            await Task.Delay(CapturePollInterval, ct).ConfigureAwait(false);
        }
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
