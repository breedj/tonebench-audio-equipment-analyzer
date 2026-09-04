using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class FrequencyResponseEstimatorTests
{
    private const int FftSize = 16384;
    private const double SampleRateHz = 48000.0;
    private const double StartHz = 20.0;
    private const double EndHz = 20000.0;

    /// <summary>Sweep bounds for the distortion tests: the end is low enough that a third harmonic stays below Nyquist.</summary>
    private const double DistortionStartHz = 20.0;

    private const double DistortionEndHz = 4000.0;

    private static double BinHz => SampleRateHz / FftSize;

    [Fact]
    public void Estimate_OfAStraightLoopback_IsFlatAtZeroDbAcrossTheSweptBand()
    {
        var stimulus = Chirp();
        var result = Estimate(stimulus, stimulus);

        // Not exactly 0, and the miss is neither noise nor an approximation: the H1 regulariser
        // scales every bin by exactly 1/(1 + lambda/Sxx), so the bins where the sweep has least
        // energy droop most. Predicting it from the reported confidence rather than allowing a
        // tolerance is what makes this a check on the regulariser instead of a check on luck.
        foreach (int bin in BandBins())
        {
            double expectedDb = PredictedRegulariserBiasDb(result.Confidence[bin]);
            Assert.Equal(expectedDb, FrequencyResponseEstimator.ToDb(result.Magnitude[bin]), 9);
            Assert.InRange(expectedDb, -0.005, 0.0);
        }
    }

    [Fact]
    public void Estimate_OfAOnePoleLowPass_MatchesTheFiltersOwnTransferFunction()
    {
        const double cornerHz = 1000.0;
        var stimulus = Chirp();
        var response = OnePoleLowPass(stimulus, cornerHz);

        var result = Estimate(stimulus, response);

        // Compare against the difference equation's exact response rather than the analog prototype
        // it approximates -- the two part company well before Nyquist, and it is the digital filter
        // that actually produced these samples.
        foreach (int bin in BandBins())
        {
            // Same regulariser bias as the loopback case, since it multiplies whatever the true
            // response is rather than adding to it.
            double expectedDb = FrequencyResponseEstimator.ToDb(OnePoleMagnitude(bin * BinHz, cornerHz))
                + PredictedRegulariserBiasDb(result.Confidence[bin]);
            Assert.Equal(expectedDb, FrequencyResponseEstimator.ToDb(result.Magnitude[bin]), 6);
        }
    }

    [Fact]
    public void Estimate_OfAOnePoleLowPass_PutsTheMinusThreeDbCornerWhereItBelongs()
    {
        const double cornerHz = 1000.0;
        var stimulus = Chirp();
        var response = OnePoleLowPass(stimulus, cornerHz);

        var result = Estimate(stimulus, response);
        var traceDb = result.Magnitude.Select(FrequencyResponseEstimator.ToDb).ToArray();

        int firstBin = (int)Math.Ceiling(StartHz * 2.0 / BinHz);
        int lastBin = (int)Math.Floor(EndHz / 2.0 / BinHz);
        var bandwidth = BandwidthAnalyzer.Find(traceDb, BinHz, dropDb: 3.0, firstBin, lastBin);

        Assert.InRange(bandwidth.UpperHz, cornerHz * 0.95, cornerHz * 1.05);
        Assert.True(bandwidth.LowerClamped, "a low pass has no lower corner inside the band");
    }

    [Fact]
    public void Estimate_IsUnchangedByWhereTheCaptureWindowLanded()
    {
        // The assumption the whole measurement rests on: the stimulus is exactly one period long, so
        // a capture starting at an arbitrary offset is a circular rotation of it, and a rotation
        // does not change a magnitude spectrum. If this ever fails, every trace acquires a ripple
        // that moves between runs.
        var stimulus = Chirp();
        var response = OnePoleLowPass(stimulus, 3000.0);

        var aligned = Estimate(stimulus, response);
        var shifted = Estimate(stimulus, Rotate(response, 4321));

        foreach (int bin in BandBins())
            Assert.Equal(aligned.Magnitude[bin], shifted.Magnitude[bin], 9);
    }

    [Fact]
    public void Estimate_RemovesTheBulkDelay_SoPhaseStaysReadable()
    {
        var stimulus = Chirp();
        var response = Rotate(stimulus, 500);

        var result = Estimate(stimulus, response);

        Assert.Equal(500.0, result.LatencySamples, 3);

        // With the delay removed, a pure delay is indistinguishable from a straight wire.
        foreach (int bin in BandBins())
            Assert.Equal(0.0, result.PhaseDeg[bin], 3);
    }

    [Fact]
    public void Estimate_AveragingAcrossDifferentlyAlignedRecords_DoesNotCancel()
    {
        // Each record lands at its own arbitrary rotation. Accumulating without removing the
        // resulting phase ramp first makes the records cancel — more averaging producing a worse
        // answer, which is the failure this ordering exists to prevent.
        var stimulus = Chirp();
        var response = OnePoleLowPass(stimulus, 5000.0);

        var estimator = new FrequencyResponseEstimator(FftSize, new MathNetBackend());
        foreach (int shift in new[] { 0, 1234, 9001, 15000 })
            estimator.Add(Spectrum(stimulus), Spectrum(Rotate(response, shift)));

        var averaged = estimator.Complete();
        var single = Estimate(stimulus, response);

        foreach (int bin in BandBins())
            Assert.Equal(single.Magnitude[bin], averaged.Magnitude[bin], 6);
    }

    [Fact]
    public void Coherence_IsOneForASingleRecord_AndFallsWhenNoiseDiffersBetweenRecords()
    {
        var stimulus = Chirp();

        var single = Estimate(stimulus, stimulus);
        foreach (int bin in BandBins())
            Assert.Equal(1.0, single.Coherence[bin], 6);

        var estimator = new FrequencyResponseEstimator(FftSize, new MathNetBackend());
        var random = new Random(7);
        for (int record = 0; record < 8; record++)
            estimator.Add(Spectrum(stimulus), Spectrum(AddNoise(stimulus, random, 0.5)));

        var noisy = estimator.Complete();
        double mean = BandBins().Average(bin => noisy.Coherence[bin]);
        Assert.InRange(mean, 0.0, 0.95);
    }

    [Fact]
    public void Confidence_CollapsesOutsideTheSweptBand()
    {
        var stimulus = Chirp();
        var result = Estimate(stimulus, stimulus);

        double inBand = result.Confidence[(int)Math.Round(1000.0 / BinHz)];
        double aboveBand = result.Confidence[(int)Math.Round(23000.0 / BinHz)];

        Assert.InRange(inBand, 0.05, 1.0);
        Assert.True(aboveBand < inBand / 100.0, $"expected the stimulus to die above the sweep, got {aboveBand} vs {inBand}");
    }

    // -- impulse gating -----------------------------------------------------------------------

    /// <summary>
    /// A memoryless cubic is the simplest DUT that distorts, and it is the whole reason the gate
    /// exists: <c>y = x + d·x³</c> raises the linear gain by <c>0.75d</c> and emits a third harmonic
    /// of <c>0.25d</c>, which sweep deconvolution returns as its own packet ahead of the impulse.
    /// Ungated that packet combs the magnitude; gated it is gone and what is left is the linear gain
    /// the algebra predicts. The sweep stops at a quarter of Nyquist so the third harmonic has
    /// somewhere to go — aliased, it would fold back to a position the gate has no reason to expect.
    /// </summary>
    [Fact]
    public void Estimate_OfACubicDut_IsCombedUngatedAndFlatOnceGated()
    {
        const double distortion = 0.05;
        var stimulus = DistortionChirp();
        var response = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            response[i] = stimulus[i] + (distortion * stimulus[i] * stimulus[i] * stimulus[i]);

        double expectedDb = FrequencyResponseEstimator.ToDb(1.0 + (0.75 * distortion));

        var ungated = Estimate(stimulus, response);
        var gated = Estimate(stimulus, response, DistortionGate());

        double ungatedRipple = RippleRmsDb(ungated, expectedDb);
        double gatedRipple = RippleRmsDb(gated, expectedDb);

        // Concrete numbers rather than a bare inequality: the point is the size of the effect, and a
        // gate that merely halved the ripple would pass "less than" while fixing nothing.
        Assert.InRange(ungatedRipple, 0.05, 1.0);
        Assert.InRange(gatedRipple, 0.0, 0.005);
        Assert.True(gatedRipple * 20.0 < ungatedRipple, $"gated {gatedRipple:0.0000} dB vs ungated {ungatedRipple:0.0000} dB");
    }

    /// <summary>
    /// The gate costs resolution and nothing else: on a DUT with no distortion at all it must return
    /// what the ungated estimator returns, since there is nothing outside the kept span to discard.
    /// </summary>
    [Fact]
    public void Gating_ALinearDut_LeavesTheResponseWhereItWas()
    {
        const double cornerHz = 1000.0;
        var stimulus = Chirp();
        var response = OnePoleLowPass(stimulus, cornerHz);

        var ungated = Estimate(stimulus, response);
        var gated = Estimate(stimulus, response, ImpulseGate.ForSweep(FftSize, StartHz, EndHz));

        foreach (int bin in BandBins())
        {
            Assert.Equal(
                FrequencyResponseEstimator.ToDb(ungated.Magnitude[bin]),
                FrequencyResponseEstimator.ToDb(gated.Magnitude[bin]),
                0.01);
        }
    }

    /// <summary>
    /// The gate has to find the impulse rather than assume it sits at index 0, because a record that
    /// arrived rotated is exactly the case the whole periodic-chirp scheme is built around.
    /// </summary>
    [Fact]
    public void Gating_ARotatedRecord_FollowsTheImpulseRatherThanTheOrigin()
    {
        const double cornerHz = 1000.0;
        var stimulus = Chirp();
        var response = Rotate(OnePoleLowPass(stimulus, cornerHz), 5000);

        var gated = Estimate(stimulus, response, ImpulseGate.ForSweep(FftSize, StartHz, EndHz));

        foreach (int bin in BandBins())
        {
            double expectedDb = FrequencyResponseEstimator.ToDb(OnePoleMagnitude(bin * BinHz, cornerHz));
            Assert.Equal(expectedDb, FrequencyResponseEstimator.ToDb(gated.Magnitude[bin]), 0.01);
        }
    }

    [Fact]
    public void ForSweep_StopsShortOfTheSecondHarmonicPacket()
    {
        var gate = ImpulseGate.ForSweep(FftSize, StartHz, EndHz);
        double secondHarmonicSamples = FftSize * Math.Log(2.0) / Math.Log(EndHz / StartHz);

        Assert.InRange(gate.PreSamples, 1, (int)secondHarmonicSamples - 1);
        Assert.Equal(FftSize / 2, gate.PostSamples);
    }

    [Theory]
    [InlineData(0, 0, 0)]                 // a zero-length gate keeps nothing
    [InlineData(FftSize, FftSize, 0)]     // longer than the record it gates
    [InlineData(4, 4, 64)]                // tapers that overlap never reach unity
    public void Constructor_RejectsAnImpossibleGate(int pre, int post, int taper) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FrequencyResponseEstimator(FftSize, new MathNetBackend(), new ImpulseGate(pre, post, taper)));

    [Fact]
    public void Complete_WithoutRecords_Throws()
    {
        var estimator = new FrequencyResponseEstimator(FftSize, new MathNetBackend());
        Assert.Throws<InvalidOperationException>(() => estimator.Complete());
    }

    [Fact]
    public void Reset_DiscardsAccumulatedRecords()
    {
        var stimulus = Chirp();
        var estimator = new FrequencyResponseEstimator(FftSize, new MathNetBackend());
        estimator.Add(Spectrum(stimulus), Spectrum(stimulus));
        Assert.Equal(1, estimator.Records);

        estimator.Reset();

        Assert.Equal(0, estimator.Records);
        Assert.Throws<InvalidOperationException>(() => estimator.Complete());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1000)]
    public void Constructor_RejectsANonPowerOfTwoSize(int size) =>
        Assert.Throws<ArgumentException>(() => new FrequencyResponseEstimator(size, new MathNetBackend()));

    // -- helpers ------------------------------------------------------------------------------

    private static double[] Chirp()
    {
        var period = new double[FftSize];
        ExponentialChirp.Fill(period, StartHz, EndHz, SampleRateHz, 1.0);
        return period;
    }

    /// <summary>Bins comfortably inside the swept band, where the stimulus actually has energy.</summary>
    private static IEnumerable<int> BandBins()
    {
        int first = (int)Math.Ceiling(StartHz * 2.0 / BinHz);
        int last = (int)Math.Floor(EndHz / 2.0 / BinHz);
        for (int bin = first; bin <= last; bin += 37)
            yield return bin;
    }

    private static FrequencyResponseResult Estimate(double[] stimulus, double[] response, ImpulseGate? gate = null)
    {
        var estimator = new FrequencyResponseEstimator(FftSize, new MathNetBackend(), gate);
        estimator.Add(Spectrum(stimulus), Spectrum(response));
        return estimator.Complete();
    }

    /// <summary>A sweep whose third harmonic still fits below Nyquist, so nothing aliases back into the record.</summary>
    private static double[] DistortionChirp()
    {
        var period = new double[FftSize];
        ExponentialChirp.Fill(period, DistortionStartHz, DistortionEndHz, SampleRateHz, 1.0);
        return period;
    }

    private static ImpulseGate DistortionGate() =>
        ImpulseGate.ForSweep(FftSize, DistortionStartHz, DistortionEndHz);

    /// <summary>
    /// RMS departure from a flat expected level, over the part of the sweep clear of its own fades —
    /// which is the comb, since the DUT itself is flat.
    /// </summary>
    private static double RippleRmsDb(FrequencyResponseResult result, double expectedDb)
    {
        double sum = 0.0;
        int count = 0;
        for (int bin = (int)Math.Ceiling(DistortionStartHz * 1.5 / BinHz);
             bin <= (int)Math.Floor(DistortionEndHz * 0.85 / BinHz);
             bin++)
        {
            double error = FrequencyResponseEstimator.ToDb(result.Magnitude[bin]) - expectedDb;
            sum += error * error;
            count++;
        }

        return Math.Sqrt(sum / count);
    }

    private static Complex[] Spectrum(double[] samples)
    {
        var spectrum = new Complex[(FftSize / 2) + 1];
        new MathNetBackend().Forward(samples, spectrum);
        return spectrum;
    }

    /// <summary>
    /// A one-pole low pass run to circular steady state — several periods in, last period out — so
    /// the result really is the periodic response and not a startup transient.
    /// </summary>
    private static double[] OnePoleLowPass(double[] period, double cornerHz)
    {
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / SampleRateHz);
        double state = 0.0;

        var output = new double[period.Length];
        for (int pass = 0; pass < 4; pass++)
            for (int i = 0; i < period.Length; i++)
            {
                state += alpha * (period[i] - state);
                output[i] = state;
            }

        return output;
    }

    /// <summary>
    /// The dB the regulariser costs at a bin of the given confidence. H1 divides by
    /// <c>Sxx + lambda</c> rather than <c>Sxx</c>, and with <c>lambda = 1e-4 * max(Sxx)</c> and
    /// <c>confidence = sqrt(Sxx / max(Sxx))</c> that ratio is exactly <c>1e-4 / confidence^2</c>.
    /// </summary>
    private static double PredictedRegulariserBiasDb(double confidence) =>
        20.0 * Math.Log10(1.0 / (1.0 + (FrequencyResponseEstimator.RegularisationFraction
            / (confidence * confidence))));

    /// <summary>|H(f)| of <see cref="OnePoleLowPass"/>'s difference equation, exactly.</summary>
    private static double OnePoleMagnitude(double freqHz, double cornerHz)
    {
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / SampleRateHz);
        double omega = 2.0 * Math.PI * freqHz / SampleRateHz;
        var denominator = 1.0 - ((1.0 - alpha) * Complex.FromPolarCoordinates(1.0, -omega));
        return alpha / denominator.Magnitude;
    }

    private static double[] Rotate(double[] samples, int by)
    {
        var rotated = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            rotated[(i + by) % samples.Length] = samples[i];
        return rotated;
    }

    private static double[] AddNoise(double[] samples, Random random, double sigma)
    {
        var noisy = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            noisy[i] = samples[i] + (sigma * ((random.NextDouble() * 2.0) - 1.0));
        return noisy;
    }
}
