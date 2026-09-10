namespace Qa403.Dsp;

/// <summary>
/// How a THD-vs-frequency sweep sizes its FFT and picks its sample rate, per point.
///
/// <para>The insight this whole class rests on: for a bin-snapped tone, <b>the bin the fundamental
/// lands on and the number of cycles in the record are the same number.</b> Bin index is
/// <c>f / (fs/N)</c> and cycles is <c>f · (N/fs)</c> — identical. So one parameter,
/// <see cref="TargetFundamentalBins"/>, sets both "enough resolution for the harmonic analyzer" and
/// "enough cycles for a meaningful record", and there is no second knob to keep consistent with
/// it.</para>
///
/// <para>A fixed FFT size is therefore sized for the <i>bottom</i> of the sweep and then paid at
/// every point above it. At 192 kHz a 131072-point record is 683 ms; that is 13.6 cycles at 20 Hz
/// (barely adequate) but 683 cycles at 1 kHz (about 10x more than anything needs). Scaling N down
/// with frequency holds the cycle count roughly constant instead, which is what makes the top of a
/// sweep fast.</para>
///
/// <para><b>What this trades away.</b> A harmonic is a coherent tone and lands in a single bin, so
/// its measured level does not depend on bin width — but the noise it competes with does. Halving N
/// doubles the bin width and raises the per-bin noise floor by 3 dB. So a smaller N does not make
/// THD less accurate, it makes low-level harmonics harder to see out of the noise. That is why
/// <see cref="TargetFundamentalBins"/> is generous rather than minimal, and why the adaptive mode is
/// a switch rather than the only behaviour.</para>
///
/// <para>Pure and unit-tested; nothing here touches a device or the pipeline.</para>
/// </summary>
public static class SweepResolution
{
    /// <summary>
    /// Bins the fundamental should sit up, equivalently cycles the record should contain.
    ///
    /// <para>Well above <c>ThdSweepRequest.MinFundamentalBins</c> (5), which is only the line below
    /// which the harmonic analyzer sums the fundamental into its own H2 and the number becomes
    /// fabricated. That comment puts "comfortable" at ~20 bins; this is deliberately three times
    /// that, because clearing the fabrication threshold is not the same as having margin against
    /// the 1/f floor and mains harmonics that live near DC.</para>
    /// </summary>
    public const int TargetFundamentalBins = 64;

    /// <summary>
    /// Widest bin the adaptive sizer will settle for, which is what bounds the FFT size from below.
    ///
    /// <para>A bin-width floor rather than a sample-count one, because what a small FFT actually
    /// costs is dynamic range: the noise competing with a harmonic scales with bin width, so this is
    /// the knob that decides how deep a harmonic stays visible. A sample count would mean something
    /// different at every sample rate. 46.875 Hz is 192 kHz / 4096, i.e. exactly the floor this used
    /// to have at the highest rate.</para>
    /// </summary>
    public const double MaxBinWidthHz = 46.875;

    /// <summary>
    /// Longest record the adaptive sizer will ask for, which is what bounds the FFT size from above.
    ///
    /// <para>A time ceiling rather than a sample-count one, and that distinction is load-bearing:
    /// with a count ceiling a <i>lower</i> sample rate makes the clamped low-frequency points
    /// <i>slower</i>, because the same number of samples takes longer to collect. Measured before
    /// this changed, a 20 Hz - 2 kHz sweep auto-selected 48 kHz and then ran 112 s against the fixed
    /// mode's 76 s -- adaptive resolution making a sweep half again slower, which is the opposite of
    /// its whole purpose. Bounding the time instead makes the sample rate genuinely speed-neutral,
    /// which is what <see cref="SampleRateFor"/> already claims. 0.6827 s is 131072 / 192 kHz, i.e.
    /// exactly the ceiling this used to have at the highest rate.</para>
    /// </summary>
    public const double MaxRecordSeconds = 131072.0 / 192000.0;

    /// <summary>
    /// Absolute cap on the FFT size regardless of sample rate, matching the fixed mode's size so
    /// adaptive is never the slower of the two and never the more memory-hungry.
    /// </summary>
    public const int MaxFftSize = 131072;

    /// <summary>Absolute floor, so a degenerate sample rate cannot ask for a meaninglessly short record.</summary>
    public const int MinFftSize = 1024;

