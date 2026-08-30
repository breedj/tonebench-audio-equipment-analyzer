using Qa403.App.Diagnostics;

namespace Qa403.App.Endpoints;

/// <summary>
/// Arms and reports the <see cref="DiagnosticRecorder"/>. Deliberately a manual switch rather than
/// something the UI turns on by itself: this writes a CSV row per analyzed record and a WAV per
/// triggered capture, which is exactly what you want while chasing an intermittent artifact and
/// exactly what you do not want running unattended for a week.
/// </summary>
public static class DiagnosticsEndpoints
{
    /// <summary>Every field optional — an omitted one leaves that setting alone, so "just turn it on" is <c>{"enabled":true}</c>.</summary>
    public sealed record DiagnosticsConfigRequest(
        bool? Enabled,
        bool? LogMetrics,
        bool? CaptureRawOnTrigger,
        double? TriggerThresholdDb);

    public sealed record DiagnosticsStatus(
        bool Enabled,
        bool LogMetrics,
        bool CaptureRawOnTrigger,
        double TriggerThresholdDb,
        string Directory,
        long RecordsObserved,
        long CapturesWritten,
        long QueueDrops);

    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/diagnostics");

        group.MapGet("/", (DiagnosticRecorder recorder) => Results.Ok(Describe(recorder)));

        group.MapPost("/", (DiagnosticsConfigRequest request, DiagnosticRecorder recorder) =>
        {
            if (request.TriggerThresholdDb is { } threshold)
            {
                if (double.IsNaN(threshold) || threshold <= 0)
                    return Results.BadRequest("triggerThresholdDb must be a positive number of dB.");

                recorder.TriggerThresholdDb = threshold;
            }

            if (request.LogMetrics is { } logMetrics)
                recorder.LogMetrics = logMetrics;

            if (request.CaptureRawOnTrigger is { } captureRaw)
                recorder.CaptureRawOnTrigger = captureRaw;

            // Last, so a request that both configures and arms cannot record a record under the old
            // settings between the two writes.
            if (request.Enabled is { } enabled)
                recorder.Enabled = enabled;

            return Results.Ok(Describe(recorder));
        });

        // A reference "good" record to diff a triggered one against — the trigger only fires on the
        // bad ones, and a glitch is much easier to read next to the record either side of it.
        group.MapPost("/capture", (DiagnosticRecorder recorder) =>
        {
            if (!recorder.Enabled)
                return Results.Conflict("Diagnostics are not enabled. POST /api/diagnostics {\"enabled\":true} first.");

            recorder.RequestCapture();
            return Results.Ok(Describe(recorder));
        });
    }

    private static DiagnosticsStatus Describe(DiagnosticRecorder recorder) => new(
        recorder.Enabled,
        recorder.LogMetrics,
        recorder.CaptureRawOnTrigger,
        recorder.TriggerThresholdDb,
        recorder.Directory,
        recorder.RecordsObserved,
        recorder.CapturesWritten,
        recorder.QueueDrops);
}
