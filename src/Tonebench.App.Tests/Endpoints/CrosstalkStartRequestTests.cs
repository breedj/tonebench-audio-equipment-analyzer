using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The crosstalk wire request maps into <see cref="CrosstalkRequest"/> field by field — the shape
/// that fails silently, see <see cref="SteppedResponseStartRequestTests"/>.
/// </summary>
public class CrosstalkStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        var wire = new CrosstalkStartRequest(
            StartFreqHz: 15.0,
            EndFreqHz: 33_000.0,
            StepsPerOctave: 9,
            Direction: "RightToLeft",
            LevelMode: "OutputPower",
            LevelValue: 2.5,
            LevelUnit: "Watt",
            LoadOhms: 4.0,
            AverageCount: 7,
            AdaptiveResolution: false,
            FftSize: 65_536,
            Window: "FlatTop",
            SampleRateHz: 96_000.0);

        var measurement = wire.ToMeasurement(
            CrosstalkDirection.RightToLeft, ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Watt, WindowType.FlatTop);

        Assert.Equal(15.0, measurement.StartFreqHz);
        Assert.Equal(33_000.0, measurement.EndFreqHz);
        Assert.Equal(9, measurement.StepsPerOctave);
        Assert.Equal(CrosstalkDirection.RightToLeft, measurement.Direction);
        Assert.Equal(ThdSweepLevelMode.OutputPower, measurement.LevelMode);
        Assert.Equal(2.5, measurement.LevelValue);
        Assert.Equal(ThdSweepLevelUnit.Watt, measurement.LevelUnit);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.Equal(65_536, measurement.FftSize);
        Assert.Equal(WindowType.FlatTop, measurement.Window);
        Assert.Equal(7, measurement.AverageCount);
        Assert.Equal(96_000.0, measurement.SampleRateHz);
        Assert.False(measurement.AdaptiveResolution);
    }

    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(13, ConstructorParameterCount(typeof(CrosstalkStartRequest)));
        Assert.Equal(13, ConstructorParameterCount(typeof(CrosstalkRequest)));
    }

    [Fact]
    public void TheSavedParametersRecordCarriesEveryRunSetting()
    {
        var runSettings = typeof(CrosstalkRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!);

        var saved = typeof(Tonebench.Data.CrosstalkParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"CrosstalkParameters is missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// <c>GET /api/crosstalk/status</c> is a <see cref="FrequencyResponseStatus"/>; it has to serialize
    /// through the app's JSON options and parse back whole — the NaN rule. Built from a real runner's
    /// idle status rather than by hand, so the record's actual defaults are what is serialized.
    /// </summary>
    [Fact]
    public void Status_SerializesThroughTheAppsJsonOptions_AndParsesBack()
    {
        var options = new JsonOptions();
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(randomSeed: 1), pipeline, NullLogger<DeviceSessionService>.Instance);
        var runner = new CrosstalkRunner(session, pipeline, hub, NullLogger<CrosstalkRunner>.Instance);

        string json = JsonSerializer.Serialize(runner.Status, options.SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("Idle", root.GetProperty("phase").GetString());
        Assert.Equal("None", root.GetProperty("phaseKind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latencySamples").ValueKind);
        Assert.Equal(0, root.GetProperty("traces").GetArrayLength());
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
