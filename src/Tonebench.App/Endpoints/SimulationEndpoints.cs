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

        // ---- the .ac sweeps, which become frequency-response results rather than spectra ----
        //
        // Separate routes rather than a discriminated one, because the two inspect responses have
        // nothing in common: a transient run is described by a duration and a timestep, a sweep by
        // a range and a point density, and a shape carrying both would be eight optional fields
        // serving two callers that never read each other's.

        group.MapPost("/inspect-ac", (
            InspectSimulationRequest request, SimulationResponseImportService import, CancellationToken ct) =>
            Guard(() => Results.Ok(ToAcResponse(import.Inspect(request.Path, ct)))));

        group.MapPost("/{measurementTypeId:long}/import-ac", async (
            long measurementTypeId,
            ImportAcSimulationRequest request,
            SimulationResponseImportService import,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                if (!EnumParsing.TryParse(
                        request.BandwidthReference, BandwidthReferenceMode.BandMedian,
                        out BandwidthReferenceMode mode, out var referenceError))
                {
                    return Results.BadRequest(referenceError);
                }

                var resultIds = await import.ImportAsync(measurementTypeId, request.ToRequest(mode), ct);
                return Results.Ok(new ImportedSimulationsResponse(resultIds));
            }));

        // The same `.ac` file read as ohms instead of as a gain. Its own route rather than a mode
        // on the one above: the two take different traces, produce different rows and land on
        // different measurement kinds, and a shape carrying both would be four optional fields
        // serving two callers that never read each other's.
        group.MapPost("/{measurementTypeId:long}/import-impedance", async (
            long measurementTypeId,
            ImportAcImpedanceRequest request,
            SimulationResponseImportService import,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                if (!EnumParsing.TryParse(
                        request.CurrentDirection, AcImpedanceCurrentDirection.Auto,
                        out AcImpedanceCurrentDirection direction, out var directionError))
                {
                    return Results.BadRequest(directionError);
                }

                var resultIds = await import.ImportImpedanceAsync(
                    measurementTypeId, request.ToRequest(direction), ct);
                return Results.Ok(new ImportedSimulationsResponse(resultIds));
            }));

        // The only thing an imported impedance can be asked to change. Its own route rather than an
        // arm of reanalyze-response, because the two rebuild different rows from different stored
        // settings -- and because a request shape carrying a band, a reference and a direction would
        // let a caller ask for three things of which two are refused.
        app.MapPost("/api/measurements/{resultId:long}/reanalyze-impedance", async (
            long resultId,
            ReanalyzeAcImpedanceRequest request,
            SimulationResponseImportService import,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                AcImpedanceCurrentDirection? direction = null;
                if (request.CurrentDirection is not null)
                {
                    if (!EnumParsing.TryParse(
                            request.CurrentDirection, AcImpedanceCurrentDirection.Auto,
                            out AcImpedanceCurrentDirection parsed, out var directionError))
                    {
                        return Results.BadRequest(directionError);
                    }

                    direction = parsed;
                }

                bool applied = await import.ReanalyzeImpedanceAsync(
                    resultId, new AcImpedanceReanalyzeRequest(direction), ct);

                return applied
                    ? Results.Ok(new ImportedSimulationResponse(resultId))
                    : Results.BadRequest(
                        "This result is not an imported simulated impedance, so there is no current direction " +
                        "to change.");
            }));

        app.MapPost("/api/measurements/{resultId:long}/reanalyze-response", async (
            long resultId,
            ReanalyzeAcSimulationRequest request,
            SimulationResponseImportService import,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                // The reference is sent whole or not at all: a mode without its band, or a band
                // without its mode, would leave the other half to be guessed from a default and
                // quietly move a setting the caller did not mention.
                BandwidthReferenceSpec? reference = null;
                if (request.BandwidthReference is not null)
                {
                    if (!EnumParsing.TryParse(
                            request.BandwidthReference, BandwidthReferenceMode.BandMedian,
                            out BandwidthReferenceMode mode, out var referenceError))
                    {
                        return Results.BadRequest(referenceError);
                    }

                    if (request.BandwidthReferenceLowHz is not { } lowHz ||
                        request.BandwidthReferenceHighHz is not { } highHz)
                    {
                        return Results.BadRequest(
                            "A bandwidth reference needs its band as well as its mode.");
                    }

                    reference = new BandwidthReferenceSpec(mode, lowHz, highHz);
                }

                bool done = await import.ReanalyzeAsync(
                    resultId,
                    new AcSimulationReanalyzeRequest(
                        request.LevelOffsetDb, request.AnalysisLowHz, request.AnalysisHighHz,
                        reference, request.RightChannelIsReference),
                    ct);
                return done
                    ? Results.Ok(new ImportedSimulationResponse(resultId))
                    : Results.Conflict(
                        "This result did not come from an imported .ac sweep, so it cannot be re-analysed. " +
                        "Only imported simulations can.");
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

    private static AcSimulationFileResponse ToAcResponse(AcSimulationFileInfo info) => new(
        info.Path,
        info.Title,
        info.Date,
        info.Plotname,
        info.Flags,
        info.PointCount,
        info.StartFreqHz,
        info.EndFreqHz,
        info.PointsPerDecade,
        info.IsLogSpaced,
        [.. info.Traces.Select(t => new SimulationTraceResponse(t.Index, t.Name, t.Unit))],
        info.Warnings,
        [.. info.Sweeps.Select(s => new AcSimulationSweepResponse(
            s.Index, s.PointCount, s.StartFreqHz, s.EndFreqHz, s.PointsPerDecade, s.IsLogSpaced, s.Label))]);

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

/// <summary>What an <c>.ac</c> sweep looks like, for the dialog's pickers and its readout.</summary>
public sealed record AcSimulationFileResponse(
    string Path,
    string Title,
    string? Date,
    string Plotname,
    IReadOnlyList<string> Flags,
    int PointCount,
    double StartFreqHz,
    double EndFreqHz,
    double PointsPerDecade,
    bool IsLogSpaced,
    IReadOnlyList<SimulationTraceResponse> Traces,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AcSimulationSweepResponse> Sweeps);

/// <summary>
/// One sweep of an <c>.ac</c> file, so the dialog can say how many there are and offer them
/// individually. An ordinary run carries exactly one.
/// </summary>
public sealed record AcSimulationSweepResponse(
    int Index,
    int PointCount,
    double StartFreqHz,
    double EndFreqHz,
    double PointsPerDecade,
    bool IsLogSpaced,
    string? Label);

/// <param name="BandwidthReference">BandMedian or Peak, as the response measurements spell it.</param>
public sealed record ImportAcSimulationRequest(
    string Path,
    int OutputTraceIndex,
    int? ReferenceTraceIndex = null,
    double LevelOffsetDb = 0.0,
    double AnalysisLowHz = 20.0,
    double AnalysisHighHz = 20_000.0,
    string? BandwidthReference = null,
    double BandwidthReferenceLowHz = BandwidthReferenceSpec.DefaultLowHz,
    double BandwidthReferenceHighHz = BandwidthReferenceSpec.DefaultHighHz,
    string? Name = null,
    string? Notes = null,
    IReadOnlyList<int>? SweepIndices = null)
{
    internal AcSimulationImportRequest ToRequest(BandwidthReferenceMode mode) => new(
        Path, OutputTraceIndex, ReferenceTraceIndex, LevelOffsetDb, AnalysisLowHz, AnalysisHighHz,
        new BandwidthReferenceSpec(mode, BandwidthReferenceLowHz, BandwidthReferenceHighHz), Name, Notes,
        SweepIndices);
}

/// <param name="CurrentDirection">
/// Auto, AsStored or Inverted. Auto is what is wanted almost always — a simulator's source current
/// flows into its own + terminal, which puts V/I half a turn round, and a passive one-port's real
/// part cannot be negative across its band.
/// </param>
public sealed record ImportAcImpedanceRequest(
    string Path,
    int VoltageTraceIndex,
    int CurrentTraceIndex,
    string? CurrentDirection = null,
    string? Name = null,
    string? Notes = null,
    IReadOnlyList<int>? SweepIndices = null)
{
    internal AcImpedanceImportRequest ToRequest(AcImpedanceCurrentDirection direction) => new(
        Path, VoltageTraceIndex, CurrentTraceIndex, direction, Name, Notes, SweepIndices);
}

/// <param name="CurrentDirection">
/// Auto, AsStored or Inverted, or omitted to leave the curve as it is. Auto is re-decided against
/// the stored curve rather than remembered, so asking for it on a result that is already the right
/// way up does nothing — which is what it should do.
/// </param>
public sealed record ReanalyzeAcImpedanceRequest(string? CurrentDirection = null);

public sealed record ReanalyzeAcSimulationRequest(
    double? LevelOffsetDb = null,
    double? AnalysisLowHz = null,
    double? AnalysisHighHz = null,
    string? BandwidthReference = null,
    double? BandwidthReferenceLowHz = null,
    double? BandwidthReferenceHighHz = null,
    bool? RightChannelIsReference = null);

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

/// <summary>
/// What an `.ac` import returns: one result id per sweep imported, in sweep order. A list rather
/// than a single id even for an ordinary one-sweep file, so the client has one shape to read.
/// </summary>
public sealed record ImportedSimulationsResponse(IReadOnlyList<long> ResultIds);
