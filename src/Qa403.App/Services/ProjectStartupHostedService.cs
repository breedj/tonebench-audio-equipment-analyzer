namespace Qa403.App.Services;

/// <summary>The project file named on the command line (<c>--project &lt;path&gt;</c>), if any.</summary>
public sealed record StartupProjectArgument(string? Path)
{
    public static StartupProjectArgument None { get; } = new((string?)null);

    /// <summary>Parses <c>--project path</c> and <c>--project=path</c>; the last one given wins.</summary>
    public static StartupProjectArgument Parse(IReadOnlyList<string> args)
    {
        string? path = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--project" && i + 1 < args.Count)
                path = args[++i];
            else if (args[i].StartsWith("--project=", StringComparison.Ordinal))
                path = args[i]["--project=".Length..];
        }
        return new StartupProjectArgument(string.IsNullOrWhiteSpace(path) ? null : path);
    }
}

/// <summary>
/// Opens a project at host startup: the one named on the command line, else — when the welcome
/// screen is switched off — the one that was open last time, if its file still exists.
///
/// <para>Server-side rather than a client redirect, so a browser reload or a second tab sees the
/// same open project instead of each deciding for itself. Registered after
/// <see cref="DatabaseLifecycleHostedService"/> (the configuration database has been migrated and
/// checked by then) and before <see cref="DeviceLifecycleHostedService"/>, which is unrelated but
/// slower.</para>
///
/// <para>Nothing here may throw. A hosted service that throws out of <see cref="StartAsync"/> takes
/// the whole host down, and a project that fails to open — moved, damaged, held by another process
/// — is exactly the case the welcome screen exists for. Every failure is logged and the app starts
/// with no project open. Cancellation is left alone: that is the host shutting down mid-startup.</para>
/// </summary>
public sealed class ProjectStartupHostedService(
    ProjectSessionService session,
    StartupProjectArgument argument,
    ILogger<ProjectStartupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string? path = argument.Path;
        string reason = "named on the command line";

        if (path is null)
        {
            var startup = await session.ReadStartupAsync(cancellationToken);
            if (startup.ShowWelcomeOnStartup)
                return;

            path = (await session.ReadLastOpenedAsync(cancellationToken)).Path;
            reason = "open last time";
            if (path is null)
                return;

            if (!File.Exists(path))
            {
                logger.LogInformation("Not reopening the last project: {Path} no longer exists", path);
                return;
            }
        }

        try
        {
            await session.OpenAsync(path, cancellationToken);
            logger.LogInformation("Opened the project {Reason}: {Path}", reason, path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the project {Reason} ({Path}); starting on the welcome screen", reason, path);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
