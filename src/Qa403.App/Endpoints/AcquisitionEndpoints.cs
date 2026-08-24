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

        group.MapPost("/start", async (AcquisitionStartRequest request, DeviceSessionService session, SpectrumPipeline pipeline, CancellationToken ct) =>
        {
            try
            {
                // Run always free-runs continuously, even if a previous Single press left the
                // pipeline holding — SingleShot is a one-off capture mode, not a sticky setting.
                pipeline.Options = pipeline.Options with { SingleShot = false };

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

        group.MapPost("/spectrum-config", async (
            SpectrumConfigRequest request, SpectrumPipeline pipeline, DeviceSessionService session, CancellationToken ct) =>
        {
            var current = pipeline.Options;

            int fftSize = request.FftSize ?? current.FftSize;
            if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
                return Results.BadRequest("FftSize must be a power of two.");

            if (!EnumParsing.TryParse<WindowType>(request.Window, current.Window, out var window, out var windowError))
                return Results.BadRequest(windowError);

            if (!EnumParsing.TryParse<AveragingMode>(request.Averaging, current.Averaging, out var averaging, out var averagingError))
                return Results.BadRequest(averagingError);

            double measurementLoHz = request.MeasurementLoFreqHz ?? current.MeasurementLoFreqHz;
            double measurementHiHz = request.MeasurementHiFreqHz ?? current.MeasurementHiFreqHz;
            if (measurementLoHz < 0.0 || measurementHiHz <= measurementLoHz)
                return Results.BadRequest("MeasurementHiFreqHz must be greater than MeasurementLoFreqHz, and neither may be negative.");

            pipeline.Options = current with
            {
                MeasurementLoFreqHz = measurementLoHz,
                MeasurementHiFreqHz = measurementHiHz,
                FftSize = fftSize,
                Window = window,
                Averaging = averaging,
                AverageCount = request.AverageCount ?? current.AverageCount,
                TargetFrameRateHz = request.TargetFrameRateHz ?? current.TargetFrameRateHz,
                OtherPeaksCount = request.OtherPeaksCount ?? current.OtherPeaksCount,
                ProcessLeft = request.ProcessLeft ?? current.ProcessLeft,
                ProcessRight = request.ProcessRight ?? current.ProcessRight,
            };

            // Which frequency is coherent depends on the FFT size, so a size change has to re-snap
            // the generator or the tone silently drifts back off-bin.
            if (fftSize != current.FftSize)
                await session.ReapplyGeneratorAsync(ct);

            return Results.Ok(pipeline.Options);
        });

        group.MapGet("/spectrum-config", (SpectrumPipeline pipeline) => Results.Ok(pipeline.Options));

        // Backs the "Single" button (see App.vue, between Run and Stop): turns on
        // SpectrumPipelineOptions.SingleShot and arms a fresh capture, so the next AverageCount
        // ticks build a clean average and then hold — no more frames for that channel until the
        // next Run or Single. Not part of SpectrumConfigRequest/spectrum-config on purpose: this is
        // a one-off action like Run/Stop, never a saved measurement setting. Run always resets
        // SingleShot back to false (see "/start" above) so a plain continuous run is never left
        // holding from a previous Single press.
        group.MapPost("/spectrum-capture", (SpectrumPipeline pipeline) =>
        {
            pipeline.Options = pipeline.Options with { SingleShot = true };
            pipeline.Flush();
            return Results.Ok(pipeline.Options);
        });

        // Backs the "Clear" button next to Save measurement result: discards the retained (stopped)
        // spectrum so the chart goes back to "Waiting for data..." and Save measurement result has
        // nothing stale left to save. Meaningful only while stopped -- the client disables the
        // button while acquiring, same as Save measurement result itself.
        group.MapPost("/clear", (SpectrumPipeline pipeline) =>
        {
            pipeline.Flush();
            return Results.Ok();
        });
    }
}

public sealed record AcquisitionStartRequest(double SampleRateHz, int ChannelCount, int? BlockFrameCount);

public sealed record SpectrumConfigRequest(
    int? FftSize, string? Window, string? Averaging, int? AverageCount, int? TargetFrameRateHz, int? OtherPeaksCount,
    bool? ProcessLeft, bool? ProcessRight,
    /// <summary>Measurement bandwidth for THD+N/SNR/noise — see <see cref="SpectrumPipelineOptions.MeasurementLoFreqHz"/>.</summary>
    double? MeasurementLoFreqHz = null, double? MeasurementHiFreqHz = null);
