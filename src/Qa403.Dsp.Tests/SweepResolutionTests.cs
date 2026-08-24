namespace Qa403.Dsp.Tests;

public class SweepResolutionTests
{
    [Fact]
    public void FftSizeFor_PutsTheFundamentalAtLeastTheTargetBinsUp()
    {
        // The property the whole adaptive mode rests on: whatever size comes back, the tone must
        // still be far enough up the spectrum for the harmonic analyzer to work with.
        foreach (double freqHz in new[] { 20.0, 47.0, 100.0, 440.0, 1000.0, 5000.0, 19000.0 })
        {
            int n = SweepResolution.FftSizeFor(freqHz, 192000.0);
            double bins = freqHz * n / 192000.0;

            // The ceiling can legitimately stop it short at the bottom of a sweep — that case is
            // covered separately below.
            if (n < SweepResolution.MaxFftSize)
                Assert.True(bins >= SweepResolution.TargetFundamentalBins, $"{freqHz} Hz landed on bin {bins:0.#} at N={n}");
        }
    }

    [Fact]
    public void FftSizeFor_BinIndexAndCycleCountAreTheSameNumber()
    {
        // Not a coincidence worth re-deriving each time someone reads the code: bin index is
        // f/(fs/N) and cycles-in-record is f*(N/fs). SweepResolution's single target constant is
        // only defensible because these are identical.
        const double freqHz = 1000.0;
        const double sampleRateHz = 96000.0;
        int n = SweepResolution.FftSizeFor(freqHz, sampleRateHz);

        double binIndex = freqHz / (sampleRateHz / n);
        double cycles = freqHz * (n / sampleRateHz);

        Assert.Equal(binIndex, cycles, 9);
    }

    [Fact]
    public void FftSizeFor_ReturnsPowersOfTwoWithinTheConfiguredBounds()
    {
        foreach (double freqHz in new[] { 1.0, 20.0, 1000.0, 90000.0 })
        {
            int n = SweepResolution.FftSizeFor(freqHz, 192000.0);

            var (floor, ceiling) = SweepResolution.BoundsFor(192000.0);
            Assert.InRange(n, floor, ceiling);
            Assert.Equal(0, n & (n - 1));
        }
    }

    [Fact]
    public void FftSizeFor_ShrinksWithFrequency_WhichIsWhereTheSpeedUpComesFrom()
    {
        int low = SweepResolution.FftSizeFor(50.0, 192000.0);
        int mid = SweepResolution.FftSizeFor(1000.0, 192000.0);
        int high = SweepResolution.FftSizeFor(10000.0, 192000.0);

        Assert.True(low > mid, $"50 Hz ({low}) should need a longer record than 1 kHz ({mid})");
        Assert.True(mid > high, $"1 kHz ({mid}) should need a longer record than 10 kHz ({high})");
    }

    [Fact]
    public void FftSizeFor_ClampsAtTheCeilingRatherThanGrowingWithoutBound()
    {
        // 1 Hz at 192 kHz would want 64 * 192000 = 12.3M points; the ceiling is what stops a sweep
        // from asking for a record measured in minutes.
        Assert.Equal(SweepResolution.MaxFftSize, SweepResolution.FftSizeFor(1.0, 192000.0));
    }

    [Fact]
    public void FftSizeFor_ClampedPointStillClearsTheAnalyzersOwnMinimum()
    {
        // 20 Hz is the bottom of a normal audio sweep and clamps at the ceiling, so it gets fewer
        // cycles than the target asks for. It must still clear the 5-bin floor below which the
        // analyzer sums the fundamental into its own H2 (ThdSweepRequest.MinFundamentalBins).
        int n = SweepResolution.FftSizeFor(20.0, 192000.0);
        double bins = 20.0 * n / 192000.0;

        Assert.Equal(SweepResolution.MaxFftSize, n);
        Assert.True(bins >= 5.0, $"20 Hz landed on bin {bins:0.#}, below the analyzer's minimum");
    }

    [Fact]
    public void BoundsFor_HoldsRecordLengthAndBinWidthConstantAcrossSampleRates()
    {
        // The fix for a real regression: with a sample-count ceiling, a 20 Hz - 2 kHz sweep dropped
        // to 48 kHz and its clamped low points got 4x longer, running 112 s against fixed mode's
        // 76 s. Both bounds are now expressed in time and resolution, so they mean the same thing
        // at every rate.
        foreach (double sampleRateHz in SweepResolution.SupportedSampleRatesHz)
        {
            var (floor, ceiling) = SweepResolution.BoundsFor(sampleRateHz);

            Assert.Equal(SweepResolution.MaxRecordSeconds, ceiling / sampleRateHz, 3);
            Assert.Equal(SweepResolution.MaxBinWidthHz, sampleRateHz / floor, 3);
        }
    }

    [Fact]
    public void FftSizeFor_ClampedLowPoint_TakesTheSameTimeAtEverySampleRate()
    {
        // 20 Hz clamps at the ceiling everywhere, so its record length is the ceiling's -- and that
        // is now identical across rates, which is what makes SampleRateFor speed-neutral in practice
        // and not just in principle.
        double reference = SweepResolution.FftSizeFor(20.0, 192000.0) / 192000.0;

        foreach (double sampleRateHz in SweepResolution.SupportedSampleRatesHz)
            Assert.Equal(reference, SweepResolution.FftSizeFor(20.0, sampleRateHz) / sampleRateHz, 3);
    }

    [Fact]
    public void FftSizeFor_RecordLengthDoesNotDependOnSampleRate()
    {
        // The reason the adaptive sample rate is not a speed knob: N scales with fs, so the record
        // length (targetBins / f) cancels it out entirely.
        const double freqHz = 1000.0;

        double at48k = SweepResolution.FftSizeFor(freqHz, 48000.0) / 48000.0;
        double at192k = SweepResolution.FftSizeFor(freqHz, 192000.0) / 192000.0;

        Assert.Equal(at48k, at192k, 9);
    }

    [Theory]
    // 2 kHz x 5 harmonics = 10 kHz, inside 48 kHz's usable 23.5 kHz.
    [InlineData(2000.0, 48000.0)]
    // 5 kHz x 5 = 25 kHz, past 48 kHz's usable band but inside 96 kHz's 47 kHz.
    [InlineData(5000.0, 96000.0)]
    // 20 kHz x 5 = 100 kHz, past every supported rate — falls back to the highest.
    [InlineData(20000.0, 192000.0)]
    public void SampleRateFor_PicksTheLowestRateThatKeepsTheHarmonicsInBand(double endFreqHz, double expected)
    {
        Assert.Equal(expected, SweepResolution.SampleRateFor(endFreqHz));
    }

    [Fact]
    public void SampleRateFor_NonPositiveEndFrequency_FallsBackToTheHighestRate()
    {
        Assert.Equal(192000.0, SweepResolution.SampleRateFor(0.0));
    }
}
