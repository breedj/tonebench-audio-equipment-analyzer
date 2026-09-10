using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// A stepped-sine Bode plot's configuration: the THD-vs-frequency sweep's stimulus, measured for
/// level and phase instead of distortion.
///
/// <para><b>Why this exists next to the chirp.</b> The chirp excites the whole band at once, and
/// against a transformer-coupled DUT that is not the same measurement: peak flux goes as V/f, so
/// the sweep's sub-10 Hz stretch asks several times more of the core than the band anyone reads.
/// Measured against a tube amplifier at −18 dBV the chirp came back up to <b>1.2 dB below a stepped
/// sine across 9–16 Hz</b> while agreeing everywhere else — see
/// <see cref="FrequencyResponseRequest.DefaultChirpStartHz"/>, whose whole purpose is to probe that
/// discrepancy without being able to settle it. One tone at a time settles it.</para>
///
/// <para>Flat and immutable, mirroring <see cref="ThdSweepRequest"/>: a run takes minutes and must
/// not change shape underneath itself because someone moved a slider in another browser tab.</para>
/// </summary>
/// <param name="GenerateLeft">
/// Play the tone on the left generator channel. Note this selects the <b>generator</b> channels
/// only — unlike <see cref="ThdSweepRequest.ProcessLeft"/>, which also picks what is analyzed.
/// Both inputs are always captured here, because the gain trace is a ratio between them and the two
/// have to come from the same tick.
/// </param>
/// <param name="RightChannelIsReference">
/// The right input carries the stimulus, so gain is the true L/R transfer function and its phase is
/// a real phase difference. Without it there is nothing to compare against but the commanded
/// generator level: gain becomes the left channel's own response shifted by the drive level, and
/// phase is not measurable at all — a stepped sine has no capture trigger, so a single channel's
/// absolute phase is wherever the record happened to land.
/// </param>
public sealed record SteppedResponseRequest(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    ThdSweepLevelMode LevelMode,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    double LoadOhms,
    bool GenerateLeft,
    bool GenerateRight,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool AdaptiveResolution,
    bool RightChannelIsReference)
{
    /// <summary>The analysis size a fixed-resolution run uses, and the panel's default. The same figure both sweeps fix.</summary>
    public const int DefaultFftSize = 131072;

    /// <summary>The sample rate a fixed-resolution run uses, and the panel's default.</summary>
    public const double DefaultSampleRateHz = 192000.0;

    /// <summary>
    /// Bins the tone must be up before its level can be read at all.
    ///
    /// <para>Deliberately <b>not</b> <see cref="ThdSweepRequest.MinFundamentalBins"/>'s 5. That
    /// figure exists because <see cref="HarmonicAnalyzer"/> sums ±3 bins around each harmonic, so
    /// below ~4 bins the fundamental is summed into its own second harmonic and THD approaches
    /// 100 %. This measurement reads no harmonics; the only requirement is that the tone's own
    /// mainlobe (the coherent bin ±1 under Hann) clears DC, which is 3 bins with a bin to spare.
    /// Applying the harmonic limit here would reject perfectly measurable low-frequency points for
    /// a reason that does not apply to them — and the bottom of the band is exactly what this
    /// measurement was added to look at.</para>
    /// </summary>
    public const int MinFundamentalBins = 3;

    /// <summary>
    /// Fraction of Nyquist the highest point may reach. A tone at Nyquist itself lands in the one
    /// bin <see cref="PsdSpectrum"/> gives half weight.
    /// </summary>
    public const double MaxFrequencyNyquistFraction = 0.98;

    public const int MinAverageCount = 1;

    public const int MaxAverageCount = 10;

    /// <summary>The requested drive level in dBV, whatever unit it was entered in. Watts convert through the load: V = sqrt(P·R).</summary>
    public double LevelDbv => LevelUnit switch
    {
        ThdSweepLevelUnit.Dbv => LevelValue,
        ThdSweepLevelUnit.Volt => VoltToDbv(LevelValue),
        ThdSweepLevelUnit.MilliVolt => VoltToDbv(LevelValue / 1000.0),
        ThdSweepLevelUnit.Watt => VoltToDbv(Math.Sqrt(Math.Max(LevelValue, 0.0) * LoadOhms)),
        _ => throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "Unknown drive level unit."),
    };

    /// <summary>
    /// True when the generator amplitude has to be found by probing rather than taken from the
    /// request — both output-referred modes name a level at the DUT's <em>output</em>. Same
    /// predicate, and for the same reason, as <see cref="FrequencyResponseRequest.SolvesGeneratorLevel"/>.
    /// </summary>
    public bool SolvesGeneratorLevel => LevelMode != ThdSweepLevelMode.GeneratorVoltage;

    /// <summary>
    /// The sample rate this run will actually use.
    ///
    /// <para>Under <see cref="AdaptiveResolution"/> this asks <see cref="SweepResolution.SampleRateFor"/>
    /// for a rate that keeps <b>one</b> harmonic in band rather than
    /// <see cref="SweepResolution.HarmonicHeadroomOrder"/>'s five, because there are no harmonics in
    /// this measurement — only the tone itself. That is a real saving rather than a corner cut: a
    /// 20 kHz sweep then runs at 48 kHz instead of 192 kHz, a quarter of the USB and FFT load, with
    /// the top point still comfortably below the anti-alias filter.</para>
    /// </summary>
    public double EffectiveSampleRateHz => AdaptiveResolution
        ? SweepResolution.SampleRateFor(EndFreqHz, harmonicOrder: 1, nyquistFraction: MaxFrequencyNyquistFraction)
        : SampleRateHz;

    /// <summary>The highest tone this run will play, given the rate it settles on.</summary>
    public double MaxFrequencyHz => MaxFrequencyNyquistFraction * EffectiveSampleRateHz / 2.0;

    /// <summary>
    /// What phase this configuration can measure. <c>arg(L/R)</c> is computed identically in both
    /// measurable cases — what <see cref="RightChannelIsReference"/> changes is what the number
    /// means, and therefore what the UI is entitled to call it.
    ///
    /// <para><see cref="GenerateRight"/> alone decides whether there is a phase at all:
    /// <see cref="Validate"/> already rejects a reference with no stimulus on it, so a right channel
    /// that is not generating is the only way to have nothing to compare against.</para>
    /// </summary>
    public ResponsePhaseKind PhaseKind => !GenerateRight
        ? ResponsePhaseKind.None
        : RightChannelIsReference ? ResponsePhaseKind.DutResponse : ResponsePhaseKind.ChannelDifference;

    /// <summary>
    /// The FFT ceiling a run reaching below <see cref="StandardLowestHz"/> is allowed to use, in
    /// place of the sweep's own.
    ///
    /// <para><see cref="SweepResolution.MaxRecordSeconds"/> bounds every sweep FFT to 0.6827 s, which
    /// is 1.465 Hz bins at <em>every</em> supported rate and so a 4.4 Hz floor at three bins. That
    /// ceiling exists so adaptive resolution can never make a THD sweep slower than fixed mode — a
    /// trade that makes sense across fifty points whose bottom end is 20 Hz, and no sense at all here,
    /// where the bottom of the band is the entire reason the measurement exists and only a handful of
    /// points are down there. The chirp reaches 0.5 Hz; this is what lets a stepped sine follow it.</para>
    ///
    /// <para>Expressed as a <b>record time</b>, for exactly the reason
    /// <see cref="SweepResolution.MaxRecordSeconds"/> is: a time bound means the same thing at every
    /// sample rate, so the floor it produces is 1.1 Hz at 48, 96 and 192 kHz alike. A sample-count
    /// bound would not — 524288 points is 2.73 s at 192 kHz but 10.9 s at 48 kHz, which would make
    /// the lower rates ruinously slow for no extra resolution.</para>
    ///
    /// <para>2.73 s is the chirp's own longest record (524288 at 192 kHz), so it is a record length
    /// this pipeline is already known to run and a bound this app already lives with.</para>
    /// </summary>
    /// <summary>
    /// The FFT ceiling a run reaching below <see cref="StandardLowestHz"/> is allowed to use, in
    /// place of the sweep's own. See <see cref="SweepResolution.ExtendedMaxRecordSeconds"/>, which
    /// carries the reasoning and is shared with the stepped-sine impedance run.
    /// </summary>
    public const double ExtendedMaxRecordSeconds = SweepResolution.ExtendedMaxRecordSeconds;

    /// <summary>Absolute cap, so a future lower sample rate cannot ask for an unbounded size.</summary>
    public const int ExtendedMaxFftSize = SweepResolution.ExtendedMaxFftSize;

    /// <summary>The FFT ceiling the sweeps use at this run's sample rate — a record-time bound, so 1.465 Hz bins at every rate.</summary>
    public int StandardFftCeiling => SweepResolution.BoundsFor(EffectiveSampleRateHz).Ceiling;

    /// <summary>The lowest tone the sweeps' own ceiling can read a level at — 4.4 Hz at every supported rate.</summary>
    public double StandardLowestHz =>
        SweepResolution.LowestToneHz(EffectiveSampleRateHz, StandardFftCeiling, MinFundamentalBins);

    /// <summary>
    /// The FFT ceiling this run may reach. Raised automatically, and only, when the run asks to start
    /// below what the standard ceiling can measure — so an ordinary 20 Hz sweep is completely
    /// unaffected and pays nothing for this.
    /// </summary>
    public int FftCeiling => StartFreqHz < StandardLowestHz ? ExtendedFftCeiling : StandardFftCeiling;

    /// <summary>The raised ceiling itself: the longest power-of-two record inside <see cref="ExtendedMaxRecordSeconds"/>.</summary>
    public int ExtendedFftCeiling => SweepResolution.ExtendedCeilingFor(EffectiveSampleRateHz);

    /// <summary>
    /// The lowest tone this run can read a level at, given everything above. 4.4 Hz normally; 1.1 Hz
    /// at 192 kHz (and lower still at the lower rates) once the raised ceiling is in play.
    /// </summary>
    public double LowestMeasurableHz => SweepResolution.LowestToneHz(
        EffectiveSampleRateHz, AdaptiveResolution ? FftCeiling : FftSize, MinFundamentalBins);

    /// <summary>
    /// The FFT size a given point is analyzed at — constant unless <see cref="AdaptiveResolution"/> is
    /// on. Deliberately not <see cref="SweepResolution.FftSizeFor"/>: that clamps to the sweeps'
    /// record-time ceiling, which is the thing <see cref="FftCeiling"/> exists to lift. Everything
    /// else about the ladder is the sweeps' own and is deliberately shared.
    /// </summary>
    public int FftSizeAt(double freqHz) => AdaptiveResolution
        ? SweepResolution.FftSizeAt(freqHz, EffectiveSampleRateHz, FftCeiling)
        : FftSize;

    /// <summary>Analysis resolution at a given point, Hz per bin.</summary>
    public double BinSizeHzAt(double freqHz) => EffectiveSampleRateHz / FftSizeAt(freqHz);

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

        if (AverageCount is < MinAverageCount or > MaxAverageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AverageCount), AverageCount,
                $"Averages must be between {MinAverageCount} and {MaxAverageCount}.");
        }

        if (SampleRateHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), SampleRateHz, "Sample rate must be positive.");
        if (!GenerateLeft && !GenerateRight)
            throw new ArgumentOutOfRangeException(nameof(GenerateLeft), "The tone has to be played on at least one channel.");

        // The reference channel has to actually be fed the stimulus, or "gain" is the left channel
        // divided by whatever noise the right input happens to be sitting on — a plausible-looking
        // trace of nothing. Rejected rather than quietly downgraded to the unreferenced mode,
        // because the two produce different numbers and the user asked for the referenced one.
        if (RightChannelIsReference && !GenerateRight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RightChannelIsReference), RightChannelIsReference,
                "The right input is the reference, so the tone has to be played on the right channel too.");
        }

        if (LevelMode is not (ThdSweepLevelMode.GeneratorVoltage or ThdSweepLevelMode.OutputVoltage or ThdSweepLevelMode.OutputPower))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelMode), LevelMode,
                "A stepped response is driven by generator (input) voltage, DUT output voltage or DUT output power.");
        }

        // Watts are only a level once a load says what voltage they are — the same rule
        // ThdSweepRequest and FrequencyResponseRequest both apply.
        if (LevelUnit == ThdSweepLevelUnit.Watt && LevelMode != ThdSweepLevelMode.OutputPower)
            throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "Watts only make sense for the output-power level mode.");

        if (LevelMode == ThdSweepLevelMode.OutputPower)
        {
            if (LevelUnit != ThdSweepLevelUnit.Watt)
                throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "An output-power level is specified in watts.");
            if (!(LoadOhms > 0.0))
                throw new ArgumentOutOfRangeException(nameof(LoadOhms), LoadOhms, "A target power needs a load above 0 ohm to name a voltage.");
        }

        if (double.IsNaN(LevelValue) || double.IsInfinity(LevelValue))
            throw new ArgumentOutOfRangeException(nameof(LevelValue), LevelValue, "Amplitude must be a finite number.");

        if (LevelUnit != ThdSweepLevelUnit.Dbv && LevelValue <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelValue), LevelValue,
                LevelUnit == ThdSweepLevelUnit.Watt ? "A target power must be positive." : "A voltage amplitude must be positive.");
        }

        RequireResolvableTone();
    }

    /// <summary>
    /// Rejects a start frequency whose tone would sit on top of DC at the size the run will use.
    /// The message names the FFT size that would work, because that is the only thing the caller can
    /// actually change about it.
    /// </summary>
    private void RequireResolvableTone()
    {
        double sampleRateHz = EffectiveSampleRateHz;
        int fftSize = FftSizeAt(StartFreqHz);
        double binSizeHz = sampleRateHz / fftSize;

        if (StartFreqHz >= MinFundamentalBins * binSizeHz)
            return;

        // Adaptive sizing has already reached for the largest record this run permits, so the only
        // thing left to change is the start frequency. Fixed mode has one more option, and naming it
        // matters because the panel offers no FFT-size control at all -- advice to "use a larger FFT"
        // would point at a knob that is not there.
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

    private static double VoltToDbv(double volts) => 20.0 * Math.Log10(Math.Max(volts, 1e-12));
}

/// <summary>
/// The immediate answer to a start request — what the run will do, before it does it. Mirrors
/// <see cref="ThdSweepStarted"/>'s role; the run itself takes minutes and happens in the background.
/// </summary>
public sealed record SteppedResponseStarted(
    int PointCount,
    IReadOnlyList<double> FrequenciesHz,
    double SampleRateHz,
    int AverageCount,
    double EstimatedSeconds);