    /// <summary>
    /// Harmonic order the auto-selected sample rate tries to keep below Nyquist. Five, matching the
    /// D2..D5 traces the sweep chart draws individually; D6+ is a power-summed bucket that
    /// degrades gracefully when its upper members fall out of band.
    /// </summary>
    public const int HarmonicHeadroomOrder = 5;

    /// <summary>Sample rates a QA403 supports, ascending — the candidates <see cref="SampleRateFor"/> chooses among.</summary>
    public static readonly double[] SupportedSampleRatesHz = [48000.0, 96000.0, 192000.0];

    /// <summary>
    /// The smallest power-of-two FFT size in [<paramref name="minFftSize"/>, <paramref name="maxFftSize"/>]
    /// that puts a tone at <paramref name="freqHz"/> at least <paramref name="targetBins"/> bins up.
    /// Clamped rather than allowed to exceed the ceiling, so a very low frequency yields the largest
    /// permitted size instead of an unbounded one.
    /// </summary>
    public static int FftSizeFor(
        double freqHz,
        double sampleRateHz,
        int targetBins = TargetFundamentalBins)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBins);

        (int floor, int ceiling) = BoundsFor(sampleRateHz);

        // A non-positive frequency has no bin to sit on; the longest record is the safest answer
        // rather than an exception, since the caller's grid is filtered elsewhere.
        if (!(freqHz > 0.0))
            return ceiling;

        // N >= targetBins * fs / f, rounded up to a power of two.
        int n = PowerOfTwoAtLeast(targetBins * sampleRateHz / freqHz);
        return Math.Clamp(n, floor, ceiling);
    }

    /// <summary>
    /// The FFT sizes available at a given sample rate: the smallest that keeps bins no wider than
    /// <see cref="MaxBinWidthHz"/>, and the largest whose record still fits
    /// <see cref="MaxRecordSeconds"/>. Both expressed in sample counts here, but derived from
    /// quantities that mean the same thing at every rate.
    /// </summary>
    public static (int Floor, int Ceiling) BoundsFor(double sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        int ceiling = Math.Clamp(PowerOfTwoAtMost(MaxRecordSeconds * sampleRateHz), MinFftSize, MaxFftSize);
        int floor = Math.Clamp(PowerOfTwoAtLeast(sampleRateHz / MaxBinWidthHz), MinFftSize, MaxFftSize);

        // At an absurdly low sample rate the two can cross; the time ceiling wins, since a record
        // longer than the budget is the one failure that compounds across every point of a sweep.
        if (floor > ceiling)
            floor = ceiling;

        return (floor, ceiling);
    }

    /// <summary>Smallest power of two greater than or equal to <paramref name="value"/>, floored at 1 and capped at <paramref name="cap"/>.</summary>
    private static int PowerOfTwoAtLeast(double value, int cap = MaxFftSize)
    {
        int n = 1;
        while (n < value && n < cap)
            n *= 2;
        return n;
    }

    /// <summary>Largest power of two less than or equal to <paramref name="value"/>, floored at 1 and capped at <paramref name="cap"/>.</summary>
    private static int PowerOfTwoAtMost(double value, int cap = MaxFftSize)
    {
        int n = 1;
        while (n * 2 <= value && n < cap)
            n *= 2;
        return n;
    }

    /// <summary>
    /// Longest record the <i>raised</i> ceiling will ask for, for a measurement whose whole point is
    /// the bottom of the band.
    ///
    /// <para><see cref="MaxRecordSeconds"/> bounds every sweep FFT to 0.6827 s, which is 1.465 Hz
    /// bins at <em>every</em> supported rate and so a 4.4 Hz floor at three bins. That ceiling exists
    /// so adaptive resolution can never make a THD sweep slower than fixed mode -- a trade that makes
    /// sense across fifty points whose bottom end is 20 Hz, and no sense at all for a measurement
    /// that exists to look below it. The chirp reaches 0.5 Hz; this is what lets a tone-at-a-time run
    /// follow it, to 1.1 Hz.</para>
    ///
    /// <para>Expressed as a <b>record time</b> for exactly the reason <see cref="MaxRecordSeconds"/>
    /// is: a time bound means the same thing at every sample rate, so the floor it produces is 1.1 Hz
    /// at 48, 96 and 192 kHz alike. A sample-count bound would not -- 524288 points is 2.73 s at
    /// 192 kHz but 10.9 s at 48 kHz, which would make the lower rates ruinously slow for no extra
    /// resolution. 2.73 s is also the chirp's own longest record, so it is a length this pipeline is
    /// already known to run.</para>
    /// </summary>
    public const double ExtendedMaxRecordSeconds = 524288.0 / 192000.0;

    /// <summary>Absolute cap on the raised ceiling, so a future lower sample rate cannot ask for an unbounded size.</summary>
    public const int ExtendedMaxFftSize = 524288;

    /// <summary>
    /// The raised FFT ceiling at a given sample rate: the longest power-of-two record inside
    /// <see cref="ExtendedMaxRecordSeconds"/>, never below the standard one.
    ///
    /// <para>Shared by every measurement that reaches below <see cref="LowestToneHz"/> -- the
    /// stepped-sine response and the stepped-sine impedance run. Three copies of this arithmetic
    /// would be one too many, and a drift between them would show up as two measurements disagreeing
    /// about which frequencies they can read at all.</para>
    /// </summary>
    public static int ExtendedCeilingFor(double sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        int standard = BoundsFor(sampleRateHz).Ceiling;
        int extended = Math.Min(
            PowerOfTwoAtMost(ExtendedMaxRecordSeconds * sampleRateHz, ExtendedMaxFftSize),
            ExtendedMaxFftSize);

        return Math.Max(standard, extended);
    }

    /// <summary>
    /// The lowest tone a record of <paramref name="fftSize"/> can read a level at, given a
    /// measurement's own minimum-bins rule.
    /// </summary>
    public static double LowestToneHz(double sampleRateHz, int fftSize, int minBins)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fftSize);

        return minBins * sampleRateHz / fftSize;
    }

    /// <summary>
    /// The adaptive size for one point against an explicit ceiling.
    ///
    /// <para>Deliberately not <see cref="FftSizeFor"/>, which clamps to the sweeps' own record-time
    /// ceiling -- that is the thing <see cref="ExtendedCeilingFor"/> exists to lift. Everything else
    /// about the ladder, the <see cref="TargetFundamentalBins"/> target and the bin-width floor, is
    /// the sweeps' own and is shared on purpose.</para>
    /// </summary>
    public static int FftSizeAt(double freqHz, double sampleRateHz, int ceiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);

        (int floor, int standardCeiling) = BoundsFor(sampleRateHz);
        int top = Math.Max(standardCeiling, ceiling);

        // A non-positive frequency has no bin to sit on; the longest record is the safest answer
        // rather than an exception, since the caller's grid is filtered elsewhere.
        if (!(freqHz > 0.0))
            return top;

        int wanted = PowerOfTwoAtLeast(TargetFundamentalBins * sampleRateHz / freqHz, top);
        return Math.Clamp(wanted, floor, top);
    }

    /// <summary>
    /// The lowest supported sample rate whose usable band still covers
    /// <paramref name="harmonicOrder"/> harmonics of <paramref name="endFreqHz"/>, or the highest
    /// supported rate when none does.
    ///
    /// <para>Note that this does <b>not</b> affect sweep speed. Under
    /// <see cref="FftSizeFor"/> the record length is <c>targetBins / f</c> seconds — the sample rate
    /// cancels, because N scales with it. Sample rate here buys exactly one thing: how many
    /// harmonics stay below Nyquist, i.e. whether D2..D5 are measurable at the top of the sweep. A
    /// sweep that stops at 2 kHz genuinely does not need 192 kHz, and dropping it cuts USB and FFT
    /// load for free.</para>
    /// </summary>
    /// <param name="nyquistFraction">Fraction of Nyquist treated as usable, matching the sweep runner's own ceiling.</param>
    public static double SampleRateFor(
        double endFreqHz,
        int harmonicOrder = HarmonicHeadroomOrder,
        double nyquistFraction = 0.98,
        IReadOnlyList<double>? supportedRatesHz = null)
    {
        var rates = supportedRatesHz ?? SupportedSampleRatesHz;
        if (rates.Count == 0)
            throw new ArgumentException("At least one sample rate must be supported.", nameof(supportedRatesHz));

        double highest = rates[0];
        foreach (double rate in rates)
            if (rate > highest)
                highest = rate;

        if (!(endFreqHz > 0.0))
            return highest;

        double needed = harmonicOrder * endFreqHz;

        double best = highest;
        foreach (double rate in rates)
        {
            if (nyquistFraction * (rate / 2.0) >= needed && rate < best)
                best = rate;
        }

        return best;
    }
}
