using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Which curve a <see cref="FrequencyResponseTrace"/> carries. The wire header's channel byte holds
/// this, the same way a sweep frame's holds the channel.
/// </summary>
public enum FrequencyResponseTraceKind
{
    /// <summary>Left input's own response, dBV.</summary>
    Left = 0,

    /// <summary>Right input's own response, dBV.</summary>
    Right = 1,

    /// <summary>
    /// Gain, dB. Left relative to the right input when that is the reference, otherwise left
    /// relative to the synthesised stimulus.
    /// </summary>
    Gain = 2,

    /// <summary>
    /// Crosstalk with the left channel driven, dB: the right input relative to the left. Values are
    /// wire bytes, never renumbered; these two are appended rather than interleaved for that reason.
    /// See <see cref="CrosstalkRunner"/>.
    /// </summary>
    CrosstalkLeftToRight = 3,

    /// <summary>Crosstalk with the right channel driven, dB: the left input relative to the right.</summary>
    CrosstalkRightToLeft = 4,

    /// <summary>
    /// The output-impedance run's gain into its first load, dB. Stored beside the Zout curve so a
    /// saved result keeps what it was derived from; never broadcast.
    /// </summary>
    GainLoad1 = 5,

    /// <summary>The output-impedance run's gain into its second load (or open), dB. See <see cref="GainLoad1"/>.</summary>
    GainLoad2 = 6,
}

/// <summary>
/// What phase a run measured, and therefore what the UI may draw and what it should call it.
///
/// <para>There is only ever one phase quantity in these measurements — <c>arg(L/R)</c> — and the
/// computation is identical however it is configured. What changes is whether it is available at
/// all and what it <em>means</em>, and that is a property of how the run was set up, not of which
/// view happens to be on screen. Reported on the status so the UI keys off the data it is showing
/// rather than off the config panel's current form, which can have moved on since.</para>
/// </summary>
public enum ResponsePhaseKind
{
    /// <summary>Nothing measured a phase — the right input carried no stimulus to compare against.</summary>
    None,

    /// <summary>
    /// Every trace carries its own phase against whatever its estimator's reference was, so the
    /// curve to draw is the one belonging to the trace on screen. The chirp, whose Left and Right
    /// traces are estimated against the synthesised sweep.
    /// </summary>
    PerTrace,

    /// <summary><c>arg(L/R)</c> with the right input as the reference — the DUT's own phase response.</summary>
    DutResponse,

    /// <summary>
    /// <c>arg(L/R)</c> with the right input measuring something of its own rather than serving as a
    /// reference — a phase difference between two measured paths, which is a different claim.
    /// </summary>
    ChannelDifference,
}

/// <summary>What a frequency-response run is doing right now. Mirrors <see cref="ThdSweepPhase"/>'s role.</summary>
public enum FrequencyResponsePhase
{
    Idle,
    DeterminingLevel,
    Measuring,
    Finished,
    Cancelled,
    Failed,

    /// <summary>
    /// Probing for the input range the DUT's output needs. Appended rather than slotted in next to
    /// <see cref="DeterminingLevel"/> on purpose: the ordinal rides in the progress frame's <c>seq</c>
    /// field, so renumbering the existing members would make an older browser mislabel every phase.
    /// </summary>
    DeterminingRange = 6,

    /// <summary>
    /// Stepping a tone across the band — the stepped-sine Bode plot's counterpart of
    /// <see cref="Measuring"/>. Appended, and with an explicit ordinal, for the same reason
    /// <see cref="DeterminingRange"/> is.
    /// </summary>
    Sweeping = 7,

    /// <summary>
    /// Turning a captured impulse response into curves — <see cref="ImpulseResponseRunner"/> only.
    /// Its own phase because on that measurement the analysis is not instant, and a run that read as
    /// finished while it was still computing would let a save through against half a result.
    ///
    /// <para>Appended with an explicit ordinal for the same reason the two above are: the ordinal
    /// rides in the progress frame's <c>seq</c> field, so renumbering would make an older browser
    /// mislabel every phase. And it belongs in <see cref="ImpulseResponseRunner"/>'s <c>Running</c>
    /// and in neither of the other two runners' — the rule is that every phase a runner can
    /// <i>enter</i> must be in its own <c>Running</c>, which
    /// <c>RunnerPhaseCoverageTests</c> now checks mechanically for all three.</para>
    /// </summary>
    Analyzing = 8,

