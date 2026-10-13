using Tonebench.App.Endpoints;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Tonebench.App.Reports;

/// <summary>A document the renderer cannot lay out: an unknown section kind, or an image that is not a PNG.</summary>
public sealed class ReportDocumentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lays a <see cref="ReportDocument"/> out on A4 with QuestPDF. Generic on purpose — see
/// <see cref="ReportsEndpoints"/> for why nothing here knows what a measurement is.
///
/// <para>The layout is Joost's 2026-10-13 design (docs/design/"R2 · Colour band"). Two things about
/// it drive every decision below. The pages are drawn at 794 px wide, which is A4 at 96 dpi, so
/// every size in that file converts with <see cref="Px"/> — one constant rather than a second set of
/// numbers to keep in step. And the cover and the chapters are two <see cref="Document"/> pages
/// rather than one: the cover has a full-bleed band and no running header, which a single page
/// definition cannot express, while QuestPDF keeps numbering across both so "Page 2 of 14" still
/// counts the cover.</para>
/// </summary>
public static class ReportPdfRenderer
{
    /// <summary>The design's pixels (A4 at 96 dpi) as points. 794 px = 210 mm = 595.3 pt.</summary>
    private static float Px(float pixels) => pixels * 0.75f;

    /// <summary>The side margin — the design's 56 px, which is also near enough the old 1.5 cm.</summary>
    private static readonly float Gutter = Px(56);

    private static readonly string Ink = "#0f172a";

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
            return Compose(doc).GeneratePdf();
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

    /// <summary>
    /// The laid-out document, short of rendering it. Public because a PDF cannot be read back: this
    /// is what lets a check of the *design* rasterise the pages (QuestPDF's own GenerateImages)
    /// rather than assert on byte counts.
    /// </summary>
    public static Document Compose(ReportDocument doc)
    {
        var palette = Palette.For(doc.Monochrome);
        return Document.Create(container =>
        {
            container.Page(page => Cover(page, doc, palette));
            if (doc.Chapters.Length > 0)
                container.Page(page => Body(page, doc, palette));
        });
    }

    // ---------------------------------------------------------------- palette

    /// <summary>
    /// The document's own colours. **Black and white is the whole document, not only the charts**:
    /// the charts were desaturated long before the pages had any colour of their own, and a mono
    /// printer handed grey curves under violet headings is worse off than one handed neither. So a
    /// monochrome document keeps every rule, tint and head exactly where the colour one has them,
    /// in greys that still separate — a chapter's own accent included, which is why
    /// <see cref="ChapterAccent"/> goes through here rather than straight from the wire.
    /// </summary>
    private sealed record Palette(
        string Accent,
        string AccentSoft,
        string AccentRule,
        string Label,
        string Rule,
        string RowRule,
        string Stripe,
        string HeadInk,
        bool Monochrome)
    {
        public static Palette For(bool monochrome) => monochrome
            ? new Palette("#334155", "#f1f5f9", "#cbd5e1", "#475569", "#cbd5e1", "#e2e8f0", "#f8fafc", "#ffffff", true)
            : new Palette("#5b21b6", "#f5f3ff", "#ddd6fe", "#475569", "#cbd5e1", "#ede9fe", "#faf8ff", "#ffffff", false);

        /// <summary>A chapter's own hue, or the document's grey when the client sent none.</summary>
        public string ChapterAccent(string? accent) =>
            Monochrome || !LooksLikeHex(accent) ? Accent : accent!;

        /// <summary>
        /// The band behind a chapter's heading: its accent mixed into white. Mixed rather than sent
        /// as a second colour, so an ink and a tint that do not belong together cannot arrive.
        /// </summary>
        public string ChapterBand(string? accent) => Mix(ChapterAccent(accent), 0.92f);
    }

