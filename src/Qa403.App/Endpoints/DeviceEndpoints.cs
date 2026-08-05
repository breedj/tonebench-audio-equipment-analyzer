using Qa403.App.Services;

namespace Qa403.App.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/device");

        group.MapGet("", (DeviceSessionService session) => Results.Ok(session.GetStatus()));

        group.MapGet("/ranges", (DeviceSessionService session) =>
        {
            var caps = session.Capabilities;
            return Results.Ok(new DeviceRangesResponse(
                caps.SampleRatesHz, caps.InputRangesDbv, caps.OutputRangesDbv, caps.MaxChannelCount));
        });

        group.MapPost("/input-range", async (InputRangeRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            try
            {
                await session.SetInputRangeAsync(request.Dbv, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/output-range", async (OutputRangeRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            try
            {
                await session.SetOutputRangeAsync(request.Dbv, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }
}

public sealed record DeviceStatusResponse(
    bool Connected,
    string ProductName,
    string SerialNumber,
    string FirmwareVersion,
    bool IsSimulated,
    double InputRangeDbv,
    double OutputRangeDbv,
    bool Acquiring,
    GeneratorChannelResponse GeneratorLeft,
    GeneratorChannelResponse GeneratorRight);

/// <summary>Wire shape matches the request-side <c>GeneratorToneRequest</c> in GeneratorEndpoints.cs (freqHz/ampDbv), not the internal <c>Qa403.Device.GeneratorTone</c>'s property names.</summary>
public sealed record GeneratorToneResponse(double FreqHz, double AmpDbv);

public sealed record GeneratorChannelResponse(bool Enabled, IReadOnlyList<GeneratorToneResponse> Tones);

public sealed record DeviceRangesResponse(
    IReadOnlyList<double> SampleRatesHz,
    IReadOnlyList<double> InputRangesDbv,
    IReadOnlyList<double> OutputRangesDbv,
    int MaxChannelCount);

public sealed record InputRangeRequest(double Dbv);

public sealed record OutputRangeRequest(double Dbv);
