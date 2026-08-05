using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Endpoints;

public static class AcquisitionEndpoints
{
    public static void MapAcquisitionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/acquisition");

        group.MapPost("/start", async (AcquisitionStartRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            try
            {
                var config = new AcquisitionConfig(request.SampleRateHz, request.ChannelCount, request.BlockFrameCount ?? 4096);
                await session.StartAcquisitionAsync(config, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/stop", async (DeviceSessionService session) =>
        {
            await session.StopAcquisitionAsync();
            return Results.Ok(session.GetStatus());
        });

        group.MapPost("/spectrum-config", (SpectrumConfigRequest request, SpectrumPipeline pipeline) =>
        {
            var current = pipeline.Options;

            int fftSize = request.FftSize ?? current.FftSize;
            if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
                return Results.BadRequest("FftSize must be a power of two.");

            if (!TryParseEnum<WindowType>(request.Window, current.Window, out var window, out var windowError))
                return Results.BadRequest(windowError);

            if (!TryParseEnum<AveragingMode>(request.Averaging, current.Averaging, out var averaging, out var averagingError))
                return Results.BadRequest(averagingError);

            pipeline.Options = current with
            {
                FftSize = fftSize,
                Window = window,
                Averaging = averaging,
                AverageCount = request.AverageCount ?? current.AverageCount,
                TargetFrameRateHz = request.TargetFrameRateHz ?? current.TargetFrameRateHz,
                OtherPeaksCount = request.OtherPeaksCount ?? current.OtherPeaksCount,
                ProcessLeft = request.ProcessLeft ?? current.ProcessLeft,
                ProcessRight = request.ProcessRight ?? current.ProcessRight,
            };

            return Results.Ok(pipeline.Options);
        });

        group.MapGet("/spectrum-config", (SpectrumPipeline pipeline) => Results.Ok(pipeline.Options));
    }

    private static bool TryParseEnum<TEnum>(string? raw, TEnum fallback, out TEnum value, out string error)
        where TEnum : struct, Enum
    {
        error = "";
        if (raw is null)
        {
            value = fallback;
            return true;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out value))
            return true;

        error = $"Unknown {typeof(TEnum).Name} '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.";
        return false;
    }
}

public sealed record AcquisitionStartRequest(double SampleRateHz, int ChannelCount, int? BlockFrameCount);

public sealed record SpectrumConfigRequest(
    int? FftSize, string? Window, string? Averaging, int? AverageCount, int? TargetFrameRateHz, int? OtherPeaksCount,
    bool? ProcessLeft, bool? ProcessRight);
