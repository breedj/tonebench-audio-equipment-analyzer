using Tonebench.Dsp;

namespace Tonebench.App.Streaming;

/// <summary>Live-reconfigurable knobs for <see cref="SpectrumPipeline"/>. Reassign <see cref="SpectrumPipeline.Options"/> to change these mid-stream.</summary>
public sealed record SpectrumPipelineOptions
{
    /// <summary>Power-of-two FFT length. Larger values raise frequency resolution and per-tick CPU cost, not frame rate — see <see cref="TargetFrameRateHz"/>.</summary>
    public int FftSize { get; init; } = 65536;

    public WindowType Window { get; init; } = WindowType.Hann;

    public AveragingMode Averaging { get; init; } = AveragingMode.Linear;

    /// <summary>Spectra averaged together (linear) or the effective time constant (exponential, alpha = 2/(count+1)). Ignored when <see cref="Averaging"/> is <see cref="AveragingMode.Off"/>.</summary>
    public int AverageCount { get; init; } = 8;

    /// <summary>
    /// Fresh signal, as a fraction of one FFT record, that must arrive between two accepted
    /// averages. 0 keeps the historical behaviour (average on every display tick); 0.5 is textbook
    /// Welch 50 % overlap; 1.0 makes consecutive averages share no samples at all.
    ///
    /// <para><b>Why this exists.</b> Averaging was paced by <see cref="TargetFrameRateHz"/>, which
    /// has no relationship to the record length, so how much an "average" actually added was an
    /// accident of the FFT size. At 131072 points and 192 kHz the record is 683 ms while ticks come
    /// every 50 ms, so eight consecutive averages shared ~93 % of their samples and spanned about
    /// 1.5 records of independent signal — roughly a sqrt(1.5) noise improvement for eight averages'
    /// worth of waiting. Below about 9600 points the opposite happened: the record was shorter than
    /// the tick, so samples between records were never analyzed at all.</para>
    ///
    /// <para>Left at 0 by default so the live spectrum view keeps updating at the display rate,
    /// where responsiveness matters more than the statistics of the average. The sweep runner sets
    /// it, because there an average that adds nothing is time added to every point.</para>
    ///
    /// <para>Cost scales the way you would expect: N averages at hop h take
    /// <c>(1 + (N-1)·h)</c> record lengths. Eight averages are 4.5 records at 0.5, and 8 at 1.0.</para>
    /// </summary>
    public double AverageHopFraction { get; init; } = 0.0;

    /// <summary>
    /// When true, each channel stops updating once it reaches <see cref="AverageCount"/> averages
    /// (or, with <see cref="Averaging"/> set to <see cref="AveragingMode.Off"/>, after its first
    /// captured spectrum) — the pipeline holds that final frame instead of continuing to reprocess
    /// new samples, and flags the completing meters frame with <see cref="StreamFlags.Held"/>.
    /// Mirrors a bench analyzer's "Single" sweep button: start, capture settles, then stops on its
    /// own instead of free-running.
    ///
    /// Not a persisted UI setting — it is whatever the last POST /api/acquisition/start asked for
    /// (<c>singleShot: true</c> is the "Single" button, anything else is Run), so a normal
    /// continuous run is never left holding from a previous Single press. The start's own flushes
    /// are what arm the capture; there must be no separate "arm" flush after the start, because a
    /// bare <see cref="SpectrumPipeline.Flush"/> zeroes the capture-delay discard the start just
    /// booked and puts the DUT's settle back inside the first record. Off by default
    /// (continuous/free-run).
    ///
    /// A count of 1 completes after one record whatever <see cref="Averaging"/> says, the same way
    /// <see cref="SpectrumAverager"/> treats <c>count &lt;= 1</c> as no averaging.
    /// </summary>
    public bool SingleShot { get; init; } = false;

    /// <summary>Decimated columns sent per frame, log-spaced across [<see cref="DisplayLoFreqHz"/>, Nyquist].</summary>
    public int DisplayColumns { get; init; } = 1600;

