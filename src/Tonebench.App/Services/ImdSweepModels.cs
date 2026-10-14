using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// What an IMD-vs-amplitude sweep is asked to do: play a two-tone pair at each of a log-spaced
/// ladder of <b>generator</b> levels and record the intermodulation at every step.
///
/// <para>The level ladder is in sine-equivalent volts — the level of a single sine whose peak
/// equals the pair's peak (<see cref="IntermodulationPresets.ToneLevelsDbv"/>). That is the
/// tutorial's convention for every IMD test and what an amplifier's rating is quoted against, so
/// "1 V" here means the same drive a 1 V single-tone THD sweep would apply at its peaks. Like the
/// THD amplitude sweep there is no level probe: the level <em>is</em> the swept variable, and the
/// chart offers the measured output (V or W into <see cref="LoadOhms"/>) as its axis.</para>
///
/// <para>The analysis preset is fixed (<see cref="FftSize"/>, <see cref="SampleRateHz"/>,
/// <see cref="Window"/>), as it is for the THD amplitude sweep and for the same reason: nothing
/// about the two frequencies changes across the run, and 131072 points at 192 kHz put SMPTE's 60 Hz
/// spacing 41 bins apart — far clear of <see cref="IntermodulationAnalyzer.MinToneSpacingBins"/>.</para>
/// </summary>
public sealed record ImdSweepRequest(
    /// <summary>The template the tones came from, kept for the record; <see cref="IntermodulationPreset.Custom"/> for a typed pair.</summary>
    IntermodulationPreset Preset,
    double ToneLowHz,
    double ToneHighHz,
    /// <summary>Amplitude ratio low:high — 4 for SMPTE/DIN, 1 for CCIF.</summary>
    double LowToHighRatio,
    IntermodulationMethod Method,
    int MaxOrder,
    /// <summary>First and last generator level, sine-equivalent volts.</summary>
    double StartLevelVolt,
    double EndLevelVolt,
    int StepsPerOctave,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int AverageCount,
    /// <summary>Capture one generator-off record before the sweep, for the residual the distortion traces sit above.</summary>
    bool MeasureNoiseFloor,
    /// <summary>Highest product frequency counted; null means the pipeline's measurement band (20 kHz) — in-band products only.</summary>
    double? MaxProductFreqHz = null,
    /// <summary>Not offered on the wire (the endpoint fixes them); parameters so a test can run the fast preset.</summary>
    int FftSize = ThdSweepRequest.AmplitudeSweepFftSize,
    double SampleRateHz = ThdSweepRequest.AmplitudeSweepSampleRateHz,
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
    public const WindowType Window = WindowType.Hann;

    public const int MinMaxOrder = 2;

    public const int MaxMaxOrder = 12;

    public double BinSizeHz => SampleRateHz / FftSize;

    /// <summary>The two tone levels for a sine-equivalent generator level — see <see cref="IntermodulationPresets.ToneLevelsDbv"/>.</summary>
    public (double LowDbv, double HighDbv) ToneLevelsDbv(double sineEquivalentDbv) =>
        IntermodulationPresets.ToneLevelsDbv(sineEquivalentDbv, LowToHighRatio);

    public void Validate()
    {
        if (!(ToneLowHz > 0.0) || !double.IsFinite(ToneLowHz))
            throw new ArgumentOutOfRangeException(nameof(ToneLowHz), ToneLowHz, "The low tone must be a positive frequency.");

        if (!(ToneHighHz > ToneLowHz) || !double.IsFinite(ToneHighHz))
            throw new ArgumentOutOfRangeException(nameof(ToneHighHz), ToneHighHz, "The high tone must be above the low tone.");

        double maxHz = 0.98 * SampleRateHz / 2.0;
        if (ToneHighHz > maxHz)
            throw new ArgumentOutOfRangeException(nameof(ToneHighHz), ToneHighHz, $"The high tone must be below {maxHz:0} Hz at {SampleRateHz:0} Hz.");

        if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(FftSize), FftSize, "FFT size must be a power of two.");
        if (MeasurementLoFreqHz < 0.0 || MeasurementHiFreqHz <= MeasurementLoFreqHz)
            throw new ArgumentOutOfRangeException(nameof(MeasurementHiFreqHz), MeasurementHiFreqHz,
                "The measurement band's upper edge must be above its lower one, and neither may be negative.");
        if (!(SampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), SampleRateHz, "Sample rate must be positive.");

        // The analyzer refuses tones it cannot separate (IntermodulationAnalyzer.MinToneSpacingBins);
        // better to say so before a run than to have every point come back empty.
        double minSpacingHz = IntermodulationAnalyzer.MinToneSpacingBins * BinSizeHz;
        if (ToneLowHz < minSpacingHz)
            throw new ArgumentOutOfRangeException(nameof(ToneLowHz), ToneLowHz, $"The low tone must be at least {minSpacingHz:0.#} Hz at this resolution.");
        if (ToneHighHz - ToneLowHz < minSpacingHz)
            throw new ArgumentOutOfRangeException(nameof(ToneHighHz), ToneHighHz, $"The tones must be at least {minSpacingHz:0.#} Hz apart at this resolution.");

        if (!(LowToHighRatio > 0.0) || !double.IsFinite(LowToHighRatio))
            throw new ArgumentOutOfRangeException(nameof(LowToHighRatio), LowToHighRatio, "The amplitude ratio must be positive.");

        if (MaxOrder < MinMaxOrder || MaxOrder > MaxMaxOrder)
            throw new ArgumentOutOfRangeException(nameof(MaxOrder), MaxOrder, $"Max order must be between {MinMaxOrder} and {MaxMaxOrder}.");

        if (StartLevelVolt < ThdSweepRequest.MinAmplitudeVolt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartLevelVolt), StartLevelVolt,
                $"Start level must be at least {ThdSweepRequest.MinAmplitudeVolt * 1000:0.###} mV.");
        }

        if (EndLevelVolt <= StartLevelVolt)
            throw new ArgumentOutOfRangeException(nameof(EndLevelVolt), EndLevelVolt, "End level must be above the start level.");

        // The ceiling is on the pair's peak, i.e. on the sine-equivalent level, which is exactly what
        // the ladder is in — so the same bound as the THD amplitude sweep applies unchanged.
        if (EndLevelVolt > ThdSweepRequest.MaxAmplitudeVolt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EndLevelVolt), EndLevelVolt,
                $"End level must be at most {ThdSweepRequest.MaxAmplitudeVolt:0.##} V ({20.0 * Math.Log10(ThdSweepRequest.MaxAmplitudeVolt):0.##} dBV) -- the ceiling of the widest output range.");
        }

        if (StepsPerOctave < 1)
            throw new ArgumentOutOfRangeException(nameof(StepsPerOctave), StepsPerOctave, "Steps per octave must be at least 1.");

        if (!(LoadOhms > 0.0))
            throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "Load impedance must be positive.");

        if (!ProcessLeft && !ProcessRight)
            throw new ArgumentOutOfRangeException(nameof(ProcessLeft), "At least one channel must be measured.");

        if (AverageCount < 1)
            throw new ArgumentOutOfRangeException(nameof(AverageCount), AverageCount, "Average count must be at least 1.");

        if (MaxProductFreqHz is { } maxProduct && !(maxProduct > 0.0))
            throw new ArgumentOutOfRangeException(nameof(MaxProductFreqHz), maxProduct, "The product band limit must be positive.");
    }
}

