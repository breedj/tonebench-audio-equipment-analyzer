using System.Reflection;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The wire request maps into <see cref="SteppedResponseRequest"/> field by field, which is a shape
/// that fails silently: a field left out of the mapping is not an error, it is a default, and the
/// run returns a plausible measurement of settings the user did not ask for. That is exactly what
/// happened to the chirp measurement's <c>ChirpStartHz</c> — three runs at 2, 5 and 8 Hz came back
/// identical to 0.05 dB.
/// </summary>
public class SteppedResponseStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        // Every value deliberately different from the record's own default, so a field that is not
        // mapped shows up as the default rather than passing by coincidence.
        var wire = new SteppedResponseStartRequest(
            StartFreqHz: 15.0,
            EndFreqHz: 33_000.0,
            StepsPerOctave: 9,
            LevelMode: "OutputPower",
            LevelValue: 2.5,
            LevelUnit: "Watt",
            LoadOhms: 4.0,
            GenerateLeft: false,
            GenerateRight: true,
            AverageCount: 7,
            RightChannelIsReference: true,
            AdaptiveResolution: false,
            FftSize: 65_536,
            Window: "FlatTop",
            SampleRateHz: 96_000.0);

        var measurement = wire.ToMeasurement(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Watt, WindowType.FlatTop);

        Assert.Equal(15.0, measurement.StartFreqHz);
        Assert.Equal(33_000.0, measurement.EndFreqHz);
        Assert.Equal(9, measurement.StepsPerOctave);
        Assert.Equal(ThdSweepLevelMode.OutputPower, measurement.LevelMode);
        Assert.Equal(2.5, measurement.LevelValue);
        Assert.Equal(ThdSweepLevelUnit.Watt, measurement.LevelUnit);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.False(measurement.GenerateLeft);
        Assert.True(measurement.GenerateRight);
        Assert.Equal(65_536, measurement.FftSize);
        Assert.Equal(WindowType.FlatTop, measurement.Window);
        Assert.Equal(7, measurement.AverageCount);
        Assert.Equal(96_000.0, measurement.SampleRateHz);
        Assert.False(measurement.AdaptiveResolution);
        Assert.True(measurement.RightChannelIsReference);
    }

    /// <summary>
    /// Fails the moment a parameter is added to either record, because the assertions above cannot
    /// know about a field nobody told them about. Deliberately blunt: the alternative is another
    /// evening spent measuring a setting that never left the browser.
    /// </summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(15, ConstructorParameterCount(typeof(SteppedResponseStartRequest)));
        Assert.Equal(15, ConstructorParameterCount(typeof(SteppedResponseRequest)));
    }

    /// <summary>
    /// The saved parameters are the third place a setting has to reach. A field the run used but
    /// <c>params_json</c> never recorded leaves a stored result that cannot say what produced it —
    /// which is the same failure as dropping it on the wire, just discovered later.
    /// </summary>
    [Fact]
    public void TheSavedParametersRecordCarriesEveryRunSetting()
    {
        var runSettings = typeof(SteppedResponseRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!);

        var saved = typeof(Qa403.Data.SteppedResponseParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"SteppedResponseParameters is missing: {string.Join(", ", missing)}");
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