    private static bool LooksLikeHex(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>`#rrggbb` lightened towards white by <paramref name="towardsWhite"/> (0..1).</summary>
    private static string Mix(string color, float towardsWhite)
    {
        int Channel(int offset)
        {
            int value = Convert.ToInt32(color.Substring(offset, 2), 16);
            return (int)Math.Round(value * (1 - towardsWhite) + 255 * towardsWhite);
        }

        return $"#{Channel(1):x2}{Channel(3):x2}{Channel(5):x2}";
    }

    // ------------------------------------------------------------------ cover

    private static void Cover(PageDescriptor page, ReportDocument doc, Palette palette)
    {
        page.Size(PageSizes.A4);
        // No margin: the band bleeds to three edges, which is the whole of this cover's identity.
        page.Margin(0);
        page.DefaultTextStyle(t => t.FontSize(Px(13)).FontColor(Ink));

        page.Content().Column(column =>
        {
            // Sized by its own content with the design's 430 px worth of padding above it, rather
            // than by a fixed height: a long project name wraps to a second line and pushes the band
            // down, where a fixed height would have to refuse it.
            column.Item()
                .Background(palette.Accent)
                .PaddingHorizontal(Gutter)
                .PaddingTop(Px(56))
                .PaddingBottom(Px(48))
                .Column(band =>
                {
                    band.Item().Row(row =>
                    {
                        row.AutoItem()
                            .Width(Px(34)).Height(Px(34))
                            .Background("#ffffff").CornerRadius(Px(9))
                            .AlignCenter().AlignMiddle()
                            .Element(c => Mark(c, palette, Px(17)));
                        row.AutoItem().PaddingLeft(Px(10)).AlignMiddle()
                            .Text("Tonebench").FontSize(Px(16)).SemiBold().FontColor("#ffffff");
                    });

                    band.Item().Height(Px(180));

                    band.Item().Text((doc.Eyebrow ?? "Report").ToUpperInvariant())
                        .FontSize(Px(13)).SemiBold().LetterSpacing(0.14f).FontColor(Mix(palette.Accent, 0.78f));
                    band.Item().PaddingTop(Px(10)).Text(doc.Title)
                        .FontSize(Px(54)).SemiBold().FontColor("#ffffff").LineHeight(1.05f);
                    if (!string.IsNullOrWhiteSpace(doc.Subtitle))
                        band.Item().PaddingTop(Px(8)).Text(doc.Subtitle).FontSize(Px(16)).FontColor(Mix(palette.Accent, 0.86f));
                    band.Item().PaddingTop(Px(10)).Text($"Generated {doc.Generated}")
                        .FontSize(Px(15)).FontColor(Mix(palette.Accent, 0.86f));
                });

            column.Item().PaddingHorizontal(Gutter).PaddingTop(Px(36)).Column(body =>
            {
                CoverStats(body, doc, palette);

                if (doc.Chapters.Length > 1)
                {
                    body.Item().PaddingTop(Px(30)).PaddingBottom(Px(8))
                        .Text("Contents").FontSize(Px(14)).SemiBold().FontColor(palette.Accent);
                    for (int i = 0; i < doc.Chapters.Length; i++)
                        CoverContentsRow(body, doc.Chapters[i], i, palette);
                }

                if (!string.IsNullOrWhiteSpace(doc.Notes))
                {
                    body.Item().PaddingTop(Px(24)).PaddingBottom(Px(6))
                        .Text("Notes").FontSize(Px(14)).SemiBold().FontColor(palette.Accent);
                    Paragraph(body, doc.Notes);
                }
            });
        });

        page.Footer().PaddingHorizontal(Gutter).PaddingBottom(Px(36)).Row(row =>
        {
            row.RelativeItem().Text(doc.Version ?? "Tonebench").FontSize(Px(12)).FontColor(palette.Label);
            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(Px(12)).FontColor(palette.Label));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }

    /// <summary>
    /// The app's mark: four rounded bars, bottom aligned, at the proportions of
    /// packaging/icons/tonebench.svg — bars 3 units wide on a 2-unit gap, heights 9/16/12/6, so the
    /// whole mark is 18 × 16 units. Drawn rather than embedded, so it is sharp at any size and needs
    /// no asset on the server; in black and white it is four greys, because four hues printed as one
    /// grey is a smudge rather than a mark.
    ///
    /// <para>Every box here is a <b>constant</b> size. A relative one inside a container that is
    /// sizing itself to its content has no width to be relative to, and QuestPDF answers that with a
    /// layout exception rather than a guess.</para>
    /// </summary>
    /// <param name="width">How wide the whole mark is, in points.</param>
    private static void Mark(IContainer container, Palette palette, float width)
    {
        string[] colors = palette.Monochrome
            ? ["#94a3b8", "#64748b", "#475569", "#334155"]
            : ["#3b82f6", "#8b5cf6", "#ec4899", "#f97316"];
        float[] heights = [9, 16, 12, 6];
        float unit = width / 18f;

        container.Width(width).Height(16 * unit).Row(row =>
        {
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) row.ConstantItem(2 * unit);
                row.ConstantItem(3 * unit)
                    .AlignBottom()
                    .Height(heights[i] * unit)
                    .Background(colors[i])
                    .CornerRadius(0.75f * unit);
            }
        });
    }

