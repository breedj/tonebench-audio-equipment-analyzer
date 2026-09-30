using Qa403.App.Services;
using Qa403.Device;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The chirp start an analyzer can actually deliver.
///
/// <para>A QA403 is DC-coupled and has no floor, so every case here must leave it alone — the 2, 5
/// and 8 Hz starts used for transformer work all reach the DUT and raising one would silently change
/// what those measurements mean.</para>
/// </summary>
public class StimulusFloorTests
{
    [Fact]
    public void Clamp_OnAnAnalyzerWithNoFloor_ChangesNothing()
    {
        foreach (double requested in new[] { 0.5, 2.0, 8.0, 1_000.0 })
        {
            (double start, string? warning) = StimulusFloor.Clamp(requested, 0.0);
            Assert.Equal(requested, start);
            Assert.Null(warning);
        }
    }

    /// <summary>The QA403 declares no floor, which is what keeps every existing measurement identical.</summary>
    [Fact]
    public void TheQa403_DeclaresNoFloor()
    {
        IAnalyzerDevice device = new Qa403.Device.Simulation.SimulatedDevice();

        Assert.Equal(0.0, device.MinStimulusFrequencyHz);
    }

    [Fact]
    public void Clamp_AtOrAboveTheFloor_ChangesNothing()
    {
        Assert.Equal((20.0, null), StimulusFloor.Clamp(20.0, 20.0));
        Assert.Equal((50.0, null), StimulusFloor.Clamp(50.0, 20.0));
    }

    /// <summary>
    /// Raised rather than refused: a default carried over from a QA403 project must not become an
    /// error to decode, and the measurement is still good over the band the card can drive.
    /// </summary>
    [Fact]
    public void Clamp_BelowTheFloor_RaisesTheStartAndSaysSo()
    {
        (double start, string? warning) = StimulusFloor.Clamp(2.0, 20.0);

        Assert.Equal(20.0, start);
        Assert.NotNull(warning);

        // Both frequencies, because "the sweep started higher than you asked" is only actionable if
        // it says where.
        Assert.Contains("20", warning);
        Assert.Contains("2 Hz", warning);
    }

    [Fact]
    public void Clamp_WithANonsenseFloor_ChangesNothing()
    {
        Assert.Equal((2.0, null), StimulusFloor.Clamp(2.0, double.NaN));
        Assert.Equal((2.0, null), StimulusFloor.Clamp(2.0, -5.0));
    }

    [Fact]
    public void Join_KeepsBothMessages_AndDropsTheEmptyOnes()
    {
        Assert.Equal("a b", StimulusFloor.Join("a", "b"));
        Assert.Equal("a", StimulusFloor.Join("a", null));
        Assert.Equal("b", StimulusFloor.Join(null, "b"));
        Assert.Null(StimulusFloor.Join(null, null));
        Assert.Equal("a", StimulusFloor.Join("a", "   "));
    }
}
