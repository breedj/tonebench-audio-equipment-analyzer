using System.Numerics;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// How an impedance run works out Z. Only <see cref="ReferenceResistor"/> is implemented; the field
/// exists on the wire from the start so adding the second method does not change the protocol.
/// </summary>
public enum ImpedanceMethod
{
    /// <summary>
    /// A known resistor in series with the DUT, one input channel either side of it. Single-pass and
    /// fully automatic, and what a bench fixture is normally wired for.
    /// </summary>
    ReferenceResistor = 0,

    /// <summary>
    /// Two measurements at different load resistances, which is how an amplifier's <i>output</i>
    /// impedance is measured — you cannot drive a generator through a resistor into a live amplifier
    /// output. Needs two passes with a physical load change between them. Not implemented yet.
    /// </summary>
    TwoLoad = 1,
}

/// <summary>What the generator plays while the impedance is measured.</summary>
public enum ImpedanceStimulus
{
    /// <summary>
    /// A periodic exponential chirp, the same one the frequency-response measurement uses. Fast, full
    /// resolution, and because Z is a ratio of two channels from <i>one</i> record the sweep's
    /// arbitrary rotation is common-mode and cancels, so the phase needs no trigger.
    /// </summary>
    Chirp = 0,

    /// <summary>
    /// One coherent tone per point. Minutes rather than seconds, and better wherever the divider
    /// output gets small: a chirp spreads its energy over thousands of bins, so each bin sits far
    /// below the sweep's own amplitude, while a tone puts the whole drive on the one bin being read.
    /// That is the difference between a usable and an unusable reading for a high-impedance DUT, for
    /// the bottom of the band, and for a shorted standard.
    /// </summary>
    SteppedSine = 1,

    /// <summary>Continuous noise, averaged as a Welch cross-spectrum. Not implemented yet.</summary>
    PinkNoise = 2,
}

/// <summary>Where the fixture's own series impedance comes from.</summary>
public enum ImpedanceCableMode
{
    /// <summary>Nothing subtracted.</summary>
    None = 0,

    /// <summary>Subtract a measured short — the same leads and connectors, measured the same way.</summary>
    Measured = 1,

    /// <summary>Subtract a typed-in resistance and inductance.</summary>
    Fixed = 2,
}

/// <summary>Which standard a run is capturing, or <see cref="None"/> for an actual measurement.</summary>
public enum ImpedanceCalTarget
{
    /// <summary>Measure the DUT.</summary>
    None = 0,

    /// <summary>
    /// Capture the open standard: nothing on the DUT terminals. This is what pins the two ADC
    /// channels' gain and phase against each other, and it is what makes the phase trace believable.
    /// </summary>
    Open = 1,

    /// <summary>Capture the short standard: the DUT terminals shorted, leaving the fixture's own series impedance.</summary>
    Short = 2,
}

/// <summary>
/// Which curve an <see cref="ImpedanceTrace"/> carries. The wire header's channel byte holds this,
/// disambiguated from a frequency-response frame's own kind by the frame's origin flag.
/// </summary>
public enum ImpedanceTraceKind
{
    /// <summary>The DUT's impedance: magnitude in dB relative to 1 ohm, phase in degrees.</summary>
    Impedance = 0,

    /// <summary>The open standard as it was measured, so the UI can show what the calibration looks like.</summary>
    OpenCal = 1,

    /// <summary>The short standard, likewise.</summary>
    ShortCal = 2,
}

