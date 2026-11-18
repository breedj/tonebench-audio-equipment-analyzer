namespace Tonebench.Dsp;

/// <summary>
/// What two channels of one record say about each other — the goniometer's own readings.
/// </summary>
/// <param name="Correlation">
/// Pearson's r over the window, taken about each channel's own mean: +1 when the two move together,
/// 0 when they are unrelated or in quadrature, −1 when they are mirror images. This is the stereo
/// meter's reading, and it is the one figure here that is always meaningful.
/// </param>
/// <param name="PhaseDeg">
/// How far B lags A, in degrees, <b>signed</b> — positive for a lag. Null unless both channels carry
/// essentially one common frequency, because outside that case a Lissajous figure is not an
/// ellipse and no single number is its phase.
/// </param>
/// <param name="AmplitudeRatio">
/// B's AC RMS over A's — 1 for a balanced pair. Null where A has nothing to divide by.
/// </param>
public readonly record struct ChannelCorrelationResult(
    double Correlation,
    double? PhaseDeg,
    double? AmplitudeRatio);

/// <summary>
/// The goniometer's arithmetic: how two channels of one record relate.
///
/// <para>Pure, and here rather than in the App layer, because every figure it produces is one a
/// reader acts on — "the channels are out of phase", "the right one is 3 dB down" — and the only
/// way to trust such a figure is to check it against a pair built from a formula.</para>
/// </summary>
public static class ChannelCorrelation
{
    /// <summary>
    /// How closely the two measured frequencies must agree before a single phase is quoted.
    ///
    /// <para>Two per cent, which is far looser than the frequency reading's own accuracy and far
    /// tighter than the gap between two unrelated signals. The point is not precision: it is to
    /// refuse the case where the two channels carry <i>different</i> tones, where the Lissajous
    /// figure is a knot rather than an ellipse and quoting one phase for it would be a number
    /// about nothing.</para>
    /// </summary>
    public const double FrequencyAgreementFraction = 0.02;

    /// <summary>
    /// The AC RMS, relative to the louder channel, below which a channel is taken as carrying
    /// nothing.
    ///
    /// <para>A correlation divides by both channels' own deviations, so on a silent channel it
    /// divides noise by noise and answers a confident number about nothing. −60 dB is well below
    /// anything a measurement is made at and well above this analyzer's own floor.</para>
    /// </summary>
    public const double SilenceFraction = 0.001;

    /// <summary>
    /// The highest tone a phase is quoted for, as a fraction of the sample rate.
    ///
    /// <para>0.45, which is where this analyzer's own analysis band stops. Above it a sampled tone
    /// is identified too shakily for the frequency the phase is measured at to be trusted.</para>
    /// </summary>
    public const double MaxToneFraction = 0.45;

