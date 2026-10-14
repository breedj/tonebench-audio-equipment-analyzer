using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Which quantity a sweep steps. The two axes share one runner, one wire format and one chart --
/// only the variable moving between points differs, and every hard rule about a stimulus change
/// (flush, discard the in-flight DAC audio, single-shot re-arm) is identical for both.
/// </summary>
public enum SweepAxis
{
    /// <summary>Frequency steps, drive level held constant -- the classic THD-vs-frequency sweep.</summary>
    Frequency,

    /// <summary>Generator amplitude steps at a fixed frequency -- THD vs amplitude.</summary>
    Amplitude,
}

/// <summary>
/// How the drive level for a THD-vs-frequency sweep is specified. Ported from the previous app's
/// <c>E_GeneratorType</c>: the generator's own amplitude is the direct case, but for an amplifier
/// the interesting question is almost always "what is the distortion at 1 W into 8 Ω", which needs
/// the generator level to be *solved for* rather than set.
/// </summary>
public enum ThdSweepLevelMode
{
    /// <summary>The generator amplitude is given directly and held constant across the sweep.</summary>
    GeneratorVoltage,

    /// <summary>A target voltage at the DUT's output; the generator level is found by probing.</summary>
    OutputVoltage,

    /// <summary>A target power into <see cref="ThdSweepRequest.LoadOhms"/>; V = sqrt(P·R), then as above.</summary>
    OutputPower,
}

/// <summary>The unit a level is expressed in. Watts is only meaningful for <see cref="ThdSweepLevelMode.OutputPower"/>.</summary>
public enum ThdSweepLevelUnit
{
    Dbv,
    Volt,
    MilliVolt,
    Watt,
}

/// <summary>
/// What each harmonic is divided by. <see cref="Conventional"/> is the textbook figure: every
/// harmonic over the fundamental at the stimulus frequency. <see cref="FrequencyNormalized"/> is
/// Temme's (Listen Inc.) normalized distortion: harmonic n over the fundamental the same sweep
/// measured <em>at n·f</em>, so the DUT's own frequency response cancels out of the reading and
/// what is left is the nonlinearity — see <see cref="Tonebench.Dsp.FrequencyNormalizedDistortion"/>.
/// Frequency axis only: an amplitude sweep has one frequency and no response to normalize against.
/// </summary>
public enum ThdMethod
{
    Conventional,
    FrequencyNormalized,
}

/// <summary>Where a running sweep is, for the status endpoint and the progress frames.</summary>
public enum ThdSweepPhase
{
    Idle,

    /// <summary>Probing at 1 kHz to solve for the generator amplitude — output-referred modes only.</summary>
    DeterminingLevel,

    /// <summary>Generator off, capturing the residual noise the distortion traces are referenced against.</summary>
    NoiseFloor,

    Sweeping,
    Finished,
    Cancelled,
    Failed,

    /// <summary>
    /// Probing for the input range the DUT's output needs, from the least sensitive one — output-referred
    /// modes only, and only while the runner owns the range. Appended with an explicit ordinal rather
    /// than slotted in next to <see cref="DeterminingLevel"/>, exactly as
    /// <see cref="FrequencyResponsePhase.DeterminingRange"/> is and for the same reason: the ordinal
    /// rides in the progress frame's <c>seq</c> field, so renumbering the existing members would make
    /// an older browser mislabel every phase.
    /// </summary>
    DeterminingRange = 7,
}

