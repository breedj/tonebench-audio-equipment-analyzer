namespace Tonebench.Dsp;

/// <summary>
/// The horizontal readings, all of them measured between the record's own settled levels rather
/// than between its extremes — see <see cref="WaveformHistogram"/> for why that distinction is the
/// whole point.
/// </summary>
/// <param name="PeriodS">
/// One period, from one rising mid-level crossing to the next. Null where fewer than two were
/// found, which is a record holding less than one cycle.
/// </param>
/// <param name="FrequencyHz">The reciprocal, carried because it is what a reader actually wants.</param>
/// <param name="PositiveWidthS">A rising crossing to the next falling one — how long the waveform spends above the middle.</param>
/// <param name="NegativeWidthS">The other half.</param>
/// <param name="DutyPercent">The positive width as a percentage of the period.</param>
/// <param name="RiseTimeS">10 % to 90 % of the first clean rising edge.</param>
/// <param name="FallTimeS">90 % to 10 % of the first clean falling edge.</param>
/// <param name="OvershootPercent">
/// How far the record goes past the top, as a percentage of the amplitude. Zero when it does not.
/// </param>
/// <param name="PreshootPercent">The same below the base.</param>
public readonly record struct WaveformTiming(
    double? PeriodS,
    double? FrequencyHz,
    double? PositiveWidthS,
    double? NegativeWidthS,
    double? DutyPercent,
    double? RiseTimeS,
    double? FallTimeS,
    double? OvershootPercent,
    double? PreshootPercent);

/// <summary>
/// Period, width, duty and edge timing from one captured record and the two levels it settles at.
///
/// <para><b>Every crossing is interpolated.</b> One sample is 5.2 µs at 192 kS/s, and a rise time
/// is routinely a handful of samples — read off integer indices, a 20 µs edge would be quoted in
/// steps of a quarter of itself. Linear interpolation between the two samples that straddle a
/// threshold is exact for a straight segment and good to far better than a sample for anything
/// band-limited enough to be on this grid, which is the same reasoning <see cref="EdgeTrigger"/>'s
/// own fraction rests on.</para>
///
/// <para><b>What is deliberately not here: anything the analyzer cannot honestly resolve.</b> The
/// DAC's reconstruction filter corners near 86 kHz, so the fastest edge it can produce rises in
/// about 4 µs — roughly one sample at the top rate. A rise time near that figure is the analyzer's
/// own, and the screen says so rather than this refusing to report it: a reading that is the
/// instrument's is still the right reading of what arrived.</para>
/// </summary>
public static class WaveformTimingMeasure
{
    /// <summary>Where the edges are measured between, as fractions of the amplitude.</summary>
    public const double LowThresholdFraction = 0.10;
    public const double HighThresholdFraction = 0.90;

