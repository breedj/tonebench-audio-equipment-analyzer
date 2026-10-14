using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The IMD sweep's wire request maps into <see cref="ImdSweepRequest"/> field by field — the
/// shape that fails silently (see <see cref="ThdSweepStartRequestTests"/>), so every field is
/// checked, the parameter counts are pinned, and every run setting is shown to reach the stored
/// parameters record.
/// </summary>
public class ImdSweepStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        // Every value deliberately different from the record's own default.
        var wire = new ImdSweepStartRequest(
            Preset: "Din",
            ToneLowHz: 250.0,
            ToneHighHz: 8000.0,
            LowToHighRatio: 3.5,
            Method: "Ccif",
            MaxOrder: 7,
            StartLevelVolt: 0.02,
            EndLevelVolt: 3.0,
            StepsPerOctave: 4,
            LoadOhms: 4.0,
            ProcessLeft: false,
            ProcessRight: true,
            AverageCount: 6,
            MeasureNoiseFloor: false,
            MaxProductFreqHz: 30_000.0,
            MeasurementLoFreqHz: 10.0,
            MeasurementHiFreqHz: 25_000.0);

        var measurement = wire.ToMeasurement(IntermodulationPreset.Din, IntermodulationMethod.Ccif);

        Assert.Equal(IntermodulationPreset.Din, measurement.Preset);
        Assert.Equal(250.0, measurement.ToneLowHz);
        Assert.Equal(8000.0, measurement.ToneHighHz);
        Assert.Equal(3.5, measurement.LowToHighRatio);
        Assert.Equal(IntermodulationMethod.Ccif, measurement.Method);
        Assert.Equal(7, measurement.MaxOrder);
        Assert.Equal(0.02, measurement.StartLevelVolt);
        Assert.Equal(3.0, measurement.EndLevelVolt);
        Assert.Equal(4, measurement.StepsPerOctave);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.False(measurement.ProcessLeft);
        Assert.True(measurement.ProcessRight);
        Assert.Equal(6, measurement.AverageCount);
        Assert.False(measurement.MeasureNoiseFloor);
        Assert.Equal(30_000.0, measurement.MaxProductFreqHz);
        // The band is this sweep's own, and is not the product limit above it.
        Assert.Equal(10.0, measurement.MeasurementLoFreqHz);
        Assert.Equal(25_000.0, measurement.MeasurementHiFreqHz);

        // The endpoint never sets the analysis preset; it stays fixed.
        Assert.Equal(ThdSweepRequest.AmplitudeSweepFftSize, measurement.FftSize);
        Assert.Equal(ThdSweepRequest.AmplitudeSweepSampleRateHz, measurement.SampleRateHz);
    }

    /// <summary>Fails the moment a parameter is added to either record, because the assertions above cannot know about it.</summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(17, ConstructorParameterCount(typeof(ImdSweepStartRequest)));
        Assert.Equal(19, ConstructorParameterCount(typeof(ImdSweepRequest)));
    }

    /// <summary>A field the run used but params_json never recorded leaves a stored result that cannot say what produced it.</summary>
    [Fact]
    public void EveryRunSettingReachesTheStoredParameters()
    {
        var runSettings = typeof(ImdSweepRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!);

        var saved = typeof(Tonebench.Data.ImdVsAmplitudeParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"ImdVsAmplitudeParameters is missing: {string.Join(", ", missing)}");
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
