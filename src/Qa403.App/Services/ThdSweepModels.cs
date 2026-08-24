using Qa403.App.Streaming;
using Qa403.Dsp;

namespace Qa403.App.Services;

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
    bool MeasureNoiseFloor)
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
    /// <see cref="Qa403.Dsp.HarmonicAnalyzer"/> sums ±3 bins around each harmonic, and the window's
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

    /// <summary>Analysis resolution, Hz per bin.</summary>
    public double BinSizeHz => SampleRateHz / FftSize;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for anything the runner cannot act on.</summary>
    public void Validate()
    {
        if (StartFreqHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(StartFreqHz), StartFreqHz, "Start frequency must be greater than 0 Hz.");
        if (EndFreqHz <= StartFreqHz)
            throw new ArgumentOutOfRangeException(nameof(EndFreqHz), EndFreqHz, "End frequency must be above the start frequency.");
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
        if (LevelMode != ThdSweepLevelMode.GeneratorVoltage && LoadOhms <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "An output-referred level needs a load above 0 Ω.");
        if (StartFreqHz < MinFundamentalBins * BinSizeHz)
        {
            int neededFftSize = 1;
            while (neededFftSize < FftSize || StartFreqHz < MinFundamentalBins * (SampleRateHz / neededFftSize))
                neededFftSize *= 2;

            throw new ArgumentOutOfRangeException(
                nameof(StartFreqHz),
                StartFreqHz,
                $"{StartFreqHz:0.##} Hz is only {StartFreqHz / BinSizeHz:0.#} bins up at {SampleRateHz / 1000:0.##} kHz / {FftSize} " +
                $"({BinSizeHz:0.###} Hz per bin); the harmonic analysis needs at least {MinFundamentalBins}. " +
                $"Use an FFT size of {neededFftSize} or more, or start the sweep above {MinFundamentalBins * BinSizeHz:0.#} Hz.");
        }

        if (LevelUnit == ThdSweepLevelUnit.Watt && LevelMode != ThdSweepLevelMode.OutputPower)
            throw new ArgumentOutOfRangeException(nameof(LevelUnit), "Watts only make sense for the output-power level mode.");
    }
}

/// <summary>
/// One measured frequency, for one channel. Percentages rather than ratios throughout because that
/// is the chart's default unit; dB is <c>20·log10(percent/100)</c> and is derived client-side.
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
    /// <summary>The 6th harmonic and above, power-summed into one bucket — see <see cref="Qa403.Dsp.SpectrumMetrics.PartialThd"/>.</summary>
    double D6PlusPercent,
    /// <summary>The generator-off residual noise as a percentage of this point's fundamental. 0 when the noise pass was skipped.</summary>
    double NoisePercent,
    /// <summary>
    /// A small (at most <see cref="SpectrumPipeline.PreviewMaxColumns"/>-point) decimated spectrum
    /// and oscilloscope trace, captured at the same moment as this point's metrics -- the only
    /// moment the underlying samples exist, since the ring buffer moves on to the next frequency the
    /// instant this point is read. Null only if the pipeline was not asked to capture previews (see
    /// <see cref="Streaming.SpectrumPipelineOptions.CapturePreview"/>), which <see cref="ThdSweepRunner"/>
    /// always turns on -- so in practice these are populated for every sweep point.
    /// </summary>
    SweepPreviewSeries? PreviewFft,
    SweepPreviewSeries? PreviewScope);

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
    IReadOnlyList<ThdSweepPoint> Points);

/// <summary>The immediate reply to a start request: the sweep itself runs in the background.</summary>
public sealed record ThdSweepStarted(int PointCount, IReadOnlyList<double> FrequenciesHz);
