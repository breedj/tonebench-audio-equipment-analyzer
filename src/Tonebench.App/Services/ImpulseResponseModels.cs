using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// What an impulse-response run divides the captured response by — which decides what the result is
/// a response <em>of</em>.
/// </summary>
public enum ImpulseReferenceMode
{
    /// <summary>
    /// The synthesised chirp alone. The result includes the analyzer's own converters and filters,
    /// and its rotation figure is the record's arbitrary start point in the periodic stimulus rather
    /// than a delay — see <see cref="FrequencyResponseEstimator.Add"/>.
    /// </summary>
    None = 0,

    /// <summary>
    /// The right input, fed the same stimulus. Cancels generator-side error, and because both spectra
    /// come from one capture the recovered rotation really is the DUT's propagation delay. Needs the
    /// right input wired to the stimulus, and it cancels nothing on the ADC side.
    /// </summary>
    RightChannel = 1,

    /// <summary>
    /// A stored loopback calibration, multiplied into the reference spectrum. Cancels the analyzer's
    /// own response — which is what the band edges are otherwise reporting — and leaves the right
    /// input free to measure something of its own.
    /// </summary>
    StoredLoopback = 2,
}

/// <summary>
/// One decimated impulse response, ready for the chart. Time is relative to the linear impulse, so
/// index 0 is <c>-GatePreMs</c> and the peak sits at t = 0.
///
/// <para>Min and max per column rather than a mean, and <b>both</b> must be drawn: an impulse
/// response is a waveform whose interesting features are single-sample excursions, and a column that
/// averaged them would erase exactly what the measurement is for. Decimated <i>linearly</i>, unlike
/// every spectrum in this app — the x axis here is time, and a log grid on it would crush the decay
/// tail into the last few columns.</para>
/// </summary>
public sealed record ImpulseTrace(
    FrequencyResponseTraceKind Kind,
    float[] TimeS,
    float[] MinVolts,
    float[] MaxVolts,
    double PeakVolts,
    double SampleRateHz,
    int FullSampleCount,
    double GatePreMs,
    double GatePostMs,
    double ResolutionLimitHz);

/// <summary>
/// One harmonic order's distortion against fundamental frequency, decimated for display.
/// </summary>
/// <param name="UpperValidHz">
/// Where this curve stops, which differs per order: the k-th harmonic can only be observed for
/// fundamentals up to the sweep's end divided by k. A chart that ran every curve to the same
/// right-hand edge would be drawing frequencies nothing measured.
/// </param>
public sealed record HarmonicCurve(
    int Order,
    float[] FundamentalHz,
    float[] LevelDbc,
    double UpperValidHz);

