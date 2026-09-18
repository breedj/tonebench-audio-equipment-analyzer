using Qa403.App.Endpoints;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Qa403.App.Reports;

/// <summary>A document the renderer cannot lay out: an unknown section kind, or an image that is not a PNG.</summary>
public sealed class ReportDocumentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lays a <see cref="ReportDocument"/> out on A4 with QuestPDF. Generic on purpose — see
/// <see cref="ReportsEndpoints"/> for why nothing here knows what a measurement is.
/// </summary>
public static class ReportPdfRenderer
{
    private static readonly string LabelColor = Colors.Grey.Darken2;
    private static readonly string RuleColor = Colors.Grey.Lighten2;

    static ReportPdfRenderer()
    {
        // An individual's hobby project qualifies for the free tier; QuestPDF asks that the choice be
        // made explicit. This static constructor is the one place, and it runs before the first
        // document — in the app and in the tests alike.
        QuestPDF.Settings.License = LicenseType.Community;

        // Only the bundled Lato: identical output on Windows and Linux, and no dependency on whatever
        // fonts a bench machine happens to have installed.
        QuestPDF.Settings.UseSystemFonts = false;
    }

    public static byte[] Render(ReportDocument doc)
    {
        try
        {
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.5f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontSize(10));

                    page.Header().Row(row =>
                    {
                        row.RelativeItem().Text(doc.Title).FontSize(9).FontColor(LabelColor);
                        row.ConstantItem(60).AlignRight().Text("QA403").FontSize(9).FontColor(LabelColor);
                    });

                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.DefaultTextStyle(t => t.FontSize(9).FontColor(LabelColor));
                        text.Span("Page ");
                        text.CurrentPageNumber();
                        text.Span(" of ");
                        text.TotalPages();
                    });

                    page.Content().Column(column =>
                    {
                        TitlePage(column, doc);
                        foreach (var chapter in doc.Chapters)
                        {
                            column.Item().PageBreak();
                            Chapter(column, chapter);
                        }
                    });
                });
            }).GeneratePdf();
        }
        catch (ReportDocumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("QuestPDF", StringComparison.Ordinal) == true)
        {
            // QuestPDF wraps a failed image decode (and any other compose-time trouble) in its own
            // exception types. A client that sent a broken PNG deserves a 400, not a 500.
            throw new ReportDocumentException($"The report could not be laid out: {ex.Message}", ex);
        }
    }

    private static void TitlePage(ColumnDescriptor column, ReportDocument doc)
    {
        column.Item().PaddingTop(80).Text(doc.Title).FontSize(26).SemiBold();
        if (!string.IsNullOrWhiteSpace(doc.Subtitle))
            column.Item().PaddingTop(4).Text(doc.Subtitle).FontSize(14).FontColor(LabelColor);
        column.Item().PaddingTop(8).Text($"Generated {doc.Generated}").FontSize(10).FontColor(LabelColor);

        if (!string.IsNullOrWhiteSpace(doc.Notes))
        {
            column.Item().PaddingTop(24).LineHorizontal(0.5f).LineColor(RuleColor);
            column.Item().PaddingTop(6).PaddingBottom(4).Text("Notes").FontSize(11).SemiBold();
            Paragraph(column, doc.Notes);
        }

        if (doc.Chapters.Length > 1)
        {
            column.Item().PaddingTop(24).LineHorizontal(0.5f).LineColor(RuleColor);
            column.Item().PaddingTop(6).PaddingBottom(4).Text("Contents").FontSize(11).SemiBold();
            for (int i = 0; i < doc.Chapters.Length; i++)
                column.Item().PaddingVertical(1).Text($"{i + 1}.  {doc.Chapters[i].Title}");
        }
    }

    private static void Chapter(ColumnDescriptor column, ReportChapter chapter)
    {
        column.Item().Text(chapter.Title).FontSize(18).SemiBold();
        column.Item().PaddingTop(2).PaddingBottom(6).LineHorizontal(0.5f).LineColor(RuleColor);

        if (chapter.Meta.Length > 0)
            KeyValues(column, chapter.Meta);

        if (!string.IsNullOrWhiteSpace(chapter.Notes))
        {
            column.Item().PaddingTop(10).PaddingBottom(2).Text("Notes").FontSize(11).SemiBold();
            Paragraph(column, chapter.Notes);
        }

        foreach (var section in chapter.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
                column.Item().PaddingTop(12).PaddingBottom(2).Text(section.Title).FontSize(11).SemiBold();
            else
                column.Item().PaddingTop(6);

            switch (section.Kind)
            {
                case ReportSectionKind.KeyValues:
                    KeyValues(column, section.Rows ?? []);
                    break;
                case ReportSectionKind.Table:
                    Table(column, section.Headers ?? [], section.Cells ?? []);
                    break;
                case ReportSectionKind.Paragraph:
                    Paragraph(column, section.Text ?? string.Empty);
                    break;
                case ReportSectionKind.Image:
                    Image(column, section);
                    break;
                default:
                    throw new ReportDocumentException($"Unknown report section kind '{section.Kind}'.");
            }
        }
    }

    private static void KeyValues(ColumnDescriptor column, ReportRow[] rows)
    {
        if (rows.Length == 0) return;

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(150);
                columns.RelativeColumn();
            });

            foreach (var row in rows)
            {
                table.Cell().PaddingVertical(1.5f).Text(row.Label).FontColor(LabelColor);
                table.Cell().PaddingVertical(1.5f).Text(row.Value);
            }
        });
    }

    private static void Table(ColumnDescriptor column, string[] headers, string[][] cells)
    {
        int columnCount = Math.Max(headers.Length, cells.Length == 0 ? 0 : cells.Max(r => r.Length));
        if (columnCount == 0) return;

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (int i = 0; i < columnCount; i++) columns.RelativeColumn();
            });

            if (headers.Length > 0)
            {
                table.Header(header =>
                {
                    for (int i = 0; i < columnCount; i++)
                    {
                        header.Cell()
                            .BorderBottom(0.75f).BorderColor(RuleColor)
                            .PaddingVertical(2).PaddingRight(6)
                            .Text(i < headers.Length ? headers[i] : string.Empty).SemiBold();
                    }
                });
            }

            foreach (var row in cells)
            {
                for (int i = 0; i < columnCount; i++)
                {
                    table.Cell()
                        .BorderBottom(0.25f).BorderColor(RuleColor)
                        .PaddingVertical(2).PaddingRight(6)
                        .Text(i < row.Length ? row[i] : string.Empty);
                }
            }
        });
    }

    private static void Paragraph(ColumnDescriptor column, string text)
    {
        // One item per line rather than one Text with embedded newlines, so a blank line between two
        // paragraphs of notes stays a blank line.
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
                column.Item().Height(6);
            else
                column.Item().Text(line);
        }
    }

    private static void Image(ColumnDescriptor column, ReportSection section)
    {
        if (section.Png is not { Length: > 0 })
            throw new ReportDocumentException("An image section needs PNG bytes.");

        column.Item().Image(section.Png).FitWidth();
        if (!string.IsNullOrWhiteSpace(section.Caption))
            column.Item().PaddingTop(2).Text(section.Caption).FontSize(9).Italic().FontColor(LabelColor);
    }
}