/// <summary>
/// One impedance measurement, as configured.
///
/// <para><b>There is no output-referred level mode here, unlike the frequency response.</b> Those
/// modes name a level at the DUT's output and solve the generator backwards to reach it, which needs
/// the DUT's gain to be a meaningful quantity. A one-port impedance has no output: the level at the
/// measurement node is <c>Z/(R+Z)</c> of the drive, i.e. it is the answer rather than a target. So
/// the generator level is simply given, and the input range is measured from what comes back.</para>
/// </summary>
/// <param name="ReferenceOhms">The series resistor. Sensitivity of H to Z is best when this is near the impedance being measured.</param>
/// <param name="RightChannelIsReference">
/// Which channel taps the generator side of the reference resistor. False — left as reference — is
/// how a bench fixture is normally wired. <b>The simulator is the other way round</b>: its DUT sits
/// on the left channel only, so a run against <c>--simulate</c> wants this true.
/// </param>
/// <param name="CalTarget">Whether this run measures a DUT or captures a calibration standard.</param>
/// <param name="OpenCal">The stored open standard to apply, or null.</param>
/// <param name="ShortCal">The stored short standard to apply, or null. Only read when <paramref name="CableMode"/> is <see cref="ImpedanceCableMode.Measured"/>.</param>
/// <param name="AnalyzerInputOhms">
/// The analyzer's own input impedance, for de-embedding it from a high-impedance measurement, or
/// null to treat it as infinite. See <see cref="ImpedanceSolveOptions.AnalyzerInputOhms"/> — there is
/// deliberately no default.
/// </param>
/// <param name="CalibrationStimulus">
/// What the <see cref="CalTarget"/> runs play, chosen independently of <paramref name="Stimulus"/>.
/// <b>A standard is a curve in frequency, not in stimulus</b>: <see cref="ImpedanceCalibration.At"/>
/// interpolates in log f, and the open standard is the two ADC channels' gain and phase mismatch,
/// which does not depend on how it was excited. So the fast chirp can calibrate a fixture whose DUT
/// is then measured one tone at a time, which is the recommended pairing rather than a shortcut.
/// </param>
/// <param name="StartFreqHz">Lowest tone a stepped run plays. Unused by the chirp, which spans from <paramref name="ChirpStartHz"/>.</param>
/// <param name="EndFreqHz">Highest tone a stepped run plays, before the Nyquist clamp.</param>
/// <param name="StepsPerOctave">Density of the stepped log grid.</param>
/// <param name="AdaptiveResolution">Size each stepped point's record for its own frequency rather than for the bottom of the band.</param>
/// <param name="ApplySweepGate">
/// Whether to gate the deconvolved impulse response before reading Z off it, which removes the
/// harmonic packets a swept measurement of a distorting DUT picks up. Costs a factor of two in
/// resolution, so it is a choice rather than always-on.
/// </param>
public sealed record ImpedanceRequest(
    ImpedanceMethod Method,
    ImpedanceStimulus Stimulus,
    double ReferenceOhms,
    bool RightChannelIsReference,
    double FrequencySpanHz,
    int ResolutionMultiplier,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    int SmoothingDenominator,
    int AverageCount,
    bool Continuous = false,
    double ChirpStartHz = 2.0,
    ImpedanceCalTarget CalTarget = ImpedanceCalTarget.None,
    ImpedanceCableMode CableMode = ImpedanceCableMode.None,
    double FixedCableOhms = 0.0,
    double FixedCableMillihenry = 0.0,
    double? AnalyzerInputOhms = null,
    bool ApplySweepGate = false,
    ImpedanceCalibration? OpenCal = null,
    ImpedanceCalibration? ShortCal = null,
    ImpedanceStimulus CalibrationStimulus = ImpedanceStimulus.Chirp,
    double StartFreqHz = 20.0,
    double EndFreqHz = 20000.0,
    int StepsPerOctave = 5,
    bool AdaptiveResolution = true,
    int SteppedFftSize = SteppedResponseRequest.DefaultFftSize,
    double SteppedSampleRateHz = SteppedResponseRequest.DefaultSampleRateHz,
    // The literal, not the const: a primary constructor's default cannot reference a member of the
    // type it is declaring. Kept equal to DefaultCalibrationLevelDbv by a test.
    double CalibrationLevelValue = 0.0)
{
    /// <summary>
    /// The drive level a calibration run uses by default, in dBV.
    ///
    /// <para><b>Louder than a measurement's, and deliberately so.</b> A shorted fixture is the worst
    /// case this measurement has: the DUT channel sits at <c>Z_lead/(R + Z_lead)</c> of the reference
    /// channel, about &#8722;60 dB for ordinary leads on a 100 ohm reference — and the QA403 has one
    /// input range shared by both channels, set from the loud one. So the short standard is measured
    /// 60 dB down whatever the range does, and the only lever left is the drive.</para>
    ///
    /// <para>Measured on a real fixture at 96 kHz span, croc-clip leads: at &#8722;20 dBV the short
    /// read <b>0.207 ohms at &#8722;22.5 degrees</b> at 20 Hz with coherence 0.80, and the phase
    /// wandered between &#177;20 degrees across the bottom of the band — noise adding on top of a
    /// small signal. The same leads at 0 dBV read a flat <b>0.127 ohms at +0.8 degrees</b> with
    /// coherence 0.998, and the midband fit settled at 0.120 ohms + 0.59 uH. Above 5 kHz the two runs
    /// agreed to within a percent, because the leads' own inductance had already lifted the signal
    /// clear of the noise there.</para>
    ///
    /// <para>Raising it costs nothing in accuracy: the open and short are properties of the
    /// <i>fixture</i>, which is linear and passive, so the standard it presents does not depend on
    /// the level it is measured at. That is exactly why this is a separate field rather than the
    /// measurement's — a driver may want measuring gently, and the fixture never does.</para>
    /// </summary>
    public const double DefaultCalibrationLevelDbv = 0.0;

    /// <summary>The smallest reference resistor that is a resistor rather than a wire.</summary>
    public const double MinReferenceOhms = 0.1;

    /// <summary>And the largest that still puts a measurable signal on the node against the analyzer's own input.</summary>
    public const double MaxReferenceOhms = 10_000_000.0;

    /// <summary>
    /// The stimulus this particular run plays.
    ///
    /// <para><b>Everything derived branches on this, never on <see cref="Stimulus"/>.</b> A
    /// calibration capture may use a different stimulus from the measurement it will be applied to —
    /// that is what <see cref="CalibrationStimulus"/> is for — so reading the measurement's selector
    /// inside a calibration run would configure the wrong grid, the wrong sample rate and the wrong
    /// analysis band, and would do it silently.</para>
    /// </summary>
    public ImpedanceStimulus EffectiveStimulus => IsCalibration ? CalibrationStimulus : Stimulus;

    /// <summary>True when this run plays one tone at a time rather than a chirp.</summary>
    public bool IsStepped => EffectiveStimulus == ImpedanceStimulus.SteppedSine;

    /// <summary>
    /// Bins a stepped tone must be up before its level can be read at all — the stepped-sine
    /// response's figure, and for its reason: only the tone itself is read, so the requirement is
    /// that its mainlobe clears DC, not the harmonic analyzer's wider one.
    /// </summary>
    public const int MinFundamentalBins = SteppedResponseRequest.MinFundamentalBins;

    /// <summary>Fraction of Nyquist the highest stepped point may reach.</summary>
    public const double MaxFrequencyNyquistFraction = SteppedResponseRequest.MaxFrequencyNyquistFraction;

    /// <summary>
    /// Spans, resolution steps and averaging all follow the frequency response exactly — same
    /// hardware, same records. A stepped run instead picks the lowest rate that keeps its top tone in
    /// band (<b>one</b> harmonic, not the sweeps' five: nothing here reads a harmonic), which drops a
    /// 20 kHz run from 192 kHz to 48 kHz.
    /// </summary>
    public double SampleRateHz => IsStepped
        ? (AdaptiveResolution
            ? SweepResolution.SampleRateFor(EndFreqHz, harmonicOrder: 1, nyquistFraction: MaxFrequencyNyquistFraction)
            : SteppedSampleRateHz)
        : FrequencySpanHz * 2.0;

    /// <summary>
    /// The analysis size. For a stepped run this is the <b>first point's</b> — every point carries its
    /// own under adaptive resolution — which is the same thing the stepped-sine response reports on
    /// its status, and it exists so the status line has a bin width to quote at all.
    /// </summary>
    public int FftSize => IsStepped
        ? FftSizeAt(SteppedGridLowHz)
        : (int)Math.Round(SampleRateHz / FrequencyResponseRequest.BaseBinHz) * ResolutionMultiplier;

    public double BinHz => SampleRateHz / FftSize;

    public int BinCount => (FftSize / 2) + 1;

    public double ChirpEndHz => FrequencyResponseRequest.ChirpEndNyquistFraction * SampleRateHz / 2.0;

    /// <summary>The highest tone a stepped run will play, given the rate it settles on.</summary>
    public double MaxFrequencyHz => MaxFrequencyNyquistFraction * SampleRateHz / 2.0;

    /// <summary>The bottom of the stepped grid, which is simply where the user asked it to start.</summary>
    private double SteppedGridLowHz => StartFreqHz;

    public double AnalysisLowHz => IsStepped
        ? StartFreqHz
        : Math.Max(4.0, ChirpStartHz * 1.25);

    public double AnalysisHighHz => IsStepped
        ? Math.Min(EndFreqHz, MaxFrequencyHz)
        : FrequencyResponseRequest.AnalysisHighNyquistFraction * SampleRateHz / 2.0;

    /// <summary>The FFT ceiling the sweeps use at this run's rate — a record-time bound, so 1.465 Hz bins at every rate.</summary>
    public int StandardFftCeiling => SweepResolution.BoundsFor(SampleRateHz).Ceiling;

    /// <summary>The lowest tone that ceiling can read a level at — 4.4 Hz at every supported rate.</summary>
    public double StandardLowestHz =>
        SweepResolution.LowestToneHz(SampleRateHz, StandardFftCeiling, MinFundamentalBins);

    /// <summary>
    /// The FFT ceiling a stepped run may reach, raised automatically — and only — when it asks to
    /// start below what the standard ceiling can measure. An ordinary 20 Hz sweep pays nothing for
    /// this. See <see cref="SweepResolution.ExtendedMaxRecordSeconds"/>.
    /// </summary>
    public int FftCeiling => StartFreqHz < StandardLowestHz
        ? SweepResolution.ExtendedCeilingFor(SampleRateHz)
        : StandardFftCeiling;

    /// <summary>The lowest tone a stepped run can read a level at, given everything above.</summary>
    public double LowestMeasurableHz => SweepResolution.LowestToneHz(
        SampleRateHz, AdaptiveResolution ? FftCeiling : SteppedFftSize, MinFundamentalBins);

    /// <summary>The analysis size one stepped point uses — constant unless <see cref="AdaptiveResolution"/> is on.</summary>
    public int FftSizeAt(double freqHz) => AdaptiveResolution
        ? SweepResolution.FftSizeAt(freqHz, SampleRateHz, FftCeiling)
        : SteppedFftSize;

    /// <summary>
    /// The generator level in dBV, whatever unit it was entered in — and <b>whichever of the two
    /// levels this run is for</b>. A calibration uses <see cref="CalibrationLevelValue"/>; see
    /// <see cref="DefaultCalibrationLevelDbv"/> for why the fixture wants a louder one than the DUT.
    /// </summary>
    public double GeneratorDbv => ToDbv(IsCalibration ? CalibrationLevelValue : LevelValue);

    /// <summary>Both levels share one unit — they are the same kind of quantity, and two unit pickers would be one too many.</summary>
    private double ToDbv(double value) => LevelUnit switch
    {
        ThdSweepLevelUnit.Dbv => value,
        ThdSweepLevelUnit.Volt => 20.0 * Math.Log10(value),
        ThdSweepLevelUnit.MilliVolt => 20.0 * Math.Log10(value / 1000.0),
        _ => throw new ArgumentOutOfRangeException(nameof(LevelUnit), LevelUnit, "Unknown drive level unit."),
    };

    /// <summary>True when this run captures a standard rather than measuring a DUT.</summary>
    public bool IsCalibration => CalTarget != ImpedanceCalTarget.None;

    /// <summary>
    /// The solver options this request implies, with both calibrations already resampled onto the
    /// analysis grid.
    ///
    /// <para><b>A calibration run applies no calibration.</b> The open standard <i>is</i> the channel
    /// mismatch, so normalising it by a previous open would report the difference between two
    /// calibrations as an impedance; the short likewise has to be measured through the same
    /// uncorrected path the open was.</para>
    /// </summary>
    /// <param name="frequenciesHz">
    /// The measured frequencies, for a stepped run whose curve is not on a uniform bin grid. Null for
    /// a chirp, where <see cref="BinHz"/> says everything. The stored standards are resolved at these
    /// frequencies either way, which is what lets a chirp-captured calibration correct a stepped
    /// measurement.
    /// </param>
    public ImpedanceSolveOptions SolveOptions(double[]? frequenciesHz = null)
    {
        if (IsCalibration)
        {
            return new ImpedanceSolveOptions
            {
                ReferenceOhms = ReferenceOhms,
                BinHz = BinHz,
                FrequenciesHz = frequenciesHz,
            };
        }

        var grid = frequenciesHz;

        return new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            FrequenciesHz = grid,
            OpenCal = OpenCal is { IsUsable: true } open ? ResolveCal(open, grid) : null,
            ShortCal = CableMode == ImpedanceCableMode.Measured && ShortCal is { IsUsable: true } shortCal
                ? ResolveCal(shortCal, grid)
                : null,
            CableOhms = CableMode == ImpedanceCableMode.Fixed ? FixedCableOhms : 0.0,
            CableHenry = CableMode == ImpedanceCableMode.Fixed ? FixedCableMillihenry / 1000.0 : 0.0,
            AnalyzerInputOhms = AnalyzerInputOhms,
        };
    }

    private Complex[] ResolveCal(ImpedanceCalibration calibration, double[]? frequenciesHz) =>
        frequenciesHz is null
            ? calibration.Resample(BinHz, BinCount)
            : calibration.ResampleAt(frequenciesHz);

    public void Validate()
    {
        if (Method != ImpedanceMethod.ReferenceResistor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Method), Method,
                "Only the reference-resistor method is implemented. The two-load method is not available yet.");
        }

        if (Stimulus == ImpedanceStimulus.PinkNoise)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Stimulus), Stimulus, "The pink-noise stimulus is not available yet.");
        }

        if (CalibrationStimulus == ImpedanceStimulus.PinkNoise)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CalibrationStimulus), CalibrationStimulus, "The pink-noise stimulus is not available yet.");
        }

        // A stepped measurement is single-shot, the same call the stepped-sine response makes: a
        // hundred-point run is minutes, and repeating it until stopped is not a mode anyone wants.
        if (Stimulus == ImpedanceStimulus.SteppedSine && Continuous)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Continuous), Continuous,
                "A stepped-sine impedance run is measured once, not repeatedly.");
        }

        // Both settings groups always ride on the request -- the panel does not know which run comes
        // next -- so each is validated only when a run would actually read it. Rejecting an unused
        // group would let an untouched default block a measurement that never looks at it.
        if (Stimulus == ImpedanceStimulus.Chirp || CalibrationStimulus == ImpedanceStimulus.Chirp)
            ValidateChirpSettings();

        if (Stimulus == ImpedanceStimulus.SteppedSine || CalibrationStimulus == ImpedanceStimulus.SteppedSine)
            ValidateSteppedSettings();

        if (double.IsNaN(ReferenceOhms) || ReferenceOhms < MinReferenceOhms || ReferenceOhms > MaxReferenceOhms)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReferenceOhms), ReferenceOhms,
                $"The reference resistor must be between {MinReferenceOhms} and {MaxReferenceOhms} ohms.");
        }

        // Watts name a power, and a one-port has no output power to aim at -- see the type remarks on
        // why there is no output-referred mode here at all.
        if (LevelUnit is not (ThdSweepLevelUnit.Dbv or ThdSweepLevelUnit.Volt or ThdSweepLevelUnit.MilliVolt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelUnit), LevelUnit, "An impedance measurement's drive level is a generator voltage.");
        }

        if (double.IsNaN(LevelValue) || double.IsInfinity(LevelValue))
            throw new ArgumentOutOfRangeException(nameof(LevelValue), LevelValue, "Amplitude must be a finite number.");

        if (LevelUnit != ThdSweepLevelUnit.Dbv && LevelValue <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(LevelValue), LevelValue, "A voltage amplitude must be positive.");

        // Checked whether or not this run is a calibration: an unusable value stored on the type
        // would otherwise lie dormant until the next time Measure was pressed.
        if (double.IsNaN(CalibrationLevelValue) || double.IsInfinity(CalibrationLevelValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CalibrationLevelValue), CalibrationLevelValue, "The calibration level must be a finite number.");
        }

        if (LevelUnit != ThdSweepLevelUnit.Dbv && CalibrationLevelValue <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CalibrationLevelValue), CalibrationLevelValue, "A calibration voltage must be positive.");
        }

        if (AverageCount is < FrequencyResponseRequest.MinAverageCount or > FrequencyResponseRequest.MaxAverageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AverageCount), AverageCount,
                $"Averages must be between {FrequencyResponseRequest.MinAverageCount} and {FrequencyResponseRequest.MaxAverageCount}.");
        }

        if (CableMode == ImpedanceCableMode.Fixed)
        {
            if (double.IsNaN(FixedCableOhms) || FixedCableOhms < 0.0)
                throw new ArgumentOutOfRangeException(nameof(FixedCableOhms), FixedCableOhms, "A cable resistance cannot be negative.");

            if (double.IsNaN(FixedCableMillihenry) || FixedCableMillihenry < 0.0)
                throw new ArgumentOutOfRangeException(nameof(FixedCableMillihenry), FixedCableMillihenry, "A cable inductance cannot be negative.");
        }

        // A standard is captured once. Repeating one would keep overwriting the calibration the
        // measurement is about to be corrected by, which is a confusing thing to leave running.
        if (IsCalibration && Continuous)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Continuous), Continuous, "A calibration standard is captured once, not repeatedly.");
        }

        if (AnalyzerInputOhms is { } zin && (double.IsNaN(zin) || zin <= 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AnalyzerInputOhms), zin, "The analyzer input impedance must be above 0 ohms, or left unset.");
        }
    }

    /// <summary>The span, resolution, smoothing and sweep-start rules, checked only when some run will play a chirp.</summary>
    private void ValidateChirpSettings()
    {
        if (!FrequencyResponseRequest.SupportedSpansHz.Contains(FrequencySpanHz))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FrequencySpanHz), FrequencySpanHz,
                $"Frequency span must be one of {string.Join(", ", FrequencyResponseRequest.SupportedSpansHz)} Hz.");
        }

        if (!FrequencyResponseRequest.SupportedResolutionMultipliers.Contains(ResolutionMultiplier))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ResolutionMultiplier), ResolutionMultiplier,
                $"Resolution multiplier must be one of {string.Join(", ", FrequencyResponseRequest.SupportedResolutionMultipliers)}.");
        }

        if (!FractionalOctaveSmoother.SupportedDenominators.Contains(SmoothingDenominator))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SmoothingDenominator), SmoothingDenominator,
                $"Smoothing must be one of {string.Join(", ", FractionalOctaveSmoother.SupportedDenominators)} (0 = none).");
        }

        if (double.IsNaN(ChirpStartHz)
            || ChirpStartHz < FrequencyResponseRequest.MinChirpStartHz
            || ChirpStartHz > FrequencyResponseRequest.MaxChirpStartHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChirpStartHz), ChirpStartHz,
                $"The chirp start must be between {FrequencyResponseRequest.MinChirpStartHz} and "
                    + $"{FrequencyResponseRequest.MaxChirpStartHz} Hz.");
        }

        // Quoted against the chirp's own sample rate rather than the run's: with a stepped
        // measurement paired to a chirp calibration, ChirpEndHz would follow the stepped rate and
        // reject a perfectly good sweep start.
        double chirpEndHz = FrequencyResponseRequest.ChirpEndNyquistFraction * FrequencySpanHz;
        if (ChirpStartHz >= chirpEndHz / 10.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChirpStartHz), ChirpStartHz,
                $"The chirp start must be at least a decade below its end ({chirpEndHz:0} Hz).");
        }
    }

    /// <summary>The stepped grid's rules, mirroring <see cref="SteppedResponseRequest.Validate"/>.</summary>
    private void ValidateSteppedSettings()
    {
        if (!(StartFreqHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(StartFreqHz), StartFreqHz, "Start frequency must be greater than 0 Hz.");

        if (EndFreqHz <= StartFreqHz)
            throw new ArgumentOutOfRangeException(nameof(EndFreqHz), EndFreqHz, "End frequency must be above the start frequency.");

        if (StepsPerOctave is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(StepsPerOctave), StepsPerOctave, "Steps per octave must be between 1 and 48.");

        if (SteppedFftSize <= 0 || (SteppedFftSize & (SteppedFftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(SteppedFftSize), SteppedFftSize, "FFT size must be a power of two.");

        if (!(SteppedSampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(SteppedSampleRateHz), SteppedSampleRateHz, "Sample rate must be positive.");

        RequireResolvableTone();
    }

    /// <summary>
    /// Rejects a start frequency whose tone would sit on top of DC at the size a stepped run would
    /// use. The message names what would work, because the panel offers no FFT-size control and the
    /// start frequency is the only thing the caller can actually change.
    /// </summary>
    private void RequireResolvableTone()
    {
        // Asked of the stepped configuration whether or not this run is the stepped one: an
        // unmeasurable start stored on the type would otherwise lie dormant until the next time the
        // stepped stimulus was selected.
        double sampleRateHz = AdaptiveResolution
            ? SweepResolution.SampleRateFor(EndFreqHz, harmonicOrder: 1, nyquistFraction: MaxFrequencyNyquistFraction)
            : SteppedSampleRateHz;

        int standardCeiling = SweepResolution.BoundsFor(sampleRateHz).Ceiling;
        double standardLowestHz = SweepResolution.LowestToneHz(sampleRateHz, standardCeiling, MinFundamentalBins);

        int ceiling = StartFreqHz < standardLowestHz
            ? SweepResolution.ExtendedCeilingFor(sampleRateHz)
            : standardCeiling;

        int fftSize = AdaptiveResolution
            ? SweepResolution.FftSizeAt(StartFreqHz, sampleRateHz, ceiling)
            : SteppedFftSize;

        double binSizeHz = sampleRateHz / fftSize;
        if (StartFreqHz >= MinFundamentalBins * binSizeHz)
            return;

        string advice = AdaptiveResolution
            ? $"Start above {MinFundamentalBins * sampleRateHz / ceiling:0.##} Hz."
            : "Turn on adaptive resolution, which sizes the low points for the frequency asked for, or "
                + $"start above {MinFundamentalBins * binSizeHz:0.##} Hz.";

        throw new ArgumentOutOfRangeException(
            nameof(StartFreqHz),
            StartFreqHz,
            $"{StartFreqHz:0.##} Hz is only {StartFreqHz / binSizeHz:0.#} bins up at {sampleRateHz / 1000:0.##} kHz / {fftSize} "
                + $"({binSizeHz:0.###} Hz per bin); reading a level needs at least {MinFundamentalBins}. {advice}");
    }
}

/// <summary>
/// One impedance curve, decimated for the chart. The same column layout a
/// <see cref="FrequencyResponseTrace"/> uses, so it rides the same wire frame and the same
/// <c>response_trace</c> rows — but the magnitude is <b>dB relative to 1 ohm</b>, not a gain.
/// </summary>
/// <param name="PeakDbOhms">The largest |Z| in the analysed band, and where it is. For a loudspeaker that is its resonance.</param>
public sealed record ImpedanceTrace(
    ImpedanceTraceKind Kind,
    double StartFreqHz,
    double EndFreqHz,
    float[] FreqHz,
    float[] MagnitudeMinDbOhms,
    float[] MagnitudeMaxDbOhms,
    float[] PhaseDeg,
    float[] Coherence,
    double PeakDbOhms,
    double PeakFreqHz);

/// <summary>What an impedance run has done so far, and the curves it has produced.</summary>
/// <param name="CapturedCalibration">
/// The standard a calibration run just measured, decimated for storage. Returned so the browser can
/// put it in the measurement type's <c>params_json</c> — the server keeps no calibration of its own,
/// so the saved settings are the single place a fixture calibration lives.
/// </param>
/// <param name="Stimulus">What this run played — the <i>effective</i> one, so a chirp calibration inside a stepped measurement reports Chirp.</param>
/// <param name="PointsMeasured">
/// Points finished, for a stepped run. Null for a chirp, which has passes and averages rather than
/// points — and null rather than 0, so the browser can tell "no points because this is not a stepped
/// run" from "a stepped run that has not finished its first point".
/// </param>
/// <param name="PointCount">Points a stepped run will measure in total. Null for a chirp.</param>
/// <param name="MedianOhms">
/// The median |Z| across the analysed band, as a one-number summary of the measurement, or null when
/// there is none — a calibration run measures a standard rather than an impedance.
/// </param>
/// <param name="DriveCurrentAmps">
/// Roughly what the DUT was driven with: the measured generator level divided by the reference
/// resistor plus the median impedance. Approximate by construction, since a sweep's current varies
/// with Z across the band, but it is the number that says whether a driver was measured gently.
/// Null alongside <paramref name="MedianOhms"/>.
/// </param>
/// <remarks>
/// <b>Both of those are nullable rather than NaN, and that is load-bearing rather than tidy.</b>
/// <c>System.Text.Json</c> refuses to write a NaN — it is not a JSON number — and it discovers this
/// part way through the response, after the status line and 70 kB of trace arrays have already gone
/// out. The result is a 200 whose body simply stops mid-array: the browser's <c>JSON.parse</c>
/// throws, the whole status is lost, and the screen looks as though the run did nothing. A
/// calibration run has no impedance to take a median of, so it hit this every time. Absent is null
/// here, the same way <see cref="FrequencyResponseStatus.LatencySamples"/> does it.
/// </remarks>
public sealed record ImpedanceStatus(
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
    double ReferenceOhms,
    bool RightChannelIsReference,
    ImpedanceCalTarget CalTarget,
    string? Error,
    IReadOnlyList<ImpedanceTrace> Traces,
    ImpedanceCalibration? CapturedCalibration = null,
    double? MedianOhms = null,
    double? DriveCurrentAmps = null,
    ImpedanceStimulus Stimulus = ImpedanceStimulus.Chirp,
    int? PointsMeasured = null,
    int? PointCount = null);

/// <summary>The shape of a run, returned as soon as it has been accepted.</summary>
public sealed record ImpedanceStarted(
    double SampleRateHz,
    int FftSize,
    double BinHz,
    int AverageCount,
    double RecordSeconds,
    bool Continuous,
    ImpedanceCalTarget CalTarget,
    ImpedanceStimulus Stimulus = ImpedanceStimulus.Chirp,
    int? PointCount = null,
    IReadOnlyList<double>? FrequenciesHz = null,
    double? EstimatedSeconds = null);