    /// <summary>
    /// Measures one window of two channels.
    ///
    /// <para><b>Phase is the argument of each channel's own single-bin transform at the common
    /// frequency, not <c>arccos(r)</c> and not a shifted correlation</b>, and both of the
    /// alternatives were tried first. Pearson's r is <c>cos φ</c> for two sinusoids of one
    /// frequency, so inverting it gives a magnitude and <b>loses the sign</b> — a channel leading
    /// by 30° and one lagging by 30° both read r = 0.866, and on a Lissajous those are two
    /// ellipses tilted opposite ways, which is the one thing the picture is read for. Correlating
    /// A against B shifted forward by a quarter period recovers the sign, but the shift has to be a
    /// whole number of samples: at 1 kHz on a 48 kS/s record a quarter period is 12 samples and the
    /// rounding costs 0.14°, which is nothing, while at 20 kHz it is 0.6 samples and the same
    /// rounding is a 67 % error on the one quantity being measured. Measured against a pair built
    /// 30° apart, the shifted correlation read 30.073° one way and 29.937° the other — an
    /// asymmetry that is pure rounding.</para>
    ///
    /// <para>A single-bin transform has no such grid. <c>φ = arg(A) − arg(B)</c> is exact for any
    /// frequency the analyzer will report, and the leakage a window holding a fractional number of
    /// periods produces lands on <i>both</i> channels alike — the same window, the same ω — so it
    /// cancels out of the difference. That is what makes this robust rather than merely
    /// accurate.</para>
    /// </summary>
    /// <param name="a">Channel A, the horizontal one.</param>
    /// <param name="b">Channel B, the vertical one. Shorter of the two wins.</param>
    /// <param name="sampleRateHz">What the samples are on.</param>
    /// <param name="frequencyAHz">A's measured frequency, or null where it has none.</param>
    /// <param name="frequencyBHz">B's.</param>
    public static ChannelCorrelationResult Measure(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        double sampleRateHz,
        double? frequencyAHz,
        double? frequencyBHz)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n < 2)
            return new ChannelCorrelationResult(0.0, null, null);

        var (meanA, devA) = MeanAndDeviation(a, n);
        var (meanB, devB) = MeanAndDeviation(b, n);

        double r = Correlate(a, b, n, meanA, meanB, devA, devB);
        double? ratio = devA > 0.0 ? devB / devA : null;

        return new ChannelCorrelationResult(
            r,
            Phase(a, b, n, sampleRateHz, frequencyAHz, frequencyBHz, meanA, meanB, devA, devB),
            ratio);
    }

    /// <summary>
    /// The signed phase, or null where no single number is the Lissajous figure's phase.
    ///
    /// <para>Four refusals, and each one is a case where a number would be worse than none: either
    /// channel without a measured frequency (noise, DC, anything with one settled level); the two
    /// frequencies disagreeing, where the figure is a knot rather than an ellipse; either channel
    /// silent, where what is left to measure is the noise floor; and a tone above
    /// <see cref="MaxToneFraction"/> of the rate, which this analyzer does not analyse anyway.</para>
    /// </summary>
    private static double? Phase(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        int n,
        double sampleRateHz,
        double? frequencyAHz,
        double? frequencyBHz,
        double meanA,
        double meanB,
        double devA,
        double devB)
    {
        if (frequencyAHz is not { } fa || frequencyBHz is not { } fb)
            return null;
        if (!(fa > 0.0) || !(fb > 0.0) || !(sampleRateHz > 0.0))
            return null;
        if (Math.Abs(fa - fb) > FrequencyAgreementFraction * Math.Max(fa, fb))
            return null;

        double louder = Math.Max(devA, devB);
        if (!(louder > 0.0) || devA < louder * SilenceFraction || devB < louder * SilenceFraction)
            return null;

        double frequency = 0.5 * (fa + fb);
        if (frequency > MaxToneFraction * sampleRateHz)
            return null;

        double omega = 2.0 * Math.PI * frequency / sampleRateHz;
        var (ar, ai) = Bin(a, n, meanA, omega);
        var (br, bi) = Bin(b, n, meanB, omega);

        // Nothing at the frequency at all, on a channel that is not silent: a figure that is not
        // about this tone, so there is no phase of it to quote.
        if ((ar * ar) + (ai * ai) <= 0.0 || (br * br) + (bi * bi) <= 0.0)
            return null;

        double degrees = (Math.Atan2(ai, ar) - Math.Atan2(bi, br)) * 180.0 / Math.PI;
        if (!double.IsFinite(degrees))
            return null;

        // Into (-180, 180], the interval a phase difference is read in.
        degrees -= 360.0 * Math.Floor((degrees + 180.0) / 360.0);
        return degrees;
    }

    /// <summary>One bin of the transform at an arbitrary frequency, about the window's own mean.</summary>
    private static (double Real, double Imaginary) Bin(ReadOnlySpan<double> x, int n, double mean, double omega)
    {
        double real = 0.0;
        double imaginary = 0.0;
        for (int i = 0; i < n; i++)
        {
            double value = x[i] - mean;
            var (sin, cos) = Math.SinCos(omega * i);
            real += value * cos;
            imaginary -= value * sin;
        }

        return (real, imaginary);
    }

    private static (double Mean, double Deviation) MeanAndDeviation(ReadOnlySpan<double> x, int n)
    {
        double sum = 0.0;
        for (int i = 0; i < n; i++)
            sum += x[i];
        double mean = sum / n;

        double squares = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = x[i] - mean;
            squares += d * d;
        }

        return (mean, Math.Sqrt(squares / n));
    }

    /// <summary>Pearson's r over the window, about each channel's own mean.</summary>
    private static double Correlate(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        int n,
        double meanA,
        double meanB,
        double devA,
        double devB)
    {
        if (!(devA > 0.0) || !(devB > 0.0))
            return 0.0;

        double sum = 0.0;
        for (int i = 0; i < n; i++)
            sum += (a[i] - meanA) * (b[i] - meanB);

        double value = sum / n / (devA * devB);
        return double.IsFinite(value) ? Math.Clamp(value, -1.0, 1.0) : 0.0;
    }
}
