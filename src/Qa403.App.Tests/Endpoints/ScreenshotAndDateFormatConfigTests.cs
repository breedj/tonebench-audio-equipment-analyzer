using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Qa403.App.Json;
using Qa403.Data;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The two configuration documents behind the Screenshot dialog and Configuration ▸ General ▸
/// "Date and time format". Both are client-owned vocabularies the server only keeps, so what is
/// pinned here is the contract the client relies on: the round trip through the app's own JSON
/// options, the defaults an empty or old document reads as, and the little validation there is.
/// </summary>
public class ScreenshotAndDateFormatConfigTests
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
    public void ScreenshotSettings_RoundTrip()
    {
        var settings = new ScreenshotSettingsConfig("Output stage, 8 Ω", SaveText: true, AddDate: true, WidthPx: 1000, AspectRatio: "2:1", Destination: "both", Monochrome: true);

        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        Assert.Contains("\"widthPx\":1000", json);
        Assert.Contains("\"aspectRatio\":\"2:1\"", json);

        var back = JsonSerializer.Deserialize<ScreenshotSettingsConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(settings, back);
    }

    [Fact]
    public void ScreenshotSettings_AnEmptyDocumentIsTheDefaults()
    {
        var back = JsonSerializer.Deserialize<ScreenshotSettingsConfig>("{}", SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(ScreenshotSettingsConfig.Default, back);
        Assert.Null(back.Title);
        Assert.Equal("file", back.Destination);
    }

    [Theory]
    [InlineData(499)]
    [InlineData(2001)]
    public void ScreenshotSettings_AWidthOutsideTheOfferedRange_IsInvalid(int widthPx)
    {
        Assert.False(new ScreenshotSettingsConfig(WidthPx: widthPx).IsValid);
    }

    [Fact]
    public void ScreenshotSettings_ABlankAspectRatioOrDestination_IsInvalid()
    {
        Assert.False(new ScreenshotSettingsConfig(AspectRatio: " ").IsValid);
        Assert.False(new ScreenshotSettingsConfig(Destination: "").IsValid);
    }

    [Fact]
    public void DateTimeFormat_RoundTripsAndDefaultsToSystem()
    {
        var back = JsonSerializer.Deserialize<DateTimeFormatConfig>("{}", SerializerOptions);
        Assert.NotNull(back);
        Assert.Equal("system", back.Format);
        Assert.True(back.IsValid);

        string json = JsonSerializer.Serialize(new DateTimeFormatConfig("iso-seconds"), SerializerOptions);
        Assert.Equal("{\"format\":\"iso-seconds\"}", json);
        Assert.False(new DateTimeFormatConfig("").IsValid);
    }
}