    /// <summary>
    /// Capturing an impedance fixture's open or short standard — <c>ImpedanceRunner</c> only. A
    /// calibration run is a measurement in every mechanical sense (it drives the generator, holds the
    /// session and takes records), so it must appear in that runner's <c>Running</c> or the run would
    /// read as finished the moment it began. Appended with an explicit ordinal for the same reason
    /// <see cref="DeterminingRange"/> is.
    /// </summary>
    Calibrating = 9,

    /// <summary>
    /// Waiting for the user to change the load between the two passes of an output-impedance run —
    /// <c>OutputImpedanceRunner</c> only. The generator is off and the acquisition is still up; the
    /// run resumes on <c>POST /api/output-impedance/continue</c> or ends on cancel. It is a busy
    /// phase — the run owns the session, the pipeline options and the input range throughout —
    /// so it must be in that runner's <c>Running</c>, which is also what keeps a project switch
    /// refused while the bench is mid-measurement. Appended with an explicit ordinal for the same
    /// reason <see cref="DeterminingRange"/> is.
    /// </summary>
    WaitingForLoadChange = 10,
}

/// <summary>
/// One ±1 dB or −3 dB reading, as the UI's Measurements table shows it. <see cref="LowerClamped"/>
/// / <see cref="UpperClamped"/> say the trace never reached the threshold on that side inside the
/// measured band, so the frequency is where the search stopped, not a corner — the old app printed
/// a bare number there and read "&gt; 96 kHz" as if it were measured.
/// </summary>
/// <param name="Symmetric">
/// The reading is a flatness band (±<see cref="DropDb"/> of the reference) rather than a roll-off
/// one (down to reference − <see cref="DropDb"/>). Carried rather than inferred from
/// <see cref="DropDb"/>: which row is which is a convention this app happens to hold, and a reading
/// that cannot say what it measured is what makes a stored result unreadable a year later.
/// </param>
public sealed record BandwidthReading(
    double DropDb,
    bool Symmetric,
    double LowerHz,
    double UpperHz,
    double BandwidthHz,
    bool LowerClamped,
    bool UpperClamped)
{
    public static BandwidthReading From(BandwidthResult result) =>
        new(result.DropDb, result.Symmetric, result.LowerHz, result.UpperHz, result.BandwidthHz,
            result.LowerClamped, result.UpperClamped);
}

/// <summary>
/// One decimated curve ready for the chart, plus the scalars the Measurements table reads.
///
/// <para><see cref="MagnitudeMinDb"/>/<see cref="MagnitudeMaxDb"/> are the min and max of the full
/// resolution bins that fell into each column — never their mean, per the project's decimation rule.
/// Both must be drawn: the max as the line, the min as the band beneath it, or a rippled response
/// is rendered as a curve sitting above where its ripple actually is.</para>
///
/// <para>The peak and the bandwidth readings are computed server-side on the <b>full-resolution,
/// smoothed</b> trace, not on these columns — decimation would move a corner by up to a column, and
/// a column near 20 kHz is far wider than the corner it is reporting.</para>
///
/// <para><see cref="GroupDelayS"/> and <see cref="PhaseDelayS"/> are derived from the
/// full-resolution unwrapped phase <i>before</i> decimation (a derivative of the column-sampled
/// phase would be aliased wherever columns are finer than bins) and then sampled at the column
/// centre like the phase itself, in seconds. Whether they are the DUT's physical delay or only its
/// <i>excess</i> delay depends on what the phase was measured against: a chirp trace estimated
/// against the synthesised sweep had the record's arbitrary rotation removed, so Left and Right —
/// and Gain without a measured reference — carry excess delay only, while Gain against the right
/// input has the measured delay put back. See <c>ResponseCaptureSupport.BuildTraces</c>.</para>
/// </summary>
public sealed record FrequencyResponseTrace(
    FrequencyResponseTraceKind Kind,
    double StartFreqHz,
    double EndFreqHz,
    float[] FreqHz,
    float[] MagnitudeMinDb,
    float[] MagnitudeMaxDb,
    float[] PhaseDeg,
    float[] Coherence,
    float[] GroupDelayS,
    float[] PhaseDelayS,
    BandwidthReading OneDb,
    BandwidthReading ThreeDb,
    double PeakDb,
    double PeakFreqHz,
    /// <summary>
    /// The level both readings were measured from. A property of the trace rather than of each
    /// reading because they share one by construction — see
    /// <c>BandwidthAnalyzer.FindReference</c>, which is called once per trace for that reason.
    /// The chart draws it, and its thresholds, from this.
    /// </summary>
    double ReferenceDb = 0.0,
    BandwidthReferenceMode ReferenceMode = BandwidthReferenceMode.Peak,
    /// <summary>
    /// The requested reference band held no bins inside the analysed range, so the peak was used
    /// instead. The UI says so rather than quoting a figure against a band it never measured.
    /// </summary>
    bool ReferenceFellBackToPeak = false);

