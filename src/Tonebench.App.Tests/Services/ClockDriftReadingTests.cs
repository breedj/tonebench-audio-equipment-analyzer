using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The verdict is the whole point of the test — a number nobody can act on is no better than the
/// warning it replaces — so the thresholds and what each one says are pinned here.
/// </summary>
public class ClockDriftReadingTests
{
    private const double Rate = 48000;
    private const int Fft = 32768;

    [Fact]
    public void OneCard_ReadsAsOneClock_AndSaysTheWarningDoesNotApply()
    {
        var verdict = ClockDriftReading.Describe(0.02, sigmaPpm: 0.05, Rate, Fft);

        Assert.Equal("Good", verdict.Quality);
        Assert.Contains("one clock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not apply", verdict.Detail);
    }

    [Fact]
    public void ThisBenchsOwnFigure_ReadsAsTwoOrdinaryCrystals()
    {
        // -104.37 ppm, a MOTU M4 against a Sound BlasterX G6, measured 2026-11-11.
        var verdict = ClockDriftReading.Describe(-104.37, sigmaPpm: 0.62, Rate, Fft);

        Assert.Equal("Good", verdict.Quality);
        Assert.Contains("ordinary crystals", verdict.Headline);
        Assert.Contains("says nothing bad about either card", verdict.Detail);

        // The two statements a reader needs: tone measurements are fine, chirps are not.
        Assert.Contains("unaffected", verdict.Detail);
        Assert.Contains("chirp", verdict.Detail);
    }

    [Fact]
    public void TheSlipItReportsIsTheOneMeasuredByHand()
    {
        // 104.37 ppm over a 32768-sample record at 48 kHz: 0.6827 s, so 71.2 us and 3.42 samples.
        var verdict = ClockDriftReading.Describe(-104.37, 0.62, Rate, Fft);

        Assert.Equal(3.42, verdict.SlipSamplesPerRecord, 2);
        Assert.Equal(71.2, verdict.SlipMicroseconds, 1);

        // And at 20 kHz that slip is most of a turn, which is why a chirp's phase cannot survive it.
        Assert.Equal(513, verdict.PhaseErrorDegAt20k, 0);
    }

    [Fact]
    public void AReadingThatIsStillMoving_IsCalledProvisionalRatherThanQuoted()
    {
        // A middle of 40 whose readings scatter by 30 is not a clock, it is a clock settling --
        // the QA403's own documented 15-30 s re-lock after a rate change looks exactly like this.
        //
        // Keyed on the robust sigma rather than on the raw spread since 2026-11-11: a 418-point
        // run on this bench spread 273 ppm and had a robust sigma of 1.9, the difference being two
        // records out of 418, so on the spread this verdict was decided by whether one bad record
        // happened to land in the run.
        var verdict = ClockDriftReading.Describe(40, sigmaPpm: 30, Rate, Fft);

        Assert.Equal("Fair", verdict.Quality);
        Assert.Contains("still moving", verdict.Headline);
        Assert.Contains("provisional", verdict.Detail);
        Assert.Contains("re-locks", verdict.Detail);
    }

    [Fact]
    public void AStableReadingIsNotCalledUnstableMerelyForBeingSmall()
    {
        // The spread test is relative, so a 2 ppm reading with 0.5 ppm of spread is settled even
        // though the ratio is large -- without the LockedPpm floor this reads as "still moving".
        var verdict = ClockDriftReading.Describe(2.0, sigmaPpm: 0.5, Rate, Fft);

        Assert.Equal("Good", verdict.Quality);
        Assert.DoesNotContain("still moving", verdict.Headline);
    }

    [Fact]
    public void FarAboveTwoCrystals_PointsAtTheRateRatherThanAtTheCrystal()
    {
        var verdict = ClockDriftReading.Describe(4200, sigmaPpm: 1, Rate, Fft);

        Assert.Equal("Poor", verdict.Quality);
        Assert.Contains("larger than two crystals explain", verdict.Headline);
        Assert.Contains("resampler", verdict.Detail);
        // Names the host APIs that always resample, which is the commonest cause and the one the
        // user can fix in a minute.
        Assert.Contains("DirectSound", verdict.Detail);
    }

    [Theory]
    [InlineData(199.0, "Good")]
    [InlineData(201.0, "Poor")]
    public void TheCrystalBoundIsWhereItSaysItIs(double ppm, string quality)
    {
        Assert.Equal(quality, ClockDriftReading.Describe(ppm, sigmaPpm: 0.1, Rate, Fft).Quality);
    }

