using Tonebench.App.Services;

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

    private static async Task<IResult> RunAsync(Func<Task<ProjectSessionResponse?>> action)
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
