using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Qa403.App.Json;
using Qa403.Data;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The configuration document behind Configuration ▸ Acquisition ▸ "External gain": its round
/// trip through the app's own JSON options, what an empty or old document reads as, and the bound.
/// </summary>
public class ExternalGainConfigTests
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
        var config = new ExternalGainConfig(LeftDb: -20.0, RightDb: 6.0);

        string json = JsonSerializer.Serialize(config, SerializerOptions);
        Assert.Contains("\"leftDb\":-20", json);
        Assert.Contains("\"rightDb\":6", json);

        var back = JsonSerializer.Deserialize<ExternalGainConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(config, back);
    }

    [Fact]
    public void AnEmptyDocumentIsNothingInThePath()
    {
        var back = JsonSerializer.Deserialize<ExternalGainConfig>("{}", SerializerOptions);
        Assert.NotNull(back);
        Assert.Equal(ExternalGainConfig.Default, back);
        Assert.Equal(0.0, back.LeftDb);
        Assert.Equal(0.0, back.RightDb);
    }

    [Theory]
    [InlineData(60.1, 0.0)]
    [InlineData(0.0, -60.1)]
    [InlineData(double.NaN, 0.0)]
    public void AGainPastTheBound_IsInvalid(double leftDb, double rightDb)
    {
        Assert.False(new ExternalGainConfig(leftDb, rightDb).IsValid);
    }

    [Fact]
    public void TheBoundItselfIsAllowed()
    {
        Assert.True(new ExternalGainConfig(-60.0, 60.0).IsValid);
    }
}
