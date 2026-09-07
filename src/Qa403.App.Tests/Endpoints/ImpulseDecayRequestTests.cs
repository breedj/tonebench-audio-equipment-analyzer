using System.Reflection;
using Qa403.App.Endpoints;
using Qa403.Dsp;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The two decay endpoints map their wire records into the DSP's settings field by field, and that
/// shape fails silently in exactly the way <see cref="ImpulseResponseStartRequestTests"/> describes:
/// a field left out of the mapping is a default rather than an error, and the surface comes back
/// looking entirely plausible while answering a question nobody asked.
///
/// <para>Worse here than there, in one respect. A measurement setting that failed to cross would at
/// least produce a run whose length or level looked wrong; a decay setting produces a picture, and a
/// picture of the wrong analysis is indistinguishable from a picture of the right one.</para>
/// </summary>
public class ImpulseDecayRequestTests
{
    [Fact]
    public void CsdRequest_ToSettings_CarriesEveryFieldAcross()
    {
        var wire = new ImpulseCsdRequest(
            FftLength: 2048,
            BlockShiftSamples: 37,
            BlockCount: 41,
            WindowMode: "Hann",
            RiseTimeMs: 0.35,
            LowHz: 123.0,
            HighHz: 17_000.0,
            DisplayColumns: 321);

        var settings = wire.ToSettings(CsdWindowMode.Hann);

        Assert.Equal(2048, settings.FftLength);
        Assert.Equal(37, settings.BlockShiftSamples);
        Assert.Equal(41, settings.BlockCount);
        Assert.Equal(CsdWindowMode.Hann, settings.WindowMode);
        Assert.Equal(0.35, settings.RiseTimeMs);
        Assert.Equal(123.0, settings.LowHz);
        Assert.Equal(17_000.0, settings.HighHz);
        Assert.Equal(321, settings.DisplayColumns);
    }

    [Fact]
    public void BurstRequest_ToSettings_CarriesEveryFieldAcross()
    {
        var wire = new ImpulseBurstDecayRequest(
            LowHz: 37.0,
            HighHz: 19_000.0,
            MaxPeriods: 27,
            PointsPerPeriod: 4,
            Resolution: "PreferFrequency",
            FrequencyColumns: 213);

        var settings = wire.ToSettings(BurstDecayResolution.PreferFrequency);

        Assert.Equal(37.0, settings.LowHz);
        Assert.Equal(19_000.0, settings.HighHz);
        Assert.Equal(27, settings.MaxPeriods);
        Assert.Equal(4, settings.PointsPerPeriod);
        Assert.Equal(BurstDecayResolution.PreferFrequency, settings.Resolution);
        Assert.Equal(213, settings.FrequencyColumns);
    }

    /// <summary>
    /// Fails the moment either side of either mapping gains a parameter, because the assertions
    /// above cannot know about a field nobody told them about.
    /// </summary>
    [Fact]
    public void BothPairsStillHaveTheFieldsTheMappingTestsCheck()
    {
        Assert.Equal(8, ConstructorParameterCount(typeof(ImpulseCsdRequest)));
        Assert.Equal(8, ConstructorParameterCount(typeof(CsdSettings)));
        Assert.Equal(6, ConstructorParameterCount(typeof(ImpulseBurstDecayRequest)));
        Assert.Equal(6, ConstructorParameterCount(typeof(BurstDecaySettings)));
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