    /// <summary>
    /// Measures <paramref name="samples"/> against the levels in <paramref name="topV"/> and
    /// <paramref name="baseV"/>.
    ///
    /// <para>The caller supplies the levels because they are a measurement in their own right and
    /// are reported beside these — and because a caller that already knows them (a stored result, a
    /// test) should not have to pay for them twice.</para>
    /// </summary>
    public static WaveformTiming Measure(ReadOnlySpan<double> samples, double sampleRateHz, double topV, double baseV)
    {
        if (samples.Length < 2 || !(sampleRateHz > 0.0) || !(topV > baseV))
            return default;

        double amplitude = topV - baseV;
        double mid = baseV + (amplitude * 0.5);
        double low = baseV + (amplitude * LowThresholdFraction);
        double high = baseV + (amplitude * HighThresholdFraction);

        // The mid-level crossings, in order, each with its direction. One pass; a record of a
        // 20 kHz tone at 192 kS/s holds a few hundred of them and a 1 s record of 20 kHz holds
        // 40 000, so this is a list rather than an array of every possible crossing.
        double? firstRise = null;
        double? secondRise = null;
        double? firstFallAfterRise = null;
        double? firstFall = null;

        for (int i = 1; i < samples.Length; i++)
        {
            double a = samples[i - 1];
            double b = samples[i];

            if (a < mid && b >= mid)
            {
                double at = i - 1 + Fraction(a, b, mid);
                if (firstRise is null)
                    firstRise = at;
                else if (secondRise is null)
                    secondRise = at;
            }
            else if (a > mid && b <= mid)
            {
                double at = i - 1 + Fraction(a, b, mid);
                firstFall ??= at;
                if (firstRise is not null && firstFallAfterRise is null)
                    firstFallAfterRise = at;
            }
        }

        double? periodSamples = firstRise is { } r1 && secondRise is { } r2 ? r2 - r1 : null;
        double? period = periodSamples / sampleRateHz;
        double? positive =
            firstRise is { } rise && firstFallAfterRise is { } fall && fall > rise ? (fall - rise) / sampleRateHz : null;
        double? duty = period > 0 && positive is { } pos ? pos / period * 100.0 : null;
        double? negative = period is { } p && positive is { } pw ? p - pw : null;

        return new WaveformTiming(
            PeriodS: period,
            FrequencyHz: period > 0 ? 1.0 / period : null,
            PositiveWidthS: positive,
            NegativeWidthS: negative is > 0 ? negative : null,
            DutyPercent: duty,
            RiseTimeS: EdgeTime(samples, sampleRateHz, low, high, rising: true),
            FallTimeS: EdgeTime(samples, sampleRateHz, low, high, rising: false),
            OvershootPercent: Beyond(samples, topV, amplitude, above: true),
            PreshootPercent: Beyond(samples, baseV, amplitude, above: false));
    }

    /// <summary>
    /// The first edge that crosses both thresholds in the same direction without turning back.
    ///
    /// <para>Taken from the <b>first</b> such edge rather than averaged over every one, which is
    /// what a bench scope quotes and is the only honest answer on a record that holds one edge —
    /// the transient this screen exists for. A record of a repetitive waveform has many and they
    /// agree, so the first is as good as any.</para>
    /// </summary>
    private static double? EdgeTime(ReadOnlySpan<double> samples, double sampleRateHz, double low, double high, bool rising)
    {
        double? crossedFirst = null;

        for (int i = 1; i < samples.Length; i++)
        {
            double a = samples[i - 1];
            double b = samples[i];

            double near = rising ? low : high;
            double far = rising ? high : low;
            bool crossedNear = rising ? a < near && b >= near : a > near && b <= near;
            bool crossedFar = rising ? a < far && b >= far : a > far && b <= far;
            // Turning back before the far threshold abandons the attempt: a ring that pokes through
            // 10 % and falls away is not the start of an edge.
            bool turnedBack = rising ? a >= near && b < near : a <= near && b > near;

            if (crossedNear)
                crossedFirst ??= i - 1 + Fraction(a, b, near);
            else if (turnedBack)
                crossedFirst = null;

            if (crossedFar && crossedFirst is { } start)
            {
                double end = i - 1 + Fraction(a, b, far);
                return end > start ? (end - start) / sampleRateHz : null;
            }
        }

        return null;
    }

    /// <summary>How far past a level the record goes, as a percentage of the amplitude; zero when it does not.</summary>
    private static double? Beyond(ReadOnlySpan<double> samples, double level, double amplitude, bool above)
    {
        if (!(amplitude > 0.0))
            return null;

        double worst = 0.0;
        foreach (double v in samples)
        {
            double past = above ? v - level : level - v;
            if (past > worst)
                worst = past;
        }

        return worst / amplitude * 100.0;
    }

    /// <summary>Where between two samples a threshold is crossed, 0 at the first and 1 at the second.</summary>
    private static double Fraction(double a, double b, double threshold)
    {
        double span = b - a;
        if (span == 0.0)
            return 0.0;
        double t = (threshold - a) / span;
        return t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
    }
}
