using System.Numerics;
using Qa403.Dsp;

namespace Qa403.App.Services;

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
        int displayColumns)
    {
        double binHz = request.BinHz;
        int binCount = values.Length;

        var magnitude = new double[binCount];
        for (int k = 0; k < binCount; k++)
            magnitude[k] = values[k].Magnitude;

        var smoothed = FractionalOctaveSmoother.Smooth(magnitude, binHz, request.SmoothingDenominator);

        var traceDb = new double[binCount];
        for (int k = 0; k < binCount; k++)
            traceDb[k] = ImpedanceMath.ToDbOhms(smoothed[k]);

        // Only where the sweep put energy. Outside it the ratio is noise over nothing and the peak
        // search would lock onto whichever out-of-band bin happened to be loudest.
        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(binCount - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));
        if (lastBin <= firstBin)
            lastBin = Math.Min(binCount - 1, firstBin + 1);

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
            // Read straight off the complex value with no unwrapping, unlike the frequency response's
            // -- and that is a property of the quantity rather than an omission. A passive one-port's
            // impedance angle is bounded to +/-90 degrees, so it can never leave the principal branch
            // and there are no turns to accumulate. A curve that does run past 90 degrees is not a
            // wrap: it is a calibration or a fixture that is wrong, and unwrapping would hide exactly
            // that.
            int centre = Math.Clamp((int)Math.Round(centerBins[c]), 0, binCount - 1);
            phaseDeg[c] = (float)(values[centre].Phase * 180.0 / Math.PI);
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
