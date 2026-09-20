using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// <c>GET /api/imd-sweep/status</c> has to serialize through the app's own JSON options and parse
/// back whole — the NaN-truncation rule <see cref="ImpedanceStatusJsonTests"/> exists for. The
/// runner tests read the status object directly and would never see a body cut off mid-array.
/// </summary>
public class ImdSweepStatusJsonTests
{
    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            return options.SerializerOptions;
        }
    }

    [Fact]
    public void Status_SerializesThroughTheAppsJsonOptions_AndParsesBack()
    {
        var point = new ImdSweepPoint(
            Channel: 0, GeneratorSineEquivalentDbv: -20.0, ToneLowDbv: -21.94, ToneHighDbv: -33.98,
            SineEquivalentDbv: -19.98, CombinedRmsDbv: -22.26, GainDb: 0.02, PowerWatt: 0.00125,
            ImdPercent: 0.02, OrderPercent: [0.015, 0.013], NoisePercent: 0.001,
            ToneLowHz: 58.59, ToneHighHz: 7001.95, PreviewFft: null, PreviewScope: null);
        var status = new ImdSweepStatus(
            Running: false, ThdSweepPhase.Finished, Index: 1, Total: 1, CurrentLevelVolt: 0.0, Error: null,
            Points: [point], IntermodulationMethod.Smpte, MaxOrder: 5);

        string json = JsonSerializer.Serialize(status, SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal("Finished", root.GetProperty("phase").GetString());
        Assert.Equal("Smpte", root.GetProperty("method").GetString());
        var p = root.GetProperty("points")[0];
        Assert.Equal(0.02, p.GetProperty("imdPercent").GetDouble());
        Assert.Equal(2, p.GetProperty("orderPercent").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("previewFft").ValueKind);
        Assert.Equal(58.59, p.GetProperty("toneLowHz").GetDouble());
    }
}
