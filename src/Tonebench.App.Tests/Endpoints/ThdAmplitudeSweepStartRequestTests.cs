using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The amplitude axis' wire request maps into <see cref="ThdSweepRequest"/> the same silent way the
/// frequency one does (see <see cref="ThdSweepStartRequestTests"/>): a field the mapping forgets is
/// a default, and the run then measures a sweep the user did not ask for. The THD stop threshold
/// was the first optional setting to arrive here, and an unmapped threshold reads as "sweep to the
/// end" -- exactly the behaviour it was added to change.
/// </summary>
public class ThdAmplitudeSweepStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        var wire = new ThdAmplitudeSweepStartRequest(
            FixedFreqHz: 2_000.0,
            StartAmplitudeVolt: 0.02,
            EndAmplitudeVolt: 3.0,
            StepsPerOctave: 5,
            LoadOhms: 4.0,
            ProcessLeft: false,
            ProcessRight: true,
            AverageCount: 3,
            MeasureNoiseFloor: false,
            StopThdPercent: 0.75);

        var measurement = wire.ToMeasurement();

        Assert.Equal(2_000.0, measurement.FixedFreqHz);
        Assert.Equal(2_000.0, measurement.StartFreqHz);
        Assert.Equal(2_000.0, measurement.EndFreqHz);
        Assert.Equal(0.02, measurement.StartAmplitudeVolt);
        Assert.Equal(3.0, measurement.EndAmplitudeVolt);
        Assert.Equal(5, measurement.StepsPerOctave);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.False(measurement.ProcessLeft);
        Assert.True(measurement.ProcessRight);
        Assert.Equal(3, measurement.AverageCount);
        Assert.False(measurement.MeasureNoiseFloor);
        Assert.Equal(0.75, measurement.StopThdPercent);

        // What the axis fixes rather than asks for.
        Assert.Equal(SweepAxis.Amplitude, measurement.Axis);
        Assert.Equal(ThdSweepLevelMode.GeneratorVoltage, measurement.LevelMode);
        Assert.Equal(ThdSweepRequest.AmplitudeSweepFftSize, measurement.FftSize);
        Assert.Equal(ThdSweepRequest.AmplitudeSweepSampleRateHz, measurement.SampleRateHz);
        Assert.Equal(WindowType.Hann, measurement.Window);
        Assert.False(measurement.AdaptiveResolution);
        Assert.Equal(ThdMethod.Conventional, measurement.ThdMethod);
    }

    [Fact]
    public void ToMeasurement_WithoutAThreshold_LeavesItNull()
    {
        var wire = new ThdAmplitudeSweepStartRequest(1_000.0, 0.001, 1.0, 2, 8.0, true, true, 1);

        Assert.Null(wire.ToMeasurement().StopThdPercent);
    }

    /// <summary>Fails the moment a parameter is added to the wire record, so the test above learns about it.</summary>
    [Fact]
    public void TheWireRecordStillHasTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(10, ConstructorParameterCount(typeof(ThdAmplitudeSweepStartRequest)));
    }

    /// <summary>
    /// The saved parameters record has to carry every amplitude-axis setting too, or a stored
    /// result cannot say whether its sweep was cut short on purpose.
    /// </summary>
    [Fact]
    public void TheSavedParametersRecordCarriesEveryAmplitudeSweepSetting()
    {
        string[] fixedOrFrequencyOnly =
        [
            nameof(ThdSweepRequest.StartFreqHz), nameof(ThdSweepRequest.EndFreqHz),
            nameof(ThdSweepRequest.LevelMode), nameof(ThdSweepRequest.LevelValue), nameof(ThdSweepRequest.LevelUnit),
            nameof(ThdSweepRequest.AdaptiveResolution), nameof(ThdSweepRequest.Axis), nameof(ThdSweepRequest.ThdMethod),
        ];

        var runSettings = typeof(ThdSweepRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .Where(name => !fixedOrFrequencyOnly.Contains(name));

        var saved = typeof(Tonebench.Data.ThdVsAmplitudeParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"ThdVsAmplitudeParameters is missing: {string.Join(", ", missing)}");
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