    private static void CoverStats(ColumnDescriptor column, ReportDocument doc, Palette palette)
    {
        var stats = doc.Stats ?? [];
        if (stats.Length == 0) return;

        column.Item().Row(row =>
        {
            int index = 0;
            void Card(string value, string label, Action<TextDescriptor>? dynamicValue = null)
            {
                row.RelativeItem().PaddingLeft(index++ == 0 ? 0 : Px(12))
                    .Background(palette.AccentSoft).CornerRadius(Px(10))
                    .PaddingVertical(Px(14)).PaddingHorizontal(Px(16))
                    .Column(card =>
                    {
                        if (dynamicValue is null)
                            card.Item().Text(value).FontSize(Px(26)).SemiBold().FontColor(palette.Accent);
                        else
                            card.Item().Text(dynamicValue);
                        card.Item().Text(label).FontSize(Px(12)).FontColor(palette.Label);
                    });
            }

            foreach (var stat in stats) Card(stat.Value, stat.Label);

            // Only the renderer can know how long the document came out, so the page count is its own
            // card rather than a number the client would have to guess at.
            Card(string.Empty, "Pages", text =>
            {
                text.DefaultTextStyle(t => t.FontSize(Px(26)).SemiBold().FontColor(palette.Accent));
                text.TotalPages();
            });
        });

    }

    private static void CoverContentsRow(ColumnDescriptor column, ReportChapter chapter, int index, Palette palette)
    {
        column.Item().PaddingVertical(Px(9)).BorderBottom(0.75f).BorderColor(palette.RowRule).Row(row =>
        {
            row.ConstantItem(Px(10)).AlignMiddle()
                .Height(Px(10)).Background(palette.ChapterAccent(chapter.Accent)).CornerRadius(Px(5));

            row.RelativeItem().PaddingLeft(Px(12)).AlignMiddle().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(Px(13)));
                text.Span(chapter.Eyebrow ?? chapter.Title).Medium();
                if (chapter.Eyebrow is not null && !string.IsNullOrWhiteSpace(chapter.Title))
                    text.Span($" — {chapter.Title}").FontColor(palette.Label);
            });

            if (!string.IsNullOrWhiteSpace(chapter.Status))
            {
                var (background, ink) = StatusPill(chapter.StatusTone, palette);
                row.AutoItem().AlignMiddle().PaddingRight(Px(10))
                    .Background(background).CornerRadius(Px(8))
                    .PaddingVertical(Px(2)).PaddingHorizontal(Px(8))
                    .Text(chapter.Status).FontSize(Px(12)).FontColor(ink);
            }

