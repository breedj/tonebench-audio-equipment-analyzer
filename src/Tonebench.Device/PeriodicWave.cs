namespace Tonebench.Device;

/// <summary>
/// The square and triangle stimuli: <see cref="GeneratorKind.Square"/> and
/// <see cref="GeneratorKind.Triangle"/>.
///
/// <para>Shared rather than duplicated per synthesizer, the way <see cref="PinkNoiseShaper"/> is and
/// unlike the chirp formula: there is real arithmetic here (the period quantisation, and the three
/// different voltages one level means), and a simulator that disagreed with the hardware about any
/// of it would read as a DUT that behaves differently under <c>--simulate</c>.</para>
///
/// <para><b>Hard-switched at an exact integer sample period, not band-limited additive synthesis,
/// and that is a correctness decision rather than a shortcut.</b> A sequence whose period is exactly
/// <see cref="PeriodFrames"/> samples is periodic in the sampled domain, so its entire spectrum lies
/// on multiples of <c>fs/M</c> — every harmonic above Nyquist folds onto another odd harmonic of the
/// same fundamental, and there is no inharmonic content anywhere in the record. That is the property
/// an FFT analyzer needs; a square generated from a free-running phase accumulator instead scatters
/// folded harmonics across the spectrum at levels tens of dB above this app's own noise floor.
/// Additive synthesis of the odd harmonics up to Nyquist would be exact too, but it costs one cosine
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
/// DC offset and a full set of even harmonics on a waveform that should have neither.</para>
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

    /// <summary>Which kinds this class synthesizes — the one place the pair is named, so a switch elsewhere cannot cover one and miss the other.</summary>
    public static bool IsWaveKind(GeneratorKind kind) =>
        kind is GeneratorKind.Square or GeneratorKind.Triangle;

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
    /// This waveform's instantaneous value at <paramref name="frameIndex"/>, in volts.
    ///
    /// <para>Integer modulo on the frame index, like the chirp's wrap and for the same reason: the
    /// period has to land on exactly the same frame however long the stream has been running, or
    /// the waveform slowly walks off its own grid and every harmonic smears.</para>
    ///
    /// <para>Both shapes start at zero and rise, i.e. sine phase, so a square and a sine of the
    /// same frequency have their fundamentals in phase and the oscilloscope's zero-crossing
    /// trigger lands at the same place on all three stimuli.</para>
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
    /// The peak volts a level of <paramref name="levelDbv"/> means for this shape.
    ///
    /// <para><b>The level is rms throughout, as it is for every other kind here</b> — dBV
    /// referenced to 1 Vrms, which is what the tones, the comb and the noise all mean by it. So the
    /// three shapes at one level have three different peaks: a square's peak equals its rms
    /// (crest factor 1, 3.01 dB *below* a sine of the same level), a triangle's is sqrt(3) times it
    /// (4.77 dB above), and a sine's is sqrt(2). Matching peaks instead would have made
    /// "0 dBV" mean three different output powers and every level comparison between shapes wrong;
    /// this way a square and a sine at the same dBV deliver the same power into the same load.</para>
    /// </summary>
    public static double PeakVolts(GeneratorKind kind, double levelDbv)
    {
        double rmsV = Math.Pow(10.0, levelDbv / 20.0);

        return kind switch
        {
            GeneratorKind.Square => rmsV,
            GeneratorKind.Triangle => Math.Sqrt(3.0) * rmsV,
            _ => 0.0,
        };
    }

    /// <summary>
    /// Level of the <i>fundamental</i> alone, dBV, for a shape at <paramref name="levelDbv"/> total
    /// rms. Square: the fundamental carries 4/pi of the peak, i.e. 0.9003 of the total rms, 0.9121 dB
    /// down. Triangle: 8/pi^2 of the peak, 0.9926 of the total rms, 0.0633 dB down.
    ///
    /// <para>This is what goes in <see cref="ChannelGeneratorConfig.Tones"/> for these kinds, so
    /// everything downstream that tracks "the fundamental" — the scope trigger, the Gen tile, the
    /// gain reading — sees the line the analyzer will actually measure rather than the waveform's
    /// total. The remainder is the harmonic series, which is exactly what the measurement is for:
    /// an ideal square reads THD 48.3 % and an ideal triangle 12.1 %.</para>
    ///
    /// <para><b>A sampled square's lines are not 4/(pi.n), and <paramref name="periodFrames"/> is
    /// the correction.</b> Every harmonic of the ideal shape above Nyquist folds back onto an odd
    /// harmonic below it and adds, which has an exact closed form: a square of period <c>M</c>
    /// samples has peak amplitude <c>4/(M.sin(pi.k/M))</c> at odd harmonic <c>k</c>, and a triangle
    /// the square of the same factor. So the whole series is the continuous-time one scaled by
    /// <c>((pi/M)/sin(pi/M))^p</c> with <c>p</c> = 1 for a square and 2 for a triangle, and the
    /// harmonic ratios are <c>(sin(pi/M)/sin(pi.k/M))^p</c> rather than <c>1/k^p</c>. It is
    /// negligible where a square is usually used and not where it is interesting: 0.003 dB on the
    /// fundamental at 64 samples a cycle (750 Hz at 48 kHz) and 0.9 dB at the four-sample floor,
    /// and at the top of the series 0.17 dB at H7 of that same 64-sample square. This is the
    /// hardware's own reconstruction and not an error to correct for — but the figure the app
    /// *reports* has to be the one the analyzer will read, so it is applied here. Pass 0 (the
    /// default) for the continuous-time limit, which is what a panel that does not know the sample
    /// rate should quote.</para>
    /// </summary>
    public static double FundamentalDbv(GeneratorKind kind, double levelDbv, int periodFrames = 0)
    {
        double ratio = kind switch
        {
            GeneratorKind.Square => 4.0 / (Math.PI * Math.Sqrt(2.0)),
            GeneratorKind.Triangle => 8.0 / (Math.PI * Math.PI * Math.Sqrt(2.0)) * Math.Sqrt(3.0),
            _ => 0.0,
        };

        if (ratio <= 0.0)
            return double.NegativeInfinity;

        return levelDbv + 20.0 * Math.Log10(ratio * SamplingFactor(kind, periodFrames));
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
            GeneratorKind.Square => factor,
            GeneratorKind.Triangle => factor * factor,
            _ => 1.0,
        };
    }

    /// <summary>
    /// How many harmonics of <paramref name="frequencyHz"/> fit below Nyquist at
    /// <paramref name="sampleRateHz"/>, counting the fundamental — the number the panel shows,
    /// because it is what sets the edge. Half the period, by construction: a square of period M
    /// samples has its highest line at <c>(M/2 - 1)</c> odd harmonics up.
    /// </summary>
    public static int HarmonicsBelowNyquist(double frequencyHz, double sampleRateHz)
    {
        int period = PeriodFrames(frequencyHz, sampleRateHz);
        if (period <= 0)
            return 0;

        // Odd harmonics 1, 3, 5, ... strictly below period/2 (Nyquist is period/2 harmonics up).
        return (period / 2 + 1) / 2;
    }
}
