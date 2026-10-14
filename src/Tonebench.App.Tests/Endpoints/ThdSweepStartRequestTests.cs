using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The wire request maps into <see cref="ThdSweepRequest"/> field by field, which is a shape that
/// fails silently: a field left out of the mapping is not an error, it is a default, and the run
/// returns a plausible measurement of settings the user did not ask for. The chirp measurement's
/// <c>ChirpStartHz</c> went that way once; the THD method would have been the next candidate, since
/// a sweep that quietly ran conventional looks exactly like one that was asked to.
/// </summary>
public class ThdSweepStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        // Every value deliberately different from the record's own default, so a field that is not
        // mapped shows up as the default rather than passing by coincidence.
        var wire = new ThdSweepStartRequest(
            StartFreqHz: 15.0,
            EndFreqHz: 33_000.0,
            StepsPerOctave: 9,
            LevelMode: "OutputPower",
            LevelValue: 2.5,
            LevelUnit: "Watt",
            LoadOhms: 4.0,
            ProcessLeft: false,
            ProcessRight: true,
            FftSize: 65_536,
            Window: "FlatTop",
            AverageCount: 7,
            SampleRateHz: 96_000.0,
            MeasureNoiseFloor: false,
            AdaptiveResolution: true,
            ThdMethod: "FrequencyNormalized",
            MeasurementLoFreqHz: 10.0,
            MeasurementHiFreqHz: 30_000.0);

        var measurement = wire.ToMeasurement(
            ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Watt, WindowType.FlatTop, ThdMethod.FrequencyNormalized);

        Assert.Equal(15.0, measurement.StartFreqHz);
        Assert.Equal(33_000.0, measurement.EndFreqHz);
        Assert.Equal(9, measurement.StepsPerOctave);
        Assert.Equal(ThdSweepLevelMode.OutputPower, measurement.LevelMode);
        Assert.Equal(2.5, measurement.LevelValue);
        Assert.Equal(ThdSweepLevelUnit.Watt, measurement.LevelUnit);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.False(measurement.ProcessLeft);
        Assert.True(measurement.ProcessRight);
        Assert.Equal(65_536, measurement.FftSize);
        Assert.Equal(WindowType.FlatTop, measurement.Window);
        Assert.Equal(7, measurement.AverageCount);
        Assert.Equal(96_000.0, measurement.SampleRateHz);
        Assert.False(measurement.MeasureNoiseFloor);
        Assert.True(measurement.AdaptiveResolution);
        Assert.Equal(ThdMethod.FrequencyNormalized, measurement.ThdMethod);
        Assert.Equal(10.0, measurement.MeasurementLoFreqHz);
        Assert.Equal(30_000.0, measurement.MeasurementHiFreqHz);

        // The frequency endpoint never sets the amplitude-axis fields; they stay at their defaults.
        Assert.Equal(SweepAxis.Frequency, measurement.Axis);
    }

    /// <summary>
    /// Fails the moment a parameter is added to either record, because the assertions above cannot
    /// know about a field nobody told them about. The runner's request carries five more than the
    /// wire one: the amplitude axis' own settings, which arrive through the other endpoint.
    /// </summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(18, ConstructorParameterCount(typeof(ThdSweepStartRequest)));
        Assert.Equal(23, ConstructorParameterCount(typeof(ThdSweepRequest)));
    }

    /// <summary>
    /// The saved parameters are the third place a setting has to reach. A field the run used but
    /// <c>params_json</c> never recorded leaves a stored result that cannot say what produced it.
    /// The five amplitude-axis settings are exempt: they live on <c>ThdVsAmplitudeParameters</c>.
    /// </summary>
    [Fact]
    public void TheSavedParametersRecordCarriesEveryFrequencySweepSetting()
    {
        string[] amplitudeAxisOnly =
        [
            nameof(ThdSweepRequest.Axis), nameof(ThdSweepRequest.FixedFreqHz),
            nameof(ThdSweepRequest.StartAmplitudeVolt), nameof(ThdSweepRequest.EndAmplitudeVolt),
            nameof(ThdSweepRequest.StopThdPercent),
        ];

        var runSettings = typeof(ThdSweepRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .Where(name => !amplitudeAxisOnly.Contains(name));

        var saved = typeof(Tonebench.Data.ThdVsFrequencyParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"ThdVsFrequencyParameters is missing: {string.Join(", ", missing)}");
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
