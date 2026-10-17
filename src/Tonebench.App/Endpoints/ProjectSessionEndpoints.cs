using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The open project file, the recent-projects list and the file browser behind Open/New/Save As.
///
/// <para>Error mapping, the same throughout: <see cref="ProjectFileException"/> — the file the user
/// named cannot be used — is a 400 with the message; <see cref="InvalidOperationException"/> — the
/// session refusing to act right now (a measurement running, nothing open, a target that exists) —
/// is a 409; an I/O failure from the file system (a read-only folder, a file held by another
/// process) is a 400 with what the OS said. All three carry text the status bar shows as is.</para>
/// </summary>
public static class ProjectSessionEndpoints
{
    public static void MapProjectSessionEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/project");

        // Never a 409: this is how the client decides between the welcome screen and the workspace.
        project.MapGet("", (ProjectSessionService session) => Results.Ok(ToResponse(session.Current)));

        project.MapPost("/open", (OpenProjectRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () => ToResponse(await session.OpenAsync(request.Path, ct))));

        project.MapPost("/create", (CreateProjectRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () => ToResponse(await session.CreateAsync(request.Path, request.Name, request.Notes, ct))));

        project.MapPost("/close", (ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await session.CloseAsync(ct);
                return (ProjectSessionResponse?)null;
            }));

        // The path is sent back and checked against the open project: a stale tab deleting whatever
        // happens to be open now is the one mistake here that cannot be undone.
        project.MapPost("/delete", (DeleteProjectRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var current = session.Current;
                if (current is null || !Tonebench.Data.RecentProjects.PathComparer.Equals(current.Path, request.Path))
                    throw new InvalidOperationException("That project is not the one that is open; nothing was deleted.");
                await session.DeleteAsync(ct);
                return (ProjectSessionResponse?)null;
            }));

        project.MapPost("/save-as", (SaveProjectAsRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () => ToResponse(await session.SaveAsAsync(request.Path, request.Name, request.Notes, request.IncludeResults, ct))));

        project.MapPut("/properties", (ProjectPropertiesRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () => ToResponse(await session.UpdatePropertiesAsync(request.Name, request.Notes, ct))));

        // The measurements and saved results of a project file — the open one with no `path`, any
        // other file with one. One shape for both, so the Export dialog (which ticks the open
        // project) and the Import dialog (which ticks a file it was handed) render through the same
        // component and cannot drift apart. Reading another file opens nothing: the session, the
        // recent list and the last-opened document are all untouched.
        project.MapGet("/contents", (string? path, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () => ToResponse(string.IsNullOrWhiteSpace(path)
                ? await session.ReadContentsAsync(ct)
                : await session.ReadContentsAsync(path, ct))));

        project.MapPost("/export", (ProjectExportRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var (selection, onClash) = ReadSelection(request.Items, request.OnNameClash);
                return ToResponse(await session.ExportAsync(
                    request.Path, request.ProjectName, request.ProjectNotes, selection, onClash, ct));
            }));

        project.MapPost("/import", (ProjectImportRequest request, ProjectSessionService session, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var (selection, onClash) = ReadSelection(request.Items, request.OnNameClash);
                return ToResponse(await session.ImportAsync(request.Path, selection, onClash, ct));
            }));

        var projects = app.MapGroup("/api/projects");

        // Missing files are listed as missing, not pruned: the user decides whether an entry for a
        // file on a drive that is not plugged in right now is stale.
        projects.MapGet("/recent", async (ProjectSessionService session, CancellationToken ct) =>
        {
            var recent = await session.ReadRecentAsync(ct);
            return Results.Ok(recent.Entries.Select(e =>
                new RecentProjectResponse(e.Path, e.Name, e.LastOpenedUtc, File.Exists(e.Path))));
        });

        projects.MapPost("/recent/remove", async (RemoveRecentProjectRequest request, ProjectSessionService session, CancellationToken ct) =>
        {
            await session.RemoveRecentAsync(request.Path, ct);
            return Results.NoContent();
        });

        app.MapGet("/api/files/browse", (string? path, string? ext, FileBrowserService browser) =>
        {
            try
            {
                // No `ext` means "project files", which is both the current extension and the one
                // they carried before the rename — not just ProjectPaths.Extension, or a bench full
                // of .qa403 files would list as an empty folder.
                return Results.Ok(string.IsNullOrWhiteSpace(ext)
                    ? browser.Browse(path, ProjectPaths.OpenableExtensions)
                    // Comma-separated, so one dialog can offer a family of them: the simulation
                    // import takes LTspice's `.raw` and QSpice's `.qraw`, which are the same
                    // container. Browse already lists several extensions; this is only the wire
                    // spelling for it.
                    : browser.Browse(path, ext.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
            catch (ProjectFileException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (IOException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }

    /// <summary>
    /// Generic over the response so the transfer routes share this one error mapping rather than
    /// growing a second copy of it — which is the whole reason they live in this file.
    /// </summary>
    private static async Task<IResult> RunAsync<T>(Func<Task<T?>> action)
        where T : class
    {
        try
        {
            var response = await action();
            return response is null ? Results.NoContent() : Results.Ok(response);
        }
        // Plain text, not a JSON string: the client appends a failed response's body to its error
        // verbatim, and a message wrapped in quotes with its backslashes doubled reads as a bug.
        catch (ProjectFileException ex)
        {
            return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Text(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// The wire selection as the transfer wants it, with every clash policy parsed up front.
    ///
    /// <para>A bad policy name is a <see cref="ProjectFileException"/> — a 400 listing the valid
    /// names — rather than an uncaught <c>ArgumentException</c> 500 or, worse, a silent fall back to
    /// the default: "rename" and "merge" do different things to the user's project and guessing
    /// between them is not a service.</para>
    /// </summary>
    internal static (IReadOnlyList<TransferSelection> Selection, TypeNameClash OnClash) ReadSelection(
        IReadOnlyList<ProjectTransferItemRequest>? items, string? onNameClash)
    {
        if (!EnumParsing.TryParse(onNameClash, TypeNameClash.Rename, out var fallback, out string error))
            throw new ProjectFileException(error);

        var selection = new List<TransferSelection>();
        foreach (var item in items ?? [])
        {
            if (!EnumParsing.TryParse<TypeNameClash>(item.OnNameClash, fallback, out var policy, out error))
                throw new ProjectFileException(error);

            // null, not the fallback, when the item said nothing: the repository takes the
            // request's default in that case, and recording the resolved value here would make a
            // later change to that default silently not apply.
            selection.Add(new TransferSelection(
                item.TypeId, item.ResultIds ?? [], item.OnNameClash is null ? null : policy));
        }

        return (selection, fallback);
    }

    private static ProjectContentsResponse ToResponse(ProjectContents contents) =>
        new(contents.Path, contents.ProjectId, contents.ProjectName, contents.ProjectNotes, contents.SchemaVersion,
            [.. contents.Types.Select(t => new ProjectContentsTypeResponse(
                t.TypeId, t.Kind, t.Name, t.Notes, t.SortOrder,
                [.. t.Results.Select(r => new ProjectContentsResultResponse(
                    r.ResultId, r.Name, r.Timestamp, r.Source, r.Captures, r.ApproximateBytes))]))]);

    private static ProjectTransferResponse ToResponse(TransferReport report) =>
        new([.. report.Types.Select(t => new ProjectTransferTypeResponse(
                t.SourceTypeId, t.SourceName, t.DestinationTypeId, t.DestinationName, t.Outcome.ToString()))],
            report.ResultsCopied,
            report.CapturesCopied);

    private static ProjectSessionResponse ToResponse(ProjectSessionInfo? info) =>
        info is null
            ? new ProjectSessionResponse(false, null, null)
            : new ProjectSessionResponse(true, info.Path, new ProjectResponse(info.Id, info.Name, info.Notes, info.CreatedUtc));
}

/// <summary>What is open, if anything. <c>Project.Notes</c> is the description — the wire name predates the menu.</summary>
public sealed record ProjectSessionResponse(bool Open, string? Path, ProjectResponse? Project);

public sealed record OpenProjectRequest(string Path);

public sealed record CreateProjectRequest(string Name, string? Notes, string Path);

public sealed record DeleteProjectRequest(string Path);

/// <param name="IncludeResults">Copy the whole file (results included) rather than a fresh file with the measurement types only.</param>
public sealed record SaveProjectAsRequest(string Name, string? Notes, string Path, bool IncludeResults);

public sealed record ProjectPropertiesRequest(string Name, string? Notes);

public sealed record RecentProjectResponse(string Path, string Name, DateTimeOffset LastOpenedUtc, bool Exists);

public sealed record RemoveRecentProjectRequest(string Path);

/// <summary>
/// One project file's measurements and saved results. <paramref name="Path"/> is always the file
/// the user named, even where it was read through a migrated copy of itself.
/// </summary>
public sealed record ProjectContentsResponse(
    string Path,
    long ProjectId,
    string ProjectName,
    string? ProjectNotes,
    int SchemaVersion,
    IReadOnlyList<ProjectContentsTypeResponse> Types);

public sealed record ProjectContentsTypeResponse(
    long TypeId,
    string Kind,
    string Name,
    string? Notes,
    int SortOrder,
    IReadOnlyList<ProjectContentsResultResponse> Results);

/// <param name="ApproximateBytes">
/// Roughly what this result's stored blobs come to, so the dialog can say how large a transfer is
/// before it is attempted. A whole number of bytes, never a formatted string and never a floating
/// point value — see <c>ProjectContentsResult</c> for why that matters on a response record.
/// </param>
public sealed record ProjectContentsResultResponse(
    long ResultId,
    string Name,
    DateTimeOffset TimestampUtc,
    string? Source,
    int Captures,
    long ApproximateBytes);

/// <param name="OnNameClash">
/// This measurement's own clash policy, overriding the request's. Null — the usual case — takes the
/// request's.
/// </param>
public sealed record ProjectTransferItemRequest(
    long TypeId,
    IReadOnlyList<long> ResultIds,
    string? OnNameClash = null);

/// <param name="ProjectName">
/// The project name to give a file that does not exist yet. Ignored when the file is already a
/// project: an export appends to it and leaves its own name and description alone.
/// </param>
public sealed record ProjectExportRequest(
    string Path,
    string? ProjectName,
    string? ProjectNotes,
    IReadOnlyList<ProjectTransferItemRequest> Items,
    string? OnNameClash = null);

public sealed record ProjectImportRequest(
    string Path,
    IReadOnlyList<ProjectTransferItemRequest> Items,
    string? OnNameClash = null);

/// <param name="DestinationName">What it ended up called — different from the source name only when it was renamed, null when it was skipped.</param>
public sealed record ProjectTransferTypeResponse(
    long SourceTypeId,
    string SourceName,
    long? DestinationTypeId,
    string? DestinationName,
    string Outcome);

public sealed record ProjectTransferResponse(
    IReadOnlyList<ProjectTransferTypeResponse> Types,
    int ResultsCopied,
    int CapturesCopied);

public sealed record FileBrowseEntry(string Name, string Path, DateTimeOffset? ModifiedUtc, long? SizeBytes);

public sealed record FileBrowseRoot(string Label, string Path);

/// <summary>One directory listing for the in-app file dialog — see <see cref="FileBrowserService"/>.</summary>
/// <param name="Path">The directory listed, absolute.</param>
/// <param name="Parent">Its parent, or <c>null</c> at a root.</param>
/// <param name="Roots">Where the dialog can jump to: drives (Windows) or <c>/</c> and home (Linux), Documents, and the default projects folder.</param>
/// <param name="DefaultFolder">Where new projects go by default — see <see cref="ProjectPaths.DefaultFolder"/>.</param>
/// <param name="Separator">The host's directory separator, so the client can join a folder and a file name the way the server will read it.</param>
public sealed record FileBrowseResponse(
    string Path,
    string? Parent,
    IReadOnlyList<FileBrowseEntry> Directories,
    IReadOnlyList<FileBrowseEntry> Files,
    IReadOnlyList<FileBrowseRoot> Roots,
    string DefaultFolder,
    string Separator);
