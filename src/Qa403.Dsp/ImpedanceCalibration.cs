using System.Numerics;

namespace Qa403.Dsp;

/// <summary>
/// One fixture calibration standard -- an open or a short -- reduced to a curve small enough to store
/// in a measurement type's <c>params_json</c> and put back on any analysis grid later.
///
/// <para><b>Why it is decimated rather than kept at full resolution.</b> A calibration is only useful
/// if it survives closing the app, and the place a measurement type's settings live is a JSON column.
/// A full-resolution cal is a bin per analysis bin -- 65537 complex numbers at the longest record --
/// which is megabytes of JSON per standard. These curves are smooth by construction (an open is the
/// two ADC channels' gain and phase mismatch; a short is a few tens of milliohms of lead and
/// connector), so a few hundred log-spaced points carry all of it.</para>
///
/// <para><b>The stored curve is resampled even when it was captured a moment ago</b>, and that is
/// deliberate. Applying the full-resolution array while it is still in memory and the interpolated
/// one after a restart would make the same calibration mean two slightly different things, with the
/// better answer available only until the user closed the window -- a difference that would show up
/// as an unexplained shift and be very hard to attribute. One path, always.</para>
/// </summary>
/// <param name="FreqHz">Ascending capture frequencies, log-spaced across the analysed band.</param>
/// <param name="Real">Real part of the measured ratio at each frequency.</param>
/// <param name="Imag">Imaginary part of the measured ratio at each frequency.</param>
public sealed record ImpedanceCalibration(double[] FreqHz, double[] Real, double[] Imag)
{
    /// <summary>
    /// Points per stored standard. Over the 2 Hz - 96 kHz a full-span sweep covers this is about 50
    /// per decade, which resolves anything as smooth as a channel mismatch with room to spare.
    /// </summary>
    public const int DefaultPoints = 256;

    /// <summary>
    /// False for a curve that is empty, ragged or missing an array, which is how an absent
    /// calibration reads.
    ///
    /// <para>Null-tolerant despite the arrays being declared non-nullable, because these records
    /// arrive deserialized from a browser's stored settings: JSON has no idea what the declaration
    /// says, and a missing property leaves a null behind for the first <c>.Length</c> to trip
    /// over.</para>
    /// </summary>
    public bool IsUsable =>
        FreqHz is { Length: >= 2 } && Real?.Length == FreqHz.Length && Imag?.Length == FreqHz.Length;

    /// <summary>
    /// Reduces a full-resolution measured ratio to the stored curve, sampling it at log-spaced
    /// frequencies across <paramref name="lowHz"/> to <paramref name="highHz"/>.
    /// </summary>
    public static ImpedanceCalibration Capture(
        ReadOnlySpan<Complex> h, double binHz, double lowHz, double highHz, int points = DefaultPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lowHz);
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        if (highHz <= lowHz)
            throw new ArgumentOutOfRangeException(nameof(highHz), "The calibration band must have some width.");
        if (h.Length < 2)
            throw new ArgumentException("A calibration needs a spectrum to sample.", nameof(h));

        // Never sample past the spectrum that was actually measured: the top of the requested band can
        // sit above Nyquist for the grid this capture was taken on.
        double topHz = Math.Min(highHz, (h.Length - 1) * binHz);
        if (topHz <= lowHz)
            topHz = (h.Length - 1) * binHz;

        var freqHz = new double[points];
        var real = new double[points];
        var imag = new double[points];

        double logLow = Math.Log(lowHz);
        double logStep = (Math.Log(topHz) - logLow) / (points - 1);

        for (int i = 0; i < points; i++)
        {
            double f = Math.Exp(logLow + (i * logStep));
            var value = SampleAt(h, binHz, f);

            freqHz[i] = f;
            real[i] = value.Real;
            imag[i] = value.Imaginary;
        }

        return new ImpedanceCalibration(freqHz, real, imag);
    }

    /// <summary>
    /// The stored curve put back on an analysis grid of <paramref name="binCount"/> bins spaced
    /// <paramref name="binHz"/> apart, interpolated linearly in log frequency.
    ///
    /// <para>Bins outside the captured band -- DC, and anything above the top of the sweep -- hold the
    /// nearest end value rather than extrapolating. A calibration says nothing about frequencies it
    /// never saw, and a straight line drawn out of its last two points would say something confident
    /// and wrong.</para>
    /// </summary>
    public Complex[] Resample(double binHz, int binCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binCount);
        if (!IsUsable)
            throw new InvalidOperationException("This calibration has no usable curve.");

        var values = new Complex[binCount];
        for (int k = 0; k < binCount; k++)
            values[k] = At(k * binHz);

        return values;
    }

    /// <summary>The stored curve at one frequency, held flat outside the captured band.</summary>
    public Complex At(double freqHz)
    {
        if (freqHz <= FreqHz[0])
            return new Complex(Real[0], Imag[0]);

        int last = FreqHz.Length - 1;
        if (freqHz >= FreqHz[last])
            return new Complex(Real[last], Imag[last]);

        int upper = UpperBound(FreqHz, freqHz);
        int lower = upper - 1;

        double span = Math.Log(FreqHz[upper]) - Math.Log(FreqHz[lower]);
        double t = span > 0.0 ? (Math.Log(freqHz) - Math.Log(FreqHz[lower])) / span : 0.0;

        return new Complex(
            Real[lower] + (t * (Real[upper] - Real[lower])),
            Imag[lower] + (t * (Imag[upper] - Imag[lower])));
    }

    /// <summary>Linear interpolation between the two bins straddling <paramref name="freqHz"/>.</summary>
    private static Complex SampleAt(ReadOnlySpan<Complex> h, double binHz, double freqHz)
    {
        double exact = freqHz / binHz;
        int lower = (int)Math.Floor(exact);

        if (lower <= 0)
            return h[Math.Min(1, h.Length - 1)];
        if (lower >= h.Length - 1)
            return h[h.Length - 1];

        double t = exact - lower;
        return h[lower] + (t * (h[lower + 1] - h[lower]));
    }

    /// <summary>Index of the first entry strictly greater than <paramref name="value"/>. Callers have already excluded both ends.</summary>
    private static int UpperBound(double[] ascending, double value)
    {
        int low = 0;
        int high = ascending.Length - 1;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (ascending[mid] > value)
                high = mid;
            else
                low = mid + 1;
        }

        return low;
    }
}
