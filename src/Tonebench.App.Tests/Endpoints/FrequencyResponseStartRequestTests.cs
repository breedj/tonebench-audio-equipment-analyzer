using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The wire request maps into <see cref="FrequencyResponseRequest"/> field by field, which is a
/// shape that fails silently: a field left out of the mapping is not an error, it is a default, and
/// the run returns a plausible measurement of settings the user did not ask for.
/// </summary>
public class FrequencyResponseStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        // Every value deliberately different from the record's own default, so a field that is not
        // mapped shows up as the default rather than passing by coincidence.
        var wire = new FrequencyResponseStartRequest(
            FrequencySpanHz: 48_000.0,
            ResolutionMultiplier: 4,
            LevelMode: "GeneratorVoltage",
            LevelValue: -13.5,
            LevelUnit: "Dbv",
            RightChannelIsReference: true,
            SmoothingDenominator: 6,
            AverageCount: 7,
            Continuous: true,
            ChirpStartHz: 8.0,
            LoadOhms: 4.0,
            BandwidthReference: "Peak",
            BandwidthReferenceLowHz: 300.0,
            BandwidthReferenceHighHz: 3_000.0);

        var measurement = wire.ToMeasurement(
            ThdSweepLevelMode.GeneratorVoltage, ThdSweepLevelUnit.Dbv, BandwidthReferenceMode.Peak);

        Assert.Equal(48_000.0, measurement.FrequencySpanHz);
        Assert.Equal(4, measurement.ResolutionMultiplier);
        Assert.Equal(ThdSweepLevelMode.GeneratorVoltage, measurement.LevelMode);
        Assert.Equal(-13.5, measurement.LevelValue);
        Assert.Equal(ThdSweepLevelUnit.Dbv, measurement.LevelUnit);
        Assert.True(measurement.RightChannelIsReference);
        Assert.Equal(6, measurement.SmoothingDenominator);
        Assert.Equal(7, measurement.AverageCount);
        Assert.True(measurement.Continuous);
        Assert.Equal(8.0, measurement.ChirpStartHz);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.Equal(BandwidthReferenceMode.Peak, measurement.BandwidthReference);
        Assert.Equal(300.0, measurement.BandwidthReferenceLowHz);
        Assert.Equal(3_000.0, measurement.BandwidthReferenceHighHz);
    }

    /// <summary>
    /// Fails the moment a parameter is added to either record, because the assertions above cannot
    /// know about a field nobody told them about. Deliberately blunt: the alternative is another
    /// evening spent measuring a setting that never left the browser.
    /// </summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(14, ConstructorParameterCount(typeof(FrequencyResponseStartRequest)));
        Assert.Equal(14, ConstructorParameterCount(typeof(FrequencyResponseRequest)));
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
