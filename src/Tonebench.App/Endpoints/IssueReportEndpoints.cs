using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Endpoints;

/// <summary>
/// <b>Report an issue</b>: the dialog asks what went wrong, this builds the zip the user sends.
///
/// <para>Three routes rather than one, and the split follows what the dialog has to do. It needs to
/// say what it is about to include <i>before</i> anything is built, because one of the tick boxes
/// can turn a 300 kB zip into a 500 MB one and the user should see the figure first. It then builds
/// the file — which is written to disk rather than returned, see
/// <see cref="IssueReportService"/> — and finally hands it over, either as a download or by opening
/// the folder it is in.</para>
/// </summary>
public static class IssueReportEndpoints
{
    public static void MapIssueReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/issue-report");

        group.MapGet("", async (IssueReportService service, CancellationToken ct) =>
            Results.Ok(await service.DescribeAsync(ct)));

        // Deliberately *not* refused while a measurement runs, unlike every other POST that touches
        // the session. This one only reads, and the single most useful moment to press it is while
        // the fault is on screen -- which, for most of this app's faults, means mid-run.
        group.MapPost("", async (IssueReportRequest request, IssueReportService service, CancellationToken ct) =>
            Results.Ok(await service.BuildAsync(request, ct)));

        // The download. By file name, resolved inside the reports folder and checked to be in it:
        // the name comes back from the POST above and nothing else, but this is a route that returns
        // a file off the disk of the machine the browser is on, so it gets to be the one place that
        // says so. A traversal would be the user's own files, this being a loopback server bound to
        // 127.0.0.1 -- which is a reason to keep the check, not to skip it.
        group.MapGet("/download", (string name) =>
        {
            string directory = TonebenchAppData.IssueReportsDirectory;
            string full = Path.GetFullPath(Path.Combine(directory, name));

            if (Path.GetFileName(full) != name
                || !full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(full))
            {
                return Results.NotFound("No such report. Build one first — they are not kept for ever.");
            }

            // FileShare.Read on the way out, so a download running while the next report is being
            // written cannot fail on the lock. Download rather than inline: a zip has nothing to
            // render, and the name is the one the user will attach to a mail.
            return Results.File(
                new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read),
                "application/zip",
                fileDownloadName: name);
        });

        group.MapPost("/open-folder", () => ConfigEndpoints.OpenInFileManager(TonebenchAppData.IssueReportsDirectory));
    }
}
