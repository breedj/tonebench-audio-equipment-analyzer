using System.Numerics;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// One output-impedance (damping factor) run: the DUT's gain measured with a stepped sine at a
/// fixed output level into two different loads, and the output impedance solved from the two —
/// <see cref="OutputImpedanceMath.Solve"/> — against frequency. The tutorial's <i>PWR Output
/// Impedance</i> uses a load and then no load; here the second pass is another load by default
/// (8 Ω then 4 Ω), because an output transformer with signal on it and nothing across its secondary
/// is how tube amplifiers are killed, and "open" is the explicit option for the amplifiers that can
/// take it. The acquisition half is the stepped sweep — the same grid, level modes and adaptive
/// resolution as <see cref="SteppedResponseRequest"/> — which is why this runs on
/// <see cref="SteppedCaptureSupport"/> like the crosstalk measurement does.
///
/// <para>Three resistances, on purpose. <see cref="LoadOhms"/> is the resistor connected for the
/// first pass, and what a power target converts through; <see cref="SecondLoadOhms"/> the one for
/// the second (null = open); <see cref="NominalOhms"/> is what the damping factor is quoted
/// against, because a specification sheet always says 8 Ω whatever tap the amplifier was measured
/// on, while a tube amplifier's 4 Ω tap is measured with 4 Ω.</para>
///
/// <para><see cref="RightChannelIsReference"/> decides what the ratio is. Off, each pass reads the
/// tone's <em>level</em> on the left input and the ratio is a ratio of magnitudes — the QA40x's
/// figure, which assumes Zout is in phase with the load and under-reads a reactive one (an output
/// coil's j0.19 Ω into 8 Ω moves the level by 0.03 dB, as if it were 0.03 Ω). On, the right output
/// is looped to the right input in both passes so each gain is a complex value and the ratio is
/// complex too: Zout comes out with its phase and the coil is recovered.</para>
/// </summary>
public sealed record OutputImpedanceRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    ThdSweepLevelMode LevelMode,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    double LoadOhms,
    double? SecondLoadOhms,
    double NominalOhms,
    bool RightChannelIsReference,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool AdaptiveResolution,
    /// <summary>
    /// What to play. <see cref="ImpedanceStimulus.SteppedSine"/> by default, so every caller and
    /// every measurement stored before the broadband pair existed measures exactly what it did.
    /// </summary>
    ImpedanceStimulus Stimulus = ImpedanceStimulus.SteppedSine,
    // The literals, not the consts below: a primary constructor's default value cannot reference a
    // member of the type it is declaring. Kept equal to them by OutputImpedanceStartRequestTests.
    /// <summary>The broadband analysed span in Hz; the sample rate is twice it. Read only by a chirp or noise run.</summary>
    double FrequencySpanHz = 24_000.0,
    /// <summary>Multiplies the broadband record length, i.e. divides the bin size. Read only by a chirp or noise run.</summary>
    int ResolutionMultiplier = 1,
    /// <summary>Where the chirp starts. Read only by a chirp run.</summary>
    double ChirpStartHz = 10.0,
    /// <summary>Where the pink sequence stops rising towards DC. Read only by a noise run.</summary>
    double NoiseCutoffHz = 5.0,
    /// <summary>
    /// The fractional-octave band the broadband curve is smoothed over. Inert on a stepped run,
    /// whose every sample is a measurement on a grid already coarser than the narrowest band.
    /// </summary>
    int SmoothingDenominator = 3)
{
    public const int DefaultFftSize = SteppedResponseRequest.DefaultFftSize;

    public const double DefaultSampleRateHz = SteppedResponseRequest.DefaultSampleRateHz;

    public const int MinFundamentalBins = SteppedResponseRequest.MinFundamentalBins;

    public const double MaxFrequencyNyquistFraction = SteppedResponseRequest.MaxFrequencyNyquistFraction;

    public const int MinAverageCount = SteppedResponseRequest.MinAverageCount;

    public const int MaxAverageCount = SteppedResponseRequest.MaxAverageCount;

    /// <summary>The passes a run makes, in order: the first load (the level target is defined into it), then the second load or open.</summary>
    public const int PassCount = 2;

    public const double DefaultFrequencySpanHz = 24_000.0;

    public const double DefaultChirpStartHz = 10.0;

    public const double DefaultNoiseCutoffHz = 5.0;

    /// <summary>
    /// 1/3 octave, the widest band on offer, and chosen for signal-to-noise rather than for looks.
    /// Zout is a <i>difference</i> of two nearly equal gains — with 8 Ω then 4 Ω the ratio is
    /// q ≈ 1 − Zout/8, i.e. about a millidecibel per milliohm — and a broadband stimulus spreads
    /// its energy over the whole span, so a single bin is nowhere near that good. The smoothing band
    /// is what buys it back: an RMS over the ~600 bins a 1/3-octave band spans at 1 kHz is worth
    /// some 28 dB, which is the difference between a readable milliohm figure and noise.
    /// </summary>
    public const int DefaultSmoothingDenominator = 3;

    /// <summary>Whether the run is a tone grid rather than one broadband record per pass.</summary>
    public bool IsStepped => Stimulus == ImpedanceStimulus.SteppedSine;

    /// <summary>Whether the broadband stimulus is the periodic pink sequence rather than the chirp.</summary>
    public bool IsNoise => Stimulus == ImpedanceStimulus.PinkNoise;

    /// <summary>
    /// The acquisition half of a broadband run, as the frequency-response measurement would express
    /// it — the one place the sweep's rules and arithmetic live, borrowed exactly as
    /// <see cref="ImpulseResponseRequest.AsAcquisition"/> borrows them.
    ///
    /// <para><b>The bandwidth reference is left at its default</b> (200 Hz – 2 kHz), because this
    /// screen has no reference control and the impulse-response screen is in the same position for
    /// the same reason. It decides one thing here: the band an output-referred target — "1 W into
    /// 8 Ω" — is measured over, which on an amplifier flat through the midband is the figure anyone
    /// would have meant. Nonsense on a stepped run, which has no span and probes one tone, so
    /// nothing may call this without checking <see cref="IsStepped"/> first.</para>
    /// </summary>
    public FrequencyResponseRequest AsBroadbandAcquisition() =>
        new(FrequencySpanHz,
            ResolutionMultiplier,
            LevelMode,
            LevelValue,
            LevelUnit,
            RightChannelIsReference,
            SmoothingDenominator,
            AverageCount,
            Continuous: false,
            ChirpStartHz: ChirpStartHz,
            LoadOhms: LoadOhms,
            Stimulus: IsNoise ? ResponseStimulus.PinkNoise : ResponseStimulus.Chirp,
            NoiseCutoffHz: NoiseCutoffHz);

    /// <summary>True when the second pass is taken with nothing on the output — the tutorial's arrangement.</summary>
    public bool SecondPassIsOpen => SecondLoadOhms is null;

    /// <summary>The requested drive level in dBV, whatever unit it was entered in. Watts convert through the load: V = sqrt(P·R).</summary>
    public double LevelDbv => LevelUnit.ToDbv(LevelValue, LoadOhms);

    /// <summary>True when the generator amplitude has to be found by probing the DUT's output at 1 kHz, in the loaded pass.</summary>
    public bool SolvesGeneratorLevel => LevelMode != ThdSweepLevelMode.GeneratorVoltage;

    /// <summary>
    /// What the acquisition is clocked at, whichever stimulus is playing: the broadband span's own
    /// rate on a chirp or noise run, the grid's on a stepped one. The runner starts the stream with
    /// this, so a branch missing here is a broadband run measured on a stepped sweep's rate.
    /// </summary>
    public double EffectiveSampleRateHz => !IsStepped
        ? AsBroadbandAcquisition().SampleRateHz
        : AdaptiveResolution
            ? SweepResolution.SampleRateFor(EndFreqHz, harmonicOrder: 1, nyquistFraction: MaxFrequencyNyquistFraction)
            : SampleRateHz;

    /// <summary>The broadband record length in samples. Meaningless on a stepped run.</summary>
    public int BroadbandFftSize => AsBroadbandAcquisition().FftSize;

    /// <summary>The broadband bin size in Hz. Meaningless on a stepped run.</summary>
    public double BroadbandBinHz => AsBroadbandAcquisition().BinHz;

    /// <summary>The bottom of the analysed band on a broadband run.</summary>
    public double AnalysisLowHz => AsBroadbandAcquisition().AnalysisLowHz;

    /// <summary>The top of the analysed band on a broadband run.</summary>
    public double AnalysisHighHz => AsBroadbandAcquisition().AnalysisHighHz;

    public double MaxFrequencyHz => MaxFrequencyNyquistFraction * EffectiveSampleRateHz / 2.0;

    public int StandardFftCeiling => SweepResolution.BoundsFor(EffectiveSampleRateHz).Ceiling;

    public double StandardLowestHz =>
        SweepResolution.LowestToneHz(EffectiveSampleRateHz, StandardFftCeiling, MinFundamentalBins);

    public int FftCeiling => StartFreqHz < StandardLowestHz ? ExtendedFftCeiling : StandardFftCeiling;

    public int ExtendedFftCeiling => SweepResolution.ExtendedCeilingFor(EffectiveSampleRateHz);

    public double LowestMeasurableHz => SweepResolution.LowestToneHz(
        EffectiveSampleRateHz, AdaptiveResolution ? FftCeiling : FftSize, MinFundamentalBins);

    public int FftSizeAt(double freqHz) => AdaptiveResolution
        ? SweepResolution.FftSizeAt(freqHz, EffectiveSampleRateHz, FftCeiling)
        : FftSize;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for anything the runner cannot act on.</summary>
    public void Validate()
    {
        // Only the settings some run will actually read, the ImpedanceRequest rule: a stepped grid
        // left at a figure a broadband run never looks at must not refuse that run, or a stimulus
        // becomes unreachable because of a control the panel has hidden.
        if (IsStepped)
        {
            if (StartFreqHz <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(StartFreqHz), StartFreqHz, "Start frequency must be greater than 0 Hz.");
            if (EndFreqHz <= StartFreqHz)
                throw new ArgumentOutOfRangeException(nameof(EndFreqHz), EndFreqHz, "End frequency must be above the start frequency.");
            if (StepsPerOctave is < 1 or > 48)
                throw new ArgumentOutOfRangeException(nameof(StepsPerOctave), StepsPerOctave, "Steps per octave must be between 1 and 48.");
            if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(FftSize), FftSize, "FFT size must be a power of two.");
        }
        else
        {
            // Delegated whole: the span, the resolution and the chirp start are the frequency
            // response's rules, and a second copy of them here is a second thing to keep in step.
            AsBroadbandAcquisition().Validate();

            if (SmoothingDenominator < 1)
                throw new ArgumentOutOfRangeException(nameof(SmoothingDenominator), SmoothingDenominator, "The smoothing band's denominator must be at least 1.");
        }

        if (AverageCount is < MinAverageCount or > MaxAverageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AverageCount), AverageCount,
                $"Averages must be between {MinAverageCount} and {MaxAverageCount}.");
        }

        if (SampleRateHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), SampleRateHz, "Sample rate must be positive.");

        // Both resistances are read at every point, not only under the power mode: the load is the
        // R in Zout = R·(ratio − 1) and the nominal is the numerator of the damping factor.
        if (!(LoadOhms > 0.0) || double.IsInfinity(LoadOhms))
            throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "The first load must be a positive resistance in ohms.");
        if (SecondLoadOhms is { } second)
        {
            if (!(second > 0.0) || double.IsInfinity(second))
                throw new ArgumentOutOfRangeException(nameof(SecondLoadOhms), second, "The second load must be a positive resistance in ohms, or open.");
            // Two equal loads measure the same divider twice and solve for nothing — the formula's
            // denominator is zero at every point.
            if (Math.Abs(second - LoadOhms) < 1e-6 * Math.Max(1.0, LoadOhms))
                throw new ArgumentOutOfRangeException(nameof(SecondLoadOhms), second, "The two loads must differ; two passes into the same load say nothing about the output impedance.");
        }
        if (!(NominalOhms > 0.0) || double.IsInfinity(NominalOhms))
            throw new ArgumentOutOfRangeException(nameof(NominalOhms), NominalOhms, "The nominal impedance the damping factor is quoted against must be positive.");

        if (LevelMode is not (ThdSweepLevelMode.GeneratorVoltage or ThdSweepLevelMode.OutputVoltage or ThdSweepLevelMode.OutputPower))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelMode), LevelMode,
                "An output-impedance run is driven by generator (input) voltage, DUT output voltage or DUT output power.");
        }

        if (LevelUnit.IsPower() && LevelMode != ThdSweepLevelMode.OutputPower)
            throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "Watts only make sense for the output-power level mode.");

        if (LevelMode == ThdSweepLevelMode.OutputPower && !LevelUnit.IsPower())
            throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "An output-power level is specified in watts or milliwatts.");

        if (double.IsNaN(LevelValue) || double.IsInfinity(LevelValue))
            throw new ArgumentOutOfRangeException(nameof(LevelValue), LevelValue, "Amplitude must be a finite number.");

        if (LevelUnit != ThdSweepLevelUnit.Dbv && LevelValue <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelValue), LevelValue,
                LevelUnit.IsPower() ? "A target power must be positive." : "A voltage amplitude must be positive.");
        }

        // A broadband run has no tone to resolve; its own band check is AsBroadbandAcquisition's.
        if (IsStepped)
            RequireResolvableTone();
    }

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

