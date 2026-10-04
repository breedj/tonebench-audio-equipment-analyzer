using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The input-range ladder, shared by the sweep runner, the frequency-response runner and — by
/// arithmetic, not by code — the device's own <c>Qa40xAutoRanger</c>. These pin the ladder itself,
/// which is what the three have to agree on.
/// </summary>
public class InputRangeSelectorTests
{
    /// <summary>The QA403's own steps, as <c>DeviceCapabilities.InputRangesDbv</c> reports them.</summary>
    private static readonly double[] Ladder = [0.0, 6.0, 12.0, 18.0, 24.0, 30.0, 36.0, 42.0];

    [Theory]
    // Straight from the previous app's DetermineAttenuation table: level + 5 dB, rounded up the ladder.
    [InlineData(-10.0, 0.0)]
    [InlineData(-5.0, 0.0)]
    [InlineData(-4.9, 6.0)]
    [InlineData(0.0, 6.0)]
    [InlineData(1.0, 6.0)]
    [InlineData(7.0, 12.0)]
    [InlineData(13.0, 18.0)]
    [InlineData(19.0, 24.0)]
    [InlineData(25.0, 30.0)]
    [InlineData(31.0, 36.0)]
    [InlineData(37.0, 42.0)]
    public void For_KeepsFiveDbOfHeadroomAboveTheLevel(double levelDbv, double expectedRangeDbv) =>
        Assert.Equal(expectedRangeDbv, InputRangeSelector.For(Ladder, levelDbv));

    [Fact]
    public void For_ALevelPastTheTopOfTheLadder_PicksTheWidestRange()
    {
        // Better clipped on the widest range than clipped on the most sensitive one.
        Assert.Equal(42.0, InputRangeSelector.For(Ladder, 100.0));
    }

    [Fact]
    public void For_NoSignal_PicksTheWidestRange()
    {
        // NaN means the probe measured nothing. Choosing the most sensitive range there would clip
        // the instant something did arrive.
        Assert.Equal(42.0, InputRangeSelector.For(Ladder, double.NaN));
    }

    [Fact]
    public void For_Silence_PicksTheMostSensitiveRange()
    {
        Assert.Equal(0.0, InputRangeSelector.For(Ladder, double.NegativeInfinity));
    }

    [Fact]
    public void For_HeadroomMatchesTheDeviceRanger()
    {
        // Qa40xAutoRanger.HeadroomDb is internal to the device assembly, so this pins the number
        // rather than the reference. If one moves, they must both move.
        Assert.Equal(5.0, InputRangeSelector.HeadroomDb);
    }

    [Fact]
    public void For_RejectsAnEmptyLadder() =>
        Assert.Throws<ArgumentException>(() => InputRangeSelector.For([], 0.0));
}
