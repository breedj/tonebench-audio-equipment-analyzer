namespace Qa403.Dsp;

/// <summary>
/// Turns a sequence of wrapped phase angles into a continuous curve, one sample at a time.
///
/// <para>The rule is "keep each step within half a turn of the last": a response that accumulates
/// several turns of phase across the band then reads as a continuous curve rather than a sawtooth
/// that resets every 360°. It is stateful because that is inherent — where a sample belongs depends
/// on where the previous one ended up, so the same raw angle unwraps to different values depending
/// on what came before it.</para>
///
/// <para>Shared rather than written twice. <see cref="FrequencyResponseEstimator"/> unwraps across
/// FFT bins and the stepped-sine response unwraps across sweep points; those are the same operation
/// on different axes, and two hand-rolled copies of the <c>ceil((step-180)/360)</c> arithmetic is
/// how the two curves would come to disagree about a DUT that inverts.</para>
/// </summary>
public sealed class PhaseUnwrapper
{
    private double _previousDeg;
    private double _turns;
    private bool _started;

    /// <summary>Degrees per radian — callers usually hold a <see cref="System.Numerics.Complex.Phase"/>.</summary>
    public const double DegreesPerRadian = 180.0 / Math.PI;

    /// <summary>Unwraps one wrapped angle in degrees against everything fed in before it.</summary>
    public double Next(double rawDeg)
    {
        if (_started)
        {
            double step = rawDeg + _turns - _previousDeg;
            if (step > 180.0)
                _turns -= 360.0 * Math.Ceiling((step - 180.0) / 360.0);
            else if (step < -180.0)
                _turns += 360.0 * Math.Ceiling((-step - 180.0) / 360.0);
        }

        _previousDeg = rawDeg + _turns;
        _started = true;
        return _previousDeg;
    }

    /// <summary>Unwraps a radian angle, for callers reading straight off a complex number.</summary>
    public double NextFromRadians(double rawRad) => Next(rawRad * DegreesPerRadian);

    /// <summary>
    /// The last unwrapped value, unchanged — what a caller hands out for a bin whose phase is not a
    /// measurement (see <see cref="FrequencyResponseEstimator.PhaseFloorRelative"/>), so the turn
    /// count is carried over that bin rather than random-walked through it. 0 before the first sample.
    /// </summary>
    public double Hold() => _started ? _previousDeg : 0.0;

    /// <summary>Forgets the accumulated turns, so the next sample starts a fresh curve.</summary>
    public void Reset()
    {
        _previousDeg = 0.0;
        _turns = 0.0;
        _started = false;
    }

    /// <summary>Unwraps a whole sequence at once. <paramref name="unwrappedDeg"/> may alias <paramref name="rawDeg"/>.</summary>
    public static void Unwrap(ReadOnlySpan<double> rawDeg, Span<double> unwrappedDeg)
    {
        if (unwrappedDeg.Length < rawDeg.Length)
            throw new ArgumentException("The destination is shorter than the source.", nameof(unwrappedDeg));

        var unwrapper = new PhaseUnwrapper();
        for (int i = 0; i < rawDeg.Length; i++)
            unwrappedDeg[i] = unwrapper.Next(rawDeg[i]);
    }
}
