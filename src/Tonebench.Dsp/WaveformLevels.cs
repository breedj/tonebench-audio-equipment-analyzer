namespace Tonebench.Dsp;

/// <summary>
/// The vertical readings a scope takes of one captured record, in the record's own volts.
/// </summary>
/// <param name="MaxV">The largest sample.</param>
/// <param name="MinV">The smallest sample.</param>
/// <param name="PeakToPeakV">Max minus min.</param>
/// <param name="MeanV">
/// The arithmetic mean -- the DC component, reported rather than removed. This is the figure the
/// app did not have: <c>SpectrumRecordAnalyzer.ComputeTimeDomainLevels</c> subtracts the mean and
/// throws it away, which is right for the meters it feeds and useless for a scope.
/// </param>
/// <param name="RmsV">True RMS, DC included.</param>
/// <param name="AcRmsV">RMS about the mean, i.e. with DC removed. Equals <paramref name="RmsV"/> on a record with no offset.</param>
/// <param name="MaxIndex">
/// Which sample the maximum fell on -- the first of them, where several share the value.
///
/// <para>Carried because <i>where</i> a peak is is a reading in its own right: on a transient it is
/// the whole point, and on a record that holds one glitch it is the only way to find it without
/// hunting with a cursor. The <b>first</b> rather than the last or the middle, so a flat-topped
/// clipped waveform reports where the clipping started.</para>
/// </param>
/// <param name="MinIndex">The same for the minimum.</param>
public readonly record struct WaveformLevels(
    double MaxV,
    double MinV,
    double PeakToPeakV,
    double MeanV,
    double RmsV,
    double AcRmsV,
    int MaxIndex = 0,
    int MinIndex = 0)
{
    /// <summary>
    /// Peak over true RMS, the usual quotation. Null when the RMS is zero, which is a record of
    /// digital silence -- a crest factor of "infinity" printed beside a dead input is worse than a
    /// dash, and it is also a NaN this app may not put on a status record.
    /// </summary>
    public double? CrestFactor => RmsV > 0.0 ? Math.Max(Math.Abs(MaxV), Math.Abs(MinV)) / RmsV : null;

    /// <summary>
    /// The same ratio with DC taken out of both halves: the AC peak over the AC RMS. The one to
    /// quote for a signal riding on an offset, where the true crest factor is mostly the offset.
    /// </summary>
    public double? AcCrestFactor
    {
        get
        {
            if (!(AcRmsV > 0.0))
                return null;

            double peak = Math.Max(Math.Abs(MaxV - MeanV), Math.Abs(MinV - MeanV));
            return peak / AcRmsV;
        }
    }
}

/// <summary>
/// One pass of vertical measurements over a captured record. Pure, allocation-free and
/// independent of the sample rate -- everything here is about values, not about time.
/// </summary>
public static class WaveformMeasure
{
    /// <summary>
    /// Measures <paramref name="samples"/>.
    ///
    /// <para><b>Two passes, deliberately.</b> The AC RMS is accumulated about the mean in a second
    /// pass rather than from <c>sumOfSquares/n - mean^2</c>, which is the textbook one-pass form
    /// and is catastrophic cancellation whenever the offset is large against the signal -- exactly
    /// the case a scope meets on a tube amplifier's plate or any supply rail, where the two terms
    /// agree to many digits and their difference is rounding. This codebase has already paid for
    /// that lesson once, in the band sums of <c>FractionalOctaveSmoother</c>, where the subtraction
    /// of two large numbers produced a stopband that read -600 dB. A second pass over a quarter of
    /// a million samples costs nothing worth having.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The record is empty.</exception>
    public static WaveformLevels Measure(ReadOnlySpan<double> samples)
    {
        if (samples.IsEmpty)
            throw new ArgumentException("A record needs at least one sample to be measured.", nameof(samples));

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        int minIndex = 0;
        int maxIndex = 0;
        double sum = 0.0;
        double sumSquares = 0.0;

        for (int i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            if (v < min)
            {
                min = v;
                minIndex = i;
            }

            if (v > max)
            {
                max = v;
                maxIndex = i;
            }

            sum += v;
            sumSquares += v * v;
        }

        double mean = sum / samples.Length;

        double sumCentredSquares = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double d = samples[i] - mean;
            sumCentredSquares += d * d;
        }

        return new WaveformLevels(
            MaxV: max,
            MinV: min,
            PeakToPeakV: max - min,
            MeanV: mean,
            RmsV: Math.Sqrt(sumSquares / samples.Length),
            AcRmsV: Math.Sqrt(sumCentredSquares / samples.Length),
            MaxIndex: maxIndex,
            MinIndex: minIndex);
    }
}
