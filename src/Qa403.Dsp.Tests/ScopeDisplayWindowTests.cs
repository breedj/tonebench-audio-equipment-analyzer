namespace Qa403.Dsp.Tests;

public class ScopeDisplayWindowTests
{
    [Theory]
    [InlineData(1000.0, 0.001)] // 1 kHz: one period, no step
    [InlineData(4000.0, 0.00025)] // 4 kHz: period is 250 us, above the 200 us step threshold
    public void BelowFiveKHz_ShowsExactlyOnePeriod(double freqHz, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, ScopeDisplayWindow.SecondsFor(freqHz), 9);
    }

    [Fact]
    public void Between5And10kHz_ShowsOneAndAHalfPeriods()
    {
        // 6 kHz: period 166.67 us, in [100 us, 200 us) -> x1.5
        double period = 1.0 / 6000.0;
        Assert.Equal(period * 1.5, ScopeDisplayWindow.SecondsFor(6000.0), 9);
    }

    [Fact]
    public void Between10And20kHz_ShowsTwoPeriods()
    {
        // 15 kHz: period 66.67 us, in [50 us, 100 us) -> x2
        double period = 1.0 / 15000.0;
        Assert.Equal(period * 2.0, ScopeDisplayWindow.SecondsFor(15000.0), 9);
    }

    [Fact]
    public void Above20kHz_ShowsFourPeriods()
    {
        // 25 kHz: period 40 us, strictly below the 50 us cutoff -> x4. Exactly 20 kHz (period
        // exactly 50 us) does NOT clear this boundary -- period < 0.00005 is false there, so it
        // falls through to the x2 branch instead. That is the ported app's own off-by-a-boundary
        // behaviour, not a rounding slip in this port.
        double period = 1.0 / 25000.0;
        Assert.Equal(period * 4.0, ScopeDisplayWindow.SecondsFor(25000.0), 9);
    }

    [Fact]
    public void ExactlyTwentyKHz_FallsIntoTheTwoPeriodBranchNotFour()
    {
        double period = 1.0 / 20000.0;
        Assert.Equal(period * 2.0, ScopeDisplayWindow.SecondsFor(20000.0), 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    public void NonPositiveFrequency_ReturnsZero(double freqHz)
    {
        Assert.Equal(0.0, ScopeDisplayWindow.SecondsFor(freqHz));
    }
}
