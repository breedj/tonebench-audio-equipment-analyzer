using Microsoft.Data.Sqlite;
using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>
/// A project file made safe to read measurements out of — the source half of an import, and the
/// file the Import dialog lists before anything is copied.
///
/// <para>It exists because a file that arrived by e-mail is not a file this app may treat like one
/// of its own. Three things about it, and only one is the schema version. It may be
/// <b>read-only</b>, or sit in a folder the user cannot write, and every Tonebench file is in WAL
/// mode, which needs a writable <c>-shm</c> — so it cannot necessarily be opened at all, which
/// would stop even <i>listing</i> it. It may carry an <b>un-checkpointed <c>-wal</c></b> whose
/// contents are the last writes anybody made. And it must not be <b>rewritten merely by being
/// looked at</b>, which migrating it in place would do.</para>
///
/// <para>The schema version is load-bearing in its own right, and migration <b>015</b> is why:
/// <c>INSERT … SELECT</c> copies a value with its storage class, so copying <c>capture.fs</c> out
/// of an un-migrated file would carry a REAL-typed sample rate into the recipient's project and
/// reproduce that fault there — a 500 on the whole History list, with a Dapper message naming
/// nothing that is wrong. So the source is brought to this build's version before a single row is
/// read, and a file <i>above</i> this build's version is refused outright rather than read with
/// column lists that know nothing about its new columns.</para>
///
/// <para>So: use the file where that is both possible and already correct, and otherwise read a
/// throwaway copy. The copy is taken with <c>SqliteConnection.BackupDatabase</c> where the original
/// can be opened — SQLite's own online backup, which folds in the <c>-wal</c> and is correct with
/// other connections still writing, unlike a file copy (the same reasoning
/// <see cref="ProjectSessionService.SaveAsAsync"/> gives for using it) — and with a plain file copy
/// of the database and its <c>-wal</c> only where it cannot, which is the read-only case, where
/// nothing can be writing to it anyway.</para>
///
/// <para>Validation is <see cref="ProjectSessionService.OpenAsync"/>'s, in its order and with its
/// wordings, through that class's own <c>internal</c> helpers: exists, SQLite header,
/// <c>schema_version</c> table, version, exactly one <c>project</c> row. A second set of answers to
/// "is this one of ours" is exactly what this does not want to be.</para>
/// </summary>
public sealed class ProjectTransferSource : IAsyncDisposable
{
    private const string TempPrefix = "tonebench-import-";

    private ProjectTransferSource(string path, string reportedPath, ProjectRow project, int schemaVersion, string? copyPath)
    {
        Path = path;
        ReportedPath = reportedPath;
        Project = project;
        SchemaVersion = schemaVersion;
        CopyPath = copyPath;
    }

    /// <summary>The file to read from — the one the user named, or the throwaway copy of it.</summary>
    public string Path { get; }

    /// <summary>
    /// What to call it on screen: always the file the user named, never the copy. A temp path in a
    /// dialog or an error message reads as a bug.
    /// </summary>
    public string ReportedPath { get; }

    /// <summary>The one project row in it.</summary>
    public ProjectRow Project { get; }

    /// <summary>What it was at before anything was done to it, for the message the dialog shows.</summary>
    public int SchemaVersion { get; }

    /// <summary>The copy's path when there is one, so <see cref="DisposeAsync"/> can delete it.</summary>
    private string? CopyPath { get; }

    /// <summary>Whether the file being read is a copy rather than the one the user named.</summary>
    public bool IsCopy => CopyPath is not null;