/// <summary>
/// A frequency-response run's configuration, flat and immutable, mirroring
/// <see cref="ThdSweepRequest"/>'s role. Everything the measurement needs; nothing it derives.
/// </summary>
/// <param name="Continuous">
/// Keep re-measuring until stopped, rather than running once. RUN sets this; SINGLE does not — the
/// same split the FFT screen has. Each pass is a whole independent measurement (fresh averaging,
/// fresh estimate), so what the chart shows is one complete sweep and not a running blend of them.
/// The level probe is deliberately <b>not</b> repeated per pass: it is slow, and re-solving it every
/// second would make the trace jump whenever the DUT drifted rather than showing the drift.
/// </param>
/// <summary>
/// What a response measurement plays. ARTA's own list, since the point of offering anything but the
/// sweep is that a measurement here is comparable with one there.
///
/// <para><b>Every one of the noise kinds is periodic over the analysis record</b>, exactly as the
/// chirp is — see <see cref="Tonebench.Device.PeriodicNoise"/>. That is what lets the same
/// deconvolution serve all of them: the reference is a known, deterministic spectrum either way,
/// and the analysis window stays Rectangular because a whole period transforms exactly.</para>
///
/// <para><b>What changes with the stimulus is distortion, not the response.</b> A sweep separates
/// the DUT's harmonics into packets at known places, which is what lets the impulse screen draw
/// them and the gate delete them. Noise excites every bin at once, so its distortion comes back
/// spread over the whole record as a raised floor: there is nothing to draw and nothing to gate,
/// and a noise-measured impulse response is the honest sum of the linear response and whatever the
/// DUT's distortion contributes. Against a linear DUT the two agree; against a distorting one the
/// sweep is the better measurement, which is why it stays the default.</para>
/// </summary>
public enum ResponseStimulus
{
    /// <summary>The periodic exponential sweep. The default, and the only one that separates distortion.</summary>
    Chirp = 0,

    /// <summary>ARTA's "PN Pink": equal power per constant-percentage band. The best signal-to-noise at the bottom of the band, which is why ARTA recommends it for acoustic work.</summary>
    PinkNoise = 1,

    /// <summary>ARTA's "PN White": equal power per bin, which is what ARTA recommends for electronic systems — a bin at the top of the band is measured as well as one at the bottom.</summary>
    WhiteNoise = 2,

    /// <summary>ARTA's "Male PN[2011]" — the male speech spectrum of IEC 60268-16:2011.</summary>
    SpeechMale2011 = 3,

    /// <summary>ARTA's "Female PN[2011]" — the female spectrum of the same edition.</summary>
    SpeechFemale2011 = 4,

    /// <summary>ARTA's "Male PN[2020]" — the male spectrum as revised for Edition 5.</summary>
    SpeechMale2020 = 5,
}

