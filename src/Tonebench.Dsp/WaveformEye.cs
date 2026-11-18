namespace Tonebench.Dsp;

/// <summary>
/// What an eye diagram reads: how much the waveform varies from one period to the next, and how
/// much its crossings move.
///
/// <para><b>On an audio analyzer this is a jitter and modulation picture, not a data-eye.</b> A
/// comms eye folds a record on the symbol clock and reads the opening a receiver has to decide in;
/// there is no symbol clock here, so the fold is the signal's own measured period and what the
/// overlay shows is every cycle drawn on top of every other. A clean tone folds into one line.
/// Anything that moves it — amplitude modulation, rail sag under a burst, a drifting generator, a
/// noisy supply — spreads that line into a band, and a crossing that wanders spreads it
/// sideways.</para>
/// </summary>
/// <param name="MaxSpreadV">
/// The widest the band gets anywhere across the fold, peak to peak in volts: the largest
/// cycle-to-cycle difference in the record. Zero for a waveform that repeats exactly.
/// </param>
/// <param name="AtTimeS">
/// Where in the fold that worst spread sits, seconds from the fold's start. Worth more than the
/// figure alone: a spread at the peaks is amplitude modulation, one at the crossings is jitter, and
/// one at a single phase is something switching.
/// </param>
/// <param name="MeanSpreadV">
/// The band's average width over the whole fold — the figure to watch rather than the worst case,
/// which one bad cycle sets.
/// </param>
public readonly record struct EyeSpread(double MaxSpreadV, double AtTimeS, double MeanSpreadV);

/// <summary>
/// Folding a record on a period, and the two readings the overlay is for.
///
/// <para>Pure and in <c>Tonebench.Dsp</c> like every other waveform measurement here, because both
/// figures below are ones a reader acts on: a spread is read as modulation or noise, and a jitter
/// figure is read against a datasheet.</para>
/// </summary>
public static class WaveformEye
{
    /// <summary>
    /// Fewer folds than this is not an overlay of anything — one cycle drawn on its own is the
    /// waveform view, and the whole point here is the comparison between cycles.
    /// </summary>
    public const int MinFolds = 2;

    /// <summary>
    /// How many whole folds of <paramref name="foldFrames"/> frames a record of
    /// <paramref name="frameCount"/> holds, or 0 where it holds fewer than <see cref="MinFolds"/>.
    ///
    /// <para><b>Whole folds only, and the remainder is dropped.</b> A part-fold drawn with the rest
    /// would be a trace that stops in the middle of the picture, which reads as a waveform that
    /// stopped rather than as a window that ran out.</para>
    /// </summary>
    public static int FoldCount(int frameCount, int foldFrames)
    {
        if (frameCount <= 0 || foldFrames <= 0)
            return 0;

        int folds = frameCount / foldFrames;
        return folds >= MinFolds ? folds : 0;
    }

