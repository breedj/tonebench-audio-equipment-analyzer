using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

/// <summary>
/// Importing an LTspice transient run as a measurement result, and re-analysing one afterwards.
///
/// <para>The browser cannot read a local file, so an import is a <b>path</b> the server reads --
/// the same shape as the user-weighting import, picked through the same in-app file listing the
/// project dialogs use. Exceptions map to HTTP by type, also as there: a missing file is a 404 and
/// anything the parser or the settings refuse is a 400 carrying the message, which is the whole
/// point of those messages naming the LTspice directive to change.</para>
///
/// <para><b>Nothing here is behind <see cref="MeasurementRunGuard"/>, deliberately.</b> No device
/// is involved: an import reads a file and writes rows, exactly as saving a measurement does, and
/// that has never been refused mid-run. Refusing an import while a long sweep runs would only stop
/// someone preparing a comparison during the one stretch of time they have for it.</para>
///
/// <para>No project open is already a 409 everywhere, from the repository call that raises it.</para>
/// </summary>
public static class SimulationEndpoints
{
    public static void MapSimulationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/simulations");

        // Reads the header and the time axis only -- the trace data is not touched, so this stays
        // fast on a 100 MB file and the dialog can offer its pickers before anything is committed.
        group.MapPost("/inspect", (
            InspectSimulationRequest request, SimulationImportService import, CancellationToken ct) =>
            Guard(() =>
            {
                var info = import.Inspect(
                    request.Path, request.MeasurementHiFreqHz ?? 20_000.0, request.MeasurementLoFreqHz ?? 20.0, ct);
                return Results.Ok(ToResponse(info, request.MeasurementHiFreqHz ?? 20_000.0));
            }));

        group.MapPost("/{measurementTypeId:long}/import", async (
            long measurementTypeId,
            ImportSimulationRequest request,
            SimulationImportService import,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                long resultId = await import.ImportAsync(measurementTypeId, request.ToRequest(), ct);
                return Results.Ok(new ImportedSimulationResponse(resultId));
            }));

        // On /api/measurements rather than here, because it is an operation on a stored result
        // rather than on a file -- but kept in this file, since everything it needs to know about
        // is the simulation import's.
        app.MapPost("/api/measurements/{resultId:long}/reanalyze", async (
            long resultId,
            ReanalyzeSimulationRequest request,
            SimulationImportService import,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                bool done = await import.ReanalyzeAsync(resultId, request.ToRequest(), ct);
                return done
                    ? Results.Ok(new ImportedSimulationResponse(resultId))
                    : Results.Conflict(
                        "This result has no stored simulation samples, so it cannot be re-analysed. " +
                        "Only imported simulations can.");
            }));
    }

    /// <summary>
    /// The exception-to-status mapping, in one place so the three routes cannot disagree. A
    /// <see cref="FormatException"/> is the parser or the settings refusing the file and its message
    /// is written for the user -- it names the LTspice directive to change -- so it travels as the
    /// 400's body rather than being swallowed.
    /// </summary>
    private static IResult Guard(Func<IResult> body)
    {
        try
        {
            return body();
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound($"No file at {ex.FileName}.");
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> body)
    {
        try
        {
            return await body();
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound($"No file at {ex.FileName}.");
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static SimulationFileResponse ToResponse(SimulationFileInfo info, double measurementHiFreqHz)
    {
        double usableS = info.DurationS;
        return new SimulationFileResponse(
            info.Path,
            info.Title,
            info.Date,
            info.Plotname,
            info.Flags,
            info.PointCount,
            info.StartTimeS,
            info.EndTimeS,
            info.DurationS,
            info.NativeRateHz,
            info.MinStepS,
            info.MedianStepS,
            info.MaxStepS,
            info.LargestUsableFftSize,
            SimulationAnalysis.SuggestedFftSize(usableS, measurementHiFreqHz, info.NativeRateHz),
            // Bin width follows the duration and nothing else, which is the one thing about this
            // measurement the dialog has to get across -- a larger FFT cannot improve it.
            usableS > 0 ? 1.0 / usableS : 0.0,
            [.. info.Traces.Select(t => new SimulationTraceResponse(t.Index, t.Name, t.Unit))],
            info.Warnings);
    }
}

public sealed record InspectSimulationRequest(
    string Path, double? MeasurementHiFreqHz = null, double? MeasurementLoFreqHz = null);

public sealed record SimulationTraceResponse(int Index, string Name, string Unit);

/// <param name="BinWidthHz">
/// What the result's resolution will be, at any FFT size: it is <c>1 / duration</c>. Sent so the
/// dialog can say what a longer simulation would buy, rather than letting someone raise the FFT
/// size expecting finer bins.
/// </param>
/// <param name="SuggestedFftSize">
/// The smallest size whose derived Nyquist clears the measurement band. Larger only computes more
/// empty spectrum above it.
/// </param>
public sealed record SimulationFileResponse(
    string Path,
    string Title,
    string? Date,
    string Plotname,
    IReadOnlyList<string> Flags,
    int PointCount,
    double StartTimeS,
    double EndTimeS,
    double DurationS,
    double NativeRateHz,
    double MinStepS,
    double MedianStepS,
    double MaxStepS,
    int LargestFftSize,
    int SuggestedFftSize,
    double BinWidthHz,
    IReadOnlyList<SimulationTraceResponse> Traces,
    IReadOnlyList<string> Warnings);

public sealed record ImportSimulationRequest(
    string Path,
    int LeftTraceIndex,
    int? RightTraceIndex = null,
    double StartTimeS = 0.0,
    double LevelOffsetDb = 0.0,
    int FftSize = 0,
    WindowType Window = WindowType.Hann,
    double MeasurementLoFreqHz = 20.0,
    double MeasurementHiFreqHz = 20_000.0,
    string? Name = null,
    string? Notes = null,
    bool PeriodicRecord = true)
{
    internal SimulationImportRequest ToRequest() => new(
        Path, LeftTraceIndex, RightTraceIndex, StartTimeS, LevelOffsetDb, FftSize, Window,
        MeasurementLoFreqHz, MeasurementHiFreqHz, Name, Notes, PeriodicRecord);
}

public sealed record ReanalyzeSimulationRequest(
    double? StartTimeS = null,
    double? LevelOffsetDb = null,
    int? FftSize = null,
    WindowType? Window = null,
    double? MeasurementLoFreqHz = null,
    double? MeasurementHiFreqHz = null,
    bool? PeriodicRecord = null)
{
    internal SimulationReanalyzeRequest ToRequest() => new(
        StartTimeS, LevelOffsetDb, FftSize, Window, MeasurementLoFreqHz, MeasurementHiFreqHz, PeriodicRecord);
}

public sealed record ImportedSimulationResponse(long ResultId);