public sealed record FrequencyResponseRequest(
    double FrequencySpanHz,
    int ResolutionMultiplier,
    ThdSweepLevelMode LevelMode,
    double LevelValue,
    ThdSweepLevelUnit LevelUnit,
    bool RightChannelIsReference,
    int SmoothingDenominator,
    int AverageCount,
    bool Continuous = false,
    // The literal, not the const: a primary constructor's default value cannot reference a member of
    // the type it is declaring. Kept equal to DefaultChirpStartHz by ChirpStartDefaultMatchesTheConstant.
    double ChirpStartHz = 2.0,
    /// <summary>
    /// The amplifier load a target power is defined against, in ohms. Only read by
    /// <see cref="ThdSweepLevelMode.OutputPower"/> — the other two modes name a voltage, which needs
    /// no load. Defaulted so every existing caller keeps compiling and measuring the same thing.
    /// </summary>
    double LoadOhms = 8.0,
    /// <summary>
    /// What the ±1 dB and −3 dB corners are measured down from — see
    /// <see cref="BandwidthReferenceSpec"/> for why this is a setting and not simply the peak.
    /// Defaults to the passband median because the thing this app is pointed at is usually an
    /// amplifier; a DUT whose response <i>is</i> a peak wants <see cref="BandwidthReferenceMode.Peak"/>.
    /// </summary>
    BandwidthReferenceMode BandwidthReference = BandwidthReferenceMode.BandMedian,
    double BandwidthReferenceLowHz = BandwidthReferenceSpec.DefaultLowHz,
    double BandwidthReferenceHighHz = BandwidthReferenceSpec.DefaultHighHz,
    /// <summary>
    /// What to play. <see cref="ResponseStimulus.Chirp"/> by default, so every caller that predates
    /// this measures exactly what it did.
    /// </summary>
    ResponseStimulus Stimulus = ResponseStimulus.Chirp,
    /// <summary>
    /// Where a pink or speech sequence stops rising towards DC, in Hz. The literal rather than
    /// <see cref="Tonebench.Device.PeriodicNoise.DefaultCutoffHz"/> for the reason
    /// <see cref="ChirpStartHz"/> gives; kept equal to it by a test.
    /// </summary>
    double NoiseCutoffHz = 5.0,
    /// <summary>
    /// How far the capturing clock runs from the playing one, in parts per million, or zero where
    /// they share one. Measured by the clock-drift test; see <see cref="ChirpClockDrift"/> for what
    /// it is worth and what it cannot do.
    ///
    /// <para>Zero by default, so a one-card bench is untouched and nothing is corrected on a
    /// measurement nobody has measured the drift of — a wrong epsilon would put a ramp on where
    /// there was none.</para>
    /// </summary>
    double ClockDriftPpm = 0.0)
{
    /// <summary>Whether the stimulus is one of the periodic-noise kinds rather than the sweep.</summary>
    public bool UsesNoise => Stimulus != ResponseStimulus.Chirp;

    /// <summary>
    /// The sequence shape a noise stimulus asks <see cref="Tonebench.Device.PeriodicNoise"/> for.
    /// Throws for the chirp, which has no sequence — callers ask <see cref="UsesNoise"/> first.
    /// </summary>
    public PeriodicNoiseShape NoiseShape => Stimulus switch
    {
        ResponseStimulus.PinkNoise => PeriodicNoiseShape.Pink,
        ResponseStimulus.WhiteNoise => PeriodicNoiseShape.White,
        ResponseStimulus.SpeechMale2011 => PeriodicNoiseShape.SpeechMale2011,
        ResponseStimulus.SpeechFemale2011 => PeriodicNoiseShape.SpeechFemale2011,
        ResponseStimulus.SpeechMale2020 => PeriodicNoiseShape.SpeechMale2020,
        _ => throw new InvalidOperationException($"{Stimulus} is not a noise stimulus."),
    };

    /// <summary>
    /// Spans the UI offers, in Hz. These are <b>Nyquist</b> values — the label the old app showed —
    /// so the sample rate is twice each one, which is also why they are exactly the QA403's three
    /// supported rates halved.
    /// </summary>
    public static readonly double[] SupportedSpansHz = [24_000.0, 48_000.0, 96_000.0];

    /// <summary>
    /// Bin width at <see cref="ResolutionMultiplier"/> 1, in Hz. The base FFT size scales with the
    /// sample rate (32768 / 65536 / 131072 for 48 / 96 / 192 kHz), so this is the same number at
    /// every span — which is why the UI can offer one resolution list regardless of the span.
    /// </summary>
    public const double BaseBinHz = 48_000.0 / 32768.0;

    /// <summary>The multipliers the UI offers: 1 -&gt; 1.46 Hz, 2 -&gt; 0.73 Hz, 4 -&gt; 0.37 Hz.</summary>
    public static readonly int[] SupportedResolutionMultipliers = [1, 2, 4];

    public const int MinAverageCount = 1;

    /// <summary>
    /// How many records one measurement may fold in. Raised from 10 to <b>20</b> on 2026-10-21,
    /// after the impedance screen had it alone for a day — asked for across the board rather than
    /// kept as a divider-only allowance, and the ceiling is not what decides whether the records
    /// are worth taking: a stepped grid's per-point cost is the thing to look at, and the panel's
    /// own time estimate says it before you press RUN.
    /// </summary>
    public const int MaxAverageCount = 20;

    /// <summary>
    /// Where the chirp starts by default. Deliberately below anything worth reporting: the sweep's
    /// own fade-in lives at the very bottom of the band, and a corner measured inside it would be
    /// measuring the stimulus rather than the DUT.
    ///
    /// <para>It is a setting rather than a constant because the bottom of the sweep is not free.
    /// Peak flux in a transformer-coupled DUT goes as V/f, so the 2-8 Hz stretch asks several times
    /// more of the core than the band anyone reads — measured against a tube amplifier at -18 dBV,
    /// the chirp read up to <b>1.2 dB below a stepped-sine Bode plot</b> across 9-16 Hz while
    /// agreeing with it everywhere else and at -28 dBV. Raising the start is the one knob that tests
    /// whether the sweep's own sub-10 Hz excitation is what causes that.</para>
    /// </summary>
    public const double DefaultChirpStartHz = 2.0;

    public const double MinChirpStartHz = 0.5;

    public const double MaxChirpStartHz = 1000.0;

    /// <summary>Fraction of Nyquist the chirp sweeps up to. The last bins are where the anti-alias filter lives, not the DUT.</summary>
    public const double ChirpEndNyquistFraction = 0.98;

    /// <summary>
    /// Bottom of the band actually reported and searched for corners — inset past the sweep's
    /// fade-in.
    ///
    /// <para>The 4 Hz floor is what the 2 Hz default has always produced, so the default behaviour
    /// is unchanged. Above that the inset is only 25 %, not the octave the floor implies: the fade
    /// is a fixed 5 ms and spans under 2 % of a decade at any start frequency, and a wider inset
    /// would put the interesting band out of reach — a chirp started at 8 Hz to escape the flux
    /// question has to still report the 9-16 Hz it was raised to test.</para>
    /// </summary>
    public double AnalysisLowHz => Math.Max(4.0, ChirpStartHz * 1.25);

    public const double AnalysisHighNyquistFraction = 0.95;

    public double SampleRateHz => FrequencySpanHz * 2.0;

    /// <summary>Analysis size: the base size for this sample rate, times the resolution multiplier.</summary>
    public int FftSize => (int)Math.Round(SampleRateHz / BaseBinHz) * ResolutionMultiplier;

    public double BinHz => SampleRateHz / FftSize;

    public double ChirpEndHz => ChirpEndNyquistFraction * SampleRateHz / 2.0;

    /// <summary>
    /// The clock correction this run should apply, or null — no drift worth correcting, or a
    /// stimulus the correction does not describe.
    ///
    /// <para><b>Chirp only, and that is not a limitation to be fixed later.</b> The correction is
    /// built on the sweep's time-frequency map; periodic noise has none, so there is no ramp to
    /// remove — its error is incoherent leakage, which shows up as the coherence falling rather
    /// than as a delay.</para>
    /// </summary>
    public ChirpClockDrift? ClockDrift => Stimulus == ResponseStimulus.Chirp
        ? ChirpClockDrift.For(ClockDriftPpm, SampleRateHz, FftSize, ChirpStartHz, ChirpEndHz)
        : null;

    public double AnalysisHighHz => AnalysisHighNyquistFraction * SampleRateHz / 2.0;

    /// <summary>The three bandwidth-reference fields as the analyzer wants them.</summary>
    public BandwidthReferenceSpec BandwidthReferenceSpec =>
        new(BandwidthReference, BandwidthReferenceLowHz, BandwidthReferenceHighHz);

    /// <summary>
    /// The requested drive level in dBV, whatever unit it was entered in — see
    /// <see cref="ThdSweepLevelUnits.ToDbv"/>, which is the one place a power unit's sqrt(P·R)
    /// lives. Everything downstream of here sees a target voltage and cannot tell which of the two
    /// output-referred modes named it.
    /// </summary>
    public double LevelDbv => LevelUnit.ToDbv(LevelValue, LoadOhms);

    /// <summary>
    /// True when the generator amplitude has to be found by probing rather than taken from the
    /// request — both output-referred modes, since both name a level at the DUT's <em>output</em>.
    ///
    /// <para>A predicate rather than three <c>== OutputVoltage</c> comparisons scattered through the
    /// runner: the phase it starts in, the amplitude it seeds, and which side of the level probe the
    /// input-range work lands on are all this same question, and a mode added to the enum without
    /// touching every one of them would probe for a level and then range for a different one.</para>
    /// </summary>
    public bool SolvesGeneratorLevel => LevelMode != ThdSweepLevelMode.GeneratorVoltage;

    public void Validate()
    {
        // Only where something reads it: a cut-off left at any value by a chirp run must not refuse
        // that run, and the white sequence has no slope for a corner to start.
        if (UsesNoise && Stimulus != ResponseStimulus.WhiteNoise
            && (double.IsNaN(NoiseCutoffHz) || NoiseCutoffHz < PeriodicNoise.MinCutoffHz || NoiseCutoffHz > PeriodicNoise.MaxCutoffHz))
        {
            throw new ArgumentOutOfRangeException(
                nameof(NoiseCutoffHz), NoiseCutoffHz,
                $"The noise cut-off runs from {PeriodicNoise.MinCutoffHz:0.#} to {PeriodicNoise.MaxCutoffHz:0.#} Hz.");
        }

        if (!SupportedSpansHz.Contains(FrequencySpanHz))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FrequencySpanHz), FrequencySpanHz,
                $"Frequency span must be one of {string.Join(", ", SupportedSpansHz)} Hz.");
        }

        if (!SupportedResolutionMultipliers.Contains(ResolutionMultiplier))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ResolutionMultiplier), ResolutionMultiplier,
                $"Resolution multiplier must be one of {string.Join(", ", SupportedResolutionMultipliers)}.");
        }

        if (LevelMode is not (ThdSweepLevelMode.GeneratorVoltage or ThdSweepLevelMode.OutputVoltage or ThdSweepLevelMode.OutputPower))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelMode), LevelMode,
                "A frequency response is driven by generator (input) voltage, DUT output voltage or DUT output power.");
        }

        // Watts are only a level once a load says what voltage they are. That load is what the
        // output-power mode carries, so the unit is confined to it rather than guessed at elsewhere
        // — the same rule ThdSweepRequest applies.
        //
        // A power target is otherwise nothing special here: it converts to a target voltage up
        // front (see LevelDbv) and the run then solves for the generator exactly as OutputVoltage
        // does. What it deliberately does *not* claim is that the DUT delivers that power across
        // the whole sweep — the level is solved once, from the sweep's median gain, so the power is
        // the band-average one, not a per-frequency guarantee.
        if (LevelUnit.IsPower() && LevelMode != ThdSweepLevelMode.OutputPower)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelUnit), LevelUnit, "Watts only make sense for the output-power level mode.");
        }

        if (LevelMode == ThdSweepLevelMode.OutputPower)
        {
            if (!LevelUnit.IsPower())
            {
                throw new ArgumentOutOfRangeException(
                    nameof(LevelUnit), LevelUnit, "An output-power level is specified in watts or milliwatts.");
            }

            if (!(LoadOhms > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(LoadOhms), LoadOhms, "A target power needs a load above 0 Ω to name a voltage.");
            }
        }

        if (!FractionalOctaveSmoother.SupportedDenominators.Contains(SmoothingDenominator))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SmoothingDenominator), SmoothingDenominator,
                $"Smoothing must be one of {string.Join(", ", FractionalOctaveSmoother.SupportedDenominators)} (0 = none).");
        }

        if (AverageCount is < MinAverageCount or > MaxAverageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AverageCount), AverageCount,
                $"Averages must be between {MinAverageCount} and {MaxAverageCount}.");
        }

        BandwidthReferenceSpec.Validate();

        if (double.IsNaN(ChirpStartHz) || ChirpStartHz < MinChirpStartHz || ChirpStartHz > MaxChirpStartHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChirpStartHz), ChirpStartHz,
                $"The chirp start must be between {MinChirpStartHz} and {MaxChirpStartHz} Hz.");
        }

        // The sweep has to have somewhere to go, and the harmonic geometry the impulse gate is
        // derived from degenerates as the ratio approaches 1.
        if (ChirpStartHz >= ChirpEndHz / 10.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChirpStartHz), ChirpStartHz,
                $"The chirp start must be at least a decade below its end ({ChirpEndHz:0} Hz).");
        }

        if (double.IsNaN(LevelValue) || double.IsInfinity(LevelValue))
            throw new ArgumentOutOfRangeException(nameof(LevelValue), LevelValue, "Amplitude must be a finite number.");

        if (LevelUnit != ThdSweepLevelUnit.Dbv && LevelValue <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelValue), LevelValue,
                LevelUnit.IsPower() ? "A target power must be positive." : "A voltage amplitude must be positive.");
        }
    }
}

