using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Tonebench.App.Endpoints;
using Tonebench.App.Reports;

namespace Tonebench.App.Tests.Reports;

/// <summary>
/// Renders report documents to one PNG per page, so the *design* can be looked at.
///
/// <para>Nothing else can: a PDF cannot be read back, and the renderer's own tests can only assert
/// that bytes came out. This is how the 2026-10-13 layout was checked against Joost's mockups, and
/// it is how the next change to it should be — a page of a report is a drawing, and the only review
/// it has is somebody's eyes.</para>
///
/// <para>It writes nothing unless <c>TONEBENCH_REPORT_DUMP</c> names a directory holding one or more
/// <c>*.json</c> report documents (the client's own POST body; the browser's network tab will hand
/// you one). Each becomes <c>&lt;name&gt;-p1.png</c> beside it. Run it with:</para>
/// <code>TONEBENCH_REPORT_DUMP=/some/dir dotnet test --filter FullyQualifiedName~ReportImageDump</code>
/// </summary>
public class ReportImageDumpTests
{
    [Fact]
    public void WritesAPngPerPage_ForEveryDocumentInTheDumpDirectory()
    {
        string? directory = Environment.GetEnvironmentVariable("TONEBENCH_REPORT_DUMP");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        foreach (string path in Directory.GetFiles(directory, "*.json"))
        {
            var document = JsonSerializer.Deserialize<ReportDocument>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document is null) continue;

            string name = Path.GetFileNameWithoutExtension(path);
            int page = 1;
            foreach (byte[] image in ReportPdfRenderer.Compose(document)
                         .GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 96 }))
            {
                File.WriteAllBytes(Path.Combine(directory, $"{name}-p{page++}.png"), image);
            }
        }
    }
}
