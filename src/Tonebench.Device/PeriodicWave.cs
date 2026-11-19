namespace Tonebench.Device;

/// <summary>
/// The hard-switched shape stimuli: <see cref="GeneratorKind.Square"/>,
/// <see cref="GeneratorKind.Triangle"/>, <see cref="GeneratorKind.Sawtooth"/> and
/// <see cref="GeneratorKind.Pulse"/>.
///
/// <para>Shared rather than duplicated per synthesizer, the way <see cref="PinkNoiseShaper"/> is and
/// unlike the chirp formula: there is real arithmetic here (the period quantisation, and the three
/// different voltages one level means), and a simulator that disagreed with the hardware about any
/// of it would read as a DUT that behaves differently under <c>--simulate</c>.</para>
///
/// <para><b>Hard-switched at an exact integer sample period, not band-limited additive synthesis,
/// and that is a correctness decision rather than a shortcut.</b> A sequence whose period is exactly
/// <see cref="PeriodFrames"/> samples is periodic in the sampled domain, so its entire spectrum lies
/// on multiples of <c>fs/M</c> — every harmonic above Nyquist folds onto another harmonic of the
/// same fundamental, and there is no inharmonic content anywhere in the record. That is the property
/// an FFT analyzer needs; a square generated from a free-running phase accumulator instead scatters
/// folded harmonics across the spectrum at levels tens of dB above this app's own noise floor.
/// Additive synthesis of the harmonics up to Nyquist would be exact too, but it costs one cosine
/// per harmonic per sample — 2400 of them for a 20 Hz square at 192 kHz, against the ~50 a multitone
/// comb needs, which is well past what the TX loop can afford (see
/// <c>Hardware.Qa40xUsbDevice.TransferPump</c>'s own rule) — and it buys only Gibbs ripple in place
/// of the hardware's own. The edge this produces is also the fastest the DAC can be asked for, which
/// is the point of a square-wave test.</para>
///
/// <para>The cost is that the frequency is <b>quantised</b>: only <c>fs/M</c> for even integer
/// <c>M</c> is available, which is exact at 1 kHz on any supported rate and a few per cent off near
/// the top of the audio band (10 kHz at 192 kHz lands on 9.6 kHz, M = 20). That is reported back
/// through the effective tone list rather than hidden, exactly as coherent bin snapping already
/// reports a tone's real frequency — see <c>DeviceSessionService.ResolveWaves</c>. <c>M</c> is
/// <b>even</b> so the two halves of a square hold the same number of samples: an odd period puts a
/// DC offset and a full set of even harmonics on a waveform that should have neither. A sawtooth
/// would be happy with an odd period and gets the even one anyway, so that the four shapes at one
/// setting all play <i>one</i> frequency — a family whose members disagreed about what 10 kHz means
/// would be worse than a grid twice as coarse on one of them.</para>
///
/// <para><b>Why four kinds and not two kinds with more fields.</b> A pulse is a square with a
/// settable width, so it could have been a duty field on <see cref="GeneratorKind.Square"/> — and
/// must not be, because several things in the app rely on "Square" meaning a square: its ideal
/// harmonic ladder, its 48.3 % THD figure, and the harmonics table a reader compares a DUT against.
/// A separate kind keeps those true and costs one enum member. The <i>settings</i> are shared, for
/// the reason <see cref="WhiteNoiseSettings"/> gives for serving two noise kinds — see
/// <see cref="PeriodicWaveSettings.DutyPercent"/>, which only the pulse reads.</para>
/// </summary>
public static class PeriodicWave
{
    /// <summary>
    /// Shortest period allowed, in frames. Two samples high and two low — at that point the
    /// waveform is four points per cycle and a square is indistinguishable from a sine, so this is
    /// the floor at which the shape still means anything, and it caps the frequency at
    /// <c>fs/4</c> (48 kHz at 192 kHz).
    /// </summary>
    public const int MinPeriodFrames = 4;

