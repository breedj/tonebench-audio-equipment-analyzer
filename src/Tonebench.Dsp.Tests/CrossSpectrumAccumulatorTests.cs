using System.Numerics;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The single-bin H1 estimate a stepped-sine run is built on.
///
/// <para>The important test here is the last one: this accumulates the same quantities
/// <see cref="FrequencyResponseEstimator"/> does, and the whole justification for a second, smaller
/// class is that it is the <i>same</i> estimator restricted to one bin. Pinning the two against each
/// other is what stops it quietly becoming a different one.</para>
/// </summary>
public class CrossSpectrumAccumulatorTests
{
    [Fact]
    public void OneRecord_IsExactlyTheRatio()
    {
        var reference = new Complex(0.4, -0.3);
        var response = new Complex(0.15, 0.2);

        var accumulator = new CrossSpectrumAccumulator();
        accumulator.Add(reference, response);

        Assert.Equal(1, accumulator.Count);
        Assert.Equal((response / reference).Real, accumulator.H.Real, 12);
        Assert.Equal((response / reference).Imaginary, accumulator.H.Imaginary, 12);
    }

    /// <summary>
    /// With one record the coherence expression is an algebraic identity and carries no information,
    /// so 1.0 is what it is told to say rather than what it worked out.
    /// </summary>
    [Fact]
    public void ASingleRecord_ReportsFullCoherenceBecauseThereIsNothingToCompare()
    {
        var accumulator = new CrossSpectrumAccumulator();
        accumulator.Add(new Complex(0.4, -0.3), new Complex(0.15, 0.2));

        Assert.Equal(1.0, accumulator.Coherence, 12);
    }

    [Fact]
    public void RepeatedIdenticalRecords_RecoverTheRatioAndFullCoherence()
    {
        var reference = new Complex(0.5, 0.0);
        var response = new Complex(0.0, 0.25);

        var accumulator = new CrossSpectrumAccumulator();
        for (int i = 0; i < 8; i++)
            accumulator.Add(reference, response);

        Assert.Equal(0.5, accumulator.H.Magnitude, 12);
        Assert.Equal(Math.PI / 2.0, accumulator.H.Phase, 12);
        Assert.Equal(1.0, accumulator.Coherence, 9);
    }

    /// <summary>
    /// Noise on the response that the stimulus does not explain is exactly what coherence is for. The
    /// figure has to fall, and the estimate itself has to stay near the true ratio — that is the
    /// difference between averaging and simply believing the last record.
    /// </summary>
    [Fact]
    public void NoiseOnTheResponse_LowersCoherenceWithoutMovingTheEstimateMuch()
    {
        var reference = new Complex(1.0, 0.0);
        var random = new Random(7);

        var accumulator = new CrossSpectrumAccumulator();
        for (int i = 0; i < 200; i++)
        {
            var noise = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            accumulator.Add(reference, new Complex(0.5, 0.0) + (0.4 * noise));
        }

        Assert.InRange(accumulator.Coherence, 0.5, 0.99);
        Assert.Equal(0.5, accumulator.H.Magnitude, 1);
    }

    /// <summary>A reference channel carrying nothing is no measurement, not a very large ratio.</summary>
    [Fact]
    public void ASilentReference_YieldsZeroRatherThanADivisionByZero()
    {
        var accumulator = new CrossSpectrumAccumulator();
        accumulator.Add(Complex.Zero, new Complex(0.1, 0.1));
        accumulator.Add(Complex.Zero, new Complex(0.1, 0.1));

        Assert.Equal(Complex.Zero, accumulator.H);
        Assert.Equal(0.0, accumulator.Coherence, 12);
    }

    /// <summary>
    /// The same numbers the whole-spectrum estimator produces, on a synthetic pair driven through
    /// both. Restricted to one bin, this <i>is</i> that estimator; if the two ever disagree, one of
    /// them has changed what it means.
    /// </summary>
    [Fact]
    public void MatchesTheWholeSpectrumEstimatorAtTheSameBin()
    {
        const int FftSize = 1024;
        const int Bin = 61;
        var fft = new Fft.MathNetBackend();

        var estimator = new FrequencyResponseEstimator(FftSize, fft, gate: null, removeLatency: false);
        var accumulator = new CrossSpectrumAccumulator();
        var random = new Random(11);

        for (int record = 0; record < 5; record++)
        {
            var reference = new Complex[(FftSize / 2) + 1];
            var response = new Complex[reference.Length];

            for (int k = 0; k < reference.Length; k++)
            {
                reference[k] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
                response[k] = reference[k] * new Complex(0.7, 0.3)
                    + (0.05 * new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            }

            estimator.Add(reference, response);
            accumulator.Add(reference[Bin], response[Bin]);
        }

        var result = estimator.Complete();

        // The estimator regularises its division by a fraction of the peak power, which biases every
        // bin slightly low; three decimals is far inside that and far outside any real disagreement.
        Assert.Equal(result.Magnitude[Bin], accumulator.H.Magnitude, 3);
        Assert.Equal(result.Coherence[Bin], accumulator.Coherence, 6);
    }
}