    public double DisplayLoFreqHz { get; init; } = 10.0;

    /// <summary>
    /// Display tick rate. A <see cref="PeriodicTimer"/> paces the worker at this rate regardless
    /// of <see cref="FftSize"/>; if one tick's FFT+encode+broadcast work runs long, the timer
    /// simply does not queue up missed ticks — the next tick fires at its own scheduled time,
    /// which is what decouples display rate from both acquisition rate and FFT cost.
    /// </summary>
    public int TargetFrameRateHz { get; init; } = 20;

    /// <summary>
    /// Harmonics reported per meters frame, order 1 (fundamental) through this order — 12 gives
    /// fundamental + H2..H12, matching QuantAsylum's own convention (their THD sums H2..H12; see
    /// reference/previous version/QA40x_AUDIO_ANALYSER/frmThdFrequency.cs).
    /// </summary>
    public int HarmonicCount { get; init; } = 12;

    /// <summary>
    /// Lower edge of the measurement bandwidth for THD+N, SNR and the noise floor, in Hz.
    ///
    /// Not a display setting — this is what those figures are integrated over, and quoting any of
    /// them without a bandwidth is meaningless. The default is the standard 20 Hz - 20 kHz audio
    /// band. Critically it also keeps DC out: on a DC-coupled input a few mV of offset lives in
    /// bin 0, and integrating it reported THD+N ~33 dB worse than reality.
    /// </summary>
    public double MeasurementLoFreqHz { get; init; } = 20.0;

    /// <summary>
    /// Upper edge of the measurement bandwidth for THD+N, SNR and the noise floor, in Hz. Clamped
    /// to Nyquist. Also bounds which harmonics count toward THD, matching QuantAsylum's
    /// <c>/ThdDb/{freq}/{stopHz}</c>.
    /// </summary>
    public double MeasurementHiFreqHz { get; init; } = 20000.0;

    /// <summary>
    /// Upper bound, in Hz, on which harmonics count toward THD — overriding
    /// <see cref="MeasurementHiFreqHz"/> for that one purpose. Null (the default) means "use
    /// MeasurementHiFreqHz", which is QuantAsylum's own <c>/ThdDb/{freq}/{stopHz}</c> convention
    /// and what the live FFT view wants.
    ///
    /// The THD-vs-frequency sweep sets this to Nyquist instead. Bounded at the 20 kHz measurement
    /// band, every harmonic of a sweep point above ~7 kHz is out of band, so <c>Analyze</c> writes
    /// the fundamental and nothing else and THD reads exactly 0 — the top third of the sweep would
    /// plot as a cliff into the floor rather than a measurement. Noise, THD+N and SNR deliberately
    /// keep using <see cref="MeasurementLoFreqHz"/>/<see cref="MeasurementHiFreqHz"/>: those are
    /// integrated figures and quoting one without a stated bandwidth is meaningless.
    /// </summary>
    public double? HarmonicMaxFreqHz { get; init; }

    /// <summary>
    /// Half-width, in octaves, of the notch taken out around the fundamental before integrating
    /// THD+N/SNR — see <see cref="SpectrumMetrics.DefaultNotchOctaves"/>. 0.5 matches QuantAsylum's
    /// PyQa40x. Lower it only if you need to resolve noise very close to the carrier.
    /// </summary>
    public double NotchOctaves { get; init; } = SpectrumMetrics.DefaultNotchOctaves;

    /// <summary>
    /// Lower bound, in Hz, for locating the fundamental by peak search. Excludes DC and mains
    /// hum (50/60 Hz and its low harmonics) from being mistaken for the fundamental.
    /// </summary>
    public double FundamentalMinSearchHz { get; init; } = 200.0;

    /// <summary>
    /// Non-harmonic peaks (mains hum, IMD/mixing products, other spurs) reported per meters frame,
    /// strongest first, excluding the fundamental and its harmonics. Scanned from
    /// <see cref="DisplayLoFreqHz"/>, not <see cref="FundamentalMinSearchHz"/> — hum at 50/100 Hz
    /// is exactly the kind of thing this list exists to surface.
    /// </summary>
    public int OtherPeaksCount { get; init; } = 20;

