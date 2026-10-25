namespace Tonebench.Dsp;

/// <summary>
/// Log-spaced frequency columns across a band, and the reduction of an FFT's linear bins onto them.
///
/// <para>Its own type because two things need to agree about it exactly: the decay analyses build
/// their surfaces on this grid, and the charts label the axis from it. A column frequency
/// re-derived from a formula on the drawing side is the same class of mistake
/// <see cref="SpectrumDecimator.DecimateLogWithCenters"/> exists to prevent — it diverges silently
/// wherever the bins are sparser than the columns, which on a log axis is the whole bottom of the
/// band.</para>
/// </summary>
public static class LogFrequencyGrid
{
    /// <summary>
    /// The geometric centre of each of <paramref name="count"/> equal log-width columns spanning
    /// <paramref name="lowHz"/> to <paramref name="highHz"/>.
    /// </summary>
    public static float[] Columns(double lowHz, double highHz, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lowHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (highHz <= lowHz)
            throw new ArgumentOutOfRangeException(nameof(highHz), highHz, "The band's top must exceed its bottom.");

        double ratio = highHz / lowHz;
        var columns = new float[count];
        for (int c = 0; c < count; c++)
            columns[c] = (float)(lowHz * Math.Pow(ratio, (c + 0.5) / count));

        return columns;
    }

    /// <summary>
    /// Reduces linear FFT bins onto the same grid <see cref="Columns"/> describes, taking the
    /// <b>maximum</b> magnitude in each column.
    ///
    /// <para>Maximum, not mean, for the reason the whole app takes min/max rather than a mean: an
    /// average flattens a single-bin resonance into the floor around it, and a resonance is the one
    /// thing a decay surface is looked at for. Only the max is kept here, unlike a spectrum trace —
    /// a surface drawn as stacked curves has nowhere to put the min, and a decay's lower envelope
    /// says nothing anyway.</para>
    ///
    /// <para>A column with no bin of its own — which is every column at the bottom of the band
    /// whenever the transform is short — is <b>interpolated between the two bins it lies between</b>,
    /// which is to say the curve is drawn as a straight line from one measured point to the next,
    /// the way every other chart in this app joins its samples. It used to take the bin nearest its
    /// centre, on the reasoning that a staircase draws the resolution the analysis really has; the
    /// staircase is what that looked like, and it reads as a broken chart rather than as a coarse
    /// one. Reported 2026-10-25 beside a burst decay, which is smooth because its wavelet is
    /// evaluated <i>at</i> every column and so has a measurement per column — where a cumulative
    /// spectral decay has one linear FFT and <c>sampleRate / fftLength</c> between its bins. At
    /// 1024 points and 192 kHz that is 187.5 Hz, i.e. <b>five bins across 20 Hz – 1 kHz against 400
    /// columns</b>, so eighty columns in a row repeated one value.</para>
    ///
    /// <para>Nothing about the resolution changes and no extremum is invented: an interpolated
    /// column lies between the two bins either side of it, and the record length that set the bin
    /// spacing is on the panel beside the chart. The interpolation is <b>geometric</b> — linear in
    /// dB — because the surface is read in dB, so that is the one reading under which the drawn
    /// segment is actually straight.</para>
    /// </summary>
    public static void ReduceMax(
        ReadOnlySpan<double> binMagnitudes, double binHz, double lowHz, double highHz, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destination.Length, nameof(destination));

        int count = destination.Length;
        double ratio = highHz / lowHz;
        int lastBin = binMagnitudes.Length - 1;

        for (int c = 0; c < count; c++)
        {
            double edgeLow = lowHz * Math.Pow(ratio, (double)c / count);
            double edgeHigh = lowHz * Math.Pow(ratio, (double)(c + 1) / count);

            int first = (int)Math.Ceiling(edgeLow / binHz);
            int last = (int)Math.Floor(edgeHigh / binHz);

            if (last < first)
            {
                double position = Math.Clamp(Math.Sqrt(edgeLow * edgeHigh) / binHz, 0, lastBin);
                int below = (int)Math.Floor(position);
                int above = Math.Min(below + 1, lastBin);
                destination[c] = above == below
                    ? (float)binMagnitudes[below]
                    : (float)Geometric(binMagnitudes[below], binMagnitudes[above], position - below);
                continue;
            }

            first = Math.Clamp(first, 0, lastBin);
            last = Math.Clamp(last, 0, lastBin);

            double peak = binMagnitudes[first];
            for (int k = first + 1; k <= last; k++)
                peak = Math.Max(peak, binMagnitudes[k]);

            destination[c] = (float)peak;
        }
    }

    /// <summary>
    /// A point between two magnitudes, straight in dB. Floored first, because a bin can be exactly
    /// zero and the whole point of going through the logarithm is that it cannot take one.
    /// </summary>
    private static double Geometric(double low, double high, double fraction)
    {
        const double floor = 1e-30;
        double a = Math.Log(Math.Max(low, floor));
        double b = Math.Log(Math.Max(high, floor));
        return Math.Exp(a + (b - a) * fraction);
    }
}
