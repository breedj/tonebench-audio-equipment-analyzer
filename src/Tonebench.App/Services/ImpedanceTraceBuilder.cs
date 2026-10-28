using System.Numerics;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Turns a finished impedance estimate into the columns the chart draws.
///
/// <para>The order is the one <c>ResponseCaptureSupport.BuildTrace</c> establishes and for the same
/// reasons: smooth the <b>linear</b> magnitudes, convert to dB, find the peak on the full-resolution
/// smoothed trace, and only then decimate. Smoothing in dB would weight a null far more heavily than
/// a peak, and finding the peak after decimation would report a column's aggregate rather than the
/// resonance.</para>
/// </summary>
internal static class ImpedanceTraceBuilder
{
    /// <summary>
    /// Rebuilds the complex ratio from a finished estimate's magnitude and unwrapped phase.
    ///
    /// <para>Exact: unwrapping only adds whole turns, which <c>cos</c> and <c>sin</c> undo. Done this
    /// way rather than by giving the estimator a complex accessor because
    /// <see cref="FrequencyResponseEstimator.Complete"/> gates its working spectrum <i>in place</i>
    /// and must be called exactly once — a second accessor would be a second chance to gate
    /// twice.</para>
    /// </summary>
    public static Complex[] ToComplex(FrequencyResponseResult result)
    {
        var h = new Complex[result.Magnitude.Length];
        for (int k = 0; k < h.Length; k++)
            h[k] = Complex.FromPolarCoordinates(result.Magnitude[k], result.PhaseDeg[k] * Math.PI / 180.0);

        return h;
    }

    /// <summary>
    /// One trace from a stepped-sine run, whose curve is a handful of measured points rather than a
    /// spectrum.
    ///
    /// <para>Nothing is smoothed and nothing is decimated, so this shares almost none of
    /// <see cref="Build"/>'s body: every sample here <i>is</i> a measurement, the grid is already
    /// coarser than the narrowest fractional-octave smoothing on offer, and the min and max of a
    /// "column" are therefore the same number. The chart draws the max as the line and the min as a
    /// band beneath it, and here there is nothing between them to draw.</para>
    ///
    /// <para>Coherence is measured rather than assumed — see
    /// <see cref="CrossSpectrumAccumulator.Coherence"/>. That is the one place this is <i>better</i>
    /// informed than the chirp path rather than merely coarser: a shorted standard whose low end is
    /// noise says so here, instead of drawing a confident line at a fabricated impedance.</para>
    /// </summary>
    public static ImpedanceTrace BuildPoints(
        ImpedanceTraceKind kind,
        double[] freqHz,
        Complex[] values,
        double[] coherence)
    {
        int n = freqHz.Length;

        var traceDb = new double[n];
        for (int i = 0; i < n; i++)
            traceDb[i] = ImpedanceMath.ToDbOhms(values[i].Magnitude);

        // The explicit-frequency-axis overload, added for the stepped-sine response: this grid is log
        // spaced, so the uniform-binHz one would place the peak at the wrong frequency.
        var peak = BandwidthAnalyzer.FindPeak(traceDb, freqHz);

        var freq = new float[n];
        var mag = new float[n];
        var phaseDeg = new float[n];
        var columnCoherence = new float[n];

        for (int i = 0; i < n; i++)
        {
            freq[i] = (float)freqHz[i];
            mag[i] = (float)traceDb[i];

            // Not unwrapped, exactly as Build explains: a passive one-port's angle is bounded to
            // +/-90 degrees, so there are no turns to accumulate, and a curve that runs past that is
            // a broken fixture rather than a wrap -- which unwrapping would hide.
            phaseDeg[i] = (float)(values[i].Phase * 180.0 / Math.PI);
            columnCoherence[i] = (float)coherence[i];
        }

        return new ImpedanceTrace(
            kind,
            freqHz[0],
            freqHz[^1],
            freq,
            mag,
            (float[])mag.Clone(),
            phaseDeg,
            columnCoherence,
            peak.LevelDb,
            peak.FreqHz);
    }

