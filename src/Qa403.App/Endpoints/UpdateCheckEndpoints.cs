using Qa403.App.Services;

namespace Qa403.App.Endpoints;

public static class UpdateCheckEndpoints
{
    public static void MapUpdateCheckEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/update-check");

        group.MapGet("", async (UpdateCheckService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatusAsync(ct)));
    }
}
