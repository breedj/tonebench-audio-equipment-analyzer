namespace Qa403.Dsp.Tests;

public class IntermodulationPresetsTests
{
    [Fact]
    public void ToneLevels_FourToOne_IsMinus1p94AndMinus13p98()
    {
        var (low, high) = IntermodulationPresets.ToneLevelsDbv(0.0, 4.0);

        Assert.Equal(-1.94, low, 2);
        Assert.Equal(-13.98, high, 2);
        // The tutorial's worked example: 31.6 Vrms rated sine → 25.29 and 6.32 Vrms.
        var (lowV, highV) = IntermodulationPresets.ToneLevelsDbv(20.0 * Math.Log10(31.6), 4.0);
        Assert.Equal(25.28, Math.Pow(10.0, lowV / 20.0), 2);
        Assert.Equal(6.32, Math.Pow(10.0, highV / 20.0), 2);
    }

    [Fact]
    public void ToneLevels_OneToOne_IsMinus6p02Each()
    {
        var (low, high) = IntermodulationPresets.ToneLevelsDbv(24.0, 1.0);

        Assert.Equal(24.0 - 6.02, low, 2);
        Assert.Equal(24.0 - 6.02, high, 2);
    }

    [Theory]
    [InlineData(-6.0, 4.0)]
    [InlineData(10.0, 1.0)]
    [InlineData(-30.0, 2.5)]
    public void SineEquivalentDbv_InvertsToneLevels(double sineEquivalentDbv, double ratio)
    {
        var (low, high) = IntermodulationPresets.ToneLevelsDbv(sineEquivalentDbv, ratio);

        Assert.Equal(sineEquivalentDbv, IntermodulationPresets.SineEquivalentDbv(low, high), 9);
    }

    [Fact]
    public void Resolve_EveryPreset_HasTheTutorialsDefaults()
    {
        Assert.Equal(new IntermodulationPresetSpec(IntermodulationPreset.Smpte, 60.0, 7_000.0, 4.0, IntermodulationMethod.Smpte, 5), IntermodulationPresets.Resolve(IntermodulationPreset.Smpte));
        Assert.Equal(new IntermodulationPresetSpec(IntermodulationPreset.Din, 250.0, 8_000.0, 4.0, IntermodulationMethod.Smpte, 5), IntermodulationPresets.Resolve(IntermodulationPreset.Din));
        Assert.Equal(new IntermodulationPresetSpec(IntermodulationPreset.CcifItu, 19_000.0, 20_000.0, 1.0, IntermodulationMethod.Ccif, 3), IntermodulationPresets.Resolve(IntermodulationPreset.CcifItu));
        Assert.Equal(new IntermodulationPresetSpec(IntermodulationPreset.Ccif18p5, 18_500.0, 19_500.0, 1.0, IntermodulationMethod.Ccif, 3), IntermodulationPresets.Resolve(IntermodulationPreset.Ccif18p5));
        Assert.Throws<ArgumentException>(() => IntermodulationPresets.Resolve(IntermodulationPreset.Custom));
        Assert.Equal(5, IntermodulationPresets.DefaultMaxOrder(IntermodulationMethod.Smpte));
        Assert.Equal(3, IntermodulationPresets.DefaultMaxOrder(IntermodulationMethod.Ccif));
    }
}