/// <summary>The arithmetic of the load-step measurement, on its own so it can be pinned.</summary>
public static class OutputImpedanceMath
{
    /// <summary>
    /// The output impedance behind two gains into two loads. With <c>q = G₂/G₁</c> (the second pass
    /// relative to the first; complex when both carry a phase, a plain magnitude ratio otherwise) and
    /// the amplifier a source behind <c>Z</c>: <c>G₁ = R₁/(R₁+Z)</c>, <c>G₂ = R₂/(R₂+Z)</c>, so
    /// <c>Z = R₁·R₂·(q − 1) / (R₂ − R₁·q)</c>. With the second pass open (<paramref name="secondLoadOhms"/>
    /// null) that is the tutorial's <c>Z = R₁·(q − 1)</c>.
    ///
    /// <para>A magnitude-only ratio assumes Z in phase with the loads, and a reactive Z reads low —
    /// see <see cref="OutputImpedanceRequest"/>. For any positive Z the denominator has the sign of
    /// <c>R₂ − R₁</c> (it is <c>R₂·(R₂ − R₁)/(R₂ + Z)</c>); a ratio that puts it at zero or the wrong
    /// side — which no positive Z produces, but noise on a very high damping factor can — is clamped
    /// to <see cref="ImpedanceMath.MaxOhms"/> rather than returned as a negative or infinite resistance.</para>
    /// </summary>
    public static Complex Solve(Complex ratioSecondOverFirst, double firstLoadOhms, double? secondLoadOhms)
    {
        var q = ratioSecondOverFirst;
        if (secondLoadOhms is not { } r2)
            return firstLoadOhms * (q - Complex.One);

        var denominator = r2 - (firstLoadOhms * q);
        if (!(denominator.Real * (r2 - firstLoadOhms) > 0.0) && Math.Abs(denominator.Imaginary) < 1e-12)
            return new Complex(ImpedanceMath.MaxOhms, 0.0);

        var z = firstLoadOhms * r2 * (q - Complex.One) / denominator;
        return z.Magnitude > ImpedanceMath.MaxOhms ? new Complex(ImpedanceMath.MaxOhms, 0.0) : z;
    }
}

