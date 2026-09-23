namespace Qa403.Device.Simulation;

/// <summary>
/// One classic low-pass alignment as an analog prototype: its poles, and for the elliptic its
/// zeros, all normalised to a corner of 1 rad/s. Fifth order throughout — two conjugate pole pairs
/// and one real pole — which is what "5 poles" means and what puts the roll-off at 100 dB per decade
/// on the three all-pole designs.
///
/// <para>Four filters, each the textbook answer to a different question. <b>Butterworth</b> is
/// maximally flat in the passband and −3 dB at its corner. <b>Chebyshev</b> (type I) buys a far
/// steeper transition with 1 dB of equal ripple in the passband, and its corner is the ripple
/// edge, −1 dB, not −3. <b>Bessel</b> has the flattest group delay, i.e. the least overshoot on a
/// step, and pays with the gentlest roll-off of the four; it is normalised so its corner is −3 dB
/// like the Butterworth's. <b>Elliptic</b> (Cauer) adds zeros in the stopband: 1 dB of passband
/// ripple, 60 dB of equal-ripple stopband attenuation, and the steepest transition anything of
/// this order can have. The four cost the same to run and are worth having side by side, because
/// a frequency-response measurement of each shows exactly the trade each one made.</para>
///
/// <para>The numbers were computed with <c>scipy.signal</c> (<c>butter</c>, <c>cheby1(rp=1)</c>,
/// <c>bessel(norm='mag')</c>, <c>ellip(rp=1, rs=60)</c>; <c>analog=True, output='zpk'</c>) and are
/// written out rather than derived here because only two of the four have a closed form and the
/// elliptic's needs Jacobi elliptic functions — a table with a stated source is easier to verify
/// than a port of those. The manual carries the same table (<c>web/src/manual/sim/filters.ts</c>)
/// and draws its expected curves from it; <c>FilterDutTests</c> pins the Butterworth and Chebyshev
/// entries against their closed forms and the other two against the properties that define them.</para>
/// </summary>
/// <param name="Name">The alignment's name, as the enum and the UI use it.</param>
/// <param name="PolePairs">Each conjugate pole pair's upper member as <c>(sigma, omega)</c>, i.e. the pole is <c>sigma + j omega</c>.</param>
/// <param name="RealPole">The one real pole, as a positive distance down the negative real axis.</param>
/// <param name="ZeroOmegas">
/// The elliptic's zero pairs, <c>±j omega</c>, one per pole pair in the same order (each pole
/// pair takes the zero nearest it, the usual pairing); empty for the all-pole designs.
/// </param>
internal sealed record FilterPrototype(
    string Name,
    (double Sigma, double Omega)[] PolePairs,
    double RealPole,
    double[] ZeroOmegas)
{
    /// <summary>Maximally flat: −3.01 dB at the corner, then 100 dB per decade.</summary>
    public static FilterPrototype Butterworth { get; } = new(
        "Butterworth",
        [(-0.309016994374947, 0.951056516295154), (-0.809016994374947, 0.587785252292473)],
        1.0,
        []);

    /// <summary>Type I, 1 dB ripple: 0 dB at DC, −1 dB at the corner, five ripple extrema between them.</summary>
    public static FilterPrototype Chebyshev { get; } = new(
        "Chebyshev",
        [(-0.08945836220019, 0.990107112003389), (-0.234205032817997, 0.611919847721094)],
        0.289493341235613,
        []);

    /// <summary>Maximally flat group delay, normalised to −3.01 dB at the corner (scipy's <c>norm='mag'</c>).</summary>
    public static FilterPrototype Bessel { get; } = new(
        "Bessel",
        [(-0.957676548562681, 1.471124320730394), (-1.380877325860439, 0.717909587626768)],
        1.502316271447478,
        []);

    /// <summary>Cauer, 1 dB passband ripple, 60 dB stopband: −1 dB at the corner, −60 dB by twice it.</summary>
    public static FilterPrototype Elliptic { get; } = new(
        "Elliptic",
        [(-0.071731220371103, 0.99419350430997), (-0.230737733537983, 0.66479439438804)],
        0.325517851567125,
        [1.740548328054631, 2.6540921270289]);

    /// <summary>
    /// The prototype's group delay at DC, in units of <c>1/omega_c</c> — what a delay reading of the
    /// Bessel should settle at across its passband. Only the Bessel's is worth quoting; it is
    /// <c>2.4274</c> for this normalisation (2.4274 / (2π · 2 kHz) = 193 µs).
    /// </summary>
    public const double BesselDelayAtDc = 2.4274107;

    /// <summary>
    /// The sections that realise this prototype as a low-pass with its corner at
    /// <paramref name="cornerHz"/>: one biquad per pole pair (with the elliptic's zero pair in its
    /// numerator) and a first-order section for the real pole, each mapped by the bilinear transform
    /// <b>prewarped at the corner</b>. Prewarped, unlike <see cref="Biquad.FromAnalog"/>'s plain
    /// mapping, because these filters are specified by their corner and nothing else: without it a
    /// 2 kHz corner lands at 1.9 kHz at 48 kHz, which a frequency-response measurement would report
    /// as the filter being wrong rather than the sampling being right.
    ///
    /// <para>Every section has unity gain at DC, so the cascade does too — which is what keeps the
    /// four filters inside the every-DUT-is-unity rule, and what makes the Chebyshev's and the
    /// elliptic's passband ripple sit <i>below</i> 0 dB, as an odd-order design's does.</para>
    /// </summary>
    public Biquad[] Sections(double cornerHz, double sampleRateHz)
    {
        double wc = 2.0 * Math.PI * cornerHz;
        var sections = new Biquad[PolePairs.Length + 1];

        for (int i = 0; i < PolePairs.Length; i++)
        {
            var (sigma, omega) = PolePairs[i];
            double poleMagnitudeSquared = ((sigma * sigma) + (omega * omega)) * wc * wc;
            double a1 = -2.0 * sigma * wc;

            if (i < ZeroOmegas.Length)
            {
                // (s^2 + wz^2) scaled so the section reads unity at DC: wp^2/wz^2.
                double zeroSquared = ZeroOmegas[i] * ZeroOmegas[i] * wc * wc;
                double gain = poleMagnitudeSquared / zeroSquared;
                sections[i] = Biquad.FromAnalog(gain, 0.0, gain * zeroSquared, 1.0, a1, poleMagnitudeSquared, sampleRateHz, cornerHz);
            }
            else
            {
                sections[i] = Biquad.FromAnalog(0.0, 0.0, poleMagnitudeSquared, 1.0, a1, poleMagnitudeSquared, sampleRateHz, cornerHz);
            }
        }

        // The real pole: a / (s + a), first order, so the s^2 coefficients are zero on both sides.
        double a = RealPole * wc;
        sections[^1] = Biquad.FromAnalog(0.0, 0.0, a, 0.0, 1.0, a, sampleRateHz, cornerHz);

        return sections;
    }
}
