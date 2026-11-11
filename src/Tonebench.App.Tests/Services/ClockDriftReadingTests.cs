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
        var verdict = ClockDriftReading.Describe(0.02, spreadPpm: 0.05, Rate, Fft);

        Assert.Equal("Good", verdict.Quality);
        Assert.Contains("one clock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not apply", verdict.Detail);
    }

    [Fact]
    public void ThisBenchsOwnFigure_ReadsAsTwoOrdinaryCrystals()
    {
        // -104.37 ppm, a MOTU M4 against a Sound BlasterX G6, measured 2026-11-11.
        var verdict = ClockDriftReading.Describe(-104.37, spreadPpm: 0.62, Rate, Fft);

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
        // Mean of 40 with a spread of 30 is not a clock, it is a clock settling -- the QA403's own
        // documented 15-30 s re-lock after a rate change looks exactly like this.
        var verdict = ClockDriftReading.Describe(40, spreadPpm: 30, Rate, Fft);

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
        var verdict = ClockDriftReading.Describe(2.0, spreadPpm: 0.5, Rate, Fft);

        Assert.Equal("Good", verdict.Quality);
        Assert.DoesNotContain("still moving", verdict.Headline);
    }

    [Fact]
    public void FarAboveTwoCrystals_PointsAtTheRateRatherThanAtTheCrystal()
    {
        var verdict = ClockDriftReading.Describe(4200, spreadPpm: 1, Rate, Fft);

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
        Assert.Equal(quality, ClockDriftReading.Describe(ppm, spreadPpm: 0.1, Rate, Fft).Quality);
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
}