/// <summary>
/// Everything a sweep needs. Deliberately a flat record rather than a reference to the live
/// pipeline options: a sweep runs for minutes and must not change shape underneath itself because
/// someone moved a slider in another browser tab.
/// </summary>
public sealed record ThdSweepRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    ThdSweepLevelMode LevelMode,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool MeasureNoiseFloor,
    /// <summary>
    /// Size the FFT per point and pick the sample rate from the sweep's own range, instead of
    /// holding <see cref="FftSize"/> and <see cref="SampleRateHz"/> fixed across every point.
    ///
    /// <para>A fixed size is chosen for the bottom of the sweep and then paid at every point above
    /// it, where the record is orders of magnitude longer than the measurement needs — see
    /// <see cref="SweepResolution"/>. When this is on, <see cref="FftSize"/> and
    /// <see cref="SampleRateHz"/> are ignored in favour of
    /// <see cref="EffectiveSampleRateHz"/>/<see cref="FftSizeAt"/>; when it is off both are used
    /// exactly as given, which is what makes the fixed mode a reproducible reference.</para>
    /// </summary>
    bool AdaptiveResolution = false,

    /// <summary>Which quantity this sweep steps. See <see cref="SweepAxis"/>.</summary>
    SweepAxis Axis = SweepAxis.Frequency,

    /// <summary>
    /// The one frequency an <see cref="SweepAxis.Amplitude"/> sweep measures at. Ignored on the
    /// frequency axis, where the tone is what moves.
    /// </summary>
    double FixedFreqHz = 1000.0,

    /// <summary>Lowest generator amplitude of an amplitude sweep, in volts RMS.</summary>
    double StartAmplitudeVolt = 0.001,

    /// <summary>Highest generator amplitude of an amplitude sweep, in volts RMS.</summary>
    double EndAmplitudeVolt = 1.0,

    /// <summary>
    /// How the harmonics are referenced — see <see cref="Services.ThdMethod"/>. Under
    /// <see cref="ThdMethod.FrequencyNormalized"/> the runner keeps stepping the fundamental past
    /// <see cref="EndFreqHz"/> (to <see cref="ReferenceEndFreqHz"/>) so every harmonic of every
    /// requested point has a fundamental to be divided by.
    /// </summary>
    ThdMethod ThdMethod = ThdMethod.Conventional,

    /// <summary>
    /// Amplitude axis only: end the sweep early once any measured channel's THD exceeds this many
    /// percent. The point that crossed it is kept -- it is the informative one -- and the run
    /// finishes as <see cref="ThdSweepPhase.Finished"/> with <see cref="ThdSweepStatus.StopReason"/>
    /// saying why. Null means sweep to the end. A DUT is driven one step past the threshold by
    /// construction; the check needs the point to exist.
    /// </summary>
    double? StopThdPercent = null,
    /// <summary>
    /// The band this sweep's noise figures are read over: the noise-floor pass
    /// (<see cref="SweepCaptureSupport.MeanBandBinVrms"/>, which is the figure the chart draws)
    /// and the THD+N and SNR recorded on every point.
    ///
    /// <para>It deliberately does <b>not</b> bound the harmonics. <c>BuildSweepOptions</c> sets
    /// <see cref="Streaming.SpectrumPipelineOptions.HarmonicMaxFreqHz"/> to Nyquist instead:
    /// bounded at a 20 kHz band, every harmonic of a point above ~7 kHz is out of band and THD
    /// reads exactly 0, so the top third of a sweep would plot as a cliff into the floor. That
    /// property's own remarks carry the reasoning.</para>
    ///
    /// <para>The sweep's own setting: until 2026-10-14 a sweep simply inherited whatever band the
    /// live pipeline held, i.e. whichever FFT measurement happened to be open last, which no
    /// stored result could say afterwards.</para>
    /// </summary>
    double MeasurementLoFreqHz = 20.0,
    double MeasurementHiFreqHz = 20000.0)
{
    /// <summary>The level expressed in dBV. Watts are converted through the load: V = sqrt(P·R).</summary>
    public double LevelDbv => LevelUnit switch
    {
        ThdSweepLevelUnit.Dbv => LevelValue,
        ThdSweepLevelUnit.Volt => 20.0 * Math.Log10(Math.Max(LevelValue, double.Epsilon)),
        ThdSweepLevelUnit.MilliVolt => 20.0 * Math.Log10(Math.Max(LevelValue / 1000.0, double.Epsilon)),
        ThdSweepLevelUnit.Watt => 20.0 * Math.Log10(Math.Max(Math.Sqrt(Math.Max(LevelValue, 0.0) * LoadOhms), double.Epsilon)),
        _ => LevelValue,
    };

    /// <summary>
    /// Bins the fundamental must be up before its harmonics can be measured at all.
    ///
    /// <see cref="Tonebench.Dsp.HarmonicAnalyzer"/> sums ±3 bins around each harmonic, and the window's
    /// own mainlobe occupies the fundamental's bin ±1. H2's skirt therefore clears the fundamental
    /// only when 2f − 3·Δf &gt; f + Δf, i.e. f &gt; 4·Δf. Below that the analyzer sums the
    /// fundamental into its own second harmonic and THD approaches 100 % — measured on the bench at
    /// 5.86 Hz bins: 17.6 Hz (bin 3) read 99.96 %, 29.3 Hz (bin 5) read 40.8 %, and 41 Hz (bin 7)
    /// read a correct 0.00024 %.
    ///
    /// This is the "that number is fabricated" line, not a quality bar. Clearing it by a bin or two
    /// still leaves the harmonics close to DC, where the 1/f floor is high and mains hum can land
    /// inside a harmonic's skirt; comfortable margin is more like 20 bins.
    /// </summary>
    public const int MinFundamentalBins = 5;

    /// <summary>
    /// The FFT size every amplitude sweep runs at. Fixed rather than offered, for the same reason a
    /// frequency sweep's is (see <c>SweepConfigPanel.vue</c>): the harmonic analysis needs the
    /// fundamental several bins up, and 192 kHz / 131072 gives 1.465 Hz bins and a 0.68 s record.
    /// </summary>
    public const int AmplitudeSweepFftSize = 131072;

    /// <summary>The sample rate every amplitude sweep runs at. See <see cref="AmplitudeSweepFftSize"/>.</summary>
    public const double AmplitudeSweepSampleRateHz = 192000.0;

    /// <summary>
    /// Lowest generator amplitude an amplitude sweep may ask for, in volts (1 mV). Below this the
    /// tone is under the analyzer's own residual at every input range and the point measures the
    /// noise floor rather than the DUT.
    /// </summary>
    public const double MinAmplitudeVolt = 0.001;

    /// <summary>
    /// Highest generator amplitude, in volts. 7.9 V is 17.96 dBV -- the ceiling of the 18 dBV output
    /// range, and so the most the DAC can produce without clipping against the attenuator itself.
    /// </summary>
    public const double MaxAmplitudeVolt = 7.9;

    /// <summary>
    /// The lowest tone this sweep will play: its start frequency, or -- on the amplitude axis, where
    /// the tone never moves -- that one fixed frequency. Only used to lower
    /// <see cref="Tonebench.App.Streaming.SpectrumPipelineOptions.FundamentalMinSearchHz"/> far enough
    /// that the pipeline can locate the sweep's own tone.
    /// </summary>
    public double SweepToneFreqHz => Axis == SweepAxis.Amplitude ? FixedFreqHz : StartFreqHz;

    /// <summary>Analysis resolution at the fixed <see cref="FftSize"/>, Hz per bin. See <see cref="BinSizeHzAt"/> for the adaptive-aware value.</summary>
    public double BinSizeHz => SampleRateHz / FftSize;

    /// <summary>
    /// The sample rate this sweep will actually run at: the requested one, or — under
    /// <see cref="AdaptiveResolution"/> — the lowest supported rate that still keeps
    /// <see cref="SweepResolution.HarmonicHeadroomOrder"/> harmonics of <see cref="EndFreqHz"/> in
    /// band. Note this does not change how long the sweep takes (see
    /// <see cref="SweepResolution.SampleRateFor"/>); it changes how many harmonics are measurable
    /// at the top of it, and how much USB and FFT load the sweep costs.
    /// </summary>
    public double EffectiveSampleRateHz =>
        AdaptiveResolution ? SweepResolution.SampleRateFor(EndFreqHz) : SampleRateHz;

    /// <summary>The FFT size a given sweep point is analyzed at — constant unless <see cref="AdaptiveResolution"/> is on.</summary>
    public int FftSizeAt(double freqHz) =>
        AdaptiveResolution ? SweepResolution.FftSizeFor(freqHz, EffectiveSampleRateHz) : FftSize;

    /// <summary>Analysis resolution at a given sweep point, Hz per bin.</summary>
    public double BinSizeHzAt(double freqHz) => EffectiveSampleRateHz / FftSizeAt(freqHz);

    /// <summary>
    /// How far past <see cref="EndFreqHz"/> the fundamental is measured under
    /// <see cref="ThdMethod.FrequencyNormalized"/>: <see cref="SweepResolution.HarmonicHeadroomOrder"/>
    /// times the end (the D2..D5 the chart draws individually all have a reference), capped where
    /// the sweep itself is capped. Equal to <see cref="EndFreqHz"/> for a conventional sweep, so
    /// callers can always read this as "the highest tone the sweep will play".
    /// </summary>
    public double ReferenceEndFreqHz =>
        ThdMethod == ThdMethod.FrequencyNormalized
            ? Math.Min(SweepResolution.HarmonicHeadroomOrder * EndFreqHz, MaxSweepFreqHz)
            : EndFreqHz;

    /// <summary>The highest tone any frequency sweep plays: a little under Nyquist, matching the runner's own ceiling.</summary>
    public double MaxSweepFreqHz => MaxFrequencyNyquistFraction * EffectiveSampleRateHz / 2.0;

    /// <summary>
    /// Fraction of Nyquist a sweep tone may reach. At Nyquist itself a tone has no harmonics and
    /// sits on the half-weighted top bin; 98 % keeps the last point clear of both.
    /// </summary>
    public const double MaxFrequencyNyquistFraction = 0.98;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for anything the runner cannot act on.</summary>
    public void Validate()
    {
        ValidateCommon();

        if (Axis == SweepAxis.Amplitude)
            ValidateAmplitudeSweep();
        else
            ValidateFrequencySweep();
    }

    /// <summary>The checks that mean the same thing on either axis -- grid density, analysis size, channels.</summary>
    private void ValidateCommon()
    {
        if (StepsPerOctave is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(StepsPerOctave), StepsPerOctave, "Steps per octave must be between 1 and 48.");
        if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(FftSize), FftSize, "FFT size must be a power of two.");
        if (AverageCount < 1)
            throw new ArgumentOutOfRangeException(nameof(AverageCount), AverageCount, "Average count must be at least 1.");
        if (SampleRateHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), SampleRateHz, "Sample rate must be positive.");
        if (!ProcessLeft && !ProcessRight)
            throw new ArgumentOutOfRangeException(nameof(ProcessLeft), "At least one channel must be measured.");
        if (MeasurementLoFreqHz < 0.0 || MeasurementHiFreqHz <= MeasurementLoFreqHz)
            throw new ArgumentOutOfRangeException(nameof(MeasurementHiFreqHz), MeasurementHiFreqHz,
                "The measurement band's upper edge must be above its lower one, and neither may be negative.");
    }

    private void ValidateFrequencySweep()
    {
        if (StartFreqHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(StartFreqHz), StartFreqHz, "Start frequency must be greater than 0 Hz.");
        if (EndFreqHz <= StartFreqHz)
            throw new ArgumentOutOfRangeException(nameof(EndFreqHz), EndFreqHz, "End frequency must be above the start frequency.");
        if (LevelMode != ThdSweepLevelMode.GeneratorVoltage && LoadOhms <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "An output-referred level needs a load above 0 Ω.");

        // Judged against the size and rate the sweep will actually use at its lowest point, which
        // under AdaptiveResolution is neither of the requested ones. The adaptive sizer already
        // reaches for the largest permitted FFT down there, so this can still fail -- it just means
        // the start frequency is genuinely too low to analyze at any size the sweep will use.
        RequireResolvableFundamental(StartFreqHz, nameof(StartFreqHz), FftSizeAt(StartFreqHz));

        if (LevelUnit == ThdSweepLevelUnit.Watt && LevelMode != ThdSweepLevelMode.OutputPower)
            throw new ArgumentOutOfRangeException(nameof(LevelUnit), "Watts only make sense for the output-power level mode.");
        if (StopThdPercent is not null)
            throw new ArgumentOutOfRangeException(nameof(StopThdPercent), StopThdPercent, "A THD stop threshold only applies to an amplitude sweep.");
    }

    /// <summary>
    /// The amplitude axis rejects the two settings that only mean something when the frequency
    /// moves, rather than quietly ignoring them: adaptive resolution sizes the FFT from the sweep's
    /// frequency range (there is only one frequency here), and a level *mode* cannot be solved for
    /// when the level is the thing being stepped.
    ///
    /// <para>The noise-floor pass is <em>not</em> one of them: a residual the distortion traces sit
    /// above is as useful against level as against frequency — arguably more so, since the bottom of
    /// a level sweep is where the DUT's own distortion disappears into it.</para>
    /// </summary>
    private void ValidateAmplitudeSweep()
    {
        if (StartAmplitudeVolt < MinAmplitudeVolt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartAmplitudeVolt), StartAmplitudeVolt,
                $"Start amplitude must be at least {MinAmplitudeVolt * 1000:0.###} mV.");
        }

        if (EndAmplitudeVolt <= StartAmplitudeVolt)
            throw new ArgumentOutOfRangeException(nameof(EndAmplitudeVolt), EndAmplitudeVolt, "End amplitude must be above the start amplitude.");

        if (EndAmplitudeVolt > MaxAmplitudeVolt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EndAmplitudeVolt), EndAmplitudeVolt,
                $"End amplitude must be at most {MaxAmplitudeVolt:0.##} V ({20.0 * Math.Log10(MaxAmplitudeVolt):0.##} dBV) -- the ceiling of the widest output range.");
        }

        // Unconditional here, unlike a frequency sweep: the chart can plot output *power* against
        // the swept level, and that needs a load whichever way the level was specified.
        if (LoadOhms <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "A load above 0 Ω is needed to report output power.");

        RequireResolvableFundamental(FixedFreqHz, nameof(FixedFreqHz), FftSize);

        if (AdaptiveResolution)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveResolution), "An amplitude sweep measures one frequency, so there is nothing for adaptive resolution to adapt to.");
        if (LevelMode != ThdSweepLevelMode.GeneratorVoltage)
            throw new ArgumentOutOfRangeException(nameof(LevelMode), LevelMode, "An amplitude sweep steps the generator voltage itself, so it has no level mode to solve for.");
        if (ThdMethod != ThdMethod.Conventional)
            throw new ArgumentOutOfRangeException(nameof(ThdMethod), ThdMethod, "An amplitude sweep measures one frequency, so there is no frequency response to normalize the harmonics against.");
        if (StopThdPercent is { } stopThd && !(stopThd > 0.0))
            throw new ArgumentOutOfRangeException(nameof(StopThdPercent), StopThdPercent, "The THD stop threshold must be above 0 %.");
    }

    /// <summary>
    /// Rejects a tone too few bins up for its own harmonics to be separable -- see
    /// <see cref="MinFundamentalBins"/>. The message names the FFT size that would work, because
    /// that is the only thing the caller can actually change about it.
    /// </summary>
    private void RequireResolvableFundamental(double freqHz, string paramName, int effectiveFftSize)
    {
        double effectiveSampleRateHz = EffectiveSampleRateHz;
        double effectiveBinSizeHz = effectiveSampleRateHz / effectiveFftSize;

        if (freqHz >= MinFundamentalBins * effectiveBinSizeHz)
            return;

        int neededFftSize = 1;
        while (neededFftSize < effectiveFftSize || freqHz < MinFundamentalBins * (effectiveSampleRateHz / neededFftSize))
            neededFftSize *= 2;

        string sizeAdvice = AdaptiveResolution
            ? $"Start the sweep above {MinFundamentalBins * effectiveBinSizeHz:0.#} Hz, or turn off adaptive resolution and pick an FFT size of {neededFftSize} or more."
            : $"Use an FFT size of {neededFftSize} or more, or stay above {MinFundamentalBins * effectiveBinSizeHz:0.#} Hz.";

        throw new ArgumentOutOfRangeException(
            paramName,
            freqHz,
            $"{freqHz:0.##} Hz is only {freqHz / effectiveBinSizeHz:0.#} bins up at {effectiveSampleRateHz / 1000:0.##} kHz / {effectiveFftSize} " +
            $"({effectiveBinSizeHz:0.###} Hz per bin); the harmonic analysis needs at least {MinFundamentalBins}. " +
            sizeAdvice);
    }
}

