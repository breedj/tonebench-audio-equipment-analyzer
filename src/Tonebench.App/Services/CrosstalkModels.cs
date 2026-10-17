using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>Which amplifier channel is the aggressor. <see cref="Both"/> runs the two directions back to back.</summary>
public enum CrosstalkDirection
{
    /// <summary>The left channel is driven; what arrives on the right input is the crosstalk.</summary>
    LeftToRight,

    /// <summary>The right channel is driven; what arrives on the left input is the crosstalk.</summary>
    RightToLeft,

    /// <summary>Left-to-right, then right-to-left, as two passes of one run.</summary>
    Both,
}

/// <summary>One pass of a crosstalk run: which channel plays, and which trace the pass produces.</summary>
public readonly record struct CrosstalkPass(StreamChannel Aggressor, FrequencyResponseTraceKind Kind)
{
    public StreamChannel Victim => Aggressor == StreamChannel.Left ? StreamChannel.Right : StreamChannel.Left;

    public bool GenerateLeft => Aggressor == StreamChannel.Left;

    public bool GenerateRight => Aggressor == StreamChannel.Right;

    /// <summary>"L→R" / "R→L", for logs and the progress line.</summary>
    public string Label => Aggressor == StreamChannel.Left ? "L->R" : "R->L";
}

