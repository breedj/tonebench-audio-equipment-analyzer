namespace Qa403.Dsp.Tests;

public class ScopeDisplayWindowTests
{
    /// <summary>
    /// The four branches of <see cref="ScopeDisplayWindow.SecondsFor"/>, as (frequency, periods
    /// shown). The window is one period below 5 kHz and opens up as the period shrinks, so a 25 kHz
    /// trace is not two pixels of sawtooth: x1.5 from 100 us, x2 from 50 us, x4 below that.
    ///
    /// <para>One theory rather than a fact per branch, because the branches differ only in the
    /// multiplier and the boundary they sit on -- which is exactly what the rows below say.</para>
    /// </summary>
    [Theory]
    [InlineData(1000.0, 1.0)] // one period, no step
    [InlineData(4000.0, 1.0)] // period 250 us, still above the 200 us step threshold
    [InlineData(6000.0, 1.5)] // period 166.67 us, in [100 us, 200 us)
    [InlineData(15000.0, 2.0)] // period 66.67 us, in [50 us, 100 us)
    // Exactly 50 us, and it stays on x2: `period < 0.00005` is false there, so 20 kHz falls through
    // to the x2 branch rather than clearing into x4. That is the ported app's own
    // off-by-a-boundary behaviour, not a rounding slip in this port.
    [InlineData(20000.0, 2.0)]
    [InlineData(25000.0, 4.0)] // period 40 us, strictly below the 50 us cutoff
    public void SecondsFor_ShowsThePeriodsItsBranchPromises(double freqHz, double periods)
    {
        Assert.Equal(periods / freqHz, ScopeDisplayWindow.SecondsFor(freqHz), 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    public void NonPositiveFrequency_ReturnsZero(double freqHz)
    {
        Assert.Equal(0.0, ScopeDisplayWindow.SecondsFor(freqHz));
    }
}