    /// <summary>Narrowest and widest a pulse may be, as a percentage of its period — see <see cref="WidthFrames"/>.</summary>
    public const double MinDutyPercent = 1.0;

    /// <inheritdoc cref="MinDutyPercent"/>
    public const double MaxDutyPercent = 99.0;

    /// <summary>Which kinds this class synthesizes — the one place the set is named, so a switch elsewhere cannot cover one and miss another.</summary>
    public static bool IsWaveKind(GeneratorKind kind) =>
        kind is GeneratorKind.Square or GeneratorKind.Triangle
            or GeneratorKind.Sawtooth or GeneratorKind.Pulse;

    /// <summary>
    /// Whether this shape's series holds <b>every</b> harmonic rather than the odd ones alone.
    ///
    /// <para>A square and a triangle are half-wave symmetric — the second half of a period is the
    /// negative of the first — which is exactly the condition that kills every even harmonic. A
    /// sawtooth is not, and a pulse is only at 50 %. The distinction is worth naming because it is
    /// what the stimulus is <i>chosen</i> for: a sawtooth's even harmonics are what makes it the
    /// shape to reach for when a DUT's second-order behaviour is the subject.</para>
    /// </summary>
    public static bool HasEvenHarmonics(GeneratorKind kind) =>
        kind is GeneratorKind.Sawtooth or GeneratorKind.Pulse;

    /// <summary>
    /// One cycle as a whole, even number of frames: the nearest even integer to
    /// <c>sampleRateHz / frequencyHz</c>, floored at <see cref="MinPeriodFrames"/>. Returns 0 for a
    /// frequency or rate that cannot produce a waveform at all, which the synthesizers answer with
    /// silence.
    /// </summary>
    public static int PeriodFrames(double frequencyHz, double sampleRateHz)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0.0 || !double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
            return 0;

        // Round the number of *half* cycles, then double it. Rounding the whole period and forcing
        // it even afterwards would quantise in steps of two frames, i.e. twice as coarsely, for
        // nothing: at 10 kHz / 192 kHz that is the difference between 9.6 kHz and 10.1 kHz.
        double halfPeriod = sampleRateHz / (2.0 * frequencyHz);
        long half = (long)Math.Round(halfPeriod, MidpointRounding.AwayFromZero);
        long period = Math.Max(half * 2, MinPeriodFrames);