/// <summary>
/// Whether the reference input was actually carrying anything, and how much — see
/// <see cref="ResponseCaptureSupport.CheckReferenceChannel"/>.
/// </summary>
/// <param name="SnrDb">
/// The reference channel's per-bin signal-to-noise ratio, estimated from the roughness of its own
/// phase. See <see cref="TransferFunctionSnr"/> for why that and not coherence or level.
/// </param>
/// <param name="Usable">
/// Whether it is enough to divide by. False is the case this exists for: the panel offers
/// <b>Right channel is reference</b> ticked by default, and a bench with one channel wired has
/// nothing on that input at all.
/// </param>
public sealed record ReferenceChannelReading(double SnrDb, bool Usable);

/// <summary>
/// How much clock drift the run on screen still carries, and therefore whether the stored drift
/// figure has gone stale — see <see cref="ChirpDriftResidual"/> for the measurement and
/// <see cref="ResponseCaptureSupport.EstimateDriftResidual"/> for the conditions it needs.
///
/// <para><b>Why this is worth carrying at all.</b> A crystal's frequency moves with temperature, so
/// a stored ε goes out of date — and nothing else in the app can notice, because the clock-drift
/// test needs the output looped back to the input and a bench with a DUT patched in is not in that
/// state. This is read off the measurement the user has just taken, which makes it the only thing
/// that can tell a fresh figure from a stale one <i>while measuring</i>. It is a prompt to
/// re-measure, never a substitute for the measurement.</para>
/// </summary>
/// <param name="ResidualPpm">
/// The drift left on the trace. On a corrected run this is the error in the figure that corrected
/// it; on an uncorrected one it is the drift itself.
/// </param>
/// <param name="AppliedPpm">
/// What this run was corrected by, 0 when it was not. Carried with the residual because the two are
/// only meaningful together: 8 ppm left over after correcting 104 is a figure doing its job, and
/// 8 ppm on an uncorrected run is a bench that has never measured its drift.
/// </param>
/// <param name="SigmaPpm">The fit's own standard error. Optimistic — see <see cref="ChirpDriftResidualFit.SigmaPpm"/>.</param>
/// <param name="FitRmsDeg">How far the fitted model missed the phase. See <see cref="ChirpDriftResidualFit.FitRmsDeg"/>.</param>
/// <param name="Bins">How many bins the fit used, after the flatness gate dropped the rest.</param>
/// <param name="FromHz">The bottom of the band actually fitted.</param>
/// <param name="ToHz">The top of it. Below the analysed band's own top where the path rolls off.</param>
/// <param name="SlipSamples">
/// What the residual costs <i>this</i> run: how far the two clocks still separate across one
/// analysis record. Carried rather than derived on the client, because it belongs to the run's own
/// record length — a panel computing it from the staged settings would quote a figure for a
/// resolution the measurement on screen was not taken at.
/// </param>
/// <param name="RecordSeconds">That record's length, so the slip can be quoted against it.</param>
/// <param name="ReferenceSnrDb">
/// What the reference channel this was read off was carrying. Reported with the figure because the
/// figure is only worth what that channel was: a reading taken off an input with nothing on it is
/// the way this measurement goes wrong, and it has gone wrong that way once already.
/// </param>
public sealed record DriftResidualReading(
    double ResidualPpm,
    double AppliedPpm,
    double SigmaPpm,
    double FitRmsDeg,
    int Bins,
    double FromHz,
    double ToHz,
    double SlipSamples,
    double RecordSeconds,
    double ReferenceSnrDb);

