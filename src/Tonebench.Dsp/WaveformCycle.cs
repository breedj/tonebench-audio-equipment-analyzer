namespace Tonebench.Dsp;

/// <summary>
/// The vertical readings taken over <b>one period</b> rather than over the whole window.
/// </summary>
/// <param name="MeanV">The arithmetic mean over that cycle.</param>
/// <param name="RmsV">True RMS over it, DC included.</param>
/// <param name="StdevV">RMS about the cycle's own mean — the AC RMS of one cycle.</param>
/// <param name="AreaVs">Its integral, in volt-seconds.</param>
public readonly record struct WaveformCycle(double MeanV, double RmsV, double StdevV, double AreaVs);

/// <summary>
/// One pass of vertical measurements over a single cycle.
///
/// <para><b>Why a cycle reading exists at all, and it is not a convenience.</b> An RMS over a window
/// holding a whole number of cycles is the waveform's RMS; over a window holding 10.125 of them it
/// is not, and the error is the part-cycle weighted against the ten. For a sine the mean square
/// carries a term <c>−sin(4πn)/(4πn)</c> of itself, where <c>n</c> is how many cycles the window
/// holds — so the RMS is out by up to <b>0.39 % at ten cycles in the window, 1.9 % at two and
/// 3.6 % at one</b>, worst at <c>n + 1/8</c> and <i>exactly</i> zero at a whole number and at
/// <c>n + 1/4</c>. Nothing on screen says which of those a reader has, because the figure is a
/// perfectly ordinary number either way. A scope's window is set by the timebase and almost never
/// holds a whole number of cycles, so the whole-window RMS of a periodic signal is routinely the
/// wrong one to quote and this is the right one. On a <i>transient</i> the reverse holds and the
/// window's own figure is what matters, which is why both are reported and neither replaces the
/// other.</para>
///
/// <para>The same argument applies to the mean, where it is starker: the mean of a sine over a whole
/// cycle is exactly zero and over 10.3 cycles is whatever the 0.3 contributes.</para>
///
/// <para>The cycle is the one <see cref="WaveformTimingMeasure"/> measured the period over — the
/// first — so the period, the duty and these readings all describe the same stretch of the record.
/// Averaging over every whole cycle in the window would be a better estimator and a different
/// measurement; "cycle" means one, as it does on a bench instrument.</para>
/// </summary>
public static class WaveformCycleMeasure
{
    /// <summary>
    /// Measures the <paramref name="count"/> samples from <paramref name="first"/>.
    ///
    /// <para>Null when that range is not inside <paramref name="samples"/> or holds fewer than two
    /// of them — a "cycle" of one sample has no mean worth the name, and absent is this screen's
    /// answer to a reading a waveform does not have.</para>
    /// </summary>
    public static WaveformCycle? Measure(ReadOnlySpan<double> samples, int first, int count, double sampleRateHz)
    {
        if (first < 0 || count < 2 || first + count > samples.Length || !(sampleRateHz > 0.0))
            return null;

        var cycle = samples.Slice(first, count);

        double sum = 0.0;
        double sumSquares = 0.0;
        foreach (double v in cycle)
        {
            sum += v;
            sumSquares += v * v;
        }

        double mean = sum / count;

        // About the mean in a second pass, never sumSquares/n - mean^2: that one-pass form is
        // catastrophic cancellation whenever the offset is large against the signal, which is
        // exactly a supply rail or a tube's plate. WaveformMeasure's own remarks carry the whole
        // argument and the band-sum fault this codebase has already paid for once.
        double sumCentredSquares = 0.0;
        foreach (double v in cycle)
        {
            double d = v - mean;
            sumCentredSquares += d * d;
        }

        return new WaveformCycle(
            MeanV: mean,
            RmsV: Math.Sqrt(sumSquares / count),
            StdevV: Math.Sqrt(sumCentredSquares / count),
            AreaVs: sum / sampleRateHz);
    }
}
