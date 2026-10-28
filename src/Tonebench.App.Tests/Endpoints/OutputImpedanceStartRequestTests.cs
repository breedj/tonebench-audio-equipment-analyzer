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
/// The output-impedance wire request maps into <see cref="OutputImpedanceRequest"/> field by field —
/// the shape that fails silently, see <see cref="SteppedResponseStartRequestTests"/>.
/// </summary>
public class OutputImpedanceStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        var wire = new OutputImpedanceStartRequest(
            StartFreqHz: 15.0,
            EndFreqHz: 33_000.0,
            StepsPerOctave: 9,
            LevelMode: "OutputPower",
            LevelValue: 2.5,
            LevelUnit: "Watt",
            LoadOhms: 4.0,
            SecondLoadOhms: 2.0,
            NominalOhms: 6.0,
            RightChannelIsReference: true,
            AverageCount: 7,
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
        Assert.Equal(2.0, measurement.SecondLoadOhms);
        Assert.Equal(6.0, measurement.NominalOhms);
        Assert.True(measurement.RightChannelIsReference);
        Assert.Equal(65_536, measurement.FftSize);
        Assert.Equal(WindowType.FlatTop, measurement.Window);
        Assert.Equal(7, measurement.AverageCount);
        Assert.Equal(96_000.0, measurement.SampleRateHz);
        Assert.False(measurement.AdaptiveResolution);
    }

    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(15, ConstructorParameterCount(typeof(OutputImpedanceStartRequest)));
        Assert.Equal(15, ConstructorParameterCount(typeof(OutputImpedanceRequest)));
    }

    [Fact]
    public void TheSavedParametersRecordCarriesEveryRunSetting()
    {
        var runSettings = typeof(OutputImpedanceRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!);

        var saved = typeof(Tonebench.Data.OutputImpedanceParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"OutputImpedanceParameters is missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// <c>GET /api/output-impedance/status</c> has to serialize through the app's JSON options and
    /// parse back whole — the NaN rule: every reading is null until measured, never NaN. Built from a
    /// real runner's idle status so the record's actual defaults are what is serialized.
    /// </summary>
    [Fact]
    public void Status_SerializesThroughTheAppsJsonOptions_AndParsesBack()
    {
        var options = new JsonOptions();
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(randomSeed: 1), pipeline, NullLogger<DeviceSessionService>.Instance);
        var runner = new OutputImpedanceRunner(session, pipeline, hub, NullLogger<OutputImpedanceRunner>.Instance);

        string json = JsonSerializer.Serialize(runner.Status, options.SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("Idle", root.GetProperty("phase").GetString());
        Assert.False(root.GetProperty("running").GetBoolean());
        foreach (string absent in new[] { "trace", "medianOhms", "zoutAt1kOhms", "dampingFactorAt1k", "firstGainAt1kDb", "secondGainAt1kDb", "inputRangeDbv", "warning", "secondLoadOhms" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty(absent).ValueKind);
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