    public static async Task<ProjectTransferSource> OpenAsync(string requestedPath, CancellationToken ct)
    {
        string full = ProjectPaths.Normalize(requestedPath);

        if (!File.Exists(full))
            throw new ProjectFileException($"'{full}' does not exist.");
        if (!await ProjectSessionService.LooksLikeSqliteAsync(full, ct))
            throw new ProjectFileException($"'{full}' is not a Tonebench project file (not an SQLite database).");

        var runner = new MigrationRunner();

        // Reading it where it lies, which is the ordinary case and costs nothing.
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenUnpooledAtAsync(full, create: false, ct);
            int version = await RequireKnownVersionAsync(connection, full, runner);

            if (version == runner.LatestVersion)
                return new ProjectTransferSource(full, full, await ProjectSessionService.SingleProjectAsync(connection, full), version, copyPath: null);

            // Older, and the file itself is writable — but migrating it would rewrite a file the
            // user only asked to read, so the copy is what gets migrated. SQLite's own backup
            // rather than File.Copy: it folds the -wal in and is safe if something else is writing.
            string copied = TempPath();
            await BackupToAsync(connection, copied, ct);
            return await AdoptCopyAsync(copied, full, version, runner, ct);
        }
        catch (ProjectFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // It cannot be opened: read-only, on a read-only share, or held by something else. A
            // plain file copy is all that is left, and it is sound here precisely because a file
            // nothing can write is a file nothing is writing. The -wal goes too, or the copy is
            // missing whatever had not been checkpointed when it was sent.
            string copied = TempPath();
            try
            {
                File.Copy(full, copied, overwrite: true);
                if (File.Exists(full + "-wal"))
                    File.Copy(full + "-wal", copied + "-wal", overwrite: true);
            }
            catch (Exception copyFailure) when (copyFailure is IOException or UnauthorizedAccessException)
            {
                Delete(copied);
                throw new ProjectFileException(
                    $"'{full}' could not be read ({ex.Message.Trim()}), and copying it to read it instead failed: {copyFailure.Message.Trim()}");
            }

            return await AdoptCopyAsync(copied, full, schemaVersion: null, runner, ct);
        }
    }

    /// <summary>The file's measurements and their saved results — what the Import dialog lists.</summary>
    public async Task<ProjectContents> ReadContentsAsync(CancellationToken ct)
    {
        await using var connection = await SqliteConnectionFactory.OpenUnpooledAtAsync(Path, create: false, ct);
        var types = await ProjectTransferRepository.ListContentsAsync(connection, Project.Id, ct);

        return new ProjectContents(ReportedPath, Project.Id, Project.Name, Project.Notes, SchemaVersion, types);
    }

    public ValueTask DisposeAsync()
    {
        if (CopyPath is not null)
        {
            // Unpooled connections throughout, so every handle on the copy is really closed by now
            // and this needs no process-wide ClearAllPools — which would drop the open project's
            // pool on every import.
            foreach (string candidate in new[] { CopyPath, CopyPath + "-wal", CopyPath + "-shm" })
                Delete(candidate);
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<ProjectTransferSource> AdoptCopyAsync(
        string copyPath, string reportedPath, int? schemaVersion, MigrationRunner runner, CancellationToken ct)
    {
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenUnpooledAtAsync(copyPath, create: false, ct);
            int version = schemaVersion ?? await RequireKnownVersionAsync(connection, reportedPath, runner);

            await runner.RunAsync(connection, ct);
            var project = await ProjectSessionService.SingleProjectAsync(connection, reportedPath);

            return new ProjectTransferSource(copyPath, reportedPath, project, version, copyPath);
        }
        catch
        {
            foreach (string candidate in new[] { copyPath, copyPath + "-wal", copyPath + "-shm" })
                Delete(candidate);
            throw;
        }
    }

    /// <summary>
    /// The file's <c>schema_version</c>, refusing one this build cannot read. Both numbers are
    /// named, because "update Tonebench" is only actionable if the user can see that the file is
    /// ahead and by how much.
    /// </summary>
    private static async Task<int> RequireKnownVersionAsync(SqliteConnection connection, string reportedPath, MigrationRunner runner)
    {
        if (!await ProjectSessionService.TableExistsAsync(connection, "schema_version"))
            throw new ProjectFileException($"'{reportedPath}' is an SQLite database but not a Tonebench project file.");

        int version = await MigrationRunner.ReadVersionAsync(connection, CancellationToken.None);
        if (version > runner.LatestVersion)
        {
            throw new ProjectFileException(
                $"'{reportedPath}' was written by a newer version of Tonebench (its database is at version {version}; "
                    + $"this build knows {runner.LatestVersion}). Update Tonebench to read it.");
        }

        return version;
    }

    /// <summary>
    /// SQLite's online backup into a fresh file. A plain connection with no pragmas, for the reason
    /// <see cref="ProjectSessionService.SaveAsAsync"/> gives: the backup takes the source's page
    /// size, and a destination already switched to WAL would refuse to change its own.
    /// </summary>
    private static async Task BackupToAsync(SqliteConnection from, string destinationPath, CancellationToken ct)
    {
        await using var to = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await to.OpenAsync(ct);
        from.BackupDatabase(to);
    }

    private static string TempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}{ProjectPaths.Extension}");

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file that will not go away is not a reason to fail an import that worked.
        }
    }
}