    /// <summary>
    /// How many frames one fold of <paramref name="periodS"/> seconds is, rounded to a whole
    /// sample, or 0 where that is not a usable fold.
    ///
    /// <para>Rounded rather than truncated, and that matters: the fold length is the <i>only</i>
    /// thing holding successive cycles on top of each other, so an error of half a sample per fold
    /// walks the overlay sideways by half a sample per cycle — over a hundred folds, fifty samples,
    /// which draws a clean tone as a solid band and reads as jitter that is not there. What remains
    /// after rounding is up to half a sample per fold and is a real limit of the method rather than
    /// an error in it: a period that is not a whole number of samples cannot be folded exactly, and
    /// the app's own square and triangle generators quantise their periods for the same reason.</para>
    /// </summary>
    public static int FoldFrames(double periodS, double sampleRateHz)
    {
        if (!double.IsFinite(periodS) || periodS <= 0.0 || !double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
            return 0;

        double frames = Math.Round(periodS * sampleRateHz);
        return frames >= 2.0 && frames <= int.MaxValue ? (int)frames : 0;
    }

    /// <summary>
    /// How wide the band between the folds gets, column by column.
    ///
    /// <para>Taken over the folds at <i>each position in the fold</i>, which is what makes it a
    /// reading of the overlay rather than of the record: the spread at position i is
    /// <c>max(fold[·][i]) − min(fold[·][i])</c>, i.e. how much that one phase of the waveform moved
    /// from cycle to cycle. A peak-to-peak of the whole record would be the waveform's own
    /// amplitude and would say nothing about repeatability.</para>
    ///
    /// <para>Null for fewer than <see cref="MinFolds"/> folds, or for folds of different lengths —
    /// comparing position i across folds of unequal length compares two different phases, which is
    /// the plausible wrong answer rather than an obvious one.</para>
    /// </summary>
    public static EyeSpread? Measure(IReadOnlyList<double[]> folds, double sampleRateHz)
    {
        if (folds.Count < MinFolds || !double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
            return null;

        int length = folds[0].Length;
        if (length <= 0)
            return null;
        for (int f = 1; f < folds.Count; f++)
            if (folds[f].Length != length)
                return null;

        double worst = 0.0;
        int worstAt = 0;
        double total = 0.0;
        for (int i = 0; i < length; i++)
        {
            double low = double.PositiveInfinity;
            double high = double.NegativeInfinity;
            for (int f = 0; f < folds.Count; f++)
            {
                double v = folds[f][i];
                if (!double.IsFinite(v))
                    continue;
                if (v < low) low = v;
                if (v > high) high = v;
            }

            if (!double.IsFinite(low) || !double.IsFinite(high))
                continue;

            double spread = high - low;
            total += spread;
            if (spread > worst)
            {
                worst = spread;
                worstAt = i;
            }
        }

        return new EyeSpread(worst, worstAt / sampleRateHz, total / length);
    }

    /// <summary>
    /// How much the folds' own crossings of <paramref name="levelV"/> move, as a standard deviation
    /// in seconds — the overlay's horizontal thickness, read as period jitter.
    ///
    /// <para><b>Interpolated, for <see cref="EdgeTrigger"/>'s reason.</b> One sample is 5.2 µs at
    /// 192 kS/s and the jitter worth reading is a fraction of that, so an integer crossing index
    /// would quantise the whole figure to the sample interval and report nothing below it. A
    /// crossing between two samples is where the straight line between them cuts the level.</para>
    ///
    /// <para>Null unless at least <see cref="MinFolds"/> folds have a rising crossing of that level
    /// — a figure computed from one crossing is a figure of nothing, and a level no fold crosses is
    /// a level outside the waveform. The σ is the population one over the folds measured: these are
    /// all the cycles there are in the record, not a sample of a larger set.</para>
    /// </summary>
    public static double? CrossingJitterS(IReadOnlyList<double[]> folds, double sampleRateHz, double levelV)
    {
        if (folds.Count < MinFolds || !double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0 || !double.IsFinite(levelV))
            return null;

        var crossings = new List<double>(folds.Count);
        foreach (var fold in folds)
        {
            if (FirstRisingCrossing(fold, levelV) is { } at)
                crossings.Add(at / sampleRateHz);
        }

        if (crossings.Count < MinFolds)
            return null;

        double mean = 0.0;
        foreach (double c in crossings)
            mean += c;
        mean /= crossings.Count;

        double sum = 0.0;
        foreach (double c in crossings)
        {
            double d = c - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / crossings.Count);
    }

    /// <summary>
    /// The first rising crossing of <paramref name="levelV"/> in a fold, as a fractional sample
    /// index, or null where it has none.
    /// </summary>
    private static double? FirstRisingCrossing(double[] fold, double levelV)
    {
        for (int i = 1; i < fold.Length; i++)
        {
            double a = fold[i - 1];
            double b = fold[i];
            if (!double.IsFinite(a) || !double.IsFinite(b))
                continue;
            if (a < levelV && b >= levelV)
            {
                double step = b - a;
                // A step of exactly zero cannot happen here (a < level <= b), so the division is
                // safe; the clamp is for a denormal step rather than for a zero one.
                double fraction = step > 0.0 ? Math.Clamp((levelV - a) / step, 0.0, 1.0) : 0.0;
                return (i - 1) + fraction;
            }
        }

        return null;
    }
}