    /// <summary>
    /// One trace from a complex curve.
    ///
    /// <para>The magnitude columns are <c>20 log10</c> of <paramref name="values"/>'s magnitude,
    /// which makes them <b>dB relative to 1 ohm</b> for an impedance curve and a plain dB ratio for a
    /// calibration standard. Both ride the same columns; which one a trace holds is
    /// <see cref="ImpedanceTrace.Kind"/>'s job to say.</para>
    /// </summary>
    public static ImpedanceTrace Build(
        ImpedanceRequest request,
        ImpedanceTraceKind kind,
        Complex[] values,
        double[] coherence,
        int displayColumns) =>
        Build(
            request.BinHz, request.AnalysisLowHz, request.AnalysisHighHz, request.SmoothingDenominator,
            kind, values, coherence, displayColumns);

    /// <summary>
    /// The same build from the four numbers it actually reads, for a caller that is not an
    /// <see cref="ImpedanceRequest"/> — the output-impedance measurement's broadband path, which
    /// solves the very same <c>Z</c> from a ratio of two passes and must smooth and decimate it
    /// identically or the two screens would disagree about one curve.
    /// </summary>
    public static ImpedanceTrace Build(
        double binHz,
        double analysisLowHz,
        double analysisHighHz,
        int smoothingDenominator,
        ImpedanceTraceKind kind,
        Complex[] values,
        double[] coherence,
        int displayColumns)
    {
        var band = AnalysisBand(binHz, analysisLowHz, analysisHighHz, values.Length);

        var magnitude = new double[values.Length];
        for (int k = 0; k < values.Length; k++)
            magnitude[k] = values[k].Magnitude;

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, binHz, smoothingDenominator, band);

        // The angle over the same bands, as a vector mean — see FractionalOctaveSmoother.SmoothPhaseDeg.
        // Smoothed because the magnitude is: a column whose level is an RMS over ten bins and whose
        // angle is the raw centre bin of those ten draws a readable curve beside an unreadable one,
        // from one capture.
        var smoothedPhaseDeg = FractionalOctaveSmoother.SmoothPhaseDeg(values, binHz, smoothingDenominator, band);

