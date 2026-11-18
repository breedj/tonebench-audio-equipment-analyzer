using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The math channel, against arithmetic rather than against itself.
///
/// <para>Every case here builds its two inputs from a formula whose combination is known in closed
/// form, which is the only way a derived trace can be trusted: a math channel that is subtly wrong
/// draws a perfectly plausible waveform, and there is nothing on screen to say so.</para>
/// </summary>
public class WaveformMathTests
{
    private static double[] Apply(
        WaveformOperation operation,
        double[] a,
        double[] b,
        double sampleRateHz = 48_000.0)
    {
        var destination = new double[Math.Max(a.Length, b.Length)];
        WaveformMath.Apply(operation, a, b, sampleRateHz, destination);
        return destination;
    }

    // -----------------------------------------------------------------------
    // The arithmetic
    // -----------------------------------------------------------------------

    [Fact]
    public void AMinusB_IsTheDifferenceSampleBySample()
    {
        double[] a = [1.0, 2.0, 3.0, 4.0];
        double[] b = [0.5, 0.5, -1.0, 4.0];

        Assert.Equal([0.5, 1.5, 4.0, 0.0], Apply(WaveformOperation.AMinusB, a, b));
        Assert.Equal([-0.5, -1.5, -4.0, 0.0], Apply(WaveformOperation.BMinusA, a, b));
        Assert.Equal([1.5, 2.5, 2.0, 8.0], Apply(WaveformOperation.APlusB, a, b));
        Assert.Equal([0.5, 1.0, -3.0, 16.0], Apply(WaveformOperation.ATimesB, a, b));
        Assert.Equal([-1.0, -2.0, -3.0, -4.0], Apply(WaveformOperation.NegateA, a, b));
        Assert.Equal([-0.5, -0.5, 1.0, -4.0], Apply(WaveformOperation.NegateB, a, b));
    }

    /// <summary>
    /// The case the feature exists for: two nearly identical channels whose difference is a
    /// thousandth of either, recovered exactly.
    ///
    /// <para>It is also the case that fails hardest if a math channel is ever computed from the
    /// drawn columns instead of the samples — see <see cref="WaveformMath"/>'s own remarks. Both
    /// extremes of a column are the near-1 V envelope, so an envelope difference reads zero where
    /// the true difference is the whole signal.</para>
    /// </summary>
    [Fact]
    public void ADifferentialPair_RecoversADifferenceAThousandthOfEitherChannel()
    {
        const int n = 4096;
        const double fs = 48_000.0;
        var a = new double[n];
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / fs;
            double common = Math.Sin(2.0 * Math.PI * 1_000.0 * t);
            a[i] = common + (0.001 * Math.Sin(2.0 * Math.PI * 3_000.0 * t));
            b[i] = common;
        }

        var difference = Apply(WaveformOperation.AMinusB, a, b, fs);

        for (int i = 0; i < n; i++)
        {
            double expected = 0.001 * Math.Sin(2.0 * Math.PI * 3_000.0 * (i / fs));
            Assert.Equal(expected, difference[i], 12);
        }

