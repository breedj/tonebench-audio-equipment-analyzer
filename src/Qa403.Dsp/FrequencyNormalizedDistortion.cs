namespace Qa403.Dsp;

/// <summary>
/// A channel's measured fundamental level against frequency — the DUT's own response as one
/// sweep saw it — kept sorted so it can be read at any frequency in between.
///
/// Interpolation is linear in dB over log frequency: that is the straight line a first-order
/// roll-off draws on a Bode plot, so a 6 dB/octave slope reads exactly right at every frequency
/// between two grid points, and a resonance is at worst cut across by a chord. Outside the grid
/// there is nothing to read from and <see cref="VrmsAt"/> says so with <c>null</c> rather than
/// extrapolating a slope the sweep never measured.
/// </summary>
public sealed class FundamentalResponse
{
    private readonly List<double> _freqHz = [];
    private readonly List<double> _vrms = [];

    public int Count => _freqHz.Count;

    public void Clear()
    {
        _freqHz.Clear();
        _vrms.Clear();
    }

    /// <summary>
    /// Adds one measured point, keeping the grid sorted. A fundamental the analyzer never located
    /// arrives as a non-positive or non-finite level and is dropped rather than stored — a zero in
    /// the grid would turn every ratio taken against it into infinity, and infinity is exactly as
    /// unserializable as NaN. A point at a frequency already present replaces the earlier one.
    /// </summary>
    public void Add(double freqHz, double vrms)
    {
        if (!(freqHz > 0.0) || !double.IsFinite(freqHz) || !(vrms > 0.0) || !double.IsFinite(vrms))
            return;

        int index = _freqHz.BinarySearch(freqHz);
        if (index >= 0)
        {
            _vrms[index] = vrms;
            return;
        }

        index = ~index;
        _freqHz.Insert(index, freqHz);
        _vrms.Insert(index, vrms);
    }

    /// <summary>The fundamental at <paramref name="freqHz"/>, or <c>null</c> outside the measured span.</summary>
    public double? VrmsAt(double freqHz)
    {
        if (_freqHz.Count == 0 || !(freqHz > 0.0) || freqHz < _freqHz[0] || freqHz > _freqHz[^1])
            return null;

        int index = _freqHz.BinarySearch(freqHz);
        if (index >= 0)
            return _vrms[index];

        int hi = ~index;
        int lo = hi - 1;

        double t = Math.Log(freqHz / _freqHz[lo]) / Math.Log(_freqHz[hi] / _freqHz[lo]);
        double dbLo = 20.0 * Math.Log10(_vrms[lo]);
        double dbHi = 20.0 * Math.Log10(_vrms[hi]);
        return Math.Pow(10.0, (dbLo + (t * (dbHi - dbLo))) / 20.0);
    }
}

/// <summary>
/// One point's frequency-normalized distortion, as ratios (multiply by 100 for percent, the same
/// convention as <see cref="SpectrumMetrics.PartialThd"/>). Every field is <c>null</c> where the
/// sweep has not (yet) measured the fundamental at the harmonic's own frequency — which is what
/// makes these safe to serialize: absent is null, never NaN or infinity.
/// </summary>
/// <param name="Thd">All harmonics that have a reference, power-summed. Null when the second harmonic has none.</param>
/// <param name="HarmonicRatio">Orders 2..5, index 0 = second harmonic. Fixed length four; null per order without a reference.</param>
/// <param name="D6Plus">Orders six and above that have a reference, power-summed. Null when none does.</param>
public sealed record NormalizedDistortion(double? Thd, IReadOnlyList<double?> HarmonicRatio, double? D6Plus)
{
    public const int IndividualOrders = 4;
}

/// <summary>
/// Frequency-normalized distortion (Temme, Listen Inc., AES 1993; audioXpress March 2026): each
/// harmonic is compared to the fundamental measured <em>at the harmonic's own frequency</em>
/// rather than at the stimulus frequency,
///
/// <code>nTHD(f) = sqrt( Σ_{n≥2} A_n(f)² / A_1(n·f)² )</code>
///
/// so that the DUT's linear response — a roll-off, a resonance, a room reflection — is removed
/// from the distortion figure and what remains is the nonlinearity. A conventional THD divides
/// every harmonic by A_1(f), which on a device with a 6 dB/octave slope reads the second harmonic
/// 6 dB wrong and the third 9.5 dB wrong for reasons that have nothing to do with distortion.
///
/// This is pure arithmetic on numbers the sweep already measures: the harmonics come from
/// <see cref="HarmonicAnalyzer"/> at one point, and A_1(n·f) is read off the sweep's own
/// fundamental trace (<see cref="FundamentalResponse"/>), which is why the fundamental has to be
/// swept past the top of the requested band before the top points can be normalized.
/// </summary>
public static class FrequencyNormalizedDistortion
{
    /// <summary>
    /// Normalizes one point's harmonics against the fundamental response. Orders whose n·f lies
    /// outside the measured response come back null and are left out of the sums; the total is
    /// null when even the second harmonic has no reference, so a point never reports a "THD" made
    /// of whichever harmonics happened to be available.
    /// </summary>
    /// <param name="freqHz">The stimulus frequency the harmonics belong to.</param>
    /// <param name="harmonics">The located harmonics of that point. Order 1 is ignored; it is the reference at f, not at n·f.</param>
    /// <param name="response">The fundamental measured across the sweep so far.</param>
    public static NormalizedDistortion Compute(double freqHz, ReadOnlySpan<Harmonic> harmonics, FundamentalResponse response)
    {
        var individual = new double?[NormalizedDistortion.IndividualOrders];
        double thdSum = 0.0;
        double d6Sum = 0.0;
        bool hasSecond = false;
        bool hasSixthOrAbove = false;

        foreach (var harmonic in harmonics)
        {
            if (harmonic.Order < 2)
                continue;

            double? ratio = Ratio(harmonic.AmplitudeVrms, response.VrmsAt(harmonic.Order * freqHz));
            if (ratio is not { } r)
                continue;

            if (harmonic.Order == 2)
                hasSecond = true;

            thdSum += r * r;

            int slot = harmonic.Order - 2;
            if (slot < individual.Length)
            {
                individual[slot] = r;
            }
            else
            {
                hasSixthOrAbove = true;
                d6Sum += r * r;
            }
        }

        return new NormalizedDistortion(
            hasSecond ? Math.Sqrt(thdSum) : null,
            individual,
            hasSixthOrAbove ? Math.Sqrt(d6Sum) : null);
    }

    private static double? Ratio(double harmonicVrms, double? referenceVrms)
    {
        if (referenceVrms is not { } reference || !(reference > 0.0) || !double.IsFinite(harmonicVrms))
            return null;

        double ratio = harmonicVrms / reference;
        return double.IsFinite(ratio) ? ratio : null;
    }
}
