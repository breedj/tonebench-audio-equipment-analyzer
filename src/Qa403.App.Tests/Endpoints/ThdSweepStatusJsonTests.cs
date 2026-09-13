using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Qa403.App.Services;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The sweep status has to survive being written as JSON with the frequency-normalized figures
/// in every state they can be in: absent (a conventional sweep), partly filled in (a normalized
/// sweep in progress, or its reference points at the top) and complete.
///
/// <para>Same reasoning as <see cref="ImpedanceStatusJsonTests"/>: <c>System.Text.Json</c> refuses
/// a NaN <em>and</em> an infinity, and finds out part way through the response, so a point whose
/// figure was "not available" encoded as NaN — or a ratio taken against a fundamental of zero —
/// would truncate <c>GET /api/sweep/status</c> mid-array with a 200 already sent. The runner tests
/// read the status object directly and would never see it.</para>
/// </summary>
public class ThdSweepStatusJsonTests
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

    private static ThdSweepPoint Point(double freqHz, double? nThd, IReadOnlyList<double?>? nHarmonics, double? nD6Plus) =>
        new(freqHz, 0, freqHz, -20.0, 0.0, 0.00125, 0.5, 0.6, 90.0, [0.5, 0.1], 0.01, 0.001, -20.0, null, null,
            nThd, nHarmonics, nD6Plus);

    private static ThdSweepStatus Status(ThdMethod method, params ThdSweepPoint[] points) =>
        new(Running: false, ThdSweepPhase.Finished, points.Length, points.Length, 0.0, -20.0, null, points,
            SweepAxis.Frequency, 0.0, method);

    [Fact]
    public void AConventionalSweepsStatus_WritesTheNormalizedFieldsAsNull()
    {
        string json = JsonSerializer.Serialize(Status(ThdMethod.Conventional, Point(1000.0, null, null, null)), SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var point = root.GetProperty("points")[0];

        Assert.Equal("Conventional", root.GetProperty("thdMethod").GetString());
        Assert.Equal(JsonValueKind.Null, point.GetProperty("normalizedThdPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("normalizedHarmonicPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("normalizedD6PlusPercent").ValueKind);
    }

    [Fact]
    public void APartlyNormalizedPoint_KeepsNullsInsideTheHarmonicList()
    {
        var status = Status(
            ThdMethod.FrequencyNormalized,
            Point(1000.0, 0.51, [0.5, 0.1, null, null], null),
            Point(8000.0, null, [null, null, null, null], null));

        string json = JsonSerializer.Serialize(status, SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var points = root.GetProperty("points");

        Assert.Equal("FrequencyNormalized", root.GetProperty("thdMethod").GetString());

        var first = points[0].GetProperty("normalizedHarmonicPercent");
        Assert.Equal(4, first.GetArrayLength());
        Assert.Equal(0.5, first[0].GetDouble());
        Assert.Equal(JsonValueKind.Null, first[2].ValueKind);
        Assert.Equal(0.51, points[0].GetProperty("normalizedThdPercent").GetDouble());

        var top = points[1].GetProperty("normalizedHarmonicPercent");
        Assert.Equal(4, top.GetArrayLength());
        Assert.All(top.EnumerateArray(), h => Assert.Equal(JsonValueKind.Null, h.ValueKind));
        Assert.Equal(JsonValueKind.Null, points[1].GetProperty("normalizedThdPercent").ValueKind);
    }

    [Fact]
    public void TheJsonWriterRefusesNaNAndInfinity_WhichIsWhyAbsentIsNull()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Serialize(new { v = double.NaN }, SerializerOptions));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Serialize(new { v = double.PositiveInfinity }, SerializerOptions));
    }
}
