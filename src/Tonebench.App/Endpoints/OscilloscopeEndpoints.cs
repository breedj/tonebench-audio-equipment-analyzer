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
            if (!TryParseEnums(request, out var mode, out var triggerKind, out var source, out var slope, out var enumError))
                return Results.BadRequest(enumError);

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

        // Deliberately not behind MeasurementRunGuard, and that is the whole point of it: the
        // guard refuses a /start while this very runner holds the session, which is right for a
        // start and wrong for a change to the capture already running. A scope's controls take
        // effect as they are turned -- dragging the trigger line IS how a level is set -- so
        // "stop or cancel it first" is the one answer this route must never give.
        group.MapPost("/retune", (OscilloscopeStartRequest request, OscilloscopeRunner runner) =>
        {
            if (!TryParseEnums(request, out var mode, out var triggerKind, out var source, out var slope, out var enumError))
                return Results.BadRequest(enumError);

            // Refused rather than ignored. Reconfiguring the generator books a stimulus-latency and
            // capture-delay discard, so it cannot ride on a control that moves per pointer move --
            // and a field accepted and silently dropped is how a setting comes to look applied
            // when it is not.
            if (request.GeneratorLeft is not null || request.GeneratorRight is not null)
                return Results.BadRequest("The generator is not part of a retune.");

            try
            {
                return Results.Ok(runner.Retune(request.ToMeasurement(mode, triggerKind, source, slope, generator: null)));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

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
                if (!WasCaptured(runner.LastRequest, ch))
                    continue;

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
                    slice.Columns?.Select(c => (float)c.Max).ToArray(),
                    slice.MeanV));
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

    /// <summary>
    /// Whether the measurement asked for this channel — the Acquisition panel's Left and Right
    /// boxes, which are a scope's CH1/CH2 buttons.
    ///
    /// <para><b>Filtered here rather than at the capture.</b> The record keeps both inputs: the
    /// buffer holds them, the trigger may watch either, and what is <c>capture.pcm</c> is what the
    /// analyzer saw — a CSV column headed <c>right_volt</c> has to be the right input, which a
    /// record cut down to one unlabelled channel could not promise. What the <i>screen</i> is
    /// about is this request, and drawing an input the measurement does not capture is how an
    /// orange trace came to sit under a left-only capture (reported 2026-11-17). Whether a captured
    /// channel is <i>drawn</i> is a display matter and lives in the display rail.</para>
    ///
    /// <para>No request — a record parked by a measurement this runner has since been handed away
    /// from — answers for every channel, since nothing here knows better.</para>
    /// </summary>
    internal static bool WasCaptured(OscilloscopeRequest? request, int channel) => request switch
    {
        null => true,
        { ProcessLeft: false, ProcessRight: false } => true,
        _ => channel == 1 ? request.ProcessRight : request.ProcessLeft,
    };

    /// <summary>
    /// The four enums a start or a retune carries, parsed once. Shared rather than copied because
    /// the two routes must agree about what a mode or a slope is called, and a second copy is how
    /// they come to disagree.
    /// </summary>
    private static bool TryParseEnums(
        OscilloscopeStartRequest request,
        out ScopeRunMode mode,
        out TriggerKind triggerKind,
        out TriggerSource source,
        out TriggerSlope slope,
        out string? error)
    {
        triggerKind = TriggerKind.Edge;
        source = TriggerSource.ChannelA;
        slope = TriggerSlope.Rising;

        if (!EnumParsing.TryParse(request.Mode, ScopeRunMode.Single, out mode, out error))
            return false;
        if (!EnumParsing.TryParse(request.TriggerKind, TriggerKind.Edge, out triggerKind, out error))
            return false;
        if (!EnumParsing.TryParse(request.TriggerSource, TriggerSource.ChannelA, out source, out error))
            return false;
        if (!EnumParsing.TryParse(request.TriggerSlope, TriggerSlope.Rising, out slope, out error))
            return false;

        return true;
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
/// <param name="MeanV">
/// The window's average, measured on the samples themselves — see
/// <see cref="ScopeFrameSlice.MeanV"/>. The one vertical reading that cannot be taken from what is
/// drawn, which is why it travels beside the picture rather than being worked out from it.
/// </param>
public sealed record ScopeChannelSlice(
    int Channel,
    double X0,
    double Dx,
    bool Raw,
    float[]? Samples,
    float[]? Min,
    float[]? Max,
    double MeanV);

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