public static class CrosstalkDirectionExtensions
{
    /// <summary>The passes a direction runs, in order.</summary>
    public static IReadOnlyList<CrosstalkPass> Passes(this CrosstalkDirection direction) => direction switch
    {
        CrosstalkDirection.LeftToRight => [new CrosstalkPass(StreamChannel.Left, FrequencyResponseTraceKind.CrosstalkLeftToRight)],
        CrosstalkDirection.RightToLeft => [new CrosstalkPass(StreamChannel.Right, FrequencyResponseTraceKind.CrosstalkRightToLeft)],
        CrosstalkDirection.Both =>
        [
            new CrosstalkPass(StreamChannel.Left, FrequencyResponseTraceKind.CrosstalkLeftToRight),
            new CrosstalkPass(StreamChannel.Right, FrequencyResponseTraceKind.CrosstalkRightToLeft),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown crosstalk direction."),
    };
}

/// <summary>
/// One crosstalk run: drive one amplifier channel (the aggressor) with a stepped sine at a fixed
/// level, read the tone on both inputs at every point, and report victim − aggressor in dB against
/// frequency. The tutorial's <i>AMP Crosstalk</i>, with the same grid, level modes and adaptive
/// resolution as <see cref="SteppedResponseRequest"/> — the acquisition half is the same stepped
/// sweep, which is why the two share <see cref="SteppedCaptureSupport"/>. What this record does not
/// have, on purpose: a reference channel (the aggressor is the reference by definition), a phase
/// (a crosstalk path's phase is not a figure anyone quotes), and a bandwidth reference (a
/// crosstalk trace has no passband to find corners against).
/// </summary>
public sealed record CrosstalkRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    CrosstalkDirection Direction,
    ThdSweepLevelMode LevelMode,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    double LoadOhms,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool AdaptiveResolution)
{
    /// <summary>The analysis size a fixed-resolution run uses, and the panel's default. The same figure the stepped response fixes.</summary>
    public const int DefaultFftSize = SteppedResponseRequest.DefaultFftSize;

    /// <summary>The sample rate a fixed-resolution run uses, and the panel's default.</summary>
    public const double DefaultSampleRateHz = SteppedResponseRequest.DefaultSampleRateHz;

    /// <summary>Bins the tone must be up before its level can be read — see <see cref="SteppedResponseRequest.MinFundamentalBins"/>.</summary>
    public const int MinFundamentalBins = SteppedResponseRequest.MinFundamentalBins;

    public const double MaxFrequencyNyquistFraction = SteppedResponseRequest.MaxFrequencyNyquistFraction;

    public const int MinAverageCount = SteppedResponseRequest.MinAverageCount;

    public const int MaxAverageCount = SteppedResponseRequest.MaxAverageCount;

    /// <summary>The passes this run makes, in order — one per direction.</summary>
    public IReadOnlyList<CrosstalkPass> Passes => Direction.Passes();

    /// <summary>The requested drive level in dBV, whatever unit it was entered in. Watts convert through the load: V = sqrt(P·R).</summary>
    public double LevelDbv => LevelUnit.ToDbv(LevelValue, LoadOhms);

    /// <summary>True when the generator amplitude has to be found by probing the aggressor's output — see <see cref="SteppedResponseRequest.SolvesGeneratorLevel"/>.</summary>
    public bool SolvesGeneratorLevel => LevelMode != ThdSweepLevelMode.GeneratorVoltage;

    /// <summary>The sample rate this run will actually use — see <see cref="SteppedResponseRequest.EffectiveSampleRateHz"/>.</summary>
    public double EffectiveSampleRateHz => AdaptiveResolution
        ? SweepResolution.SampleRateFor(EndFreqHz, harmonicOrder: 1, nyquistFraction: MaxFrequencyNyquistFraction)
        : SampleRateHz;

    /// <summary>The highest tone this run will play, given the rate it settles on.</summary>
    public double MaxFrequencyHz => MaxFrequencyNyquistFraction * EffectiveSampleRateHz / 2.0;

    /// <summary>The FFT ceiling the sweeps use at this run's sample rate.</summary>
    public int StandardFftCeiling => SweepResolution.BoundsFor(EffectiveSampleRateHz).Ceiling;

    public double StandardLowestHz =>
        SweepResolution.LowestToneHz(EffectiveSampleRateHz, StandardFftCeiling, MinFundamentalBins);

    /// <summary>Raised only when the run starts below the standard ceiling's floor — see <see cref="SteppedResponseRequest.FftCeiling"/>.</summary>
    public int FftCeiling => StartFreqHz < StandardLowestHz ? ExtendedFftCeiling : StandardFftCeiling;

    public int ExtendedFftCeiling => SweepResolution.ExtendedCeilingFor(EffectiveSampleRateHz);

    /// <summary>The lowest tone this run can read a level at, given everything above.</summary>
    public double LowestMeasurableHz => SweepResolution.LowestToneHz(
        EffectiveSampleRateHz, AdaptiveResolution ? FftCeiling : FftSize, MinFundamentalBins);

    /// <summary>The FFT size a given point is analyzed at — constant unless <see cref="AdaptiveResolution"/> is on.</summary>
    public int FftSizeAt(double freqHz) => AdaptiveResolution
        ? SweepResolution.FftSizeAt(freqHz, EffectiveSampleRateHz, FftCeiling)
        : FftSize;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for anything the runner cannot act on.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Direction))
            throw new ArgumentOutOfRangeException(nameof(Direction), Direction, "Direction must be LeftToRight, RightToLeft or Both.");

        if (StartFreqHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(StartFreqHz), StartFreqHz, "Start frequency must be greater than 0 Hz.");
        if (EndFreqHz <= StartFreqHz)
            throw new ArgumentOutOfRangeException(nameof(EndFreqHz), EndFreqHz, "End frequency must be above the start frequency.");
        if (StepsPerOctave is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(StepsPerOctave), StepsPerOctave, "Steps per octave must be between 1 and 48.");
        if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(FftSize), FftSize, "FFT size must be a power of two.");

        if (AverageCount is < MinAverageCount or > MaxAverageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AverageCount), AverageCount,
                $"Averages must be between {MinAverageCount} and {MaxAverageCount}.");
        }

        if (SampleRateHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), SampleRateHz, "Sample rate must be positive.");

        if (LevelMode is not (ThdSweepLevelMode.GeneratorVoltage or ThdSweepLevelMode.OutputVoltage or ThdSweepLevelMode.OutputPower))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelMode), LevelMode,
                "A crosstalk run is driven by generator (input) voltage, DUT output voltage or DUT output power.");
        }

        if (LevelUnit.IsPower() && LevelMode != ThdSweepLevelMode.OutputPower)
            throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "Watts only make sense for the output-power level mode.");

        if (LevelMode == ThdSweepLevelMode.OutputPower)
        {
            if (!LevelUnit.IsPower())
                throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "An output-power level is specified in watts or milliwatts.");
            if (!(LoadOhms > 0.0))
                throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "A target power needs a load above 0 ohm to name a voltage.");
        }

        if (double.IsNaN(LevelValue) || double.IsInfinity(LevelValue))
            throw new ArgumentOutOfRangeException(nameof(LevelValue), LevelValue, "Amplitude must be a finite number.");

        if (LevelUnit != ThdSweepLevelUnit.Dbv && LevelValue <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelValue), LevelValue,
                LevelUnit.IsPower() ? "A target power must be positive." : "A voltage amplitude must be positive.");
        }

        RequireResolvableTone();
    }

    /// <summary>Rejects a start frequency whose tone would sit on top of DC at the size the run will use — see <see cref="SteppedResponseRequest"/>.</summary>
    private void RequireResolvableTone()
    {
        double sampleRateHz = EffectiveSampleRateHz;
        int fftSize = FftSizeAt(StartFreqHz);
        double binSizeHz = sampleRateHz / fftSize;

        if (StartFreqHz >= MinFundamentalBins * binSizeHz)
            return;

        string advice = AdaptiveResolution
            ? $"Start above {LowestMeasurableHz:0.##} Hz."
            : $"Turn on adaptive resolution, which sizes the low points for the frequency asked for (down to "
                + $"{MinFundamentalBins * sampleRateHz / ExtendedFftCeiling:0.##} Hz), "
                + $"or start above {MinFundamentalBins * binSizeHz:0.##} Hz.";

        throw new ArgumentOutOfRangeException(
            nameof(StartFreqHz),
            StartFreqHz,
            $"{StartFreqHz:0.##} Hz is only {StartFreqHz / binSizeHz:0.#} bins up at {sampleRateHz / 1000:0.##} kHz / {fftSize} " +
            $"({binSizeHz:0.###} Hz per bin); reading a level needs at least {MinFundamentalBins}. " +
            advice);
    }

}

/// <summary>
/// The immediate answer to a start request. <see cref="PointCount"/> is per pass; the status's
/// <c>PointCount</c> is the total over every pass, and <see cref="EstimatedSeconds"/> covers them all.
/// </summary>
public sealed record CrosstalkStarted(
    int PassCount,
    int PointCount,
    IReadOnlyList<double> FrequenciesHz,
    double SampleRateHz,
    int AverageCount,
    double EstimatedSeconds);
