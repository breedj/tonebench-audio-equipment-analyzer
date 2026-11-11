using Tonebench.App.Services;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The clock-drift test's REST surface. Polled rather than streamed: a reading lands about once a
/// second, where the frames exist for things that land twenty times a second, and the status is
/// small enough that there is nothing to be gained by splitting it.
///
/// <para>Nothing here is saved to a project. The figure is a property of the <i>bench</i> — which
/// two cards are plugged in — rather than of a DUT, so it belongs with the sound card settings and
/// not in a measurement's results.</para>
/// </summary>
public static class ClockDriftEndpoints
{
    public static void MapClockDriftEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/clock-drift");

        group.MapPost("/start", async (ClockDriftRequest request, ClockDriftRunner runner, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await runner.StartAsync(request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        }).AddEndpointFilter<MeasurementRunGuard>();

        group.MapPost("/cancel", async (ClockDriftRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        group.MapGet("/status", (ClockDriftRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (ClockDriftRunner runner) =>
            runner.Running
                ? Results.Conflict("Stop the clock drift test before clearing it.")
                : Results.Ok(runner.Clear()));
    }
}
