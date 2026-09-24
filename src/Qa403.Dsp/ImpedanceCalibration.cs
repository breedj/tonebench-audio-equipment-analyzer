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
    /// The |H| that separates the two standards. An open reads within a fraction of a dB of unity --
    /// it is only the two ADC channels' gain mismatch -- and a short reads a lead resistance over the
    /// reference resistor, which for ordinary leads on 100 ohms is about -60 dB. So the two are three
    /// orders of magnitude apart and any threshold between them does the job; -6 dB is far from both.
    /// </summary>
    public const double StandardMagnitudeThreshold = 0.5;

    /// <summary>
    /// The median |H| of the stored curve -- near 1 for an open, near 0 for a short -- or NaN when
    /// there is no usable curve. A median because a standard is smooth by construction and the ends
    /// of the captured band are the noisiest part of it.
    /// </summary>
    public double MedianMagnitude
    {
        get
        {
            if (!IsUsable)
                return double.NaN;

            var magnitudes = new double[FreqHz.Length];
            for (int i = 0; i < magnitudes.Length; i++)
                magnitudes[i] = double.Hypot(Real[i], Imag[i]);

            Array.Sort(magnitudes);
            int mid = magnitudes.Length / 2;
            return magnitudes.Length % 2 == 1
                ? magnitudes[mid]
                : 0.5 * (magnitudes[mid - 1] + magnitudes[mid]);
        }
    }

    /// <summary>
    /// Whether this curve has the shape of an <b>open</b> rather than a short.
    ///
    /// <para>A standard is entirely self-identifying, which is worth exploiting: the capture says
    /// which one it is regardless of what the user meant to connect. Capturing an open with the
    /// fixture still shorted stores a second short, and the correction then divides by the near-zero
    /// difference between two standards that should be a thousand apart -- inflating H by some 60 dB
    /// and reporting a flat negative impedance of exactly minus the reference resistor, at 180
    /// degrees, with coherence 1.0. Measured on a real Jamo 504 on 2026-09-24: -101 ohms flat across
    /// 15 Hz - 20 kHz, with no trace of the speaker's two resonances, from a fixture a tone test
    /// proved was perfect. Nothing downstream can tell that from a real measurement, so the check
    /// belongs on the standard itself.</para>
    /// </summary>
    public bool LooksLikeOpen => MedianMagnitude >= StandardMagnitudeThreshold;

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
    /// A standard captured one tone at a time, stored exactly as measured.
    ///
    /// <para>No resampling, unlike <see cref="Capture"/>: a stepped run's points already <i>are</i> a
    /// sparse log-spaced grid, so pushing them through an intermediate 256-point curve would only
    /// interpolate an interpolation. The record shape is the same either way, so nothing downstream
    /// can tell which route a standard took.</para>
    /// </summary>
    public static ImpedanceCalibration FromPoints(ReadOnlySpan<double> freqHz, ReadOnlySpan<Complex> h)
    {
        if (freqHz.Length < 2)
            throw new ArgumentException("A calibration needs at least two points.", nameof(freqHz));
        if (h.Length != freqHz.Length)
            throw new ArgumentException($"There are {freqHz.Length} frequencies and {h.Length} values.", nameof(h));

        var frequencies = new double[freqHz.Length];
        var real = new double[freqHz.Length];
        var imag = new double[freqHz.Length];

        for (int i = 0; i < freqHz.Length; i++)
        {
            // Ascending is what At's binary search assumes; a caller that hands over an unsorted grid
            // would otherwise get silently wrong interpolation rather than an error.
            if (freqHz[i] <= 0.0 || (i > 0 && freqHz[i] <= freqHz[i - 1]))
                throw new ArgumentException("Calibration frequencies must be positive and strictly ascending.", nameof(freqHz));

            frequencies[i] = freqHz[i];
            real[i] = h[i].Real;
            imag[i] = h[i].Imaginary;
        }

        return new ImpedanceCalibration(frequencies, real, imag);
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

        var freqHz = new double[binCount];
        for (int k = 0; k < binCount; k++)
            freqHz[k] = k * binHz;

        return ResampleAt(freqHz);
    }

    /// <summary>
    /// The stored curve at an arbitrary set of frequencies -- a stepped run's own log-spaced grid,
    /// where <see cref="Resample"/>'s uniform bins mean nothing.
    ///
    /// <para>One interpolation path, deliberately: <see cref="Resample"/> is a wrapper over this, so a
    /// standard cannot mean one thing on a chirp's grid and a slightly different thing on a stepped
    /// one. That matters because the two share a stored calibration -- a chirp-captured open is the
    /// ADC channels' mismatch however it was excited, and applying it to a stepped run is the
    /// recommended pairing rather than an edge case.</para>
    /// </summary>
    public Complex[] ResampleAt(ReadOnlySpan<double> freqHz)
    {
        if (!IsUsable)
            throw new InvalidOperationException("This calibration has no usable curve.");

        var values = new Complex[freqHz.Length];
        for (int i = 0; i < freqHz.Length; i++)
            values[i] = At(freqHz[i]);

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