    /// <summary>
    /// Whether the left/right channel is processed (FFT + meters) and broadcast at all. The
    /// device still always samples both channels — a stereo ADC can't do otherwise — this only
    /// controls DSP/broadcast, i.e. what the UI's channel checkboxes actually mean.
    /// </summary>
    public bool ProcessLeft { get; init; } = true;

    // Only the left channel is on out of the box — matching the generator's own left-only default
    // (see MeasurementSaveService.BuildParameters) so a fresh measurement has one clean channel to
    // look at rather than an unused right channel cluttering the chart.
    public bool ProcessRight { get; init; } = false;

    /// <summary>
    /// When true, every tick also decimates a small FFT and oscilloscope preview (at most
    /// <see cref="SpectrumPipeline.PreviewMaxColumns"/> points each) into the retained snapshot —
    /// see <see cref="SpectrumSnapshot.PreviewFft"/>/<see cref="SpectrumSnapshot.PreviewScope"/>.
    ///
    /// Off by default: the live FFT/scope charts already stream their own full-rate frames over the
    /// WebSocket, so computing a second, smaller decimation of the same tick for them would be pure
    /// waste. <see cref="ThdSweepRunner"/> turns this on because a sweep point's capture is
    /// discarded (the ring buffer moves on to the next frequency) the instant
    /// <see cref="SpectrumPipeline.LatestSnapshots"/> is read -- unlike the live view, there is no
    /// second chance to ask the device what a given frequency's waveform looked like, so the sweep
    /// preview has to be taken at the only moment the samples exist.
    /// </summary>
    public bool CapturePreview { get; init; } = false;

    /// <summary>
    /// Also send, per tick and channel, the scope tail with its fundamental fitted and subtracted
    /// (<see cref="Tonebench.Dsp.SineFit"/>): the THD residual, on which crossover spikes and harmonic
    /// residue are visible in the time domain. A second scope frame flagged
    /// <see cref="StreamFlags.ScopeResidual"/>, sent after the raw one so the client can trigger on
    /// the raw and slice the residual at the same offset. Off by default -- it is a fit over the
    /// whole record per channel per tick, and one more frame on the wire, for a view most ticks do
    /// not show. Needs a fundamental; a tick without one sends no residual.
    /// </summary>
    public bool ScopeResidual { get; init; } = false;

    /// <summary>
    /// Keep each analyzed record's raw complex spectrum on the retained snapshot
    /// (<see cref="SpectrumSnapshot.Complex"/>), not just its magnitudes.
    ///
    /// <para>Off by default and meant to be turned on only for the duration of a
    /// frequency-response capture. The live path has no use for phase, and retaining it there would
    /// copy a 65537-element <c>Complex</c> array per channel on every one of ~20 ticks a second for
    /// nobody.</para>
    ///
    /// <para>The spectrum is of the <b>windowed</b> record, so anything reading the phase should
    /// also set <see cref="Window"/> to <see cref="WindowType.Rectangular"/>. For the chirp that is
    /// not a compromise but the correct choice: the stimulus is exactly periodic in the record, so
    /// the untapered DFT is exact, and a taper would destroy the periodicity the measurement
    /// depends on.</para>
    /// </summary>
    public bool RetainComplexSpectrum { get; init; } = false;

    /// <summary>
    /// When set, every tick also measures two-tone intermodulation between these tones (see
    /// <see cref="IntermodulationAnalyzer"/>) and carries the result on
    /// <see cref="SpectrumMeters.Intermodulation"/> and its own stream frame. Null (the default)
    /// means no IMD figure at all — not zero, nothing.
    ///
    /// The FFT screen sets this through <c>POST /api/acquisition/spectrum-config</c> from its
    /// staged stimulus whenever exactly two tones are configured, so the reading follows the
    /// generator; <c>ImdSweepRunner</c> sets it directly for the run and restores whatever was here
    /// afterwards. The tone frequencies are the <b>snapped</b> generator frequencies — the analyzer
    /// still peak-searches ±2 bins, so a bin of drift is tolerated, but a frequency that is off
    /// by more than that measures the noise floor and reports it as IMD.
    /// </summary>
    public IntermodulationSpec? Intermodulation { get; init; }