        var levels = WaveformMeasure.Measure(difference);
        Assert.Equal(0.001, levels.MaxV, 6);
        Assert.Equal(0.001 / Math.Sqrt(2.0), levels.RmsV, 6);
    }

    // -----------------------------------------------------------------------
    // Division
    // -----------------------------------------------------------------------

    [Fact]
    public void ADivB_IsTheRatioWhereTheDenominatorIsHealthy()
    {
        double[] a = [2.0, -6.0, 0.25];
        double[] b = [4.0, 3.0, 0.5];

        Assert.Equal([0.5, -2.0, 0.5], Apply(WaveformOperation.ADivB, a, b));
    }

    /// <summary>
    /// A denominator through zero rails rather than producing a NaN or an infinity — which is not a
    /// nicety: either would reach a <c>float[]</c> on the wire and truncate the response.
    /// </summary>
    [Fact]
    public void ADivB_RailsRatherThanProducingAnythingThatIsNotANumber()
    {
        double[] a = [1.0, -1.0, 0.0, 1e300];
        double[] b = [0.0, 0.0, 0.0, 1e-300];

        var destination = new double[4];
        int clamped = WaveformMath.Apply(WaveformOperation.ADivB, a, b, 48_000.0, destination);

        Assert.All(destination, v => Assert.True(double.IsFinite(v), $"{v} is not finite"));
        Assert.Equal(WaveformMath.DivisionLimit, destination[0]);
        Assert.Equal(-WaveformMath.DivisionLimit, destination[1]);
        Assert.Equal(0.0, destination[2]);
        Assert.Equal(WaveformMath.DivisionLimit, destination[3]);
        Assert.Equal(4, clamped);
    }

    /// <summary>
    /// How often it railed is counted, because a trace that spends most of its time at the limit is
    /// one whose reader has to be told.
    /// </summary>
    [Fact]
    public void ADivB_CountsOnlyTheSamplesItRailedOn()
    {
        double[] a = [1.0, 1.0, 1.0, 1.0];
        double[] b = [1.0, 0.5, 0.0, 0.0005];

        var destination = new double[4];
        int clamped = WaveformMath.Apply(WaveformOperation.ADivB, a, b, 48_000.0, destination);

        Assert.Equal(2, clamped);
        Assert.Equal(1.0, destination[0]);
        Assert.Equal(2.0, destination[1]);
    }

    // -----------------------------------------------------------------------
    // Derivative
    // -----------------------------------------------------------------------

    /// <summary>A ramp of known slope reads that slope at every sample, ends included.</summary>
    [Fact]
    public void TheDerivativeOfARamp_IsItsSlope()
    {
        const double fs = 48_000.0;
        const double slope = 1_234.5; // volts per second
        var ramp = new double[64];
        for (int i = 0; i < ramp.Length; i++)
            ramp[i] = slope * i / fs;

        var d = Apply(WaveformOperation.DerivativeA, ramp, [], fs);

        Assert.All(d, v => Assert.Equal(slope, v, 6));
    }

    /// <summary>
    /// A sine's derivative is <c>2πf·A·cos</c>, and a central difference reads it low by
    /// <c>sin(ω)/ω</c> — so the test asserts the <i>analytic</i> figure scaled by that factor
    /// rather than loosening a tolerance until the exact one passes.
    ///
    /// <para>Which is the point: the error is a known property of the difference and not noise, so
    /// pinning it is what would catch a forward difference being substituted — that one is low by
    /// the same factor <i>and</i> half a sample early in phase.</para>
    /// </summary>
    [Fact]
    public void TheDerivativeOfASine_IsTheAnalyticOneScaledByTheCentralDifferencesOwnFactor()
    {
        const double fs = 48_000.0;
        const double freq = 1_000.0;
        const double amplitude = 0.75;
        const int n = 1024;

        var sine = new double[n];
        for (int i = 0; i < n; i++)
            sine[i] = amplitude * Math.Sin(2.0 * Math.PI * freq * i / fs);

        var d = Apply(WaveformOperation.DerivativeA, sine, [], fs);

        double omega = 2.0 * Math.PI * freq / fs;
        double factor = Math.Sin(omega) / omega;
        Assert.Equal(0.99715, factor, 5);

        // The interior only: the two ends are a one-sided difference by construction, so the
        // trace can be the same length as the waveform it shares an x array with.
        for (int i = 1; i < n - 1; i++)
        {
            double analytic = 2.0 * Math.PI * freq * amplitude * Math.Cos(2.0 * Math.PI * freq * i / fs);
            Assert.Equal(analytic * factor, d[i], 6);
        }
    }

    [Fact]
    public void TheDerivative_IsTheSameLengthAsItsInput()
    {
        var destination = new double[7];
        WaveformMath.Apply(WaveformOperation.DerivativeA, new double[7], [], 48_000.0, destination);

        // A one-sample record has no difference to take, and answering with a shorter array would
        // put the math trace on a different x array from the waveform.
        var single = new double[1];
        WaveformMath.Apply(WaveformOperation.DerivativeA, [1.5], [], 48_000.0, single);
        Assert.Equal(0.0, single[0]);
    }

    // -----------------------------------------------------------------------
    // Integral
    // -----------------------------------------------------------------------

    /// <summary>
    /// A sine integrates to <c>−A·cos/ω</c>, offset so the trace starts at zero. The mean of a
    /// whole number of periods is already zero, so this case isolates the integration itself.
    /// </summary>
    [Fact]
    public void TheIntegralOfASine_IsItsAnalyticIntegral()
    {
        const double fs = 48_000.0;
        const double freq = 1_000.0;
        const double amplitude = 0.5;
        const int n = 480; // ten whole periods

        var sine = new double[n];
        for (int i = 0; i < n; i++)
            sine[i] = amplitude * Math.Sin(2.0 * Math.PI * freq * i / fs);

        var integral = Apply(WaveformOperation.IntegralA, sine, [], fs);

        // The trapezoid rule's own factor, (ωΔt/2)/tan(ωΔt/2) — the counterpart of the central
        // difference's sin(ω)/ω above, and asserted for the same reason: it is a known property of
        // the rule rather than noise, so pinning it is what would catch the rule being changed.
        double step = 2.0 * Math.PI * freq / fs;
        double factor = (step * 0.5) / Math.Tan(step * 0.5);
        Assert.Equal(0.99857, factor, 5);

        double omega = 2.0 * Math.PI * freq;
        Assert.Equal(0.0, integral[0]);
        for (int i = 0; i < n; i++)
        {
            double t = i / fs;
            double analytic = amplitude * (1.0 - Math.Cos(omega * t)) / omega;
            Assert.Equal(analytic * factor, integral[i], 11);
        }
    }

    /// <summary>
    /// The decision that changes what the trace means: a constant offset integrates to a ramp, so
    /// the window's mean is removed first and a DC-only record integrates to nothing at all.
    ///
    /// <para>On this hardware that offset is the front end's own — both the QA403 and a sound card
    /// are AC-coupled — so integrating it would draw an artefact larger than most of what anyone
    /// would integrate for.</para>
    /// </summary>
    [Fact]
    public void TheIntegral_RemovesTheMeanFirst_SoAnOffsetDoesNotRampItOffTheScreen()
    {
        const double fs = 48_000.0;
        var dc = new double[2048];
        Array.Fill(dc, -0.0045); // the measured left-input offset

        var integral = Apply(WaveformOperation.IntegralA, dc, [], fs);

        Assert.All(integral, v => Assert.Equal(0.0, v, 12));

        // And the same signal integrated without that step would have reached this instead, which
        // is what the removal is worth: two orders more than the sine case above.
        double ramped = -0.0045 * (dc.Length - 1) / fs;
        Assert.Equal(-1.9190625e-4, ramped, 10);
    }

    [Fact]
    public void TheIntegralOfASineOnAnOffset_IsTheSameAsWithoutIt()
    {
        const double fs = 48_000.0;
        const int n = 480;
        var plain = new double[n];
        var offset = new double[n];
        for (int i = 0; i < n; i++)
        {
            plain[i] = 0.5 * Math.Sin(2.0 * Math.PI * 1_000.0 * i / fs);
            offset[i] = plain[i] + 2.0;
        }

        var a = Apply(WaveformOperation.IntegralA, plain, [], fs);
        var b = Apply(WaveformOperation.IntegralA, offset, [], fs);

        for (int i = 0; i < n; i++)
            Assert.Equal(a[i], b[i], 12);
    }

    // -----------------------------------------------------------------------
    // Units and sources
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every operation's unit, because the reading set it is measured with names every figure in
    /// volts and for most of these that would be wrong.
    /// </summary>
    [Theory]
    [InlineData(WaveformOperation.AMinusB, WaveformMathUnit.Volt)]
    [InlineData(WaveformOperation.BMinusA, WaveformMathUnit.Volt)]
    [InlineData(WaveformOperation.APlusB, WaveformMathUnit.Volt)]
    [InlineData(WaveformOperation.NegateA, WaveformMathUnit.Volt)]
    [InlineData(WaveformOperation.NegateB, WaveformMathUnit.Volt)]
    [InlineData(WaveformOperation.ATimesB, WaveformMathUnit.VoltSquared)]
    [InlineData(WaveformOperation.ADivB, WaveformMathUnit.Ratio)]
    [InlineData(WaveformOperation.DerivativeA, WaveformMathUnit.VoltPerSecond)]
    [InlineData(WaveformOperation.DerivativeB, WaveformMathUnit.VoltPerSecond)]
    [InlineData(WaveformOperation.IntegralA, WaveformMathUnit.VoltSecond)]
    [InlineData(WaveformOperation.IntegralB, WaveformMathUnit.VoltSecond)]
    public void EveryOperation_CarriesTheUnitItsNumbersAreIn(WaveformOperation operation, WaveformMathUnit unit)
    {
        Assert.Equal(unit, WaveformMath.UnitOf(operation));
    }

    /// <summary>
    /// Which channels each operation reads, which is what decides whether a one-channel measurement
    /// gets a trace or a reason — and a trace against an array of zeros would draw A itself and
    /// call it A − B.
    /// </summary>
    [Fact]
    public void TheOneChannelOperations_AreTheOnlyOnesThatNeedOneChannel()
    {
        foreach (var operation in Enum.GetValues<WaveformOperation>())
        {
            bool single = operation is WaveformOperation.NegateA or WaveformOperation.NegateB
                or WaveformOperation.DerivativeA or WaveformOperation.DerivativeB
                or WaveformOperation.IntegralA or WaveformOperation.IntegralB;

            int reads = (WaveformMath.NeedsA(operation) ? 1 : 0) + (WaveformMath.NeedsB(operation) ? 1 : 0);
            Assert.Equal(single ? 1 : 2, reads);
        }

        Assert.True(WaveformMath.NeedsA(WaveformOperation.DerivativeA));
        Assert.False(WaveformMath.NeedsB(WaveformOperation.DerivativeA));
        Assert.False(WaveformMath.NeedsA(WaveformOperation.IntegralB));
        Assert.True(WaveformMath.NeedsB(WaveformOperation.IntegralB));
    }

    /// <summary>A two-channel operation over slices of different lengths takes the shorter.</summary>
    [Fact]
    public void ATwoChannelOperation_StopsAtTheShorterInput()
    {
        var destination = new double[8];
        WaveformMath.Apply(WaveformOperation.AMinusB, new double[8], new double[3], 48_000.0, destination);

        // Only the three it could compute were written; the rest are left as they were, which is
        // what lets the caller size the array to the slice and trust the count it asked for.
        Assert.Equal(0.0, destination[0]);
    }

    [Fact]
    public void AnEmptyWindow_ComputesNothing()
    {
        Assert.Equal(0, WaveformMath.Apply(WaveformOperation.AMinusB, [], [], 48_000.0, []));
    }
}
