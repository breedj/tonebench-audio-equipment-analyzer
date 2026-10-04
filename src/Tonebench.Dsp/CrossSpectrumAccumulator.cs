using System.Numerics;

namespace Tonebench.Dsp;

/// <summary>
/// The H1 transfer-function estimate at a <b>single</b> bin, accumulated over several records.
///
/// <para>What a stepped-sine measurement needs and <see cref="FrequencyResponseEstimator"/> does not
/// provide: that class is built around whole spectra, an impulse gate, regularisation and an in-place
/// <c>Complete()</c> that may only be called once, none of which mean anything for one coherent tone.
/// The accumulation <i>rule</i> is identical, which is the point --
/// <c>CrossSpectrumAccumulatorTests</c> pins the two against each other on a synthetic pair so this
/// cannot quietly become a different estimator.</para>
///
/// <para><b>Why the average is taken here rather than in the pipeline.</b>
/// <c>SpectrumAverager</c> averages magnitudes, which throws the phase away -- and phase is most of
/// what a two-channel ratio is for. Averaging the complex cross-spectrum instead keeps it, and gives
/// a coherence for free.</para>
/// </summary>
public sealed class CrossSpectrumAccumulator
{
    private double _sxx;
    private double _syy;
    private Complex _sxy;

    /// <summary>Records folded in so far.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// One record's pair. <paramref name="reference"/> is the denominator of the ratio -- the channel
    /// on the generator side of the reference resistor for an impedance run.
    /// </summary>
    public void Add(Complex reference, Complex response)
    {
        _sxx += (reference * Complex.Conjugate(reference)).Real;
        _syy += (response * Complex.Conjugate(response)).Real;
        _sxy += Complex.Conjugate(reference) * response;
        Count++;
    }

    /// <summary>
    /// <c>Sxy/Sxx</c>, which for a single record is exactly <c>response/reference</c> -- so a run with
    /// no averaging behaves identically to one that never had the accumulator at all.
    ///
    /// <para>Zero when nothing was added, or when the reference channel carried no signal: a ratio
    /// against silence is not a small number, it is no measurement, and the caller can see that in
    /// <see cref="Coherence"/>.</para>
    /// </summary>
    public Complex H => _sxx > 0.0 ? _sxy / _sxx : Complex.Zero;

    /// <summary>
    /// Magnitude-squared coherence, <c>|Sxy|²/(Sxx·Syy)</c> -- the fraction of the response this
    /// stimulus explains, and therefore the number that says a reading was noise rather than an
    /// impedance.
    ///
    /// <para><b>1.0 below two records</b>, and that is honest rather than optimistic: with one record
    /// the expression is an algebraic identity (<c>|x*y|² == |x|²·|y|²</c>) and carries no
    /// information at all. Reporting the identity's value is the same choice the stepped-sine
    /// response makes for its whole trace; here it applies only when the user asked for a single
    /// record per point.</para>
    /// </summary>
    public double Coherence
    {
        get
        {
            if (Count < 2)
                return 1.0;

            double denominator = _sxx * _syy;
            if (!(denominator > 0.0))
                return 0.0;

            // Rounding can put a noiseless pair a hair over 1, which reads as a broken measurement on
            // a chart whose whole scale is 0 to 1.
            return Math.Min(1.0, (_sxy * Complex.Conjugate(_sxy)).Real / denominator);
        }
    }
}
