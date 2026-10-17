using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tonebench.App.Endpoints;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// <c>PublishTrimmed</c> is on, which disables reflection-based JSON entirely, so every type that
/// crosses the API surface needs a <see cref="JsonSerializableAttribute"/> line on
/// <c>AppJsonContext</c>. A missing one is invisible in a debug build — reflection JSON is still
/// available there — and throws <c>JsonTypeInfo metadata ... was not provided</c> only in a published
/// build, which is the worst possible place to find out.
///
/// <para>That is not hypothetical: three stepped-sine DTOs sat unregistered from the day that
/// measurement shipped until this test was written. Checking the whole surface beats trusting the
/// next person to remember one more line.</para>
/// </summary>
public class AppJsonContextTests
{
    [Fact]
    public void EveryEndpointDtoIsRegisteredForSourceGeneratedJson()
    {
        var contextType = typeof(FrequencyResponseStartRequest).Assembly
            .GetTypes()
            .Single(t => t.Name == "AppJsonContext");

        // Asks the generated context itself rather than reading its attributes. That is the same
        // question the runtime asks on a trimmed build, and it is the only version that gets nested
        // DTOs right: a type reachable from a registered root (a generator tone inside a generator
        // channel, say) gets generated metadata without a line of its own, and an attribute scan
        // would demand one for every last leaf.
        var context = (JsonSerializerContext)contextType
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

        var missing = typeof(FrequencyResponseStartRequest).Assembly
            .GetTypes()
            .Where(IsEndpointDto)
            .Where(t => context.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These endpoint DTOs have no source-generated JSON metadata, so they will throw on a "
                + $"trimmed publish: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The annotation list is the one nested type on this surface that lives in another assembly
    /// (<c>Tonebench.Data</c>), so it has no line of its own and relies entirely on being reachable
    /// from a registered root. Serialized through a resolver that is <i>only</i> the generated
    /// context, which is what a trimmed publish has: missing metadata throws here rather than on a
    /// bench.
    /// </summary>
    [Fact]
    public void TheAnnotationListRoundTripsThroughTheSourceGeneratedResolver()
    {
        var contextType = typeof(FrequencyResponseStartRequest).Assembly
            .GetTypes()
            .Single(t => t.Name == "AppJsonContext");
        var context = (JsonSerializerContext)contextType
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        var options = new JsonSerializerOptions { TypeInfoResolver = context };

        // The annotation row travels as a JsonElement -- opaque on this side on purpose, so nothing
        // here can strip a field -- which is also why this compares its JSON rather than the value:
        // a JsonElement has no equality of its own.
        var row = JsonSerializer.Deserialize<JsonElement>(
            """{"id":1,"style":"point","text":"Mains hum","view":"Spectrum","x":50,"y":-98.2,"xQuantity":"hz","yQuantity":"dbv","channel":0,"labelOffsetX":0.45,"labelOffsetY":14}""");
        var request = new UpdateMeasurementRequest("renamed", null, [row]);

        string json = JsonSerializer.Serialize(request, typeof(UpdateMeasurementRequest), options);
        var back = (UpdateMeasurementRequest?)JsonSerializer.Deserialize(json, typeof(UpdateMeasurementRequest), options);

        var backRow = Assert.Single(back!.ChartAnnotations!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(row.GetRawText()), JsonNode.Parse(backRow.GetRawText())));
    }

    /// <summary>
    /// A record declared in the endpoints namespace — the shape every request and response body in
    /// this app takes. Deliberately narrow: it is the DTOs that cross the wire, not every type an
    /// endpoint file happens to contain.
    /// </summary>
    private static bool IsEndpointDto(Type type) =>
        type is { IsClass: true, IsPublic: true, IsAbstract: false }
        && type.Namespace == "Tonebench.App.Endpoints"
        && type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;
}
