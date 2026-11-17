namespace Tonebench.Dsp;

/// <summary>
/// The two settled levels of a record — <b>Vtop</b> and <b>Vbase</b> — found from a histogram of
/// the samples rather than from the extremes.
///
/// <para><b>Why not max and min.</b> They are what a rise time has to be measured between, and a
/// square wave that overshoots has a maximum well above the level it settles at: measuring 10 % to
/// 90 % of the <i>peaks</i> then reports an edge that is slower than it is, by whatever the
/// overshoot is. The same two numbers are what the overshoot itself is quoted against. So the pair
/// is the mode of the upper half of the distribution and the mode of the lower half, which on a
/// flat-topped waveform is the level each half settles at and is unmoved by a ring on the corner.
/// </para>
///
/// <para><b>What this deliberately does not try to decide is whether the waveform has a top at
/// all.</b> A sine's histogram is a bathtub — the density really is highest at the two rails, where
/// the slope is zero — so no density test separates it from a square, and every bench instrument
/// quotes a top and a base for a sine anyway (they come out at the peaks, which is the honest
/// answer to the question as asked). The one case that genuinely has no pair is a record with
/// <i>one</i> level: DC, silence, or noise with no structure. That is what the separation test
/// below refuses, and it refuses it because a "top" and a "base" a hundredth of a division apart
/// are two names for one number.</para>
/// </summary>
public static class WaveformHistogram
{
    /// <summary>
    /// How many bins the record's range is divided into.
    ///
    /// <para>256 is the usual choice and is a compromise with a reason at each end: too few and a
    /// level is located only to within a coarse step of the range, too many and the mode of a
    /// slightly drooping or noisy level scatters across neighbours and the peak is wherever the
    /// noise happened to pile up.</para>
    /// </summary>
    public const int BinCount = 256;

    /// <summary>
    /// How many bins are summed around a candidate when looking for the mode.
    ///
    /// <para>The mode is the centre of the fullest window rather than the fullest single bin, so a
    /// level that drifts by a few tenths of a percent over the record — a real amplifier's square
    /// wave droops — is still found as one level instead of being split between two bins that each
    /// lose to a sharper peak elsewhere.</para>
    /// </summary>
    public const int SmoothingBins = 3;

    /// <summary>
    /// How far apart the two modes have to be, as a fraction of the record's range, to be called a
    /// top and a base. Below it the record has one level and this returns null.
    /// </summary>
    public const double MinSeparationFraction = 0.10;

    /// <summary>
    /// The settled high and low levels, or null where the record has only one.
    ///
    /// <para>Each level is the <b>mean of the samples in its own window</b> rather than the centre
    /// of the bin: the bin is 1/256 of the range wide, and quoting its centre would put a visible
    /// quantisation on a reading that is otherwise exact. The mean of what is in it is free, since
    /// the pass that fills the histogram can accumulate it.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The record is empty.</exception>
    public static (double TopV, double BaseV)? TopBase(ReadOnlySpan<double> samples)
    {
        if (samples.IsEmpty)
            throw new ArgumentException("A record needs at least one sample to be measured.", nameof(samples));

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (double v in samples)
        {
            if (v < min)
                min = v;
            if (v > max)
                max = v;
        }

        double range = max - min;
        if (!(range > 0.0) || !double.IsFinite(range))
            return null;

        Span<int> counts = stackalloc int[BinCount];
        Span<double> sums = stackalloc double[BinCount];
        counts.Clear();
        sums.Clear();

        double scale = BinCount / range;
        foreach (double v in samples)
        {
            int bin = (int)((v - min) * scale);
            if (bin >= BinCount)
                bin = BinCount - 1;
            else if (bin < 0)
                bin = 0;
            counts[bin]++;
            sums[bin] += v;
        }

        // Each half searched on its own, which is the whole of what makes this a *pair*: the single
        // fullest window of a 10 % duty pulse is its base, and a global search would find that twice.
        int half = BinCount / 2;
        (double level, int weight) upper = ModeOf(counts, sums, half, BinCount);
        (double level, int weight) lower = ModeOf(counts, sums, 0, half);

        if (upper.weight == 0 || lower.weight == 0)
            return null;
        if (upper.level - lower.level < range * MinSeparationFraction)
            return null;

        return (upper.level, lower.level);
    }

    /// <summary>The fullest smoothing window with its centre in [from, to), as the mean of its samples.</summary>
    private static (double Level, int Weight) ModeOf(ReadOnlySpan<int> counts, ReadOnlySpan<double> sums, int from, int to)
    {
        int reach = SmoothingBins / 2;
        int bestCentre = -1;
        int bestWeight = 0;

        for (int centre = from; centre < to; centre++)
        {
            int weight = 0;
            for (int b = centre - reach; b <= centre + reach; b++)
            {
                if (b >= 0 && b < counts.Length)
                    weight += counts[b];
            }

            if (weight > bestWeight)
            {
                bestWeight = weight;
                bestCentre = centre;
            }
        }

        if (bestCentre < 0 || bestWeight == 0)
            return (0.0, 0);

        double sum = 0.0;
        int count = 0;
        for (int b = bestCentre - reach; b <= bestCentre + reach; b++)
        {
            if (b >= 0 && b < counts.Length)
            {
                sum += sums[b];
                count += counts[b];
            }
        }

        return (sum / count, bestWeight);
    }
}
