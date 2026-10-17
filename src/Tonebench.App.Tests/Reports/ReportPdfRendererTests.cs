using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Endpoints;
using Tonebench.App.Json;
using Tonebench.App.Reports;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Reports;

/// <summary>
/// The PDF renderer against a document that exercises every section kind. Nothing here checks the
/// layout — QuestPDF owns that — only that the document is accepted, that what comes back is a PDF,
/// and that the two ways a client can send something unrenderable are refused rather than skipped.
/// </summary>
public class ReportPdfRendererTests
{
    /// <summary>A 1×1 white PNG, the smallest image QuestPDF's decoder accepts.</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private static ReportDocument SampleDocument(params ReportSection[] extra) =>
        new(
            Title: "GM70 amplifier",
            Subtitle: "Project report",
            Generated: "18-09-2026 10:15",
            Notes: "First line.\n\nThird line, after a blank.",
            Chapters:
            [
                new ReportChapter(
                    "stepped-ac - V(out)",
                    Meta: [new ReportRow("Measurement", "Frequency response"), new ReportRow("Taken", "18-09-2026 09:58")],
                    Notes: "Measured at the 8 Ω tap.",
                    Sections:
                    [
                        new ReportSection(ReportSectionKind.Figures, Figures:
                        [
                            new ReportFigure("Max. gain", "18.14 dB", "8.073 x"),
                            new ReportFigure("Peak at", "1000.0 Hz"),
                        ]),
                        new ReportSection(ReportSectionKind.Image, Title: "Chart", Png: OnePixelPng, Caption: "Gain vs frequency"),
                        new ReportSection(ReportSectionKind.KeyValues, Title: "Settings",
                            Rows: [new ReportRow("Span", "20 Hz – 20 kHz"), new ReportRow("Level", "−18 dBV")]),
                        new ReportSection(ReportSectionKind.Groups, Title: "Settings", Groups:
                        [
                            new ReportGroup("Acquisition", [new ReportRow("Sample rate", "192 000 Hz")]),
                            new ReportGroup("Analysis", [new ReportRow("Band", "20 Hz – 20 kHz")]),
                        ]),
                        new ReportSection(ReportSectionKind.Table, Title: "Measurements",
                            Headers: ["", "f(low)", "f(high)", "BW"],
                            Cells: [["±1 dB", "23.1 Hz", "18.4 kHz", "18.4 kHz"], ["−3 dB", "11.4 Hz", "44.0 kHz", "44.0 kHz"]]),
                        new ReportSection(ReportSectionKind.Paragraph, Text: "The lower −3 dB corner is clamped to the sweep's start."),
                        .. extra,
                    ],
                    Eyebrow: "Frequency response",
                    Accent: "#0e7490"),
                new ReportChapter("Settings and notes", Meta: [], Notes: null,
                    Sections: [new ReportSection(ReportSectionKind.Callout, Title: "Not measured.", Text: "This chapter carries the settings and notes only.")],
                    Eyebrow: "Impedance", Accent: "#0f766e"),
            ],
            Eyebrow: "Project report",
            Stats: [new ReportRow("Chapters", "2"), new ReportRow("With stored results", "1")],
            Version: "Tonebench 0.2.0");

    [Fact]
    public void RendersEverySectionKind_AndTheBytesAreAPdf()
    {
        byte[] pdf = ReportPdfRenderer.Render(SampleDocument());

        Assert.True(pdf.Length > 1000, $"A two-chapter report came back as {pdf.Length} bytes.");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void AnEmptyDocument_StillRendersATitlePage()
    {
        byte[] pdf = ReportPdfRenderer.Render(new ReportDocument("smoke", null, "now", null, []));
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    }

    /// <summary>
    /// Black and white is the whole document, not only the charts — see the renderer's Palette. A
    /// monochrome document must lay out the same (the greys are substituted where the hues go, and
    /// nothing moves), so the two differ by colour alone and a reader of one is reading the other.
    /// </summary>
    [Fact]
    public void InBlackAndWhite_TheSameDocumentStillRenders_AndKeepsItsShape()
    {
        byte[] colour = ReportPdfRenderer.Render(SampleDocument());
        byte[] mono = ReportPdfRenderer.Render(SampleDocument() with { Monochrome = true });

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(mono, 0, 5));
        // Within a few per cent: the same text, images and boxes, drawn in different colours.
        Assert.InRange(mono.Length, colour.Length * 0.9, colour.Length * 1.1);
    }

