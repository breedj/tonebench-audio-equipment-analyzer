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
                // Backs both Run and Single: SingleShot is a one-off capture mode, not a sticky
                // setting, so it is whatever this start asks for — Run always free-runs even if a
                // previous Single left the pipeline holding. Set *before* the start, because the
                // start's own flushes (Start()'s bare one, then the capture delay's) are what arm
                // the capture: they clear the held flags and the averagers, and the settle discard
                // is the last flush to land. Single used to be armed by a separate call after this
                // one that did a bare Flush(), and SampleRingBuffer.Reset(0) zeroed the settle
                // discard that had just been booked — so a Single's first record started at the
                // tone step while Run's started a capture delay later, which on a tube amp's
                // 12 Hz high-pass ringing read as a bump around 50 Hz that no averaging removed.
                pipeline.Options = pipeline.Options with { SingleShot = request.SingleShot ?? false };

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

/// <param name="SingleShot">
/// True for the FFT screen's Single button: capture the configured averages, then hold (see
/// <see cref="SpectrumPipelineOptions.SingleShot"/>). Null or false free-runs.
/// </param>
public sealed record AcquisitionStartRequest(double SampleRateHz, int ChannelCount, int? BlockFrameCount, bool? SingleShot = null);

public sealed record SpectrumConfigRequest(
    int? FftSize, string? Window, string? Averaging, int? AverageCount, int? TargetFrameRateHz, int? OtherPeaksCount,
    bool? ProcessLeft, bool? ProcessRight,
    /// <summary>Measurement bandwidth for THD+N/SNR/noise — see <see cref="SpectrumPipelineOptions.MeasurementLoFreqHz"/>.</summary>
    double? MeasurementLoFreqHz = null, double? MeasurementHiFreqHz = null);
