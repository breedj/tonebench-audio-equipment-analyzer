using Qa403.App.Services;

namespace Qa403.App.Endpoints;

/// <summary>
/// The process's own identity and its off switch — what a <i>second</i> instance of the app talks
/// to at startup. There is only ever meant to be one: the browser, the Vite dev proxy and the
/// launch scripts all assume port 8399, and until 2026-09-21 a second start quietly took 8400 and
/// left the user with two apps, one of them holding the QA403. See <c>Program.ReplaceRunningInstance</c>.
/// </summary>
public static class AppEndpoints
{
    /// <summary>
    /// What identifies a listener on 8399 as this app rather than something else that happens to
    /// answer HTTP there. Fixed for the product's life: a newer build has to recognise an older one.
    /// </summary>
    public const string ProductId = "qa403-audio-analyzer";

    public static void MapAppEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/app");

        group.MapGet("", () =>
            Results.Ok(new AppIdentity(ProductId, Environment.ProcessId, UpdateCheckService.CurrentVersion)));

        // The same graceful shutdown as Ctrl+C: hosted services stop in order, the device is torn
        // down properly (pump joined, StreamingStop written, interface released -- everything the
        // hard rules on shutdown exist for), the database is checkpointed. 202 rather than 200
        // because the process is still alive when this returns; the caller waits on the pid.
        group.MapPost("/shutdown", (IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("Qa403.App.Lifecycle").LogInformation(
                "Shutdown requested over the API (another instance is starting) — running graceful shutdown");
            lifetime.StopApplication();
            return Results.Accepted(value: new AppIdentity(ProductId, Environment.ProcessId, UpdateCheckService.CurrentVersion));
        });
    }
}

/// <summary>Who is listening: the product, so a stranger on the port is left alone, and the pid, so a shutdown can be waited on.</summary>
public sealed record AppIdentity(string Product, int Pid, string Version);
