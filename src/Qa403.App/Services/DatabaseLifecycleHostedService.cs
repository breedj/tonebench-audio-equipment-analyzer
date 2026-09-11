using Qa403.Data;

namespace Qa403.App.Services;

/// <summary>
/// What the status endpoint reports about the two SQLite files: <c>null</c> when both passed their
/// startup check, otherwise what SQLite said about the one that did not.
///
/// <para>A singleton written once, by <see cref="DatabaseLifecycleHostedService.StartAsync"/>, so a
/// damaged file is announced in the status bar from the first poll instead of as a 500 on whichever
/// read first touches the bad page. Both real cases (10 and 11 Sept 2026) surfaced the latter way,
/// days after the damage, on one history entry, with a stack trace naming a repository.</para>
/// </summary>
public sealed class DatabaseHealth
{
    public string? Problem { get; internal set; }
}

/// <summary>
/// Checks both databases at startup and closes them properly at shutdown.
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
/// <para>A checkpoint that reports <c>busy</c> is logged as a warning and not retried: at this point
/// nothing should hold a write lock, and a retry would hide whatever does.</para>
/// </summary>
public sealed class DatabaseLifecycleHostedService(
    Qa403DatabaseOptions databaseOptions,
    Qa403ConfigDatabaseOptions configOptions,
    DatabaseHealth health,
    ILogger<DatabaseLifecycleHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, path) in Databases())
        {
            string? problem = await SqliteConnectionFactory.QuickCheckAsync(path, cancellationToken);
            if (problem is null)
            {
                logger.LogInformation("QA403 database {Name} passed quick_check ({Path})", name, path);
                continue;
            }

            // Error, not Fatal: the app keeps running, because the UI and every undamaged row are
            // still usable and the status bar carries the message. The history is what to act on.
            logger.LogError(
                "QA403 database {Name} at {Path} is damaged: {Problem}. Every row outside the damaged pages is "
                    + "still readable; rebuild the file by copying each table row by row into a fresh database and keep "
                    + "the damaged one beside it — see CLAUDE.md, \"A damaged qa403.db\".",
                name, path, problem);

            health.Problem ??= $"{name} database is damaged ({problem}) — see the log; saves may fail and one or more stored results cannot be opened.";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, path) in Databases())
        {
            try
            {
                var (completed, frames) = await SqliteConnectionFactory.CheckpointAsync(path, cancellationToken);
                if (completed)
                    logger.LogInformation("QA403 database {Name}: checkpointed {Frames} WAL frames, WAL truncated", name, frames);
                else
                    logger.LogWarning("QA403 database {Name}: checkpoint reported busy — a connection was still writing during shutdown", name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "QA403 database {Name}: checkpoint failed", name);
            }
        }

        SqliteConnectionFactory.CloseAllPools();
        logger.LogInformation("QA403 database connections closed");
    }

    private IEnumerable<(string Name, string Path)> Databases()
    {
        yield return ("measurement", databaseOptions.Path);
        yield return ("configuration", configOptions.Path);
    }
}