/// <summary>
/// One measured sweep point, for one channel. Percentages rather than ratios throughout because
/// that is the chart's default unit; dB is <c>20·log10(percent/100)</c> and is derived client-side.
///
/// <para>The same record serves both axes -- what varies between points is <see cref="FreqHz"/> on
/// a frequency sweep and <see cref="GeneratorDbv"/> on an amplitude one, and the chart's three
/// x-axis choices are all already here: the generator level, the measured
/// <see cref="FundamentalDbv"/> (the DUT's output) and <see cref="PowerWatt"/>.</para>
/// </summary>
public sealed record ThdSweepPoint(
    double FreqHz,
    int Channel,
    /// <summary>
    /// The fundamental the analyzer actually located, which is not necessarily
    /// <see cref="FreqHz"/>: the pipeline finds it by peak search, so if the tone never reached the
    /// DUT (or something louder sits in the band) this diverges from the frequency that was asked
    /// for. Worth carrying rather than assuming — it is the one number that tells a stale or
    /// mis-locked reading apart from a real one.
    /// </summary>
    double MeasuredFreqHz,
    double FundamentalDbv,
    double GainDb,
    double PowerWatt,
    double ThdPercent,
    double ThdNPercent,
    double SnrDb,
    /// <summary>D2..D5 as a percentage of the fundamental, index 0 = 2nd harmonic. Short if the upper harmonics fell outside the spectrum.</summary>
    IReadOnlyList<double> HarmonicPercent,
    /// <summary>The 6th harmonic and above, power-summed into one bucket — see <see cref="Tonebench.Dsp.SpectrumMetrics.PartialThd"/>.</summary>
    double D6PlusPercent,
    /// <summary>The generator-off residual noise as a percentage of this point's fundamental. 0 when the noise pass was skipped.</summary>
    double NoisePercent,
    /// <summary>
    /// The generator amplitude this point was measured at. Constant across a frequency sweep (it is
    /// the level the whole sweep ran at, solved by probing for the output-referred modes); the swept
    /// variable itself on an amplitude sweep, and there the chart's "input voltage" x axis.
    /// </summary>
    double GeneratorDbv,
    /// <summary>
    /// A small (at most <see cref="SpectrumPipeline.PreviewMaxColumns"/>-point) decimated spectrum
    /// and oscilloscope trace, captured at the same moment as this point's metrics -- the only
    /// moment the underlying samples exist, since the ring buffer moves on to the next frequency the
    /// instant this point is read. Null only if the pipeline was not asked to capture previews (see
    /// <see cref="Streaming.SpectrumPipelineOptions.CapturePreview"/>), which <see cref="ThdSweepRunner"/>
    /// always turns on -- so in practice these are populated for every sweep point.
    /// </summary>
    SweepPreviewSeries? PreviewFft,
    SweepPreviewSeries? PreviewScope,
    /// <summary>
    /// The frequency-normalized figures — each harmonic over the fundamental measured at its own
    /// frequency (see <see cref="ThdMethod.FrequencyNormalized"/>) — as a percentage. Null on a
    /// conventional sweep, and null on a normalized one wherever the fundamental at n·f has not
    /// been measured: every point until the sweep reaches 2f, and for good on the points near the
    /// top of the extended grid, above which nothing was swept. <b>Null, never NaN</b>: this record
    /// is serialized on the status endpoint, and a NaN truncates the whole response.
    /// </summary>
    double? NormalizedThdPercent = null,
    /// <summary>Normalized D2..D5, index 0 = second harmonic; always four entries, each null without a reference.</summary>
    IReadOnlyList<double?>? NormalizedHarmonicPercent = null,
    /// <summary>Normalized sixth harmonic and above, power-summed, like <see cref="D6PlusPercent"/>.</summary>
    double? NormalizedD6PlusPercent = null);

