using Qa403.App.Services;
using Qa403.Device;

namespace Qa403.App.Endpoints;

public static class GeneratorEndpoints
{
    public static void MapGeneratorEndpoints(this WebApplication app)
    {
        app.MapPost("/api/generator", async (GeneratorRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            var config = new GeneratorConfig(ToChannelConfig(request.Left), ToChannelConfig(request.Right));
            await session.ConfigureGeneratorAsync(config, ct);
            return Results.Ok(session.GetStatus());
        });
    }

    private static ChannelGeneratorConfig ToChannelConfig(GeneratorChannelRequest request)
    {
        var tones = request.Tones.Select(t => new GeneratorTone(t.FreqHz, t.AmpDbv)).ToArray();
        return new ChannelGeneratorConfig(request.Enabled, tones);
    }
}

public sealed record GeneratorToneRequest(double FreqHz, double AmpDbv);

public sealed record GeneratorChannelRequest(bool Enabled, IReadOnlyList<GeneratorToneRequest> Tones);

public sealed record GeneratorRequest(GeneratorChannelRequest Left, GeneratorChannelRequest Right);
