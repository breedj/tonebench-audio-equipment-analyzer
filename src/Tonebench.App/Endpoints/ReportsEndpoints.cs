using Microsoft.AspNetCore.Mvc;
using Tonebench.App.Reports;

namespace Tonebench.App.Endpoints;

/// <summary>
/// PDF reports. The browser assembles the whole document — what a measurement is called, which
/// settings it ran with, its readings, and the chart as a PNG it rendered itself — and this endpoint
/// only lays it out. That split is deliberate: every number a report shows is one the screen already
/// formats, the calculations behind them (bandwidth corners, component fits) live in the client, and
/// the chart is the app's own chart, drawn by the same code at a fixed size. Nothing here knows what a
/// measurement kind is, so adding a kind to the reports is client work alone.
/// </summary>
public static class ReportsEndpoints
{
    /// <summary>
    /// A project report is one PNG per stored result, base64 in JSON — a 1600×900 chart is 100-200 kB
    /// and a hundred of them clears Kestrel's 30 MB default with no margin. Loopback-only server, so
    /// a generous limit costs nothing.
    /// </summary>
    public const long MaxReportRequestBytes = 256L * 1024 * 1024;

    public static void MapReportsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/reports/pdf", (ReportDocument document) =>
            {
                if (document.Chapters is null)
                    return Results.BadRequest("A report needs a chapters array.");

                try
                {
                    byte[] pdf = ReportPdfRenderer.Render(document);
                    return Results.File(pdf, "application/pdf", "report.pdf");
                }
                catch (ReportDocumentException ex)
                {
                    return Results.BadRequest(ex.Message);
                }
            })
            .WithMetadata(new RequestSizeLimitAttribute(MaxReportRequestBytes));
    }
}

/// <summary>The whole report: a title page and one chapter per measurement.</summary>
/// <param name="Title">The measurement's name, or the project's.</param>
/// <param name="Subtitle">The project's name under a measurement report; "Project report" under a project's.</param>
/// <param name="Generated">When the client built it, already formatted in the user's locale.</param>
/// <param name="Notes">Project notes, on the title page.</param>
/// <param name="Eyebrow">Over the title on the cover: "Project report", "Measurement report".</param>
/// <param name="Stats">The figures beside the contents — chapters, how many carry a stored result.
/// The renderer adds the page count itself, since only it can know one.</param>
/// <param name="Version">"Tonebench v0.2.0", in the cover's footer.</param>
/// <param name="Monochrome">Black and white: the document's own accents as well as its charts. A
/// mono printer gets grey charts under violet headings otherwise.</param>
public sealed record ReportDocument(
    string Title,
    string? Subtitle,
    string Generated,
    string? Notes,
    ReportChapter[] Chapters,
    string? Eyebrow = null,
    ReportRow[]? Stats = null,
    string? Version = null,
    bool Monochrome = false);

/// <summary>One measurement: a title, a few key facts, its notes and then its sections in order.</summary>
/// <param name="Title">What this chapter is of — a stored result's name, or "Settings and notes".</param>
/// <param name="Eyebrow">The measurement it belongs to, over the title in the chapter's band.</param>
/// <param name="Accent">The kind's colour as <c>#rrggbb</c>, which is what makes a chapter findable
/// by its edge; the band's tint is mixed from it rather than sent, so the two cannot disagree.</param>
/// <param name="Status">What the contents page says about this chapter: when it was measured, or
/// that it carries settings only.</param>
/// <param name="StatusTone">Which pill the status wears: <c>stored</c>, <c>live</c> or <c>none</c>.</param>
public sealed record ReportChapter(
    string Title,
    ReportRow[] Meta,
    string? Notes,
    ReportSection[] Sections,
    string? Eyebrow = null,
    string? Accent = null,
    string? Status = null,
    string? StatusTone = null);

public sealed record ReportRow(string Label, string Value);

/// <summary>
/// One flat record with a <see cref="Kind"/> discriminator rather than a polymorphic hierarchy: four
/// optional fields are simpler than polymorphic source-generated metadata, and the renderer rejects an
/// unknown kind outright rather than skipping it, so a client typo cannot produce a silently thinner
/// report.
/// </summary>
/// <param name="Kind">One of <see cref="ReportSectionKind"/>.</param>
/// <param name="Rows">For <c>keyValues</c>.</param>
/// <param name="Headers">For <c>table</c>: the header row.</param>
/// <param name="Cells">For <c>table</c>: the body rows.</param>
/// <param name="Text">For <c>paragraph</c>; newlines are kept.</param>
/// <param name="Png">For <c>image</c>: the PNG bytes, base64 on the wire.</param>
/// <param name="Caption">For <c>image</c>. The renderer prefixes its own figure number.</param>
/// <param name="Figures">For <c>figures</c>: the few numbers a reader of this kind looks for first.</param>
/// <param name="Groups">For <c>groups</c>: named boxes of rows, for a settings list long enough that
/// one column of it is unreadable.</param>
public sealed record ReportSection(
    string Kind,
    string? Title = null,
    ReportRow[]? Rows = null,
    string[]? Headers = null,
    string[][]? Cells = null,
    string? Text = null,
    byte[]? Png = null,
    string? Caption = null,
    ReportFigure[]? Figures = null,
    ReportGroup[]? Groups = null);

/// <summary>A headline number on a card: what it is, what it reads, and what qualifies it.</summary>
public sealed record ReportFigure(string Label, string Value, string? Sub = null);

/// <summary>A named box of rows inside a <c>groups</c> section.</summary>
public sealed record ReportGroup(string Name, ReportRow[] Rows);

public static class ReportSectionKind
{
    public const string KeyValues = "keyValues";
    public const string Table = "table";
    public const string Paragraph = "paragraph";
    public const string Image = "image";
    public const string Figures = "figures";
    public const string Groups = "groups";
    public const string Callout = "callout";
}