        return Render(binHz, band, kind, smoothed, smoothedPhaseDeg, coherence, displayColumns);
    }

    /// <summary>
    /// The bins a curve of this bin size may be smoothed, searched and drawn over.
    ///
    /// <para>Only where the stimulus put energy. Outside it the ratio is noise over nothing and the
    /// peak search would lock onto whichever out-of-band bin happened to be loudest — and, which is
    /// why this is settled <i>before</i> any smoothing rather than after it, a smoothing band
    /// reaching past the top of it drags the last columns toward whatever an out-of-band ratio
    /// inverts to. This family has the worst arithmetic for that of any measurement here:
    /// <c>Z = R·H/(1 − H)</c> of a noise ratio wandering near 1 is clamped at
    /// <see cref="ImpedanceMath.MaxOhms"/>, and an RMS over a band holding one such bin <i>is</i>
    /// that bin. Measured 2026-10-21 on a 1 Ω fixture: the last columns read 4.37 Ω at 1/3 octave
    /// against a true 1.06, while 1/48 — whose band reaches 3 % past each bin rather than 12 % —
    /// read 1.07 and smoothing None read 1.14.</para>
    /// </summary>
    public static FractionalOctaveSmoother.Band AnalysisBand(
        double binHz, double analysisLowHz, double analysisHighHz, int binCount)
    {
        int firstBin = Math.Max(1, (int)Math.Ceiling(analysisLowHz / binHz));
        int lastBin = Math.Min(binCount - 1, (int)Math.Floor(analysisHighHz / binHz));
        if (lastBin <= firstBin)
            lastBin = Math.Min(binCount - 1, firstBin + 1);

        return new FractionalOctaveSmoother.Band(firstBin, lastBin);
    }

    /// <summary>
    /// The same trace from a curve that has <b>already</b> been smoothed, for a caller that had to
    /// do its own averaging earlier in the arithmetic.
    ///
    /// <para>The output-impedance measurement is that caller, and the reason is not a preference
    /// about where a loop lives. Its Z is solved from a gain ratio sitting within a decibel of one,
    /// so a bin's worth of noise becomes ohms; <c>|Z|</c> is a magnitude, so that noise rectifies,
    /// and an RMS of already-rectified values keeps every bit of the bias. It therefore averages
    /// the <i>ratio</i> — see <see cref="FractionalOctaveSmoother.SmoothComplex"/> — and solves
    /// once, and what arrives here must not be smoothed a second time. Everything after the
    /// averaging is shared with <see cref="Build"/>, so the two screens still draw, decimate and
    /// find their peak identically.</para>
    /// </summary>
    public static ImpedanceTrace BuildSmoothed(
        double binHz,
        FractionalOctaveSmoother.Band band,
        ImpedanceTraceKind kind,
        Complex[] values,
        double[] coherence,
        int displayColumns)
    {
        var magnitude = new double[values.Length];
        var phaseDeg = new double[values.Length];
        for (int k = 0; k < values.Length; k++)
        {
            magnitude[k] = values[k].Magnitude;
            phaseDeg[k] = values[k].Phase * 180.0 / Math.PI;
        }

        return Render(binHz, band, kind, magnitude, phaseDeg, coherence, displayColumns);
    }

    /// <summary>
    /// dB, the peak, and the decimation onto display columns — everything both builds do once their
    /// curve has been averaged, so that the only thing they differ in is the averaging.
    /// </summary>
    private static ImpedanceTrace Render(
        double binHz,
        FractionalOctaveSmoother.Band band,
        ImpedanceTraceKind kind,
        double[] smoothed,
        double[] smoothedPhaseDeg,
        double[] coherence,
        int displayColumns)
    {
        int binCount = smoothed.Length;
        int firstBin = band.FirstBin;
        int lastBin = band.LastBin;

        var traceDb = new double[binCount];
        for (int k = 0; k < binCount; k++)
            traceDb[k] = ImpedanceMath.ToDbOhms(smoothed[k]);

        // For an impedance curve this is the resonance: the frequency a driver's motional impedance
        // peaks at, which is the first number anyone reads off one of these.
        var peak = BandwidthAnalyzer.FindPeak(traceDb, binHz, firstBin, lastBin);

        int columns = Math.Min(displayColumns, lastBin - firstBin + 1);
        var minMax = new MinMax[columns];
        var centerBins = new double[columns];
        SpectrumDecimator.DecimateLogWithCenters(traceDb.AsSpan(0, lastBin + 1), minMax, centerBins, firstBin);

        var freqHz = new float[columns];
        var magMin = new float[columns];
        var magMax = new float[columns];
        var phaseDeg = new float[columns];
        var columnCoherence = new float[columns];

        for (int c = 0; c < columns; c++)
        {
            freqHz[c] = (float)(centerBins[c] * binHz);
            magMin[c] = (float)minMax[c].Min;
            magMax[c] = (float)minMax[c].Max;

            // Phase and coherence at the column's centre rather than min/max'd, exactly as the
            // frequency-response trace does it: the min/max rule exists so a single-bin spike
            // survives decimation, and neither of these has spikes worth preserving.
            //
            // Never unwrapped, unlike the frequency response's -- and that is a property of the
            // quantity rather than an omission. A passive one-port's impedance angle is bounded to
            // +/-90 degrees, so it can never leave the principal branch and there are no turns to
            // accumulate. A curve that does run past 90 degrees is not a wrap: it is a calibration or
            // a fixture that is wrong, and unwrapping would hide exactly that. The vector mean above
            // keeps that property, since the argument of a sum of phasors cannot run past 180 either.
            int centre = Math.Clamp((int)Math.Round(centerBins[c]), 0, binCount - 1);
            phaseDeg[c] = (float)smoothedPhaseDeg[centre];
            columnCoherence[c] = (float)coherence[centre];
        }

        return new ImpedanceTrace(
            kind,
            firstBin * binHz,
            lastBin * binHz,
            freqHz,
            magMin,
            magMax,
            phaseDeg,
            columnCoherence,
            peak.LevelDb,
            peak.FreqHz);
    }
}
