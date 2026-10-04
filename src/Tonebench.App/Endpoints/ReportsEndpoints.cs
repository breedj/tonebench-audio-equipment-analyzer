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
public sealed record ReportDocument(
    string Title,
    string? Subtitle,
    string Generated,
    string? Notes,
    ReportChapter[] Chapters);

/// <summary>One measurement: a title, a few key facts, its notes and then its sections in order.</summary>
public sealed record ReportChapter(
    string Title,
    ReportRow[] Meta,
    string? Notes,
    ReportSection[] Sections);

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
/// <param name="Caption">For <c>image</c>.</param>
public sealed record ReportSection(
    string Kind,
    string? Title = null,
    ReportRow[]? Rows = null,
    string[]? Headers = null,
    string[][]? Cells = null,
    string? Text = null,
    byte[]? Png = null,
    string? Caption = null);

public static class ReportSectionKind
{
    public const string KeyValues = "keyValues";
    public const string Table = "table";
    public const string Paragraph = "paragraph";
    public const string Image = "image";
}