/// <summary>
/// The immediate answer to a start request. <see cref="PointCount"/> is per pass; the status's
/// <c>PointCount</c> is the total over both passes, and <see cref="EstimatedSeconds"/> covers both
/// (not the wait between them, which is the user's).
/// </summary>
public sealed record OutputImpedanceStarted(
    int PassCount,
    int PointCount,
    IReadOnlyList<double> FrequenciesHz,
    double SampleRateHz,
    int AverageCount,
    double EstimatedSeconds);

/// <summary>
/// Where the output-impedance measurement is and what it has found.
///
/// <para>The point counters run over both passes so the progress line stays monotone; the phase
/// says which pass is being measured (<see cref="FrequencyResponsePhase.WaitingForLoadChange"/>
/// between them). <see cref="Trace"/> is the Zout curve, an impedance trace in every respect, and
/// is null until the open pass has completed at least two points. Every reading below it is
/// <b>null when absent, never NaN</b> — <c>System.Text.Json</c> refuses a NaN part way through the
/// body, and the client then sees a 200 with a truncated document and nothing to say why.</para>
/// </summary>
/// <param name="InputRangeDbv">The one input range both passes were read at — the first pass picks it and the second holds it.</param>
/// <param name="SecondLoadOhms">The second pass's load; null when it was taken open.</param>
/// <param name="Warning">Set when the range moved between the passes at some point (a step-up firing as the clip backstop), which puts that point's ratio in doubt.</param>
public sealed record OutputImpedanceStatus(
    bool Running,
    FrequencyResponsePhase Phase,
    int PassesCompleted,
    /// <summary>
    /// Points on the tone grid, or <b>null</b> on a chirp or noise run, which folds whole records
    /// into one estimate and has no grid at all — the <see cref="ImpedanceStatus"/> convention, and
    /// what the progress line branches on rather than on the phase. Null, never zero: zero is a
    /// stepped run that has measured nothing yet.
    /// </summary>
    int? PointsMeasured,
    int? PointCount,
    double GeneratorAmpDbv,
    double? InputRangeDbv,
    double BinHz,
    double SampleRateHz,
    int FftSize,
    double LoadOhms,
    double? SecondLoadOhms,
    double NominalOhms,
    bool RightChannelIsReference,
    string? Error,
    string? Warning,
    ImpedanceTrace? Trace,
    double? MedianOhms,
    double? ZoutAt1kOhms,
    double? DampingFactorAt1k,
    double? FirstGainAt1kDb,
    double? SecondGainAt1kDb,
    /// <summary>What the run on screen was played with. Defaulted so a caller that predates the broadband pair still compiles and still means what it said.</summary>
    ImpedanceStimulus Stimulus = ImpedanceStimulus.SteppedSine,
    /// <summary>Records folded in so far on a broadband pass, and how many it will take. Both 0 on a stepped run, which counts points instead.</summary>
    int AveragesTaken = 0,
    int AverageCount = 0);
