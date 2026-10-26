using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

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
    /// <summary>
    /// The depth travels as a time and the server converts it with the record's own rate. The
    /// client cannot: it knows only the live runner's rate, which is 0 before anything has run, so
    /// a stored result opened on a fresh start asked for one sample and came back flat.
    /// </summary>
    [Fact]
    public void CsdRequest_WithASpanInMilliseconds_ConvertsItAtTheRecordsOwnRate()
    {
        var wire = Wire() with { SpanMs = 2.46, SpanSamples = 1 };

        Assert.Equal(472, wire.SpanSamplesAt(192_000));
        Assert.Equal(118, wire.SpanSamplesAt(48_000));
    }

    /// <summary>One request serves every stacked result, and they need not share a rate.</summary>
    [Fact]
    public void CsdRequest_WithASpanInMilliseconds_AnswersEachRateOnItsOwn()
    {
        var wire = Wire() with { SpanMs = 10.0 };

        Assert.NotEqual(wire.SpanSamplesAt(48_000), wire.SpanSamplesAt(192_000));
    }

    /// <summary>A build that sends no time, and a record whose rate is not known, keep the count.</summary>
    [Fact]
    public void CsdRequest_WithNoSpanInMilliseconds_KeepsTheSampleCount()
    {
        Assert.Equal(37, Wire().SpanSamplesAt(192_000));
        Assert.Equal(37, (Wire() with { SpanMs = 2.46 }).SpanSamplesAt(0));
    }

    /// <summary>A span too short to reach a whole sample is still a slice, never a refusal.</summary>
    [Fact]
    public void CsdRequest_WithASpanShorterThanASample_IsStillOneSample()
    {
        Assert.Equal(1, (Wire() with { SpanMs = 0.0001 }).SpanSamplesAt(48_000));
    }

    static ImpulseCsdRequest Wire() => new(
        FftLength: 2048,
        SpanSamples: 37,
        BlockCount: 41,
        WindowMode: "Hann",
        RiseTimeMs: 0.35,
        LowHz: 123.0,
        HighHz: 17_000.0,
        DisplayColumns: 321,
        StartBeforePeakMs: 1.25);

    [Fact]
    public void CsdRequest_ToSettings_CarriesEveryFieldAcross()
    {
        var wire = new ImpulseCsdRequest(
            FftLength: 2048,
            SpanSamples: 37,
            BlockCount: 41,
            WindowMode: "Hann",
            RiseTimeMs: 0.35,
            LowHz: 123.0,
            HighHz: 17_000.0,
            DisplayColumns: 321,
            StartBeforePeakMs: 1.25);

        var settings = wire.ToSettings(CsdWindowMode.Hann, sampleRateHz: 0);

        Assert.Equal(2048, settings.FftLength);
        Assert.Equal(37, settings.SpanSamples);
        Assert.Equal(41, settings.BlockCount);
        Assert.Equal(CsdWindowMode.Hann, settings.WindowMode);
        Assert.Equal(0.35, settings.RiseTimeMs);
        Assert.Equal(123.0, settings.LowHz);
        Assert.Equal(17_000.0, settings.HighHz);
        Assert.Equal(321, settings.DisplayColumns);
        Assert.Equal(1.25, settings.StartBeforePeakMs);
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
        // Ten against the settings' nine: SpanMs is the wire's alone, since the settings record
        // takes the depth already converted at the record's own rate. See ImpulseCsdRequest.SpanMs.
        Assert.Equal(10, ConstructorParameterCount(typeof(ImpulseCsdRequest)));
        Assert.Equal(9, ConstructorParameterCount(typeof(CsdSettings)));
        Assert.Equal(6, ConstructorParameterCount(typeof(ImpulseBurstDecayRequest)));
        Assert.Equal(6, ConstructorParameterCount(typeof(BurstDecaySettings)));
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