    /// <summary>
    /// When set, every tick also measures the multitone comb at these frequencies and the
    /// distortion-plus-noise between its tones (see <see cref="MultitoneAnalyzer"/>), carried on
    /// <see cref="SpectrumMeters.Multitone"/> and its own stream frame. Null means no figure.
    ///
    /// <para><b>Written only by <c>DeviceSessionService</c></b>, on every generator configure and
    /// re-snap, from the effective (snapped) tone list of the first enabled Multitone channel --
    /// and cleared for any other stimulus. Unlike the IMD spec it is not pushed by the client: the
    /// comb is a function of the generator settings and the FFT size, both of which the session
    /// already owns, so deriving it there removes a round trip and the window in which the client's
    /// copy is a size behind. Everything else that rebuilds the options carries it through
    /// (<c>with</c>) or builds fresh ones for a run and restores the saved ones after.</para>
    /// </summary>
    public MultitoneSpec? Multitone { get; init; }

    /// <summary>
    /// The noise weighting in effect. Applied <b>after</b> averaging, to the displayed spectrum and
    /// to a copy of the PSD that only the noise integrals read (THD+N, SNR, the noise floor, the
    /// band RMS and the noise density) — the fundamental, the harmonics, THD, the other-peaks list
    /// and IMD are all measured on the unweighted PSD, so a weighting changes what "noise" means
    /// and nothing else. See <see cref="WeightingSpec"/>. Every sweep runner forces
    /// <see cref="WeightingSpec.None"/> for its run.
    /// </summary>
    public WeightingSpec Weighting { get; init; } = WeightingSpec.None;
}

/// <summary>
/// Which weighting curves are in effect: one built-in standard and either of the two user tables
/// (<c>UserWeightingSetting</c>), which combine multiplicatively — the QA40x's own convention, so
/// a standard curve plus a user table is the product of the two. A user slot that is checked but
/// holds no table weighs nothing (flat 0 dB).
/// </summary>
public sealed record WeightingSpec(WeightingStandard Standard, bool User1 = false, bool User2 = false)
{
    public static readonly WeightingSpec None = new(WeightingStandard.None);

    /// <summary>True when nothing is selected at all — not the same as "resolves to flat", which also covers empty user slots.</summary>
    public bool IsNone => Standard == WeightingStandard.None && !User1 && !User2;
}

/// <summary>
/// The comb a multitone distortion figure is measured against: the effective generator tone
/// frequencies, snapped. <see cref="MaxProductFreqHz"/> bounds the gaps that count; null means
/// <see cref="SpectrumPipelineOptions.MeasurementHiFreqHz"/>.
/// </summary>
public sealed record MultitoneSpec(IReadOnlyList<double> ToneFreqsHz, double? MaxProductFreqHz = null);

/// <summary>
/// The two tones an intermodulation figure is measured between, and how. <see cref="ToneLowHz"/>
/// is the lower frequency (SMPTE's modulating tone, CCIF's f1), <see cref="ToneHighHz"/> the upper.
/// <see cref="MaxProductFreqHz"/> bounds which products count; null means the pipeline's
/// <see cref="SpectrumPipelineOptions.MeasurementHiFreqHz"/>, i.e. "in-band products only" —
/// the tutorial's convention, under which CCIF 19 + 20 kHz counts 18 kHz and omits 21 kHz.
/// </summary>
public sealed record IntermodulationSpec(
    double ToneLowHz,
    double ToneHighHz,
    IntermodulationMethod Method,
    int MaxOrder,
    double? MaxProductFreqHz = null);