    [Fact]
    public void TheSignIsKept_BecauseWhichClockIsFasterIsAFactAboutTheBench()
    {
        Assert.Contains("-104", ClockDriftReading.Describe(-104.37, 0.5, Rate, Fft).Headline);
        Assert.Contains("+104", ClockDriftReading.Describe(104.37, 0.5, Rate, Fft).Headline);
    }

    [Fact]
    public void ANegligibleSlip_IsSaidToBeNegligibleRatherThanQuotedToTwoDecimals()
    {
        var locked = ClockDriftReading.Describe(0.001, 0.0005, Rate, Fft);

        Assert.Contains("less than a hundredth of a sample", ClockDriftReading.DescribeChirpCost(locked, 0.68));
    }

    [Fact]
    public void TheChirpCost_NamesTheWayOutAndNotOnlyTheProblem()
    {
        var verdict = ClockDriftReading.Describe(-104.37, 0.62, Rate, Fft);
        string cost = ClockDriftReading.DescribeChirpCost(verdict, Fft / Rate);

        Assert.Contains("right input as a reference", cost);
        Assert.Contains("stepped sine", cost);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARateThatIsNotThere_DoesNotProduceInfinities(double rate)
    {
        var verdict = ClockDriftReading.Describe(100, 1, rate, Fft);

        Assert.True(double.IsFinite(verdict.SlipSamplesPerRecord));
        Assert.True(double.IsFinite(verdict.PhaseErrorDegAt20k));
    }

    /// <summary>
    /// <b>Why the reported figure is a median.</b> Measured on this bench 2026-11-11 over a
    /// 418-reading run: two readings — 0.5 % — came back wild, one at +157 ppm against a true
    /// −104, both from the records with the lowest tone margin of the run. Both survive the
    /// dominance gate, which is set to catch a buried tone rather than a one-off bad record.
    ///
    /// <para>The consequence is how long the test must run. Taking every window of N consecutive
    /// readings from that run and asking how far its estimate lands from the run's own middle: at
    /// N = 30 a mean is out by up to <b>8.6 ppm</b> and a median by <b>1.5 ppm</b>, and the mean
    /// needs more than 120 readings to reach what the median gives with 10.</para>
    /// </summary>
    [Fact]
    public void TheReportedFigureIsAMedian_SoOneWildRecordCannotMoveIt()
    {
        // Twenty good readings around -104, plus the two this bench really produced.
        double[] good = [.. Enumerable.Range(0, 20).Select(i => -104.0 + (i % 5 - 2) * 0.4)];
        double[] withOutliers = [.. good, 156.70, -116.37];

        double clean = ClockDriftRunner.Median(good);
        double dirty = ClockDriftRunner.Median(withOutliers);
        double mean = withOutliers.Average();

        Assert.Equal(clean, dirty, 1);
        Assert.True(Math.Abs(mean - clean) > 8,
            $"the mean should be dragged by the outliers; it moved {Math.Abs(mean - clean):0.0} ppm");
    }

    /// <summary>
    /// And why the sigma beside it is robust: the sample standard deviation of that same run was
    /// <b>12.9 ppm</b> where the robust estimate was <b>1.9</b>, all of the difference being those
    /// two records. The verdict's stability test reads this one, so on the raw figure a settled
    /// bench would have been called "still moving" by one bad record.
    /// </summary>
    [Fact]
    public void TheSigmaBesideItIsRobust_SoOneWildRecordCannotInflateIt()
    {
        double[] good = [.. Enumerable.Range(0, 20).Select(i => -104.0 + (i % 5 - 2) * 0.4)];
        double[] withOutliers = [.. good, 156.70, -116.37];

        double median = ClockDriftRunner.Median(withOutliers);
        double robust = ClockDriftRunner.RobustSigma(withOutliers, median);
        double sampleSd = Math.Sqrt(
            withOutliers.Sum(v => Math.Pow(v - withOutliers.Average(), 2)) / (withOutliers.Length - 1));

        Assert.True(robust < 2.0, $"the robust sigma should describe an ordinary reading; it read {robust:0.00} ppm");
        Assert.True(sampleSd > 10.0, $"the sample sd should not; it read {sampleSd:0.0} ppm");
    }

    [Fact]
    public void TheMedianOfAnEvenNumberOfReadingsIsTheMiddleOfTheTwoMiddleOnes()
    {
        Assert.Equal(-2.5, ClockDriftRunner.Median([-4.0, -3.0, -2.0, -1.0]), 9);
        Assert.Equal(-3.0, ClockDriftRunner.Median([-4.0, -3.0, -2.0]), 9);
    }
}
