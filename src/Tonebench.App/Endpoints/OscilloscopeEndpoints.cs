using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The oscilloscope's REST surface — the same verbs and status codes as every other measurement,
/// plus <c>/record</c>, which is this one's own.
///
/// <para>The record is fetched rather than streamed in full for the reason
/// <see cref="ScopeFrameBuilder"/> gives: a one-second stereo capture is 1.5 MB, and the broadcast
/// hub's per-client channel is 64 frames deep. What goes over the socket is an overview and,
/// on demand, the zoom window; what comes back through here is the slice the client asked for,
/// which is how a tight zoom reaches real samples.</para>
/// </summary>
public static class OscilloscopeEndpoints
{
    public static void MapOscilloscopeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/oscilloscope");

        group.MapPost("/start", async (OscilloscopeStartRequest request, OscilloscopeRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Mode, ScopeRunMode.Single, out ScopeRunMode mode, out var modeError))
                return Results.BadRequest(modeError);
            if (!EnumParsing.TryParse(request.TriggerKind, TriggerKind.Edge, out TriggerKind triggerKind, out var kindError))
                return Results.BadRequest(kindError);
            if (!EnumParsing.TryParse(request.TriggerSource, TriggerSource.ChannelA, out TriggerSource source, out var sourceError))
                return Results.BadRequest(sourceError);
            if (!EnumParsing.TryParse(request.TriggerSlope, TriggerSlope.Rising, out TriggerSlope slope, out var slopeError))
                return Results.BadRequest(slopeError);

            GeneratorConfig? generator = null;
            if (request.GeneratorLeft is not null && request.GeneratorRight is not null)
            {
                if (!GeneratorEndpoints.TryMapChannel(request.GeneratorLeft, "Left", out var left, out var leftError))
                    return Results.BadRequest(leftError);
                if (!GeneratorEndpoints.TryMapChannel(request.GeneratorRight, "Right", out var right, out var rightError))
                    return Results.BadRequest(rightError);

                generator = new GeneratorConfig(left, right) { SnapToBin = request.SnapToBin ?? false };
            }

            var measurement = request.ToMeasurement(mode, triggerKind, source, slope, generator);

