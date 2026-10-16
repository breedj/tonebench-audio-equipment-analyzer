using System.Reflection;
using System.Text.Json;
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

        var request = new UpdateMeasurementRequest(
            "renamed", null, [new Tonebench.Data.SpectrumAnnotation(1, "point", "Mains hum", 50.0, -98.2, 0, 0.45, 14.0)]);

        string json = JsonSerializer.Serialize(request, typeof(UpdateMeasurementRequest), options);
        var back = (UpdateMeasurementRequest?)JsonSerializer.Deserialize(json, typeof(UpdateMeasurementRequest), options);

        Assert.Equal(request.ChartAnnotations, back!.ChartAnnotations);
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
