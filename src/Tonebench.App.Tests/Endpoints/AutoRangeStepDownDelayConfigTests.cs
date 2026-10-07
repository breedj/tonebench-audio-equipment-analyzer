using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Json;
using Tonebench.Data;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The configuration document behind QA403 settings ▸ "Auto-range step-down delay". What is pinned
/// is the contract the stored copy relies on: the round trip through the app's own JSON options, and
/// what counts as a usable value.
///
/// <para>The validity rule matters more here than for most settings, because this one is
/// <i>written into a device</i> at startup rather than read on demand. A NaN that survived into the
/// ranger is a comparison that is false forever — step-down silently off, on every run, with the
/// document putting it back after every restart. <c>ReadAutoRangeStepDownDelayAsync</c> and the
/// endpoint both gate on <see cref="AutoRangeStepDownDelayConfig.IsValid"/> for that reason.</para>
/// </summary>
public class AutoRangeStepDownDelayConfigTests
{
    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            return options.SerializerOptions;
        }
    }

    [Fact]
    public void RoundTrip()
    {
        var config = new AutoRangeStepDownDelayConfig(2500.0);

        string json = JsonSerializer.Serialize(config, SerializerOptions);
        Assert.Contains("\"delayMs\":2500", json);

        var back = JsonSerializer.Deserialize<AutoRangeStepDownDelayConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.Equal(config, back);
    }

    /// <summary>
    /// A document written by a build that did not have this field — the upgrade case, and the one
    /// every existing bench takes: an empty object is the ranger's own default rather than a throw
    /// or a zero, zero meaning "step down on the first under-range block".
    /// </summary>
    [Fact]
    public void AnEmptyDocumentIsTheDefault()
    {
        var back = JsonSerializer.Deserialize<AutoRangeStepDownDelayConfig>("{}", SerializerOptions);

        Assert.NotNull(back);
        Assert.Equal(1000.0, back.DelayMs);
        Assert.True(back.IsValid);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1000.0)]
    [InlineData(60_000.0)]
    public void AUsableDelayIsValid(double ms) => Assert.True(new AutoRangeStepDownDelayConfig(ms).IsValid);

    /// <summary>
    /// Zero is deliberately *in* range above: "step down as soon as the peak allows" is a setting
    /// someone may want, and the ranger's own level hysteresis still separates the two thresholds.
    /// Negative is not a faster version of that, it is a typo.
    /// </summary>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(60_001.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableDelayIsRejected(double ms) => Assert.False(new AutoRangeStepDownDelayConfig(ms).IsValid);
}