/// <summary>
/// An impulse-response run's configuration, flat and immutable, mirroring
/// <see cref="FrequencyResponseRequest"/>'s role.
///
/// <para><b>The acquisition half is not re-validated here.</b> A chirp is a chirp: the span list,
/// the resolution multipliers, the level-mode and watt/load rules, the averaging bounds and the
/// decade rule between the chirp's start and end are the frequency-response measurement's rules and
/// must stay identical, so <see cref="Validate"/> hands that half to
/// <see cref="FrequencyResponseRequest.Validate"/> through <see cref="AsAcquisition"/> rather than
/// restating it. The derived sweep arithmetic goes the same way, for the same reason — two copies of
/// <c>FftSize</c> that drifted would silently analyse a different record than the one the generator
/// played.</para>
/// </summary>
/// <param name="ReferenceCalibrationId">
/// Required by, and only meaningful for, <see cref="ImpulseReferenceMode.StoredLoopback"/>.
/// </param>
/// <param name="GatePreMs">
/// Kept before the impulse. This is the <b>display</b> gate — reflections and resolution — and is
/// always a subset of the anti-distortion <see cref="SweepGate"/>. See <see cref="ImpulseWindow"/>.
/// </param>
/// <param name="Continuous">
/// Keep re-measuring until stopped. Each pass is a whole independent measurement, exactly as the
/// chirp response's is.
/// </param>
public sealed record ImpulseResponseRequest(
    double FrequencySpanHz,
    int ResolutionMultiplier,
    ThdSweepLevelMode LevelMode,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    int SmoothingDenominator,
    int AverageCount,
    ImpulseReferenceMode ReferenceMode,
    long? ReferenceCalibrationId,
    double GatePreMs,
    double GatePostMs,
    WindowType GateWindow,
    bool ExtractHarmonics = true,
    // Literals, not the consts below: a primary constructor's default cannot reference a member of
    // the type it is declaring. Kept equal to them by ImpulseResponseRequestTests.
    int MaxHarmonicOrder = 5,
    bool Continuous = false,
    double ChirpStartHz = 2.0,
    double LoadOhms = 8.0,
    ResponseStimulus Stimulus = ResponseStimulus.Chirp,
    double NoiseCutoffHz = 5.0,
    /// <summary>The capture clock's offset from the playback clock in ppm — see
    /// <see cref="FrequencyResponseRequest.ClockDriftPpm"/>, which this is carried to.</summary>
    double ClockDriftPpm = 0.0)
{
    public const double DefaultGatePreMs = 1.0;

    public const double DefaultGatePostMs = 20.0;

    public const int MinHarmonicOrder = 2;

    public const int MaxSupportedHarmonicOrder = 5;

    public const int DefaultMaxHarmonicOrder = 5;

    /// <summary>
    /// Gate windows the UI offers. <see cref="WindowType.Rectangular"/> is deliberately among them:
    /// a bare cut is what isolates a reflection exactly, and it is a legitimate choice rather than a
    /// degenerate one. Flat-top is not — it is a window for reading amplitudes off a spectrum, and
    /// it has no meaning as a gate.
    ///
    /// <para>The two Kaisers are here for the opposite reason to the rectangular one: a gate's
    /// taper sets how much of a late reflection leaks back into the gated response, and α = 5 and
    /// α = 7 are two more rungs of that ladder past Blackman-Harris (−94, −120 and −171 dB of
    /// sidelobe suppression) at the usual cost in mainlobe width. Nothing about
    /// <see cref="ImpulseWindow.WindowIr"/> is specific to a cosine sum — it cuts half of whatever
    /// cached coefficient array <see cref="Window.Get"/> returns — so they work here unchanged.</para>
    /// </summary>
    public static readonly WindowType[] SupportedGateWindows =
        [WindowType.Rectangular, WindowType.Hann, WindowType.BlackmanHarris, WindowType.Kaiser5, WindowType.Kaiser7];

    /// <summary>
    /// The acquisition half, as the frequency-response measurement would express it. The one place
    /// the sweep's rules and arithmetic live.
    /// </summary>
    public FrequencyResponseRequest AsAcquisition() =>
        new(FrequencySpanHz,
            ResolutionMultiplier,
            LevelMode,
            LevelValue,
            LevelUnit,
            ReferenceMode == ImpulseReferenceMode.RightChannel,
            SmoothingDenominator,
            AverageCount,
            Continuous,
            ChirpStartHz,
            LoadOhms,
            Stimulus: Stimulus,
            NoiseCutoffHz: NoiseCutoffHz,
            ClockDriftPpm: ClockDriftPpm);

    public double SampleRateHz => AsAcquisition().SampleRateHz;

    public int FftSize => AsAcquisition().FftSize;

    public double BinHz => AsAcquisition().BinHz;

    public double ChirpEndHz => AsAcquisition().ChirpEndHz;

    public ChirpClockDrift? ClockDrift => AsAcquisition().ClockDrift;

    public double AnalysisLowHz => AsAcquisition().AnalysisLowHz;

    public double AnalysisHighHz => AsAcquisition().AnalysisHighHz;

    public double LevelDbv => AsAcquisition().LevelDbv;

    public bool SolvesGeneratorLevel => AsAcquisition().SolvesGeneratorLevel;

    /// <summary>
    /// The estimator's anti-distortion gate — fixed by the sweep geometry, not by the user, and not
    /// the same thing as <see cref="GatePreMs"/>/<see cref="GatePostMs"/>. See
    /// <see cref="ImpulseWindow"/>, which explains why confusing the two is worth a paragraph.
    /// </summary>
    public ImpulseGate SweepGate => Stimulus == ResponseStimulus.Chirp
        ? ImpulseGate.ForSweep(FftSize, ChirpStartHz, ChirpEndHz)
        : ImpulseGate.ForNoise(FftSize);

    /// <summary>
    /// Whether this run can draw harmonic curves — which only a sweep can, its distortion arriving
    /// as packets at known places. Read in place of <see cref="ExtractHarmonics"/> by everything
    /// that acts on it, so that switching the stimulus cannot produce a request that is refused:
    /// the box is simply not offered against a noise stimulus, and a stored measurement that has it
    /// set reads as off rather than as an error.
    /// </summary>
    public bool ExtractsHarmonics => ExtractHarmonics && Stimulus == ResponseStimulus.Chirp;

    /// <summary>What the display gate's length implies about the lowest frequency the gated magnitude can describe.</summary>
    public double GateResolutionHz => 1000.0 / (GatePreMs + GatePostMs);

    public void Validate()
    {
        AsAcquisition().Validate();

        if (ReferenceMode is not (ImpulseReferenceMode.None or ImpulseReferenceMode.RightChannel or ImpulseReferenceMode.StoredLoopback))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReferenceMode), ReferenceMode, "Unknown reference mode.");
        }

        if (ReferenceMode == ImpulseReferenceMode.StoredLoopback && ReferenceCalibrationId is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReferenceCalibrationId), "A stored-loopback run has to say which calibration to use.");
        }

        if (double.IsNaN(GatePreMs) || GatePreMs <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(GatePreMs), GatePreMs, "The gate must keep something before the impulse.");

        if (double.IsNaN(GatePostMs) || GatePostMs <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(GatePostMs), GatePostMs, "The gate must keep something after the impulse.");

        // A display gate wider than the sweep gate would keep the harmonic packets the sweep gate
        // exists to delete, and the result would be combed by the DUT's own distortion.
        var (maxPreMs, maxPostMs) = ImpulseWindow.Bounds(FftSize, SampleRateHz, ChirpStartHz, ChirpEndHz);

        if (GatePreMs > maxPreMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GatePreMs), GatePreMs,
                $"At this resolution the gate can keep at most {maxPreMs:0.###} ms before the impulse, "
                + "or it would keep the harmonic distortion packets with it.");
        }

        if (GatePostMs > maxPostMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GatePostMs), GatePostMs,
                $"At this resolution the gate can keep at most {maxPostMs:0.###} ms after the impulse.");
        }

        if (!SupportedGateWindows.Contains(GateWindow))
        {
            throw new ArgumentOutOfRangeException(
                nameof(GateWindow), GateWindow,
                $"The gate window must be one of {string.Join(", ", SupportedGateWindows)}.");
        }

        if (MaxHarmonicOrder is < MinHarmonicOrder or > MaxSupportedHarmonicOrder)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxHarmonicOrder), MaxHarmonicOrder,
                $"Harmonic orders run from {MinHarmonicOrder} to {MaxSupportedHarmonicOrder}.");
        }

        // The k-th harmonic can only be observed for fundamentals up to f2/k, so a sweep short enough
        // that even the second harmonic has no band left would produce an empty curve rather than an
        // error the user could act on.
        if (ExtractHarmonics && ChirpEndHz / MinHarmonicOrder <= AnalysisLowHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExtractHarmonics),
                "This sweep is too narrow for harmonic extraction: even the second harmonic has no fundamental band left.");
        }
    }
}