    /// <summary>
    /// A chapter's accent arrives as a hex string from the client, which is the one place a typo can
    /// reach the colour parser. An unusable one falls back to the document's own accent rather than
    /// taking the report down.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("teal")]
    [InlineData("#12345")]
    [InlineData("#nothex")]
    public void AnAccentTheRendererCannotParse_FallsBackInsteadOfThrowing(string? accent)
    {
        var doc = new ReportDocument("smoke", null, "now", null,
            [new ReportChapter("c", [], null, [], Eyebrow: "e", Accent: accent)]);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(ReportPdfRenderer.Render(doc), 0, 5));
    }

    /// <summary>
    /// The strings the client formats use ±, −, °, Ω and µ. A font without one of them renders a
    /// placeholder box and nothing complains; QuestPDF's glyph check turns that into an exception,
    /// which is what makes the bundled Lato's coverage a tested fact rather than an assumption.
    /// </summary>
    [Fact]
    public void TheCharactersTheClientSends_AllHaveGlyphsInTheBundledFont()
    {
        bool previous = QuestPDF.Settings.ThrowOnMissingTextGlyphs;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
        try
        {
            var doc = new ReportDocument("±1 dB −3 dB 45.0° 8.000 Ω 47 µF 1/3-octave", null, "now", null, []);
            byte[] pdf = ReportPdfRenderer.Render(doc);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        }
        finally
        {
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = previous;
        }
    }

    [Fact]
    public void AnUnknownSectionKind_IsRejectedNotSkipped()
    {
        var ex = Assert.Throws<ReportDocumentException>(() =>
            ReportPdfRenderer.Render(SampleDocument(new ReportSection("chartt"))));
        Assert.Contains("chartt", ex.Message);
    }

    [Fact]
    public void AnImageThatIsNotAPng_IsRejected()
    {
        Assert.Throws<ReportDocumentException>(() =>
            ReportPdfRenderer.Render(SampleDocument(new ReportSection(ReportSectionKind.Image, Png: [1, 2, 3]))));
        Assert.Throws<ReportDocumentException>(() =>
            ReportPdfRenderer.Render(SampleDocument(new ReportSection(ReportSectionKind.Image, Png: null))));
    }
}

