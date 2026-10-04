using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>What is open: the file, and the one project row inside it.</summary>
public sealed record ProjectSessionInfo(string Path, long Id, string Name, string? Notes, DateTimeOffset CreatedUtc);

/// <summary>
/// The open project file: opens, creates, closes, deletes and copies them, and is the only writer
/// of <see cref="CurrentProjectFile"/>.
///
/// <para>A project is one SQLite file with exactly one <c>project</c> row. Every repository is
/// stateless and opens the current file through <see cref="SqliteConnectionFactory"/>, so switching
/// projects is: refuse while anything is running, checkpoint and release the old file, validate the
/// new one, point the holder at it. The refusal mirrors <see cref="DeviceSessionService.SwitchDeviceAsync"/>
/// and asks every <see cref="IMeasurementRun"/>, not just the acquisition — a sweep between points
/// is not acquiring, and a switch in that gap reads as an unexplained mid-run failure.</para>
///
/// <para>Two things are deliberately not errors. A file that fails <c>quick_check</c> is opened
/// anyway and reported through <see cref="DatabaseHealth"/>, the posture the startup check always
/// had: every undamaged row is still readable and the status bar carries the message. And opening a
/// file written by an older build migrates it in place — so Open is a write, and a read-only file
/// fails with SQLite's own message rather than a stale schema.</para>
///
/// <para>What <i>is</i> an error, and refused before the file is touched: a path that does not exist
/// (the factory never creates one — see <see cref="SqliteConnectionFactory.OpenAtAsync"/>), a file
/// without SQLite's header, a database without a <c>schema_version</c> table (any SQLite file that is
/// not ours; migrating it would turn it into an empty project), and — after migration, which is
/// harmless on a QA403 database — a file holding no project or several. The last is the legacy
/// <c>qa403.db</c>, which held every project at once and is left where it is.</para>
/// </summary>
public sealed class ProjectSessionService(
    CurrentProjectFile current,
    ConfigRepository config,
    DatabaseHealth health,
    IEnumerable<IMeasurementRun> runs,
    IOptions<JsonOptions> jsonOptions,
    ILogger<ProjectSessionService> logger)
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ProjectSessionInfo? _current;

    public ProjectSessionInfo? Current => Volatile.Read(ref _current);

    public async Task<ProjectSessionInfo> OpenAsync(string path, CancellationToken ct)
    {
        string full = ProjectPaths.Normalize(path);

        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfBusy();

            if (!File.Exists(full))
                throw new ProjectFileException($"'{full}' does not exist.");
            if (!await LooksLikeSqliteAsync(full, ct))
                throw new ProjectFileException($"'{full}' is not a QA403 project file (not an SQLite database).");

            await CloseCoreAsync(ct);

            string? problem = await SqliteConnectionFactory.QuickCheckAsync(full, ct);
            ProjectRow row;
            await using (var connection = await SqliteConnectionFactory.OpenAtAsync(full, create: false, ct))
            {
                if (!await TableExistsAsync(connection, "schema_version"))
                    throw new ProjectFileException($"'{full}' is an SQLite database but not a QA403 project file.");

                await new MigrationRunner().RunAsync(connection, ct);
                row = await SingleProjectAsync(connection, full);
            }

            return await AdoptAsync(full, row, problem, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProjectSessionInfo> CreateAsync(string path, string name, string? notes, CancellationToken ct)
    {
        string full = ProjectPaths.EnsureExtension(ProjectPaths.Normalize(path));
        name = RequireName(name);

        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfBusy();
            if (File.Exists(full))
                throw new InvalidOperationException($"'{full}' already exists. Open it, or choose another name.");

            await CloseCoreAsync(ct);

            ProjectRow row = await CreateFileAsync(full, name, notes, ct);
            logger.LogInformation("Project created: {Path}", full);
            return await AdoptAsync(full, row, problem: null, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfBusy();
            await CloseCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Closes the open project and deletes its file, WAL and shared-memory file included.</summary>
    public async Task DeleteAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfBusy();
            var info = _current ?? throw new InvalidOperationException("No project is open.");

            await CloseCoreAsync(ct);

            foreach (string candidate in new[] { info.Path, info.Path + "-wal", info.Path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }

            await WriteDocumentAsync(
                ConfigRepository.RecentProjectsKey,
                new RecentProjectsConfig(RecentProjects.Remove((await ReadRecentAsync(ct)).Entries, info.Path)), ct);

            var last = await ReadDocumentAsync(ConfigRepository.LastOpenedProjectKey, LastOpenedProjectConfig.Default, ct);
            if (last.Path is not null && RecentProjects.PathComparer.Equals(last.Path, info.Path))
                await WriteDocumentAsync(ConfigRepository.LastOpenedProjectKey, LastOpenedProjectConfig.Default, ct);

            logger.LogInformation("Project deleted: {Path}", info.Path);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes the open project to a new file and switches to it. With <paramref name="includeResults"/>
    /// the whole database is copied through SQLite's online backup (which is correct with pooled
    /// connections still open on the source, unlike a file copy); without it, a fresh file gets the
    /// project row and the measurement types only. The caller saves dirty types first — this copies
    /// what is stored.
    /// </summary>
    public async Task<ProjectSessionInfo> SaveAsAsync(string path, string name, string? notes, bool includeResults, CancellationToken ct)
    {
        string full = ProjectPaths.EnsureExtension(ProjectPaths.Normalize(path));
        name = RequireName(name);

        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfBusy();
            var source = _current ?? throw new InvalidOperationException("No project is open.");
            if (RecentProjects.PathComparer.Equals(full, source.Path))
                throw new InvalidOperationException("Choose a different file: that is the project that is open now.");
            if (File.Exists(full))
                throw new InvalidOperationException($"'{full}' already exists. Choose another name.");

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            ProjectRow row;
            if (includeResults)
            {
                // A plain connection, no pragmas: the backup takes the source's page size, and a
                // destination already switched to WAL would refuse to change its own.
                await using (var from = await SqliteConnectionFactory.OpenAtAsync(source.Path, create: false, ct))
                await using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
                {
                    await to.OpenAsync(ct);
                    from.BackupDatabase(to);
                }

                await using var copy = await SqliteConnectionFactory.OpenAtAsync(full, create: false, ct);
                await ProjectRepository.UpdateProjectAsync(copy, source.Id, name, notes);
                // The loopback calibration lives in the configuration database now; a copy of a file
                // written by an older build may still carry rows here, and they describe nothing.
                await copy.ExecuteAsync("DELETE FROM impulse_reference;");
                row = await SingleProjectAsync(copy, full);
            }
            else
            {
                row = await CreateFileAsync(full, name, notes, ct);
                await using var from = await SqliteConnectionFactory.OpenAtAsync(source.Path, create: false, ct);
                await using var to = await SqliteConnectionFactory.OpenAtAsync(full, create: false, ct);
                await ProjectRepository.CopyTypesAsync(from, source.Id, to, row.Id);
            }

            await CloseCoreAsync(ct);
            logger.LogInformation("Project saved as {Path} ({Mode})", full, includeResults ? "with results" : "measurement types only");
            return await AdoptAsync(full, row, problem: null, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProjectSessionInfo> UpdatePropertiesAsync(string name, string? notes, CancellationToken ct)
    {
        name = RequireName(name);

        await _lock.WaitAsync(ct);
        try
        {
            var info = _current ?? throw new InvalidOperationException("No project is open.");

            await using (var connection = await SqliteConnectionFactory.OpenAtAsync(info.Path, create: false, ct))
                await ProjectRepository.UpdateProjectAsync(connection, info.Id, name, notes);

            var recent = await ReadRecentAsync(ct);
            await WriteDocumentAsync(
                ConfigRepository.RecentProjectsKey,
                new RecentProjectsConfig(RecentProjects.Rename(recent.Entries, info.Path, name)), ct);

            var updated = info with { Name = name, Notes = notes };
            Volatile.Write(ref _current, updated);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The recent list as stored; whether each file still exists is the endpoint's question.</summary>
    public Task<RecentProjectsConfig> ReadRecentAsync(CancellationToken ct) =>
        ReadDocumentAsync(ConfigRepository.RecentProjectsKey, RecentProjectsConfig.Default, ct);

    public async Task RemoveRecentAsync(string path, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var recent = await ReadRecentAsync(ct);
            await WriteDocumentAsync(
                ConfigRepository.RecentProjectsKey,
                new RecentProjectsConfig(RecentProjects.Remove(recent.Entries, path)), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<StartupConfig> ReadStartupAsync(CancellationToken ct) =>
        ReadDocumentAsync(ConfigRepository.StartupKey, StartupConfig.Default, ct);

    public Task<LastOpenedProjectConfig> ReadLastOpenedAsync(CancellationToken ct) =>
        ReadDocumentAsync(ConfigRepository.LastOpenedProjectKey, LastOpenedProjectConfig.Default, ct);

    // ---- internals -------------------------------------------------------------------------------

    private void ThrowIfBusy()
    {
        if (runs.Any(r => r.Running))
            throw new InvalidOperationException("Stop the running measurement before changing projects.");
    }

    private static string RequireName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
            throw new ProjectFileException("The project needs a name.");
        return name;
    }

    /// <summary>
    /// Releases the open file: the holder is cleared <i>first</i>, so a request arriving during the
    /// checkpoint gets a 409 rather than a connection to a file that is about to be closed under
    /// it; then the WAL is folded in and every pooled handle released, which is what lets the file
    /// be deleted, copied or reopened. <see cref="SqliteConnectionFactory.CloseAllPools"/> is
    /// process-wide and also drops the configuration database's pooled handles — harmless, they
    /// reopen on the next call.
    /// </summary>
    private async Task CloseCoreAsync(CancellationToken ct)
    {
        var info = _current;
        if (info is null)
            return;

        current.Path = null;
        Volatile.Write(ref _current, null);
        health.ProjectProblem = null;

        try
        {
            var (completed, frames) = await SqliteConnectionFactory.CheckpointAsync(info.Path, ct);
            if (completed)
                logger.LogInformation("Project closed: {Path} (checkpointed {Frames} WAL frames)", info.Path, frames);
            else
                logger.LogWarning("Project closed: {Path} — checkpoint reported busy, a connection was still writing", info.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Project closed: {Path} — checkpoint failed", info.Path);
        }

        SqliteConnectionFactory.CloseAllPools();
    }

    /// <summary>Makes <paramref name="path"/> the open project and records it as opened.</summary>
    private async Task<ProjectSessionInfo> AdoptAsync(string path, ProjectRow row, string? problem, CancellationToken ct)
    {
        var info = new ProjectSessionInfo(path, row.Id, row.Name, row.Notes, row.CreatedUtc);

        if (problem is not null)
        {
            logger.LogError(
                "Project file {Path} is damaged: {Problem}. Every row outside the damaged pages is still readable; "
                    + "rebuild the file by copying each table row by row into a fresh database and keep the damaged one "
                    + "beside it — see CLAUDE.md, \"A damaged qa403.db\".",
                path, problem);
            health.ProjectProblem = $"project file is damaged ({problem}) — see the log; saves may fail and one or more stored results cannot be opened.";
        }

        current.Path = path;
        Volatile.Write(ref _current, info);

        var recent = await ReadRecentAsync(ct);
        await WriteDocumentAsync(
            ConfigRepository.RecentProjectsKey,
            new RecentProjectsConfig(RecentProjects.Touch(recent.Entries, new RecentProjectEntry(path, row.Name, DateTimeOffset.UtcNow))),
            ct);
        await WriteDocumentAsync(ConfigRepository.LastOpenedProjectKey, new LastOpenedProjectConfig(path), ct);

        logger.LogInformation("Project open: {Name} ({Path})", row.Name, path);
        return info;
    }

    private static async Task<ProjectRow> CreateFileAsync(string path, string name, string? notes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: true, ct);
        await new MigrationRunner().RunAsync(connection, ct);
        await ProjectRepository.CreateProjectAsync(connection, name, notes);
        return await SingleProjectAsync(connection, path);
    }

    private static async Task<ProjectRow> SingleProjectAsync(SqliteConnection connection, string path)
    {
        var projects = await ProjectRepository.ListProjectsAsync(connection);
        return projects.Count switch
        {
            1 => projects[0],
            0 => throw new ProjectFileException($"'{path}' holds no project."),
            _ => throw new ProjectFileException(
                $"'{path}' holds {projects.Count} projects ({string.Join(", ", projects.Select(p => $"'{p.Name}'"))}). "
                    + "That is a database from before projects were one file each, not a project file; it cannot be opened here."),
        };
    }

    private static async Task<bool> LooksLikeSqliteAsync(string path, CancellationToken ct)
    {
        var header = new byte[SqliteHeader.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        return read == header.Length && header.AsSpan().SequenceEqual(SqliteHeader);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;", new { name }) > 0;

    private async Task<T> ReadDocumentAsync<T>(string key, T fallback, CancellationToken ct) where T : class
    {
        string? stored = await config.GetAsync(key, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(stored, jsonOptions.Value.SerializerOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private Task WriteDocumentAsync<T>(string key, T document, CancellationToken ct) =>
        config.SetAsync(key, JsonSerializer.Serialize(document, jsonOptions.Value.SerializerOptions), ct);
}