/// <summary>
/// One measured IMD sweep point for one channel. Levels are dBV; <see cref="OrderPercent"/> is
/// the per-order figure with index 0 = order 2, ending at the last order that had any product in
/// band (an absent order is absent, never zero). <see cref="PowerWatt"/> and <see cref="GainDb"/>
/// are on the sine-equivalent level — the pair's peak as a sine — so they are comparable with a
/// single-tone sweep's; <see cref="CombinedRmsDbv"/> is the pair's true RMS for anyone who wants
/// the voltmeter's number. Never NaN: a channel whose intermodulation could not be measured
/// produces no point at all.
/// </summary>
public sealed record ImdSweepPoint(
    int Channel,
    /// <summary>The generator's sine-equivalent level for this step — the sweep's x coordinate.</summary>
    double GeneratorSineEquivalentDbv,
    double ToneLowDbv,
    double ToneHighDbv,
    /// <summary>The measured output's sine-equivalent level, 20·log10(V1 + V2).</summary>
    double SineEquivalentDbv,
    double CombinedRmsDbv,
    double GainDb,
    double PowerWatt,
    double ImdPercent,
    IReadOnlyList<double> OrderPercent,
    double NoisePercent,
    double ToneLowHz,
    double ToneHighHz,
    SweepPreviewSeries? PreviewFft,
    SweepPreviewSeries? PreviewScope);

public sealed record ImdSweepStatus(
    bool Running,
    ThdSweepPhase Phase,
    int Index,
    int Total,
    /// <summary>The sine-equivalent generator level of the point being measured, in volts; 0 when not on a point.</summary>
    double CurrentLevelVolt,
    string? Error,
    IReadOnlyList<ImdSweepPoint> Points,
    IntermodulationMethod Method,
    int MaxOrder);

public sealed record ImdSweepStarted(int PointCount, IReadOnlyList<double> LevelsVolt);
