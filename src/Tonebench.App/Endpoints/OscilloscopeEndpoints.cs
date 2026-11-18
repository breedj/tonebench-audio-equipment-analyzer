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
            if (!TryParseEnums(request, out var mode, out var triggerKind, out var source, out var slope, out var accumulation, out var enumError))
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

            var measurement = request.ToMeasurement(mode, triggerKind, source, slope, accumulation, generator);

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
            if (!TryParseEnums(request, out var mode, out var triggerKind, out var source, out var slope, out var accumulation, out var enumError))
                return Results.BadRequest(enumError);

            // Refused rather than ignored. Reconfiguring the generator books a stimulus-latency and
            // capture-delay discard, so it cannot ride on a control that moves per pointer move --
            // and a field accepted and silently dropped is how a setting comes to look applied
            // when it is not.
            if (request.GeneratorLeft is not null || request.GeneratorRight is not null)
                return Results.BadRequest("The generator is not part of a retune.");

            try
            {
                return Results.Ok(runner.Retune(request.ToMeasurement(mode, triggerKind, source, slope, accumulation, generator: null)));
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
            double? gateFromS,
            double? gateToS,
            bool? residual,
            string? math,
            bool? xy,
            bool? eye,
            string? coupling,
            int? maxColumns) =>
        {
            if (runner.Record is not { } raw)
                return Results.NoContent();

            if (!TryParsePath(coupling, out var path, out var pathError))
                return Results.BadRequest(pathError);

            // The display path, applied once to the whole record: everything below works from the
            // result, so the picture, the readings, the math channel and the X-Y figure cannot
            // disagree about which trace they are of.
            var record = path.Apply(raw);

            // Asked for by name rather than always computed, like the residual: a math trace costs
            // a pass over the window per fetch, and Off is both the default and the commonest
            // state. An unknown name is a bad request rather than a silent Off -- a trace quietly
            // absent is exactly how a display setting comes to look broken.
            WaveformOperation? operation = null;
            if (!string.IsNullOrWhiteSpace(math) && !math.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                if (!EnumParsing.TryParse(math, WaveformOperation.AMinusB, out var parsed, out var mathError))
                    return Results.BadRequest(mathError);
                operation = parsed;
            }

            int columns = Math.Clamp(maxColumns ?? ScopeFrameBuilder.DefaultMaxColumns, 1, 4096);

            var channels = new List<ScopeChannelSlice>(record.ChannelCount);
            ScopeFrameSlice? geometry = null;
            // Which channel that geometry belongs to, for the eye diagram: it is drawn for one
            // input, and the readings under it are that input's.
            int? drawnChannel = null;
            for (int ch = 0; ch < record.ChannelCount; ch++)
            {
                if (!WasCaptured(runner.LastRequest, ch))
                    continue;

                var slice = fromS is { } from && toS is { } to
                    ? ScopeFrameBuilder.Window(record, ch, from, to, columns)
                    : ScopeFrameBuilder.Overview(record, ch, columns);
                geometry ??= slice;
                drawnChannel ??= ch;

                // The gate rides on the same fetch as the picture it qualifies, which is the
                // point of it being a parameter here rather than a second route: two round trips
                // could answer for two different records, and a panel heading saying "between the
                // cursors" over the previous capture's figures is a plausible wrong answer.
                var gated = gateFromS is { } gateFrom && gateToS is { } gateTo
                    ? ScopeFrameBuilder.Measure(record, ch, gateFrom, gateTo)
                    : null;

                // On the same geometry as the slice above, which is why it is built from it
                // rather than from the record: the two traces share one x array.
                var left = residual == true ? ScopeFrameBuilder.Residual(record, ch, slice, columns) : null;

                channels.Add(new ScopeChannelSlice(
                    ch,
                    slice.X0,
                    slice.Dx,
                    slice.IsRaw,
                    slice.Samples,
                    slice.Columns?.Select(c => (float)c.Min).ToArray(),
                    slice.Columns?.Select(c => (float)c.Max).ToArray(),
                    slice.Measurements,
                    gated,
                    left is null
                        ? null
                        : new ScopeResidualSlice(
                            left.Samples,
                            left.Columns?.Select(c => (float)c.Min).ToArray(),
                            left.Columns?.Select(c => (float)c.Max).ToArray(),
                            left.FitFrequencyHz,
                            left.FitAmplitudeVpk,
                            left.FitOffsetV,
                            left.ResidualRmsV,
                            left.ResidualPercent)));
            }

            // One math trace per record rather than one per channel, because it is not a channel:
            // it combines them. Built off the first drawn slice's geometry, which is every slice's
            // -- the server cuts them all from one window -- so the derived trace shares the x
            // array the waveforms are drawn against.
            ScopeMathSlice? mathSlice = null;
            if (operation is { } op && geometry is { } reference)
            {
                bool haveA = !WaveformMath.NeedsA(op) || WasCaptured(runner.LastRequest, 0);
                bool haveB = !WaveformMath.NeedsB(op) || WasCaptured(runner.LastRequest, 1);
                var built = haveA && haveB ? ScopeFrameBuilder.MathTrace(record, reference, op, columns) : null;

                if (built is not null)
                {
                    mathSlice = new ScopeMathSlice(
                        built.Operation.ToString(),
                        built.Unit.ToString(),
                        built.Samples,
                        built.Columns?.Select(c => (float)c.Min).ToArray(),
                        built.Columns?.Select(c => (float)c.Max).ToArray(),
                        built.Measurements,
                        gateFromS is { } mathGateFrom && gateToS is { } mathGateTo
                            ? ScopeFrameBuilder.MathGate(record, op, mathGateFrom, mathGateTo)
                            : null,
                        built.ClampedSamples);
                }
            }

            // The X-Y figure, when the chart is in that view. Asked for by name like the two
            // above, and for a reason of its own: it is the one slice that cannot be summarised,
            // so it is the one that costs real bytes -- see ScopeFrameBuilder.MaxXyPoints.
            ScopeXySlice? xySlice = null;
            if (xy == true && geometry is { } xyReference)
            {
                bool both = WasCaptured(runner.LastRequest, 0) && WasCaptured(runner.LastRequest, 1);
                var figure = both ? ScopeFrameBuilder.XyFigure(record, xyReference) : null;
                if (figure is not null)
                {
                    xySlice = new ScopeXySlice(
                        figure.X,
                        figure.Y,
                        figure.FrameCount,
                        figure.WindowFrameCount,
                        figure.FrameCount / record.SampleRateHz,
                        figure.Correlation,
                        figure.PhaseDeg,
                        figure.AmplitudeRatio);
                }
            }

            // The eye diagram, when the chart is in that view. Asked for by name like the three
            // above, and the most expensive of them: it is folds of samples rather than one
            // trace, so see ScopeFrameBuilder.MaxEyeFolds for what bounds it.
            //
            // <para>On the FIRST drawn channel rather than on both. Two eyes over each other is
            // not two readings -- the whole picture is a band, and two bands at different levels
            // are a wash -- and the readings are per channel, so a pair of them would have to be
            // reported separately and read separately. The rail says which channel it is.</para>
            ScopeEyeSlice? eyeSlice = null;
            if (eye == true && geometry is { } eyeReference && drawnChannel is { } eyeChannel)
            {
                var figure = ScopeFrameBuilder.EyeFigure(record, eyeChannel, eyeReference);
                if (figure is not null)
                {
                    eyeSlice = new ScopeEyeSlice(
                        eyeChannel,
                        figure.Folds,
                        figure.FoldFrameCount,
                        figure.FoldSeconds,
                        figure.PeriodHz,
                        figure.Folds.Length,
                        figure.WindowFoldCount,
                        figure.MaxSpreadV,
                        figure.SpreadAtS,
                        figure.MeanSpreadV,
                        figure.JitterS);
                }
            }

            return Results.Ok(new ScopeRecordResponse(
                record.SampleRateHz,
                record.FrameCount,
                record.TriggerOffsetS,
                record.Span.FromS,
                record.Span.ToS,
                record.Spliced,
                record.Forced,
                channels,
                mathSlice,
                xySlice,
                eyeSlice,
                path.Changes,
                record.Rolled,
                record.IsEnvelope,
                record.AveragesTaken));
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
    /// The display path a request names, parsed once and shared by the record route and the
    /// preview, so the curve the rail draws is the filter the trace gets.
    /// </summary>
    /// <summary>
    /// The same parse, for the stored-record route in MeasurementsEndpoints.
    ///
    /// <para>Shared rather than copied so a reference trace is read through exactly the path the
    /// live trace is — two parsers would be two paths, and a comparison of two different
    /// things.</para>
    /// </summary>
    internal static bool TryParseStoredPath(
        string? coupling,
        out ScopeSignalPath path,
        out string? error) =>
        TryParsePath(coupling, out path, out error);

    private static bool TryParsePath(
        string? coupling,
        out ScopeSignalPath path,
        out string? error)
    {
        path = ScopeSignalPath.Direct;

        if (!EnumParsing.TryParse(coupling, ScopeCoupling.Dc, out var parsedCoupling, out error))
            return false;

        path = new ScopeSignalPath(parsedCoupling);
        error = null;
        return true;
    }

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
        out RecordAccumulation accumulation,
        out string? error)
    {
        triggerKind = TriggerKind.Edge;
        source = TriggerSource.ChannelA;
        slope = TriggerSlope.Rising;
        accumulation = RecordAccumulation.Normal;

        if (!EnumParsing.TryParse(request.Mode, ScopeRunMode.Single, out mode, out error))
            return false;
        if (!EnumParsing.TryParse(request.TriggerKind, TriggerKind.Edge, out triggerKind, out error))
            return false;
        if (!EnumParsing.TryParse(request.TriggerSource, TriggerSource.ChannelA, out source, out error))
            return false;
        if (!EnumParsing.TryParse(request.TriggerSlope, TriggerSlope.Rising, out slope, out error))
            return false;
        if (!EnumParsing.TryParse(request.Accumulation, RecordAccumulation.Normal, out accumulation, out error))
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
/// <param name="Measurements">
/// Every reading of this window, measured on the samples themselves — see
/// <see cref="ScopeSliceMeasurements"/>. They travel beside the picture because they cannot be
/// taken from it.
/// </param>
/// <param name="Gated">
/// The same readings over the gate the request asked for — the cursor pair — or null when it asked
/// for none, or when the window it named holds no samples of this record. Never a fallback to
/// <see cref="Measurements"/>: a gated figure that is quietly the whole window's is the one answer
/// worse than no figure.
/// </param>
/// <param name="Residual">
/// This window with its fundamental removed, when the request asked for it — see
/// <see cref="ScopeFrameBuilder.Residual"/>. Null when it did not, and null when the window has no
/// fundamental to remove, which is the honest answer on noise, on DC and on anything with one
/// settled level.
/// </param>
public sealed record ScopeChannelSlice(
    int Channel,
    double X0,
    double Dx,
    bool Raw,
    float[]? Samples,
    float[]? Min,
    float[]? Max,
    ScopeSliceMeasurements Measurements,
    ScopeSliceMeasurements? Gated = null,
    ScopeResidualSlice? Residual = null);

/// <summary>
/// The residual as the client draws it: the same <c>X0</c>, <c>Dx</c> and raw-or-columns shape as
/// the slice that carries it, so the two traces share one x array.
/// </summary>
/// <param name="Samples">The residual itself, or null when the window is decimated.</param>
/// <param name="Min">Both extremes of every column, as the waveform's own are sent.</param>
/// <param name="Max">The other one.</param>
/// <param name="FitFrequencyHz">The fitted fundamental, refined rather than taken.</param>
/// <param name="FitAmplitudeVpk">Its peak amplitude.</param>
/// <param name="FitOffsetV">Its DC term.</param>
/// <param name="ResidualRmsV">RMS of what is left, over the whole window.</param>
/// <param name="ResidualPercent">That against the fundamental's RMS — a THD+N figure over this window.</param>
public sealed record ScopeResidualSlice(
    float[]? Samples,
    float[]? Min,
    float[]? Max,
    double FitFrequencyHz,
    double FitAmplitudeVpk,
    double FitOffsetV,
    double ResidualRmsV,
    double? ResidualPercent);

/// <summary>A record, as the chart fetches it.</summary>
/// <param name="SampleRateHz">The rate the samples are on.</param>
/// <param name="FrameCount">The whole record's length, whatever slice came back.</param>
/// <param name="TriggerOffsetS">Seconds from the start of the record to the trigger.</param>
/// <param name="FromS">The record's own span, relative to the trigger — what the zoom strip's axis is.</param>
/// <param name="ToS">The other end of it.</param>
/// <param name="Spliced">The record spans a dropped chunk or block.</param>
/// <param name="Forced">Auto's timeout took it; nothing crossed the level.</param>
/// <param name="Channels">One slice per captured channel.</param>
/// <param name="Math">
/// The math channel, when the request asked for one — a waveform derived from the two captured
/// ones, on the same geometry as the slices above. Null when it asked for none, and null when the
/// operation needs a channel this measurement does not capture, which the screen says in words
/// rather than drawing a trace against nothing.
/// </param>
/// <param name="Xy">
/// The X–Y figure, when the request asked for one. Null when it did not, and null when the
/// measurement captures one input — there is no second axis to plot against.
/// </param>
public sealed record ScopeRecordResponse(
    double SampleRateHz,
    int FrameCount,
    double TriggerOffsetS,
    double FromS,
    double ToS,
    bool Spliced,
    bool Forced,
    IReadOnlyList<ScopeChannelSlice> Channels,
    ScopeMathSlice? Math = null,
    ScopeXySlice? Xy = null,
    ScopeEyeSlice? Eye = null,
    bool Filtered = false,
    bool Rolled = false,
    bool Envelope = false,
    int AveragesTaken = 1);

/// <summary>
/// <summary>
/// The math channel as the client draws it: the same raw-or-columns shape as the slices beside it,
/// so every trace on the chart shares one x array.
///
/// <para><b>Its numbers are not necessarily volts</b>, which is why the unit travels with it. Every
/// field of <see cref="ScopeSliceMeasurements"/> is named <c>...V</c> and for A − B that is right;
/// for A × B, A / B, a derivative or an integral it is not, and a figure labelled volts on a scale
/// that is not volts is the plausible wrong answer this app's rules exist to prevent.</para>
/// </summary>
/// <param name="Operation">Which operation, as its enum name.</param>
/// <param name="Unit">What the numbers are in, as <see cref="WaveformMathUnit"/> spells it.</param>
/// <param name="Samples">The derived waveform, or null when the window is decimated.</param>
/// <param name="Min">Both extremes of every column, as the waveforms own are sent.</param>
/// <param name="Max">The other one.</param>
/// <param name="Measurements">Every reading of it, measured on the derived samples at full resolution.</param>
/// <param name="Gated">The same over the cursor gate, or null when none was asked for or it named nothing.</param>
/// <param name="ClampedSamples">How many samples A / B railed on; zero for every other operation.</param>
public sealed record ScopeMathSlice(
    string Operation,
    string Unit,
    float[]? Samples,
    float[]? Min,
    float[]? Max,
    ScopeSliceMeasurements Measurements,
    ScopeSliceMeasurements? Gated,
    int ClampedSamples);

/// <summary>
/// The X–Y figure as the client draws it: one channel against the other, with time thrown away.
///
/// <para><b>Pairs of samples, never columns</b> — see <see cref="ScopeXy"/>. A min/max per column
/// throws away which value of A went with which value of B, which is the whole of what this
/// picture is.</para>
/// </summary>
/// <param name="X">Channel A's samples — the horizontal axis.</param>
/// <param name="Y">Channel B's — the vertical one.</param>
/// <param name="FrameCount">How many pairs are here.</param>
/// <param name="WindowFrameCount">How many the window held; more than <paramref name="FrameCount"/> when it was truncated.</param>
/// <param name="SpanS">What the pairs drawn cover, in seconds.</param>
/// <param name="Correlation">Pearson's r over them — the stereo meter's own reading, and the one figure here that is always meaningful.</param>
/// <param name="PhaseDeg">How far B lags A, signed, or null where no single number is this figure's phase.</param>
/// <param name="AmplitudeRatio">B's AC RMS over A's.</param>
public sealed record ScopeXySlice(
    float[] X,
    float[] Y,
    int FrameCount,
    int WindowFrameCount,
    double SpanS,
    double Correlation,
    double? PhaseDeg,
    double? AmplitudeRatio);

/// <summary>
/// The eye diagram as the client draws it: the window folded on the signal’s own period, every
/// cycle over every other.
///
/// <para><b>Folds of samples, never columns</b> — see <see cref="ScopeEye"/>. A min/max per column
/// has already collapsed the folds into the band this picture exists to resolve.</para>
///
/// <para>Null when the request did not ask for one, and null when there is nothing to fold on: no
/// measured frequency, fewer than two whole periods in the window, or a period that is not a usable
/// fold. A picture folded on an invented period is a band of noise that looks exactly like a
/// jittery signal, so absent is the only honest answer.</para>
/// </summary>
/// <param name="Channel">Which input this is the eye of — the first one drawn.</param>
/// <param name="Folds">One array per cycle, all of <paramref name="FoldFrameCount"/> samples.</param>
/// <param name="FoldFrameCount">Samples in one fold, after any stride.</param>
/// <param name="FoldSeconds">What one fold covers, which is the picture’s x span.</param>
/// <param name="PeriodHz">The frequency it was folded on.</param>
/// <param name="FoldCount">How many folds are here.</param>
/// <param name="WindowFoldCount">How many whole folds the window held; more when truncated.</param>
/// <param name="MaxSpreadV">The widest the band gets anywhere across the fold, peak to peak.</param>
/// <param name="SpreadAtS">Where in the fold that worst spread sits.</param>
/// <param name="MeanSpreadV">The band’s average width over the fold.</param>
/// <param name="JitterS">The standard deviation of the folds’ own crossings, or null where too few cross.</param>
public sealed record ScopeEyeSlice(
    int Channel,
    float[][] Folds,
    int FoldFrameCount,
    double FoldSeconds,
    double PeriodHz,
    int FoldCount,
    int WindowFoldCount,
    double MaxSpreadV,
    double SpreadAtS,
    double MeanSpreadV,
    double? JitterS);


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
    double? TriggerLevelLowV,
    double? PulseWidthLoSeconds,
    double? PulseWidthHiSeconds,
    double? DropoutSeconds,
    double? AutoTimeoutSeconds,
    string? Accumulation,
    int? AverageCount,
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
        RecordAccumulation accumulation,
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
            TriggerLevelLowV ?? 0.0,
            PulseWidthLoSeconds ?? 0.0,

            // Carried through as a null, not defaulted: null is the upper bound being absent, which
            // is a condition of its own ("longer than T") rather than a missing figure.
            PulseWidthHiSeconds,
            DropoutSeconds ?? 0.0,
            AutoTimeoutSeconds ?? 0.5,
            accumulation,
            AverageCount ?? 16,
            ProcessLeft ?? true,
            ProcessRight ?? true,
            generator);
}
