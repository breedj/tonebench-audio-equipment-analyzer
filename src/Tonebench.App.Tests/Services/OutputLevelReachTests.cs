using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The 80 % rule a level probe stops on — see <see cref="OutputLevelReach"/>. The property worth
/// pinning is that "80 %" is read in the <i>target's own quantity</i>: 80 % of a power is 0.97 dB
/// down and 80 % of a voltage 1.94 dB, and a run judged in the wrong one is judged against a limit
/// twice or half the size of the one the user was promised.
/// </summary>
public class OutputLevelReachTests
{
    [Theory]
    [InlineData(ThdSweepLevelUnit.Watt, 0.9691)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, 0.9691)]
    [InlineData(ThdSweepLevelUnit.Volt, 1.9382)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 1.9382)]
    [InlineData(ThdSweepLevelUnit.Dbv, 1.9382)]
    public void TheLimitIsEightyPercentOfWhateverTheUnitMeasures(ThdSweepLevelUnit unit, double expectedDb)
    {
        Assert.Equal(expectedDb, OutputLevelReach.ShortfallLimitDb(unit), 3);
    }

    [Fact]
    public void AReadingAtExactlyEightyPercentCounts_AndOneBelowItDoesNot()
    {
        // 1 W into 8 Ω is 9.03 dBV; 0.8 W is 8.06 dBV.
        double target = ThdSweepLevelUnit.Watt.ToDbv(1.0, 8.0);
        double eightyPercent = ThdSweepLevelUnit.Watt.ToDbv(0.8, 8.0);

        Assert.True(OutputLevelReach.Reached(target, eightyPercent + 0.001, ThdSweepLevelUnit.Watt));
        Assert.False(OutputLevelReach.Reached(target, eightyPercent - 0.01, ThdSweepLevelUnit.Watt));

        // The same figures as a voltage target are comfortably inside it — which is the point of
        // asking the unit rather than picking one limit for both.
        Assert.True(OutputLevelReach.Reached(target, eightyPercent - 0.01, ThdSweepLevelUnit.Volt));
    }

    [Fact]
    public void TheProbesOwnConvergenceToleranceIsNeverEnoughToFail()
    {
        // Every probe stops correcting at 0.5 dB, so that reading must count as reached in both
        // quantities or a run that converged normally would be stopped.
        foreach (var unit in new[] { ThdSweepLevelUnit.Watt, ThdSweepLevelUnit.Volt, ThdSweepLevelUnit.Dbv })
            Assert.True(OutputLevelReach.Reached(9.03, 9.03 - 0.5, unit));
    }

    [Fact]
    public void ADutThatIsNotThereIsNowhereNear()
    {
        Assert.False(OutputLevelReach.Reached(9.03, -60.0, ThdSweepLevelUnit.Watt));
        Assert.False(OutputLevelReach.Reached(9.03, double.NaN, ThdSweepLevelUnit.Watt));
    }

    [Fact]
    public void TheMessageQuotesTheUnitTheLevelWasTypedIn()
    {
        // Asked for 1 W into 8 Ω, got 6 dB less — a quarter of the power.
        double target = ThdSweepLevelUnit.Watt.ToDbv(1.0, 8.0);
        string message = OutputLevelReach.Message(target, target - 6.0206, ThdSweepLevelUnit.Watt, 8.0, 18.0);

        Assert.Contains("0.25 W", message);
        Assert.Contains("1 W", message);
        Assert.Contains("25 %", message);
        Assert.Contains("18 dBV ceiling", message);
        Assert.Contains("powered on", message);
    }

    [Fact]
    public void TheMessageSpeaksVoltsForAVoltageTarget()
    {
        double target = ThdSweepLevelUnit.Volt.ToDbv(2.0, 8.0);
        string message = OutputLevelReach.Message(target, target - 6.0206, ThdSweepLevelUnit.Volt, 8.0, 18.0);

        Assert.Contains("1 V of the 2 V", message);
        Assert.Contains("50 %", message);
    }

    [Theory]
    [InlineData(ThdSweepLevelUnit.Watt, 1.0)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, 250.0)]
    [InlineData(ThdSweepLevelUnit.Volt, 2.83)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 775.0)]
    [InlineData(ThdSweepLevelUnit.Dbv, -3.5)]
    public void FromDbvInvertsToDbv(ThdSweepLevelUnit unit, double value)
    {
        double dbv = unit.ToDbv(value, 8.0);
        Assert.Equal(value, OutputLevelReach.FromDbv(dbv, unit, 8.0), 6);
    }
}
