using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Json;
using Tonebench.Data;

namespace Tonebench.App.Tests.Endpoints;

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
        var settings = new ScreenshotSettingsConfig(
            SaveText: true, AddDate: true, WidthPx: 1000, AspectRatio: "2:1", Destination: "both", Monochrome: true,
            Captions: [new ScreenshotCaption("Output stage", "Output stage, 8 Ω"), new ScreenshotCaption("FFT analysis", "Hum at 100 Hz")]);

        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        Assert.Contains("\"widthPx\":1000", json);
        Assert.Contains("\"aspectRatio\":\"2:1\"", json);
        Assert.Contains("\"measurement\":\"Output stage\"", json);

        var back = JsonSerializer.Deserialize<ScreenshotSettingsConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);

        // A record's generated equality is reference equality on a list, so the captions are
        // compared by value here rather than through Assert.Equal(settings, back).
        Assert.Equal(settings with { Captions = null }, back with { Captions = null });
        Assert.Equal(settings.Captions, back.Captions);
    }

    [Fact]
    public void ScreenshotSettings_KeepsACaptionForEveryMeasurement()
    {
        // One slot was the fault this replaced: a screenshot of a second measurement overwrote the
        // first measurement's caption, so "kept for that measurement" was true of one at a time.
        var settings = new ScreenshotSettingsConfig(Captions:
        [
            new ScreenshotCaption("Amp 1 kHz", "Before the recap"),
            new ScreenshotCaption("Bode sweep", "Elliptic - Nyquist"),
        ]);

        Assert.True(settings.IsValid);
        Assert.Equal(2, settings.Captions!.Count);
    }

    [Fact]
    public void ScreenshotSettings_RejectsACaptionListThatCouldNotHaveComeFromTheDialog()
    {
        Assert.False(new ScreenshotSettingsConfig(Captions: [new ScreenshotCaption("", "a caption")]).IsValid);
        Assert.False(new ScreenshotSettingsConfig(Captions: [new ScreenshotCaption("Amp", "   ")]).IsValid);
        Assert.False(new ScreenshotSettingsConfig(Captions:
            [new ScreenshotCaption("Amp", new string('x', ScreenshotCaption.MaxLength + 1))]).IsValid);

        // The cap is the backstop for a document that did not come from this client, which caps
        // the list itself.
        var tooMany = Enumerable.Range(0, ScreenshotSettingsConfig.MaxCaptions + 1)
            .Select(i => new ScreenshotCaption($"m{i}", $"t{i}"))
            .ToList();
        Assert.False(new ScreenshotSettingsConfig(Captions: tooMany).IsValid);
        Assert.True(new ScreenshotSettingsConfig(Captions: tooMany.Take(ScreenshotSettingsConfig.MaxCaptions).ToList()).IsValid);
    }

    [Fact]
    public void ScreenshotSettings_AnEmptyDocumentIsTheDefaults()
    {
        var back = JsonSerializer.Deserialize<ScreenshotSettingsConfig>("{}", SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(ScreenshotSettingsConfig.Default, back);
        Assert.Null(back.Captions);
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
