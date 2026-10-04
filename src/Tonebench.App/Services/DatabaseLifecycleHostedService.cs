using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>
/// What the status endpoint reports about the SQLite files: <c>null</c> when both passed their
/// check, otherwise what SQLite said about the one that did not.
///
/// <para>Two slots rather than one, because they have different lifetimes: the configuration file is
/// checked once, at startup, by <see cref="DatabaseLifecycleHostedService.StartAsync"/>; the project
/// file is checked by <see cref="ProjectSessionService"/> every time one is opened, and cleared when
/// it is closed. Either way a damaged file is announced in the status bar from the first poll
/// instead of as a 500 on whichever read first touches the bad page. Both real cases (10 and 11
/// Sept 2026) surfaced the latter way, days after the damage, on one history entry, with a stack
/// trace naming a repository.</para>
/// </summary>
public sealed class DatabaseHealth
{
    private string? _configProblem;
    private string? _projectProblem;

    public string? ConfigProblem
    {
        get => Volatile.Read(ref _configProblem);
        internal set => Volatile.Write(ref _configProblem, value);
    }

    public string? ProjectProblem
    {
        get => Volatile.Read(ref _projectProblem);
        internal set => Volatile.Write(ref _projectProblem, value);
    }

    public string? Problem => ConfigProblem ?? ProjectProblem;
}

/// <summary>
/// Checks the configuration database at startup and closes every database properly at shutdown.
///
/// <para><b>Registered before <see cref="DeviceLifecycleHostedService"/></b>, and that ordering is
/// the point. Hosted services stop in reverse registration order, so this one stops <i>last</i> —
/// after Kestrel has drained and after the device teardown — which is the only moment at which
/// nothing can still be writing. The checkpoint folds the WAL into the main file and
/// <see cref="SqliteConnectionFactory.CloseAllPools"/> releases every pooled native handle.</para>
///
/// <para>Without this step the process never closed its database at all. Microsoft.Data.Sqlite
/// pools connections and closes them from a <c>ProcessExit</c> hook, and when the console window is
/// closed Windows terminates the process the moment the CTRL_CLOSE handler returns — before that
/// hook, before finalizers, before anything. Ten days of logs, 65 shutdowns, and the host's own
/// final line never once made it to disk. qa403.db was found with zeroed pages twice in that period,
/// each time right after such a close. WAL mode is built to survive a dead process, but "nothing was
/// open" is the stronger guarantee, and it is cheap.</para>
///
/// <para>The project file is whichever one is open at shutdown — read from
/// <see cref="CurrentProjectFile"/> at that moment, since projects are switched at runtime — and its
/// startup check lives with the open, in <see cref="ProjectSessionService"/>. A checkpoint that
/// reports <c>busy</c> is logged as a warning and not retried: at this point nothing should hold a
/// write lock, and a retry would hide whatever does.</para>
/// </summary>
public sealed class DatabaseLifecycleHostedService(
    CurrentProjectFile current,
    TonebenchConfigDatabaseOptions configOptions,
    DatabaseHealth health,
    ILogger<DatabaseLifecycleHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string path = configOptions.Path;
        string? problem = await SqliteConnectionFactory.QuickCheckAsync(path, cancellationToken);
        if (problem is null)
        {
            logger.LogInformation("Tonebench configuration database passed quick_check ({Path})", path);
            return;
        }

        // Error, not Fatal: the app keeps running, because the UI and every undamaged row are
        // still usable and the status bar carries the message.
        logger.LogError(
            "Tonebench configuration database at {Path} is damaged: {Problem}. Rebuild the file by copying each table "
                + "row by row into a fresh database and keep the damaged one beside it — see CLAUDE.md, \"A damaged qa403.db\".",
            path, problem);

        health.ConfigProblem = $"configuration database is damaged ({problem}) — see the log; settings may fail to save.";
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, path) in Databases())
        {
            try
            {
                var (completed, frames) = await SqliteConnectionFactory.CheckpointAsync(path, cancellationToken);
                if (completed)
                    logger.LogInformation("Tonebench database {Name}: checkpointed {Frames} WAL frames, WAL truncated", name, frames);
                else
                    logger.LogWarning("Tonebench database {Name}: checkpoint reported busy — a connection was still writing during shutdown", name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tonebench database {Name}: checkpoint failed", name);
            }
        }

        SqliteConnectionFactory.CloseAllPools();
        logger.LogInformation("Tonebench database connections closed");
    }

    private IEnumerable<(string Name, string Path)> Databases()
    {
        if (current.Path is { } project)
            yield return ("project", project);
        yield return ("configuration", configOptions.Path);
    }
}