            try
            {
                return Results.Ok(await runner.StartAsync(measurement, ct));
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

        group.MapPost("/cancel", async (OscilloscopeRunner runner) =>
        {
            await runner.CancelAsync();
            return Results.Ok(runner.Status);
        });

        group.MapGet("/status", (OscilloscopeRunner runner) => Results.Ok(runner.Status));

        group.MapPost("/clear", (OscilloscopeRunner runner) =>
        {
            try
            {
                runner.Clear();
                return Results.Ok(runner.Status);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/owner", (RunOwnerRequest request, OscilloscopeRunner runner) =>
            Results.Ok(runner.SwitchOwner(request.Normalized)));

        group.MapPost("/save", async (
            SaveFrequencyResponseRequest request, OscilloscopeRunner runner, MeasurementSaveService saves, CancellationToken ct) =>
        {
            // The record and the request are read here rather than inside the save service, which
            // takes them as arguments: the service has no business reaching into a runner, and this
            // way the save is testable against a record that was never streamed.
            if (runner.Record is not { } record || runner.LastRequest is not { } measurement)
                return Results.Conflict("No oscilloscope record has been captured yet.");

            try
            {
                long id = await saves.SaveOscilloscopeAsync(
                    request.MeasurementTypeId, request.Name, request.Notes, record, measurement, request.ChartAnnotations, ct);
                return Results.Ok(new IdResponse(id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // The slice the chart is looking at. No window gives the overview the zoom strip draws;
        // a window tight enough to hold few enough samples comes back as the waveform itself
        // rather than as a summary of it.
        group.MapGet("/record", (
            OscilloscopeRunner runner,
            double? fromS,
            double? toS,
            int? maxColumns) =>
        {
            if (runner.Record is not { } record)
                return Results.NoContent();

            int columns = Math.Clamp(maxColumns ?? ScopeFrameBuilder.DefaultMaxColumns, 1, 4096);

            var channels = new List<ScopeChannelSlice>(record.ChannelCount);
            for (int ch = 0; ch < record.ChannelCount; ch++)
            {
                var slice = fromS is { } from && toS is { } to
                    ? ScopeFrameBuilder.Window(record, ch, from, to, columns)
                    : ScopeFrameBuilder.Overview(record, ch, columns);

                channels.Add(new ScopeChannelSlice(
                    ch,
                    slice.X0,
                    slice.Dx,
                    slice.IsRaw,
                    slice.Samples,
                    slice.Columns?.Select(c => (float)c.Min).ToArray(),
                    slice.Columns?.Select(c => (float)c.Max).ToArray()));
            }

            return Results.Ok(new ScopeRecordResponse(
                record.SampleRateHz,
                record.FrameCount,
                record.TriggerOffsetS,
                record.Span.FromS,
                record.Span.ToS,
                record.Spliced,
                record.Forced,
                channels));
        });
    }
}

/// <summary>
/// One channel of a record as the client draws it. Either <see cref="Samples"/> or the
/// <see cref="Min"/>/<see cref="Max"/> pair is set — and <b>both</b> of those are sent, never a
/// mean, because a mean flattens anything narrower than a column into the trace around it.
/// </summary>
/// <param name="Channel">0 for left, 1 for right.</param>
/// <param name="X0">Time of the first column or sample, relative to the trigger.</param>
/// <param name="Dx">Column width, or the sample period when raw.</param>
/// <param name="Raw">True when this is the waveform rather than a summary of it.</param>
public sealed record ScopeChannelSlice(
    int Channel,
    double X0,
    double Dx,
    bool Raw,
    float[]? Samples,
    float[]? Min,
    float[]? Max);

/// <summary>A record, as the chart fetches it.</summary>
/// <param name="SampleRateHz">The rate the samples are on.</param>
/// <param name="FrameCount">The whole record's length, whatever slice came back.</param>
/// <param name="TriggerOffsetS">Seconds from the start of the record to the trigger.</param>
/// <param name="FromS">The record's own span, relative to the trigger — what the zoom strip's axis is.</param>
/// <param name="ToS">The other end of it.</param>
/// <param name="Spliced">The record spans a dropped chunk or block.</param>
/// <param name="Forced">Auto's timeout took it; nothing crossed the level.</param>
/// <param name="Channels">One slice per captured channel.</param>
public sealed record ScopeRecordResponse(
    double SampleRateHz,
    int FrameCount,
    double TriggerOffsetS,
    double FromS,
    double ToS,
    bool Spliced,
    bool Forced,
    IReadOnlyList<ScopeChannelSlice> Channels);

/// <summary>
/// The wire shape of a start. Mapped into <see cref="OscilloscopeRequest"/> field by field in
/// <see cref="ToMeasurement"/> rather than bound straight onto it, which is the rule a missing
/// field here has already cost this project once: an unmapped parameter is not an error, it is a
/// default, and the run then succeeds and returns a perfectly plausible measurement of settings
/// the user did not ask for.
/// </summary>
public sealed record OscilloscopeStartRequest(
    double SampleRateHz,
    double RecordSeconds,
    double PreTriggerFraction,
    string? Mode,
    string? TriggerKind,
    string? TriggerSource,
    string? TriggerSlope,
    double TriggerLevelV,
    double? TriggerHysteresisV,
    double? HoldoffSeconds,
    double? AutoTimeoutSeconds,
    bool? ProcessLeft,
    bool? ProcessRight,
    GeneratorChannelRequest? GeneratorLeft,
    GeneratorChannelRequest? GeneratorRight,
    bool? SnapToBin)
{
    public OscilloscopeRequest ToMeasurement(
        ScopeRunMode mode,
        TriggerKind triggerKind,
        TriggerSource triggerSource,
        TriggerSlope triggerSlope,
        GeneratorConfig? generator) =>
        new(
            SampleRateHz,
            RecordSeconds,
            PreTriggerFraction,
            mode,
            triggerKind,
            triggerSource,
            triggerSlope,
            TriggerLevelV,
            TriggerHysteresisV ?? 0.0,
            HoldoffSeconds ?? 0.0,
            AutoTimeoutSeconds ?? 0.5,
            ProcessLeft ?? true,
            ProcessRight ?? true,
            generator);
}