/// <summary>
/// The report document and the settings document through the app's own source-generated JSON — the
/// same options Program.cs installs, which on a trimmed publish are the only ones there are.
/// </summary>
public class ReportJsonTests
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
    public void ThePngBytes_RideAsBase64UnderACamelCaseName_AndRoundTrip()
    {
        var doc = new ReportDocument("t", null, "now", null,
            [new ReportChapter("c", [], null, [new ReportSection(ReportSectionKind.Image, Png: [1, 2, 3])])]);

        string json = JsonSerializer.Serialize(doc, SerializerOptions);
        Assert.Contains("\"png\":\"AQID\"", json);
        Assert.Contains("\"kind\":\"image\"", json);

        var back = JsonSerializer.Deserialize<ReportDocument>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.Equal([1, 2, 3], back.Chapters[0].Sections[0].Png);
    }

    [Fact]
    public void TheClientsShape_DeserializesWithTheOptionalFieldsAbsent()
    {
        const string json = """
            {"title":"GM70","subtitle":null,"generated":"now","notes":null,
             "chapters":[{"title":"FR","meta":[{"label":"Taken","value":"today"}],"notes":null,
                          "sections":[{"kind":"paragraph","text":"hello"},{"kind":"keyValues","rows":[]}]}]}
            """;

        var doc = JsonSerializer.Deserialize<ReportDocument>(json, SerializerOptions);
        Assert.NotNull(doc);
        Assert.Equal("hello", doc.Chapters[0].Sections[0].Text);
        Assert.Null(doc.Chapters[0].Sections[0].Png);
        Assert.Empty(doc.Chapters[0].Sections[1].Rows!);
    }

    [Fact]
    public void ReportSettings_RoundTripThroughTheDictionary()
    {
        var settings = new ReportSettingsConfig(new Dictionary<string, ReportKindSettings>
        {
            ["FrequencyResponse"] = new(["chart", "measurements"]),
        });

        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        Assert.Contains("\"kinds\":{\"FrequencyResponse\":{\"elements\":[\"chart\",\"measurements\"]", json);

        var back = JsonSerializer.Deserialize<ReportSettingsConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(["chart", "measurements"], back.Kinds["FrequencyResponse"].Elements);
    }

    [Fact]
    public void ReportSettings_WithABlankElement_AreInvalid()
    {
        var settings = new ReportSettingsConfig(new Dictionary<string, ReportKindSettings>
        {
            ["FrequencyResponse"] = new(["chart", " "]),
        });
        Assert.False(settings.IsValid);
    }

    /// <summary>
    /// The views and the mode arrived after the first documents were written: an old document has
    /// neither and must still read, and a new one carries both. The mode is one of two words.
    /// </summary>
    [Fact]
    public void ReportSettings_ViewsAndMode_AreOptionalAndValidated()
    {
        var old = JsonSerializer.Deserialize<ReportSettingsConfig>(
            """{"kinds":{"FrequencyResponse":{"elements":["chart"]}}}""", SerializerOptions);
        Assert.NotNull(old);
        Assert.True(old.IsValid);
        Assert.Null(old.Kinds["FrequencyResponse"].Views);
        Assert.Null(old.Kinds["FrequencyResponse"].Mode);

        var full = new ReportSettingsConfig(new Dictionary<string, ReportKindSettings>
        {
            ["ImpulseResponse"] = new(["chart"], ["Impulse", "BurstDecay"], "individual"),
        });
        Assert.True(full.IsValid);
        string json = JsonSerializer.Serialize(full, SerializerOptions);
        Assert.Contains("\"views\":[\"Impulse\",\"BurstDecay\"]", json);
        Assert.Contains("\"mode\":\"individual\"", json);

        Assert.False(new ReportSettingsConfig(new Dictionary<string, ReportKindSettings>
        {
            ["ImpulseResponse"] = new(["chart"], ["Impulse", ""], null),
        }).IsValid);
        Assert.False(new ReportSettingsConfig(new Dictionary<string, ReportKindSettings>
        {
            ["ImpulseResponse"] = new(["chart"], null, "sideways"),
        }).IsValid);
    }

    /// <summary>
    /// Black and white is one flag beside the kinds, not inside each one: a project report spans
    /// kinds. A document written before it existed reads with the flag null (colour).
    /// </summary>
    [Fact]
    public void ReportSettings_ChartsMonochrome_IsOptionalAndRoundTrips()
    {
        var old = JsonSerializer.Deserialize<ReportSettingsConfig>(
            """{"kinds":{}}""", SerializerOptions);
        Assert.NotNull(old);
        Assert.True(old.IsValid);
        Assert.Null(old.ChartsMonochrome);

        var mono = new ReportSettingsConfig(new Dictionary<string, ReportKindSettings>(), ChartsMonochrome: true);
        string json = JsonSerializer.Serialize(mono, SerializerOptions);
        Assert.Contains("\"chartsMonochrome\":true", json);
        var back = JsonSerializer.Deserialize<ReportSettingsConfig>(json, SerializerOptions);
        Assert.True(back!.ChartsMonochrome);
    }
}

/// <summary>
/// How far past the peak a stored IR may be read by a decay analysis — the sweep gate rebuilt from
/// the run settings the row stores, and never past what the row holds.
/// </summary>
public class StoredImpulseDecayBoundTests
{
    private static StoredImpulseTrace Row(int length, int peakIndex) =>
        new("left", 192000.0, peakIndex, 0.5, 1.0, 20.0, "Hann", 48.0, "None", null, null, new float[length]);

    private static ImpulseResponseParameters Parameters(int multiplier) =>
        new(96000.0, multiplier, "OutputVoltage", 0.0, "Dbv", 0, 1, "None", null, 1.0, 20.0, "Hann", true, 5, ChirpStartHz: 2.0);

    [Fact]
    public void IsTheSweepGatesUsablePostSamples_WhenTheRowHoldsMore()
    {
        // 192 kHz, multiplier 1: a 131072-point sweep, whose gate keeps fftSize/2 post samples less a
        // taper of pre/2 — the same numbers the live runner uses.
        int available = ImpulseResponseRunner.StoredAvailablePostSamples(Row(131072, 65536), Parameters(1));
        var gate = ImpulseGate.ForSweep(131072, 2.0, 0.98 * 96000.0);
        Assert.Equal(gate.PostSamples - gate.TaperSamples, available);
        Assert.True(available < 65536);
    }

    [Fact]
    public void IsBoundedByTheRow_WhenTheSweepWasLongerThanWhatWasStored()
    {
        // Multiplier 4: a 524288-point sweep, but the row keeps 131072 samples centred on the peak.
        int available = ImpulseResponseRunner.StoredAvailablePostSamples(Row(131072, 65536), Parameters(4));
        Assert.Equal(131072 - 65536 - 1, available);
    }
}
