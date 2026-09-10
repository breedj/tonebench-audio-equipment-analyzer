using System.Numerics;

namespace Qa403.Dsp;

/// <summary>
/// Everything needed to turn one measured channel ratio into an impedance: the reference resistor,
/// the fixture calibration, and the optional de-embedding of the analyzer's own input impedance.
/// </summary>
/// <remarks>
/// A record rather than a long parameter list because the calibration chain is order-sensitive and
/// every field changes the answer -- a positional call site with six bare numbers in it is one
/// transposed pair away from a plausible measurement of something else.
/// </remarks>
public sealed record ImpedanceSolveOptions
{
    /// <summary>The series reference resistor, in ohms. Sensitivity of H to Z is best when this is near |Z|.</summary>
    public required double ReferenceOhms { get; init; }

    /// <summary>Analysis bin spacing. Needed only to turn a bin index into the frequency a fixed cable inductance depends on.</summary>
    public required double BinHz { get; init; }

    /// <summary>
    /// The frequency of each element, when the curve is <b>not</b> on a uniform bin grid -- which is
    /// what a stepped-sine run produces, a log-spaced point per measured tone.
    ///
    /// <para>Null for a chirp, where <see cref="BinHz"/> says everything and element <c>k</c> is at
    /// <c>k*BinHz</c>. The only place either is read is the fixed-cable inductance term, but reading
    /// the wrong one there would put a plausible reactance on every point of a stepped run.</para>
    /// </summary>
    public double[]? FrequenciesHz { get; init; }

    /// <summary>H measured with the DUT terminals open, on the analysis grid, or null for no open correction.</summary>
    public Complex[]? OpenCal { get; init; }

    /// <summary>H measured with the DUT terminals shorted, on the analysis grid, or null. Takes precedence over <see cref="CableOhms"/>.</summary>
    public Complex[]? ShortCal { get; init; }

    /// <summary>Fixed lead resistance to subtract, in ohms, when there is no measured short.</summary>
    public double CableOhms { get; init; }

    /// <summary>Fixed lead inductance to subtract, in henries, when there is no measured short.</summary>
    public double CableHenry { get; init; }

    /// <summary>
    /// The analyzer's own input impedance across the measurement node, in ohms, or null to treat it
    /// as infinite.
    ///
    /// <para>Null is right for anything from a few ohms to a few kilohms and wrong for a valve
    /// amplifier's grid input, where the analyzer's input sits in parallel with a DUT of comparable
    /// impedance. The value has to come from the QA403's datasheet; there is deliberately no default,
    /// because a guessed one would silently bias every high-impedance measurement instead of leaving
    /// the correction off.</para>
    /// </summary>
    public double? AnalyzerInputOhms { get; init; }
}

/// <summary>
/// The series-reference-resistor impedance measurement, as pure arithmetic on a measured channel
/// ratio.
///
/// <para><b>The circuit.</b> The generator drives a known resistor R into the DUT; one input channel
/// measures the generator side of R and the other the junction of R and the DUT. With
/// <c>H = V_dut / V_ref</c> that junction is a voltage divider, so <c>Z = R*H/(1 - H)</c>.</para>
///
/// <para><b>Both spectra must come from the same capture.</b> H is a complex ratio, so the stimulus's
/// arbitrary rotation is common-mode -- and therefore cancels -- only when the two channels are two
/// halves of one record. That is also why the estimator feeding this runs with
/// <c>removeLatency: false</c>; see <see cref="FrequencyResponseEstimator"/>'s constructor for what
/// removing a fraction of a sample costs in phase.</para>
///
/// <para>Pure and unit-tested; nothing here touches a device or the pipeline.</para>
/// </summary>
public static class ImpedanceMath
{
    /// <summary>
    /// The magnitude an open circuit is reported as. Z runs to infinity as <c>H -> 1</c>, and an
    /// infinity reaching a chart axis or a float BLOB is worse than a large finite number: it poisons
    /// an autoscale and decodes as NaN.
    /// </summary>
    public const double MaxOhms = 1e9;

    /// <summary>Divider ratios this close to unity are treated as an open circuit rather than divided by.</summary>
    private const double OpenEpsilon = 1e-12;

    /// <summary><c>Z = R*H/(1 - H)</c>, clamped at <see cref="MaxOhms"/> where the divider goes open.</summary>
    public static Complex FromDivider(Complex h, double referenceOhms)
    {
        Complex denominator = Complex.One - h;
        if (denominator.Magnitude < OpenEpsilon)
            return new Complex(MaxOhms, 0.0);

        return Clamp(referenceOhms * h / denominator);
    }

    /// <summary>
    /// The whole calibration chain, per bin: remove the inter-channel mismatch, invert the divider,
    /// undo the analyzer's own loading, and subtract the fixture's series impedance.
    ///
    /// <para><b>The open calibration is not simply a division by H_open, and the difference matters
    /// exactly where <see cref="ImpedanceSolveOptions.AnalyzerInputOhms"/> does.</b> Writing the
    /// channel mismatch as <c>a</c> and the analyzer input as <c>Z_in</c>, an open fixture measures
    /// <c>H_open = a*Z_in/(R + Z_in)</c> -- not <c>a</c>, because the analyzer's own input is still
    /// loading the node. Dividing by H_open therefore over-corrects by <c>Z_in/(R + Z_in)</c>. That is
    /// 0.009 dB for a 100 ohm reference against a 100 kohm input and entirely ignorable, and it is a
    /// <i>factor of two</i> when the two are comparable -- which is precisely the high-impedance case
    /// the de-embed exists for. So <c>a</c> is recovered from H_open first, using the same Z_in the
    /// de-embed then uses.</para>
    ///
    /// <para>A measured short is preferred to a typed-in cable impedance because it is the same path
    /// measured the same way: running this identical chain over the short's own H yields the
    /// fixture's series impedance directly, connectors and all.</para>
    /// </summary>
    /// <param name="h">Measured <c>V_dut/V_ref</c>, one-sided, on the analysis grid.</param>
    public static Complex[] Solve(ReadOnlySpan<Complex> h, ImpedanceSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ReferenceOhms);

