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

    /// <summary>
    /// The record's primary-constructor default has to be the literal "Db", because a record cannot
    /// name a const declared in its own body — so the two can drift apart with nothing to say so.
    /// This is what says so.
    /// </summary>
    [Fact]
    public void TheDefaultUnit_IsTheOneTheConstNames()
    {
        Assert.Equal(ExternalGainConfig.DefaultUnit, ExternalGainConfig.Default.Unit);
        Assert.Equal(ExternalGainConfig.DefaultUnit, new ExternalGainConfig(-20.0, -20.0).Unit);
    }

    /// <summary>
    /// A document written before the unit existed has no such property, and must read as dB rather
    /// than as an empty string — which <see cref="ExternalGainConfig.IsValid"/> would then reject,
    /// turning every old bench's saved gain into a 400 on the first edit.
    /// </summary>
    [Fact]
    public void ADocumentFromBeforeTheUnitExisted_ReadsAsDb()
    {
        var back = JsonSerializer.Deserialize<ExternalGainConfig>(
            """{"leftDb":-20,"rightDb":-20}""", SerializerOptions);

        Assert.NotNull(back);
        Assert.Equal(ExternalGainConfig.DefaultUnit, back.Unit);
        Assert.True(back.IsValid);
    }

    [Fact]
    public void TheUnitRoundTrips()
    {
        var config = new ExternalGainConfig(-20.0, -20.0, ExternalGainConfig.TimesUnit);

        string json = JsonSerializer.Serialize(config, SerializerOptions);
        Assert.Contains("\"unit\":\"Times\"", json);

        var back = JsonSerializer.Deserialize<ExternalGainConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(config, back);
    }

    /// <summary>
    /// Rejected rather than quietly normalised on the way in, so a typo in a hand-written document
    /// is answered by the endpoint instead of silently becoming dB.
    /// </summary>
    [Theory]
    [InlineData("db")]
    [InlineData("dB")]
    [InlineData("ratio")]
    [InlineData("")]
    public void AUnitThisAppDoesNotKnow_IsInvalid(string unit)
    {
        Assert.False(new ExternalGainConfig(-20.0, -20.0, unit).IsValid);
        Assert.False(ExternalGainConfig.IsKnownUnit(unit));
    }

    /// <summary>But a document already on disk is read leniently, rather than failing to load.</summary>
    [Fact]
    public void NormaliseUnit_FallsBackToDb()
    {
        Assert.Equal(ExternalGainConfig.DefaultUnit, ExternalGainConfig.NormaliseUnit("ratio"));
        Assert.Equal(ExternalGainConfig.DefaultUnit, ExternalGainConfig.NormaliseUnit(null));
        Assert.Equal(ExternalGainConfig.TimesUnit, ExternalGainConfig.NormaliseUnit(ExternalGainConfig.TimesUnit));
    }
}