/// <summary>A running or finished sweep, as the status endpoint reports it.</summary>
public sealed record ThdSweepStatus(
    bool Running,
    ThdSweepPhase Phase,
    int Index,
    int Total,
    double CurrentFreqHz,
    /// <summary>The generator amplitude actually used — solved by probing for the output-referred modes.</summary>
    double GeneratorAmpDbv,
    string? Error,
    IReadOnlyList<ThdSweepPoint> Points,
    /// <summary>Which quantity the sweep steps, so a reattaching client knows how to read the progress fields.</summary>
    SweepAxis Axis = SweepAxis.Frequency,
    /// <summary>The generator amplitude of the point being measured, in volts. Only moves on the amplitude axis.</summary>
    double CurrentAmplitudeVolt = 0.0,
    /// <summary>How the points' harmonics are referenced, so a client knows which of a point's two sets of figures the run is about.</summary>
    ThdMethod ThdMethod = ThdMethod.Conventional,
    /// <summary>
    /// Why a <see cref="ThdSweepPhase.Finished"/> sweep ended before its last planned point -- the
    /// THD stop threshold, today. Null for a sweep that ran to the end. Not a phase of its own, so
    /// nothing that asks "is it over" has to learn about it.
    /// </summary>
    string? StopReason = null);

/// <summary>The immediate reply to a start request: the sweep itself runs in the background.</summary>
/// <param name="PointCount">How many points the sweep will measure.</param>
/// <param name="FrequenciesHz">The grid a frequency sweep steps. One repeated value on the amplitude axis.</param>
/// <param name="AmplitudesVolt">The grid an amplitude sweep steps, in volts. Empty on the frequency axis.</param>
public sealed record ThdSweepStarted(
    int PointCount,
    IReadOnlyList<double> FrequenciesHz,
    IReadOnlyList<double>? AmplitudesVolt = null);