/// <summary>What a client polling <c>GET /api/impulse-response/status</c> sees.</summary>
/// <remarks>
/// A record of its own rather than a widened <see cref="FrequencyResponseStatus"/>. That one is
/// already shared with the stepped-sine response and already carries two fields
/// (<c>PointsMeasured</c>/<c>PointCount</c>) that exist only for one of its two users; adding
/// impulses and harmonic curves to it would put three more on every stepped-sine poll.
/// </remarks>
/// <param name="LatencySamples">
/// The DUT's propagation delay, or null when nothing measured it — which is every mode except
/// <see cref="ImpulseReferenceMode.RightChannel"/>.
/// </param>
public sealed record ImpulseResponseStatus(
    bool Running,
    FrequencyResponsePhase Phase,
    bool Continuous,
    int PassesCompleted,
    int AveragesTaken,
    int AverageCount,
    double GeneratorAmpDbv,
    double BinHz,
    double SampleRateHz,
    int FftSize,
    double? LatencySamples,
    string? Error,
    IReadOnlyList<FrequencyResponseTrace> Traces,
    IReadOnlyList<ImpulseTrace> Impulses,
    IReadOnlyList<HarmonicCurve> Harmonics,
    ImpulseReferenceMode ReferenceMode,
    string? ReferenceCalibrationName,
    double GateResolutionHz,
    double GatePreMs,
    double GatePostMs,
    /// <summary>
    /// The stimulus the run on screen played, or null where nothing has run yet.
    /// </summary>
    /// <remarks>
    /// <para>Carried so the Harmonics view can say why it is empty. Harmonic curves are extracted
    /// from a sweep's own deconvolution residue — see <see cref="ImpulseResponseRequest.ExtractsHarmonics"/>
    /// — so every noise stimulus produces none, which since Pink PN became the default (2026-10-24)
    /// is what a new measurement does. Reported 2026-10-26 as the harmonics display not working
    /// any more, with the Autofit button disabled beside it: both were correct and neither said
    /// so.</para>
    /// <para>The run's own stimulus and not the panel's staged one, which is the whole point:
    /// turning the dropdown after a run must not relabel what that run did.</para>
    /// </remarks>
    ResponseStimulus? Stimulus,
    /// <summary>
    /// The clock drift this run still shows, where it could be read — chirp only. See
    /// <see cref="DriftResidualReading"/>; the frequency-response screen carries the same field for
    /// the same reason, and the two panels word it through one client-side module.
    /// </summary>
    DriftResidualReading? DriftResidual = null,
    /// <summary>
    /// What the reference input was carrying, on a run whose reference mode is the right channel.
    /// See <see cref="DriftResidualReading"/>'s own neighbour on the frequency-response status.
    /// </summary>
    ReferenceChannelReading? Reference = null);

/// <summary>The immediate answer to a start request — what the run will do, before it does it.</summary>
public sealed record ImpulseResponseStarted(
    double SampleRateHz,
    int FftSize,
    double BinHz,
    int AverageCount,
    double RecordSeconds,
    bool Continuous,
    double GateResolutionHz);

/// <summary>A stored loopback calibration, as the UI lists it.</summary>
public sealed record LoopbackReferenceInfo(
    long Id,
    string Name,
    DateTime TakenUtc,
    double SampleRateHz,
    double ChirpStartHz,
    double ChirpEndHz,
    double RemovedDelaySamples,
    int IrSamples,
    string? DeviceSerial,
    bool IsSimulated);