/// <summary>What a client polling <c>GET /api/frequency-response/status</c> sees.</summary>
/// <param name="LatencySamples">
/// The DUT's propagation delay in samples, or null when nothing measured it. Only a run whose
/// reference is the right input channel can: against the synthesised chirp the recovered rotation
/// is the record's arbitrary start point in a periodic stimulus, not a delay.
/// </param>
public sealed record FrequencyResponseStatus(
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
    /// <summary>
    /// Points measured, and points to measure, for a <see cref="SteppedResponseRunner"/> run. Both 0
    /// for a chirp, which has no points — its progress is <see cref="AveragesTaken"/> records.
    /// Separate fields rather than borrowing those two: a stepped run has both an average count and
    /// a point count, and overloading one to carry the other leaves the readout stating a number the
    /// run never used.
    /// </summary>
    int PointsMeasured = 0,
    int PointCount = 0,
    /// <summary>
    /// What phase this run measured — see <see cref="ResponsePhaseKind"/>. Defaults to
    /// <see cref="ResponsePhaseKind.PerTrace"/>, which is the chirp's behaviour, so a runner that
    /// does not set it keeps behaving exactly as it did.
    /// </summary>
    ResponsePhaseKind PhaseKind = ResponsePhaseKind.PerTrace,
    /// <summary>
    /// The right input was fed the stimulus as a yardstick rather than measured as a signal of its
    /// own, so its dBV trace is the generator and there is nothing to offer as a curve. A separate
    /// field from <see cref="PhaseKind"/> even though the two coincide today: a future mode with a
    /// reference but no phase would make a phase-derived answer to this question wrong.
    /// </summary>
    bool RightChannelIsReference = false,
    /// <summary>
    /// Something the run needs to say that is not an error: it produced a measurement, and there is
    /// a caveat on reading it. Today that is a chirp start raised to the analyzer's own floor — see
    /// <see cref="StimulusFloor"/>. Null when there is nothing to say.
    /// </summary>
    string? Warning = null,
    /// <summary>
    /// The small FFT/oscilloscope capture behind each measured point, for the preview charts beside
    /// the chart — <see cref="SteppedResponseRunner"/> only; null for every other runner, and for a
    /// chirp, which has no points to take a preview of.
    ///
    /// <para>On the status rather than only on the wire for the reason the sweep's points are: a
    /// browser that reloads mid-run, or arrives at the screen after one finished, re-attaches
    /// through this and would otherwise have an empty rail until the next point was measured — or
    /// for ever, on a finished run.</para>
    /// </summary>
    IReadOnlyList<ResponsePointPreview>? PointPreviews = null,
    /// <summary>
    /// The clock drift this run still shows, where it could be read — chirp only, and null for
    /// every other stimulus and for a stepped sweep, neither of which has a sweep time map for the
    /// ramp to live on. See <see cref="DriftResidualReading"/>.
    /// </summary>
    DriftResidualReading? DriftResidual = null,
    /// <summary>
    /// What the reference input was carrying, on a run that had one. Null where the run measured
    /// against the synthetic stimulus instead, which has nothing to check.
    /// </summary>
    ReferenceChannelReading? Reference = null);

/// <summary>
/// One measured point's decimated spectrum and waveform, on one input — what the two small preview
/// charts beside a stepped-sine response draw.
///
/// <para>Taken at the only moment the samples exist: the pipeline's ring buffer moves on to the next
/// tone the instant the point is read, so unlike the live FFT screen there is no second chance to
/// ask what a given frequency looked like. See <c>SpectrumPipelineOptions.CapturePreview</c>.</para>
///
/// <para><paramref name="Channel"/> is the input (0 left, 1 right), not a trace kind — a preview is
/// a capture, and the Gain curve is derived rather than captured.</para>
/// </summary>
public sealed record ResponsePointPreview(
    double FreqHz, int Channel, SweepPreviewSeries? Fft, SweepPreviewSeries? Scope);

/// <summary>The immediate answer to a start request — what the run will do, before it does it.</summary>
public sealed record FrequencyResponseStarted(
    double SampleRateHz,
    int FftSize,
    double BinHz,
    int AverageCount,
    double RecordSeconds,
    bool Continuous);