        var open = options.OpenCal;
        var shortCal = options.ShortCal;

        if (open is not null && open.Length != h.Length)
            throw new ArgumentException(
                $"The open calibration has {open.Length} bins, the measurement {h.Length}.", nameof(options));
        if (shortCal is not null && shortCal.Length != h.Length)
            throw new ArgumentException(
                $"The short calibration has {shortCal.Length} bins, the measurement {h.Length}.", nameof(options));

        var frequencies = options.FrequenciesHz;
        if (frequencies is not null && frequencies.Length != h.Length)
            throw new ArgumentException(
                $"The frequency axis has {frequencies.Length} points, the measurement {h.Length}.", nameof(options));

        double r = options.ReferenceOhms;
        double? zin = options.AnalyzerInputOhms;
        var result = new Complex[h.Length];

        for (int k = 0; k < h.Length; k++)
        {
            Complex mismatch = open is null ? Complex.One : MismatchFrom(open[k], r, zin);
            Complex branch = Branch(h[k], mismatch, r, zin);

            double freqHz = frequencies is null ? k * options.BinHz : frequencies[k];

            Complex fixture = shortCal is not null
                ? Branch(shortCal[k], mismatch, r, zin)
                : new Complex(options.CableOhms, 2.0 * Math.PI * freqHz * options.CableHenry);

            result[k] = Clamp(branch - fixture);
        }

        return result;
    }

    /// <summary>
    /// The inter-channel gain and phase mismatch behind an open-fixture reading, with the analyzer
    /// input's own share of it taken back out. Reduces to H_open itself as <c>Z_in -> infinity</c>.
    /// </summary>
    private static Complex MismatchFrom(Complex hOpen, double referenceOhms, double? analyzerInputOhms) =>
        analyzerInputOhms is { } zin && zin > 0.0
            ? hOpen * ((referenceOhms + zin) / zin)
            : hOpen;

    /// <summary>
    /// The series impedance hanging off the measurement node: mismatch removed, divider inverted, and
    /// the analyzer's parallel input taken back out.
    /// </summary>
    private static Complex Branch(Complex hMeasured, Complex mismatch, double referenceOhms, double? analyzerInputOhms)
    {
        Complex h = mismatch.Magnitude < OpenEpsilon ? hMeasured : hMeasured / mismatch;
        Complex parallel = FromDivider(h, referenceOhms);

        if (analyzerInputOhms is not { } zin || zin <= 0.0)
            return parallel;

        // 1/(1/Zp - 1/Zin). A vanishing admittance means the DUT branch carried no current at all,
        // which is an open circuit by another route.
        Complex admittance = (Complex.One / parallel) - (Complex.One / zin);
        return admittance.Magnitude < 1.0 / MaxOhms
            ? new Complex(MaxOhms, 0.0)
            : Clamp(Complex.One / admittance);
    }

    /// <summary>Bounds a magnitude and turns any non-finite component into <see cref="MaxOhms"/>, preserving phase where there is one.</summary>
    private static Complex Clamp(Complex z)
    {
        if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary))
            return new Complex(MaxOhms, 0.0);

        double magnitude = z.Magnitude;
        return magnitude > MaxOhms ? z * (MaxOhms / magnitude) : z;
    }

    /// <summary>
    /// Ohms as dB relative to 1 ohm, which is how an impedance trace rides in the
    /// <c>response_trace</c> table's magnitude columns and through the chart decimator.
    /// </summary>
    public static double ToDbOhms(double ohms) => 20.0 * Math.Log10(Math.Max(ohms, 1e-9));

    public static double FromDbOhms(double dbOhms) => Math.Pow(10.0, dbOhms / 20.0);

    /// <summary>
    /// The median magnitude over <c>[firstBin, lastBin]</c>, or NaN for an empty band. Median rather
    /// than mean for the reason <c>ResponseCaptureSupport.MedianGainDb</c> gives: a mean is dragged by
    /// any resonance peak or null, and what is wanted is the level the trace actually sits at.
    /// </summary>
    public static double MedianOhms(ReadOnlySpan<Complex> z, int firstBin, int lastBin)
    {
        if (z.Length == 0)
            return double.NaN;

        firstBin = Math.Max(0, firstBin);
        lastBin = Math.Min(z.Length - 1, lastBin);
        if (lastBin < firstBin)
            return double.NaN;

        var magnitudes = new List<double>(lastBin - firstBin + 1);
        for (int k = firstBin; k <= lastBin; k++)
            magnitudes.Add(z[k].Magnitude);

        magnitudes.Sort();
        return magnitudes[magnitudes.Count / 2];
    }
}