        // A frequency below one cycle per int.MaxValue frames is not a stimulus; silence is a
        // better answer than an overflowed period.
        return period > int.MaxValue ? 0 : (int)period;
    }

    /// <summary>
    /// The frequency actually played for a requested one: <c>fs / PeriodFrames</c>. What status
    /// reports and what the panel shows beneath the typed value.
    /// </summary>
    public static double EffectiveFrequencyHz(double frequencyHz, double sampleRateHz)
    {
        int period = PeriodFrames(frequencyHz, sampleRateHz);
        return period <= 0 ? 0.0 : sampleRateHz / period;
    }

    /// <summary>
    /// How many frames of a <paramref name="periodFrames"/>-frame period a pulse spends high: the
    /// duty rounded to a whole sample, and never 0 or the whole period.
    ///
    /// <para>A whole number for the same reason the period is one — a width that fell between
    /// samples would not repeat — and clamped at both ends because a pulse that is never high and
    /// one that is always high are both silence wearing a frequency, and the control that produced
    /// them would read as broken rather than as refused. At the four-frame floor the only width
    /// available is one, two or three frames, so the duty a short period can express is coarse; the
    /// <i>effective</i> duty is reported for that reason.</para>
    /// </summary>
    public static int WidthFrames(double dutyPercent, int periodFrames)
    {
        if (periodFrames < MinPeriodFrames)
            return 0;

        int width = (int)Math.Round(
            periodFrames * ResolveDutyPercent(dutyPercent) / 100.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(width, 1, periodFrames - 1);
    }

    /// <summary>
    /// The duty to actually play for a stored or typed one: clamped to the range a pulse has, and
    /// <b>50 for anything that is not a positive number at all</b>.
    ///
    /// <para>That second half is a compatibility rule rather than a clamp, and it is load-bearing.
    /// A <c>params_json</c> written before this field existed has no <c>dutyPercent</c> in it — and
    /// an absent property does <b>not</b> come back as this record's own 50. The app's serializer
    /// has <c>AppJsonContext.Default</c> first in its resolver chain, and the source-generated
    /// resolver leaves an unset <c>init</c> property at the type's <c>default</c> rather than at the
    /// value its initializer gives it; plain reflection-based deserialization of the same document
    /// answers 50. So every square and triangle saved before today reads back as <b>0</b>, which
    /// clamped to the 1 % floor would open a stored measurement as a sliver of a pulse. Nobody can
    /// have typed a 0 — <c>GeneratorEndpoints</c> refuses anything outside 1..99 — so a
    /// non-positive duty means "not stated", and the default is the honest answer to that.</para>
    ///
    /// <para>Read by <see cref="WidthFrames"/> and by <c>DeviceSessionService.ToResponse</c>, which
    /// is the boundary a stored document comes back across, so the panel shows 50 rather than a 0
    /// it would then refuse to run.</para>
    /// </summary>
    public static double ResolveDutyPercent(double dutyPercent) =>
        double.IsFinite(dutyPercent) && dutyPercent > 0.0
            ? Math.Clamp(dutyPercent, MinDutyPercent, MaxDutyPercent)
            : 50.0;

    /// <summary>The duty a pulse really plays, in percent: <see cref="WidthFrames"/> over the period.</summary>
    public static double EffectiveDutyPercent(double dutyPercent, int periodFrames)
    {
        int width = WidthFrames(dutyPercent, periodFrames);
        return width <= 0 ? 0.0 : 100.0 * width / periodFrames;
    }

    /// <summary>
    /// This waveform's instantaneous value at <paramref name="frameIndex"/>, in volts.
    ///
    /// <para>Integer modulo on the frame index, like the chirp's wrap and for the same reason: the
    /// period has to land on exactly the same frame however long the stream has been running, or
    /// the waveform slowly walks off its own grid and every harmonic smears.</para>
    ///
    /// <para>Every shape is in <b>sine phase</b> — its fundamental starts at zero and rises — so a
    /// square, a triangle, a sawtooth, a pulse and a sine of the same frequency all have their
    /// fundamentals in phase, and the oscilloscope's zero-crossing trigger lands at the same place
    /// on all of them.</para>
    /// </summary>
    public static double Sample(GeneratorKind kind, PeriodicWaveSettings? settings, long frameIndex, double sampleRateHz)
    {
        if (settings is null)
            return 0.0;

        int period = PeriodFrames(settings.FrequencyHz, sampleRateHz);
        if (period <= 0)
            return 0.0;

        long phase = frameIndex % period;
        if (phase < 0)
            phase += period;

        double peakV = PeakVolts(kind, settings.LevelDbv);

        return kind switch
        {
            GeneratorKind.Square => phase < period / 2 ? peakV : -peakV,
            GeneratorKind.Triangle => peakV * TriangleUnit(phase, period),
            GeneratorKind.Sawtooth => peakV * SawtoothUnit(phase, period),
            GeneratorKind.Pulse => phase < WidthFrames(settings.DutyPercent, period) ? peakV : -peakV,
            _ => 0.0,
        };
    }

    /// <summary>
    /// A triangle of unit peak over one period: 0 at the start, +1 a quarter of the way through,
    /// 0 at the half, -1 at three quarters. Odd-harmonic only, like the square, and in the same
    /// phase as a sine of the same frequency.
    /// </summary>
    private static double TriangleUnit(long phase, int period)
    {
        // 4 * (phase/period) folded about the quarter and three-quarter points.
        double u = (double)phase / period;
        return u < 0.25 ? 4.0 * u
            : u < 0.75 ? 2.0 - 4.0 * u
            : 4.0 * u - 4.0;
    }

    /// <summary>
    /// A rising ramp of unit peak over one period: through zero at the start, up to +1 just before
    /// the half, then the jump to -1 and back up. Sine phase, like the rest.
    ///
    /// <para><b>The samples sit half a sample off the ideal ramp, and that is what makes its DC
    /// exactly zero.</b> Taking the ramp at <c>n/M</c> gives the M values <c>2n/M − 1</c>, whose
    /// mean is <c>−1/M</c> — half a per cent of peak at 192 samples a cycle and a quarter of it at
    /// the four-frame floor, i.e. a real DC offset that grows as the frequency does, on a stimulus
    /// a user may well be driving into a voice coil. Sampling at <c>(n + ½)/M</c> instead puts the
    /// M values symmetrically about zero, so they sum to zero exactly for any M. It costs half a
    /// sample of phase and nothing else: the harmonic amplitudes are unchanged, since the half-sample
    /// shift is a pure delay.</para>
    ///
    /// <para>What it does cost is a hair of amplitude — the extremes are <c>±(1 − 1/M)</c> rather
    /// than ±1, so the sampled rms is <c>√(1 − 1/M²)</c> of the ideal, 0.28 dB low at the floor and
    /// nothing at any usable period. That is the sampled triangle's situation too (its rms runs
    /// 1.76 dB <i>high</i> at M = 4) and is left uncorrected for the same reason: the figure that
    /// has to be right is the <i>fundamental</i> the analyzer reads, which
    /// <see cref="FundamentalDbv"/> corrects exactly.</para>
    /// </summary>
    private static double SawtoothUnit(long phase, int period)
    {
        // Half-sample offset, then a half-period rotation so the ramp crosses zero at phase 0
        // rather than starting at its own trough.
        double u = (phase + 0.5) / period + 0.5;
        u -= Math.Floor(u);
        return 2.0 * u - 1.0;
    }

    /// <summary>
    /// The peak volts a level of <paramref name="levelDbv"/> means for this shape.
    ///
    /// <para><b>The level is rms throughout, as it is for every other kind here</b> — dBV
    /// referenced to 1 Vrms, which is what the tones, the comb and the noise all mean by it. So the
    /// shapes at one level have different peaks: a square's peak equals its rms (crest factor 1,
    /// 3.01 dB *below* a sine of the same level), a triangle's and a sawtooth's are sqrt(3) times it
    /// (4.77 dB above), and a sine's is sqrt(2). Matching peaks instead would have made
    /// "0 dBV" mean several different output powers and every level comparison between shapes wrong;
    /// this way a square and a sine at the same dBV deliver the same power into the same load.</para>
    ///
    /// <para><b>A pulse's peak is its rms at every duty</b>, since every sample of it is ±peak —
    /// which is the property that makes the duty control safe to turn: narrowing a pulse changes
    /// its spectrum and its DC and leaves its loudness, its peak and the output range it needs
    /// exactly where they were. A unipolar pulse would not have it: its rms would fall as the square
    /// root of the duty, so sweeping the duty would silently drive the DUT 20 dB harder at 1 %.</para>
    /// </summary>
    public static double PeakVolts(GeneratorKind kind, double levelDbv)
    {
        double rmsV = Math.Pow(10.0, levelDbv / 20.0);

        return kind switch
        {
            GeneratorKind.Square or GeneratorKind.Pulse => rmsV,
            GeneratorKind.Triangle or GeneratorKind.Sawtooth => Math.Sqrt(3.0) * rmsV,
            _ => 0.0,
        };
    }

    /// <summary>
    /// The DC this shape carries, in volts: zero for everything but a pulse away from 50 %.
    ///
    /// <para>A bipolar pulse of duty <c>d</c> sits at <c>peak·(2d − 1)</c> on average, which at a
    /// 10 % duty is <b>−0.8 of peak</b> — most of the waveform, rather than a trim. It is reported
    /// rather than removed because removing it would mean moving the two levels apart by different
    /// amounts, and then the "peak" a reader sets would be neither of them. Both analyzer inputs are
    /// AC-coupled (corner near 0.2 Hz), so this never reaches a <i>reading</i> — it reaches the
    /// DUT, which is the point of saying it out loud.</para>
    /// </summary>
    public static double DcVolts(GeneratorKind kind, PeriodicWaveSettings? settings, double sampleRateHz)
    {
        if (kind != GeneratorKind.Pulse || settings is null)
            return 0.0;

        int period = PeriodFrames(settings.FrequencyHz, sampleRateHz);
        int width = WidthFrames(settings.DutyPercent, period);
        if (width <= 0)
            return 0.0;

        return PeakVolts(kind, settings.LevelDbv) * (2.0 * width / period - 1.0);
    }

    /// <summary>
    /// Level of the <i>fundamental</i> alone, dBV, for a shape at <paramref name="levelDbv"/> total
    /// rms. Square: the fundamental carries 4/pi of the peak, i.e. 0.9003 of the total rms, 0.9121 dB
    /// down. Triangle: 8/pi^2 of the peak, 0.9926 of the total rms, 0.0633 dB down. Sawtooth: 2/pi of
    /// the peak against an rms of peak/sqrt(3), i.e. 0.7797, 2.162 dB down. Pulse: the square's
    /// figure times <c>sin(pi.d)</c>, so it falls away as the pulse narrows — 10.2 dB further down at
    /// a 10 % duty, which is the energy that has gone into the harmonics.
    ///
    /// <para>This is what goes in <see cref="ChannelGeneratorConfig.Tones"/> for these kinds, so
    /// everything downstream that tracks "the fundamental" — the scope trigger, the Gen tile, the
    /// gain reading — sees the line the analyzer will actually measure rather than the waveform's
    /// total. The remainder is the harmonic series, which is exactly what the measurement is for:
    /// an ideal square reads THD 48.3 %, a triangle 12.1 % and a sawtooth 80.3 %.</para>
    ///
    /// <para><b>A sampled shape's lines are not the textbook ones, and <paramref name="periodFrames"/>
    /// is the correction.</b> Every harmonic of the ideal shape above Nyquist folds back onto a
    /// harmonic below it and adds, which has an exact closed form: a square of period <c>M</c>
    /// samples has peak amplitude <c>4/(M.sin(pi.k/M))</c> at odd harmonic <c>k</c>, a sawtooth
    /// <c>2/(M.sin(pi.k/M))</c> at every <c>k</c>, a pulse of width <c>w</c>
    /// <c>(2/M).sin(pi.k.w/M)/sin(pi.k/M)</c>, and a triangle the square of the square's factor. So
    /// each series is the continuous-time one scaled by <c>((pi/M)/sin(pi/M))^p</c> with <c>p</c> = 1
    /// for the square, the sawtooth and the pulse alike and 2 for the triangle, and the harmonic
    /// ratios are <c>(sin(pi/M)/sin(pi.k/M))^p</c> rather than <c>1/k^p</c>. It is negligible where
    /// these shapes are usually used and not where they are interesting: 0.003 dB on the fundamental
    /// at 64 samples a cycle (750 Hz at 48 kHz) and 0.9 dB at the four-sample floor, and at the top
    /// of the series 0.17 dB at H7 of that same 64-sample square. This is the hardware's own
    /// reconstruction and not an error to correct for — but the figure the app *reports* has to be
    /// the one the analyzer will read, so it is applied here. Pass 0 (the default) for the
    /// continuous-time limit, which is what a panel that does not know the sample rate should
    /// quote.</para>
    /// </summary>
    /// <param name="widthFrames">
    /// A pulse's own width, which decides how much of its energy is in the fundamental. Read for
    /// <see cref="GeneratorKind.Pulse"/> alone, and 0 there means the square's 50 %.
    /// </param>
    public static double FundamentalDbv(
        GeneratorKind kind, double levelDbv, int periodFrames = 0, int widthFrames = 0)
    {
        double ratio = kind switch
        {
            GeneratorKind.Square => 4.0 / (Math.PI * Math.Sqrt(2.0)),
            GeneratorKind.Triangle => 8.0 / (Math.PI * Math.PI * Math.Sqrt(2.0)) * Math.Sqrt(3.0),
            GeneratorKind.Sawtooth => 2.0 / Math.PI * Math.Sqrt(3.0) / Math.Sqrt(2.0),
            GeneratorKind.Pulse => 4.0 / (Math.PI * Math.Sqrt(2.0)) * PulseFundamentalFactor(periodFrames, widthFrames),
            _ => 0.0,
        };

        if (ratio <= 0.0)
            return double.NegativeInfinity;

        return levelDbv + 20.0 * Math.Log10(ratio * SamplingFactor(kind, periodFrames));
    }

    /// <summary>
    /// <c>sin(pi.d)</c>, how much of a square's fundamental a pulse of duty <c>d</c> keeps. One at
    /// 50 %, where a pulse <i>is</i> a square, so an unknown width answers for that case rather
    /// than for silence.
    /// </summary>
    private static double PulseFundamentalFactor(int periodFrames, int widthFrames)
    {
        if (periodFrames < MinPeriodFrames || widthFrames <= 0 || widthFrames >= periodFrames)
            return 1.0;

        return Math.Sin(Math.PI * widthFrames / periodFrames);
    }

    /// <summary>
    /// How much a whole line of the sampled series sits above the continuous-time one:
    /// <c>((pi/M)/sin(pi/M))^p</c>. One for an unknown period, so a caller that does not know the
    /// sample rate gets the textbook figure rather than a wrong correction — see
    /// <see cref="FundamentalDbv"/>, which is where the reasoning lives.
    /// </summary>
    public static double SamplingFactor(GeneratorKind kind, int periodFrames)
    {
        if (periodFrames < MinPeriodFrames)
            return 1.0;

        double factor = (Math.PI / periodFrames) / Math.Sin(Math.PI / periodFrames);

        return kind switch
        {
            GeneratorKind.Square or GeneratorKind.Sawtooth or GeneratorKind.Pulse => factor,
            GeneratorKind.Triangle => factor * factor,
            _ => 1.0,
        };
    }

    /// <summary>
    /// How many harmonics of <paramref name="frequencyHz"/> fit below Nyquist at
    /// <paramref name="sampleRateHz"/>, counting the fundamental — the number the panel shows,
    /// because it is what sets the edge.
    ///
    /// <para>Half the period for the shapes that carry every harmonic, and half that again for the
    /// odd-only ones: a square of period M samples has its highest line <c>(M/2 − 1)</c> harmonics
    /// up, of which the odd ones are what it holds.</para>
    /// </summary>
    public static int HarmonicsBelowNyquist(GeneratorKind kind, double frequencyHz, double sampleRateHz)
    {
        int period = PeriodFrames(frequencyHz, sampleRateHz);
        if (period <= 0)
            return 0;

        // Harmonics 1..(M/2 - 1) are strictly below Nyquist; M/2 lands exactly on it.
        int all = period / 2 - 1;
        if (all < 1)
            return period >= MinPeriodFrames ? 1 : 0;

        // Odd harmonics 1, 3, 5, ... strictly below period/2.
        return HasEvenHarmonics(kind) ? all : (all + 1) / 2;
    }
}