            row.ConstantItem(Px(26)).AlignMiddle().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(Px(13)).SemiBold());
                text.BeginPageNumberOfSection(SectionName(index));
            });
        });
    }

    private static (string Background, string Ink) StatusPill(string? tone, Palette palette) =>
        palette.Monochrome
            ? ("#f1f5f9", "#475569")
            : tone switch
            {
                "stored" => ("#d8f5e0", "#15803d"),
                "live" => ("#fdeccb", "#92400e"),
                _ => ("#f1f5f9", "#475569"),
            };

    private static string SectionName(int chapterIndex) => $"chapter-{chapterIndex}";

    // ------------------------------------------------------------------- body

    private static void Body(PageDescriptor page, ReportDocument doc, Palette palette)
    {
        page.Size(PageSizes.A4);
        page.Margin(0);
        page.DefaultTextStyle(t => t.FontSize(Px(13)).FontColor(Ink));

        page.Header().PaddingHorizontal(Gutter).PaddingTop(Px(44)).PaddingBottom(Px(16)).Column(header =>
        {
            header.Item().PaddingBottom(Px(10)).Row(row =>
            {
                row.RelativeItem().AlignMiddle()
                    .Text(doc.Subtitle is null ? doc.Title : $"{doc.Title} · {doc.Subtitle}")
                    .FontSize(Px(12)).FontColor(palette.Label);
                row.AutoItem().AlignMiddle().Element(c => Mark(c, palette, Px(14)));
                row.AutoItem().PaddingLeft(Px(6)).AlignMiddle()
                    .Text("Tonebench").FontSize(Px(12)).SemiBold();
            });
            header.Item().LineHorizontal(0.75f).LineColor(palette.Rule);
        });

        page.Footer().PaddingHorizontal(Gutter).PaddingBottom(Px(36)).PaddingTop(Px(8)).Column(footer =>
        {
            footer.Item().PaddingBottom(Px(8)).LineHorizontal(0.75f).LineColor(palette.Rule);
            footer.Item().Row(row =>
            {
                row.RelativeItem().Text($"Generated {doc.Generated}").FontSize(Px(12)).FontColor(palette.Label);
                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(t => t.FontSize(Px(12)).FontColor(palette.Label));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });

        page.Content().Column(column =>
        {
            for (int i = 0; i < doc.Chapters.Length; i++)
            {
                if (i > 0) column.Item().PageBreak();
                Chapter(column, doc.Chapters[i], i, palette);
            }
        });
    }

    private static void Chapter(ColumnDescriptor column, ReportChapter chapter, int index, Palette palette)
    {
        string accent = palette.ChapterAccent(chapter.Accent);

        // The band bleeds to both edges, which is what makes a new chapter unmistakable when leafing
        // through; `Section` is what lets the contents page print this chapter's page number.
        column.Item()
            .Section(SectionName(index))
            .Background(palette.ChapterBand(chapter.Accent))
            .PaddingVertical(Px(16)).PaddingHorizontal(Gutter)
            .Row(row =>
            {
                row.ConstantItem(Px(40)).AlignMiddle()
                    .Height(Px(40)).Background(accent).CornerRadius(Px(20))
                    .AlignCenter().AlignMiddle()
                    .Text($"{index + 1}").FontSize(Px(17)).SemiBold().FontColor("#ffffff");

                row.RelativeItem().PaddingLeft(Px(16)).AlignMiddle().Column(head =>
                {
                    if (!string.IsNullOrWhiteSpace(chapter.Eyebrow))
                        head.Item().Text(chapter.Eyebrow.ToUpperInvariant())
                            .FontSize(Px(12)).SemiBold().LetterSpacing(0.08f).FontColor(accent);
                    head.Item().PaddingTop(Px(2)).Text(chapter.Title).FontSize(Px(22)).SemiBold();
                });
            });

        var content = column.Item().PaddingHorizontal(Gutter).PaddingTop(Px(16));
        content.Column(body =>
        {
            if (chapter.Meta.Length > 0) MetaGrid(body, chapter.Meta, palette);

            if (!string.IsNullOrWhiteSpace(chapter.Notes))
            {
                SectionHeading(body, "Notes", palette);
                Paragraph(body, chapter.Notes);
            }

            int figure = 0;
            foreach (var section in chapter.Sections)
            {
                if (section.Kind == ReportSectionKind.Image) figure++;
                Section(body, section, index, figure, palette);
            }
        });
    }

    private static void Section(ColumnDescriptor column, ReportSection section, int chapterIndex, int figure, Palette palette)
    {
        // A callout speaks for itself, and its own lead-in is its heading.
        bool heading = !string.IsNullOrWhiteSpace(section.Title) && section.Kind != ReportSectionKind.Callout;

        if (heading) SectionHeading(column, section.Title!, palette);
        else column.Item().PaddingTop(Px(12));

        switch (section.Kind)
        {
            case ReportSectionKind.KeyValues:
                KeyValues(column, section.Rows ?? [], palette);
                break;
            case ReportSectionKind.Table:
                Table(column, section.Headers ?? [], section.Cells ?? [], palette);
                break;
            case ReportSectionKind.Paragraph:
                Paragraph(column, section.Text ?? string.Empty);
                break;
            case ReportSectionKind.Image:
                Image(column, section, chapterIndex, figure, palette);
                break;
            case ReportSectionKind.Figures:
                Figures(column, section.Figures ?? [], palette);
                break;
            case ReportSectionKind.Groups:
                Groups(column, section.Groups ?? [], palette);
                break;
            case ReportSectionKind.Callout:
                Callout(column, section.Title, section.Text ?? string.Empty, palette);
                break;
            default:
                throw new ReportDocumentException($"Unknown report section kind '{section.Kind}'.");
        }
    }

    private static void SectionHeading(ColumnDescriptor column, string title, Palette palette)
    {
        column.Item().PaddingTop(Px(16)).PaddingBottom(Px(6))
            .BorderBottom(0.75f).BorderColor(palette.AccentRule)
            .Text(title).FontSize(Px(14)).SemiBold().FontColor(palette.Accent);
        column.Item().PaddingTop(Px(8));
    }

    /// <summary>
    /// The chapter's facts, label over value, three or four to a row — the shape a reader scans,
    /// against the two-column list this used to be, which ran the same six facts down half a page.
    /// </summary>
    private static void MetaGrid(ColumnDescriptor column, ReportRow[] rows, Palette palette)
    {
        int columns = rows.Length % 4 == 0 || rows.Length > 6 ? 4 : 3;

        for (int start = 0; start < rows.Length; start += columns)
        {
            column.Item().PaddingBottom(Px(8)).Row(row =>
            {
                for (int i = 0; i < columns; i++)
                {
                    var item = row.RelativeItem().PaddingRight(Px(20));
                    if (start + i >= rows.Length) continue;
                    var fact = rows[start + i];
                    item.Column(cell =>
                    {
                        cell.Item().Text(fact.Label).FontSize(Px(12)).FontColor(palette.Label);
                        cell.Item().Text(fact.Value).Medium();
                    });
                }
            });
        }
    }

    private static void Figures(ColumnDescriptor column, ReportFigure[] figures, Palette palette)
    {
        if (figures.Length == 0) return;

        column.Item().Row(row =>
        {
            for (int i = 0; i < figures.Length; i++)
            {
                var figure = figures[i];
                row.RelativeItem().PaddingLeft(i == 0 ? 0 : Px(10))
                    .Background(palette.AccentSoft).CornerRadius(Px(10))
                    .PaddingVertical(Px(10)).PaddingHorizontal(Px(12))
                    .Column(card =>
                    {
                        card.Item().Text(figure.Label).FontSize(Px(12)).FontColor(palette.Label);
                        card.Item().PaddingTop(Px(3)).Text(figure.Value)
                            .FontSize(Px(18)).SemiBold().FontColor(palette.Accent);
                        if (!string.IsNullOrWhiteSpace(figure.Sub))
                            card.Item().PaddingTop(Px(3)).Text(figure.Sub).FontSize(Px(12)).FontColor(palette.Label);
                    });
            }
        });
    }

    private static void Groups(ColumnDescriptor column, ReportGroup[] groups, Palette palette)
    {
        if (groups.Length == 0) return;

        for (int start = 0; start < groups.Length; start += 2)
        {
            column.Item().PaddingBottom(Px(12)).Row(row =>
            {
                for (int i = 0; i < 2; i++)
                {
                    var item = row.RelativeItem().PaddingRight(i == 0 ? Px(12) : 0);
                    if (start + i >= groups.Length) continue;
                    var group = groups[start + i];
                    item.Border(0.75f).BorderColor(palette.AccentRule).CornerRadius(Px(10))
                        .PaddingVertical(Px(8)).PaddingHorizontal(Px(12))
                        .Column(box =>
                        {
                            box.Item().PaddingBottom(Px(2)).Text(group.Name.ToUpperInvariant())
                                .FontSize(Px(12)).SemiBold().LetterSpacing(0.08f).FontColor(palette.Accent);
                            foreach (var fact in group.Rows)
                            {
                                box.Item().PaddingVertical(Px(4)).BorderTop(0.5f).BorderColor(palette.RowRule).Row(line =>
                                {
                                    line.RelativeItem().PaddingTop(Px(4))
                                        .Text(fact.Label).FontSize(Px(12)).FontColor(palette.Label);
                                    line.AutoItem().PaddingTop(Px(4)).PaddingLeft(Px(10)).AlignRight()
                                        .Text(fact.Value).FontSize(Px(12)).Medium();
                                });
                            }
                        });
                }
            });
        }
    }

    private static void Callout(ColumnDescriptor column, string? lead, string text, Palette palette)
    {
        column.Item()
            .Background(palette.Stripe).Border(0.75f).BorderColor(palette.AccentRule).CornerRadius(Px(8))
            .PaddingVertical(Px(10)).PaddingHorizontal(Px(14))
            .Text(line =>
            {
                line.DefaultTextStyle(t => t.FontSize(Px(13)).FontColor("#334155"));
                if (!string.IsNullOrWhiteSpace(lead)) line.Span($"{lead} ").SemiBold().FontColor(Ink);
                line.Span(text);
            });
    }

    private static void KeyValues(ColumnDescriptor column, ReportRow[] rows, Palette palette)
    {
        if (rows.Length == 0) return;

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(Px(190));
                columns.RelativeColumn();
            });

            foreach (var row in rows)
            {
                table.Cell().BorderBottom(0.5f).BorderColor(palette.RowRule)
                    .PaddingVertical(Px(5)).Text(row.Label).FontColor(palette.Label);
                table.Cell().BorderBottom(0.5f).BorderColor(palette.RowRule)
                    .PaddingVertical(Px(5)).Text(row.Value).Medium();
            }
        });
    }

    /// <summary>
    /// A table in the accent: a header row that repeats on every page it runs onto, and alternate
    /// rows on a tint so the eye holds a line across twelve columns. No rounded outer frame, unlike
    /// the cards — a sweep's table is fifty rows and breaks across pages, where a frame would be cut
    /// in half rather than closed.
    /// </summary>
    private static void Table(ColumnDescriptor column, string[] headers, string[][] cells, Palette palette)
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
                            .Background(palette.Accent)
                            .PaddingVertical(Px(5)).PaddingHorizontal(Px(3))
                            .Text(i < headers.Length ? headers[i] : string.Empty)
                            .FontSize(Px(12)).SemiBold().FontColor(palette.HeadInk);
                    }
                });
            }

            for (int r = 0; r < cells.Length; r++)
            {
                for (int i = 0; i < columnCount; i++)
                {
                    table.Cell()
                        .Background(r % 2 == 1 ? palette.Stripe : "#ffffff")
                        .BorderBottom(0.5f).BorderColor(palette.RowRule)
                        .PaddingVertical(Px(4)).PaddingHorizontal(Px(3))
                        .Text(i < cells[r].Length ? cells[r][i] : string.Empty).FontSize(Px(12));
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
                column.Item().Height(Px(8));
            else
                column.Item().Text(line);
        }
    }

    private static void Image(ColumnDescriptor column, ReportSection section, int chapterIndex, int figure, Palette palette)
    {
        if (section.Png is not { Length: > 0 })
            throw new ReportDocumentException("An image section needs PNG bytes.");

        column.Item().Image(section.Png).FitWidth();
        if (!string.IsNullOrWhiteSpace(section.Caption))
        {
            // Numbered here rather than by the client: the number is the chapter's and the ordinal is
            // this renderer's own count of the images it has laid out.
            column.Item().PaddingTop(Px(6))
                .Text($"Figure {chapterIndex + 1}.{figure} — {section.Caption}")
                .FontSize(Px(12)).FontColor(palette.Label);
        }
    }
}
