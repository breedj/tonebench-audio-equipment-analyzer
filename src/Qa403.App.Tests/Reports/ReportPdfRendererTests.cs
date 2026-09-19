using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Qa403.App.Endpoints;
using Qa403.App.Json;
using Qa403.App.Reports;
using Qa403.App.Services;
using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Tests.Reports;

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
                    "Frequency response",
                    Meta: [new ReportRow("Measurement", "Frequency response"), new ReportRow("Taken", "18-09-2026 09:58")],
                    Notes: "Measured at the 8 Ω tap.",
                    Sections:
                    [
                        new ReportSection(ReportSectionKind.Image, Title: "Chart", Png: OnePixelPng, Caption: "Gain vs frequency"),
                        new ReportSection(ReportSectionKind.KeyValues, Title: "Settings",
                            Rows: [new ReportRow("Span", "20 Hz – 20 kHz"), new ReportRow("Level", "−18 dBV")]),
                        new ReportSection(ReportSectionKind.Table, Title: "Measurements",
                            Headers: ["", "f(low)", "f(high)", "BW"],
                            Cells: [["±1 dB", "23.1 Hz", "18.4 kHz", "18.4 kHz"], ["−3 dB", "11.4 Hz", "44.0 kHz", "44.0 kHz"]]),
                        new ReportSection(ReportSectionKind.Paragraph, Text: "The lower −3 dB corner is clamped to the sweep's start."),
                        .. extra,
                    ]),
                new ReportChapter("Impedance", Meta: [], Notes: null, Sections: []),
            ]);

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
