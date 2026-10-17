using Tonebench.App.Services;
using System.Text.Json;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

public static class MeasurementsEndpoints
{
    public static void MapMeasurementsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/measurements");

        group.MapPost("", async (
            SaveMeasurementRequest request, MeasurementSaveService saveService, MeasurementRepository repo, SpectrumPipeline pipeline, CancellationToken ct) =>
        {
            try
            {
                long resultId = await saveService.SaveAsync(
                    request.MeasurementTypeId, request.Name, request.Notes, request.ChartAnnotations, ct);
                var detail = await BuildDetailResponseAsync(resultId, repo, pipeline, ct);
                return Results.Ok(detail);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // The query parameter names ARE the wire contract, and getting one wrong fails open rather
        // than loudly: minimal APIs bind by name and silently ignore a parameter they do not
        // recognise, so a misspelled filter yields an unfiltered list rather than a 400. A caller
        // that then acts on every row -- deleting them, say -- does so believing it was scoped to
        // one measurement type. So these match the names used everywhere else in the API
        // (`measurementTypeId`, as on every save request) rather than a shorter local spelling.
        group.MapGet("", async (
            long? projectId, long? measurementTypeId, DateTimeOffset? from, DateTimeOffset? to, int? skip, int? take,
            MeasurementRepository repo, CancellationToken ct) =>
        {
            var filter = new MeasurementFilter(projectId, measurementTypeId, from, to, skip ?? 0, take ?? 100);
            var items = await repo.ListResultsAsync(filter, ct);
            return Results.Ok(new MeasurementListResponse(items.Select(ToSummaryResponse).ToArray()));
        });

        group.MapGet("/{id:long}", async (long id, MeasurementRepository repo, SpectrumPipeline pipeline, CancellationToken ct) =>
        {
            var response = await BuildDetailResponseAsync(id, repo, pipeline, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        // A saved trace's columns on their own, optionally re-decimated over a zoom window. The
        // detail route above gives the same columns over the whole span, together with the metrics
        // and parameters — none of which change when the chart is zoomed, which is the whole reason
        // this is its own route: a drag asks for it repeatedly.
        //
        // Omit either bound and it is the whole span, i.e. exactly what the detail route returns.
        group.MapGet("/{id:long}/spectrum", async (
            long id, double? loHz, double? hiHz, MeasurementRepository repo, SpectrumPipeline pipeline,
            CancellationToken ct) =>
        {
            var detail = await repo.GetResultAsync(id, ct);
            if (detail is null)
                return Results.NotFound();

            // Silently the whole span rather than a 400: the window comes from a chart's axis, so a
            // degenerate one means a zoom that has not settled, and the honest answer to "show me
            // this nonsense range" is the overview the client already knows how to draw.
            SpectrumViewport? window = null;
            if (loHz is { } lo && hiHz is { } hi && double.IsFinite(lo) && double.IsFinite(hi) && hi > lo && lo >= 0.0)
                window = new SpectrumViewport(lo, hi);

            var options = pipeline.Options;
            var channels = new List<MeasurementChannelResponse>();
            foreach (int channel in detail.AvailableChannels)
            {
                var spectrum = await repo.GetSpectrumAsync(detail.CaptureId, channel, ct);
                if (spectrum is null)
                    continue;

                var columns = DecimateForDisplay(spectrum, options.DisplayLoFreqHz, options.DisplayColumns, window);
                var metrics = detail.MetricsByChannel.TryGetValue(channel, out var m) ? m : new Dictionary<string, double>();
                channels.Add(new MeasurementChannelResponse(channel, columns, metrics, spectrum.EnbwHz));
            }

            return Results.Ok(new MeasurementSpectrumResponse(channels.ToArray()));
        });

        group.MapPut("/{id:long}", async (
            long id,
            UpdateMeasurementRequest request,
            MeasurementRepository repo,
            MeasurementSaveService saveService,
            CancellationToken ct) =>
        {
            bool updated = await repo.UpdateResultAsync(id, request.Name, request.Notes, ct);
            if (!updated) return Results.NotFound();

            // Null means leave them alone (see the request record) -- so the dialog's plain
            // name/notes save, and every caller written before annotations existed, cannot wipe a
            // result's notes by omission.
            if (request.ChartAnnotations is not null)
            {
                try
                {
                    await saveService.SetResultAnnotationsAsync(id, request.ChartAnnotations, ct);
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(ex.Message);
                }
            }

            return Results.Ok();
        });

        // Moves the result to the trash — see MeasurementRepository.TrashResultAsync, and
        // TrashEndpoints for what lists and restores it. Nothing is destroyed here; the permanent
        // delete is DELETE /api/trash/measurements/{id}, and the retention purge.
        group.MapDelete("/{id:long}", async (long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            bool trashed = await repo.TrashResultAsync(id, ct);
            return trashed ? Results.NoContent() : Results.NotFound();
        });

        // `part` is read by exactly one kind — an impulse response, whose result holds three grids
        // that cannot share a table (see CsvExporter.ExportImpulse). Every other kind ignores it
        // rather than rejecting it: a stray query string must not turn a working download into a
        // 400, and there is nothing for it to mean there.
        group.MapGet("/{id:long}/export.csv", async (
            long id, string? part, MeasurementRepository repo, CancellationToken ct) =>
        {
            var detail = await repo.GetResultAsync(id, ct);
            if (detail is null)
                return Results.NotFound();

            // A sweep result stores metrics only, no spectrum rows, so it exports through its own
            // renderer -- one row per frequency. Branching on the stored kind rather than on
            // "found no spectra" keeps a genuinely empty FFT result a 404 instead of a 0-row CSV.
            string csv;
            string suffix = string.Empty;
            if (ExportShapeFor(detail.Kind) == CsvExportShape.Impulse)
            {
                // Before the response branch, although an impulse result's magnitude and phase are
                // response_trace rows: its *own* grid — the impulse samples, the one full-resolution
                // thing it stores — is the default, and the response rows are one part of three.
                // Without this arm the kind matched nothing, fell through to the spectrum branch,
                // found no spectrum rows and answered 404 to a CSV button the history panel renders
                // on every row.
                if (ImpulsePart(part) is not { } resolved)
                    return Results.BadRequest(ImpulsePartMessage);

                if (resolved == ImpulseCsvPart.Curves)
                {
                    var impulseTraces = await repo.GetResponseTracesAsync(detail.ResultId, ct);
                    if (impulseTraces.Count == 0)
                        return Results.NotFound();

                    csv = CsvExporter.ExportResponse(detail, impulseTraces);
                    suffix = "-curves";
                }
                else
                {
                    var impulse = await repo.GetImpulseResultAsync(detail.ResultId, ct);

                    // A 404 rather than an empty file. Harmonic extraction is a checkbox on the
                    // measurement, so a result with no curves was asked not to have any, and an
                    // impulse result with no impulse is one no build of this app can write.
                    if (resolved == ImpulseCsvPart.Harmonics)
                    {
                        if (impulse is null || impulse.Harmonics.Count == 0)
                            return Results.NotFound();

                        csv = CsvExporter.ExportImpulseHarmonics(detail, impulse);
                        suffix = "-harmonics";
                    }
                    else
                    {
                        if (impulse is null || impulse.Impulses.Count == 0)
                            return Results.NotFound();

                        csv = CsvExporter.ExportImpulse(detail, impulse);
                    }
                }
            }
            else if (ExportShapeFor(detail.Kind) == CsvExportShape.Impedance)
            {
                var traces = await repo.GetResponseTracesAsync(detail.ResultId, ct);
                if (traces.Count == 0)
                    return Results.NotFound();

                csv = CsvExporter.ExportImpedance(detail, traces);
            }
            else if (ExportShapeFor(detail.Kind) == CsvExportShape.Response)
            {
                var traces = await repo.GetResponseTracesAsync(detail.ResultId, ct);
                if (traces.Count == 0)
                    return Results.NotFound();

                csv = CsvExporter.ExportResponse(detail, traces);
            }
            else if (ExportShapeFor(detail.Kind) == CsvExportShape.Sweep)
            {
                var points = await repo.GetSweepPointsAsync(detail.ResultId, ct);
                if (points.Count == 0)
                    return Results.NotFound();

                csv = CsvExporter.ExportSweep(detail, points);
            }
            else
            {
                var spectra = new List<StoredSpectrum>();
                foreach (int channel in detail.AvailableChannels)
                {
                    var spectrum = await repo.GetSpectrumAsync(detail.CaptureId, channel, ct);
                    if (spectrum is not null)
                        spectra.Add(spectrum);
                }

                if (spectra.Count == 0)
                    return Results.NotFound();

                csv = CsvExporter.Export(detail, spectra);
            }

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(csv);

            // The suffix is what keeps an impulse response's three parts from overwriting each other
            // in a downloads folder; it is empty for every other kind, so their file names are
            // unchanged.
            return Results.File(bytes, "text/csv", $"{SanitizeFileName(detail.Name)}{suffix}.csv");
        });
    }

    /// <summary>
    /// Which renderer a stored kind exports through, i.e. which tables its result actually wrote.
    ///
    /// <para>A kind missing an arm here does not fail — it falls to <see cref="Spectrum"/>, finds no
    /// <c>spectrum</c> rows and answers 404 to a CSV button the history panel renders on every row.
    /// That is what <c>ImpulseResponse</c> did. Hence <see cref="MeasurementsEndpoints.ExportShapeFor"/>
    /// and the coverage test over every kind constant.</para>
    /// </summary>
    internal enum CsvExportShape
    {
        /// <summary>Full-resolution <c>spectrum</c> blobs, one row per bin. Also the fallback, which is why a new kind must be added above.</summary>
        Spectrum,

        /// <summary><c>metric</c> rows per capture, one row per sweep point.</summary>
        Sweep,

        /// <summary><c>response_trace</c> rows in plain dB.</summary>
        Response,

        /// <summary>The same rows read as ohms.</summary>
        Impedance,

        /// <summary>Three grids that cannot share a table — see <see cref="ImpulseCsvPart"/>.</summary>
        Impulse,
    }

    /// <summary>Which of an impulse result's three grids the caller asked for.</summary>
    internal enum ImpulseCsvPart
    {
        /// <summary>The impulse samples against time: the one full-resolution thing the result stores, and the default.</summary>
        Impulse,

        /// <summary>The gated magnitude, phase and coherence against frequency — <c>response_trace</c> rows, rendered like any other response.</summary>
        Curves,

        /// <summary>The harmonic-distortion curves against fundamental frequency, long format.</summary>
        Harmonics,
    }

    internal const string ImpulsePartMessage =
        "An impulse response exports as part=impulse (the samples, the default), "
            + "part=curves (the gated magnitude and phase) or part=harmonics.";

    /// <summary>
    /// The one place a stored kind is mapped to its exporter, so the route's branches and any test
    /// of them cannot disagree about it.
    ///
    /// <para><b>The order is the rule.</b> Impulse is asked first although its magnitude and phase
    /// are <c>response_trace</c> rows, because its <i>own</i> grid is the default part. Impedance is
    /// asked before response for the same reason the other way round: it stores its curves in those
    /// same rows but in different units all the way down, so the response renderer would write
    /// ohms-in-dB into a column headed <c>_db</c> — true and useless. An output-impedance result is
    /// a Zout curve in those units plus two gain rows in plain dB, which the impedance renderer
    /// writes as <c>_db</c> columns. Crosstalk rows <i>are</i> response rows in plain dB, so the
    /// generic per-kind columns (<c>xtalk_l2r_min_db</c>, …) are exactly right for them.</para>
    /// </summary>
    internal static CsvExportShape ExportShapeFor(string kind)
    {
        if (MeasurementSaveService.IsImpulseResponseKind(kind))
            return CsvExportShape.Impulse;

        if (MeasurementSaveService.IsImpedanceKind(kind) || MeasurementSaveService.IsOutputImpedanceKind(kind))
            return CsvExportShape.Impedance;

        if (MeasurementSaveService.IsFrequencyResponseKind(kind) || MeasurementSaveService.IsCrosstalkKind(kind))
            return CsvExportShape.Response;

        if (MeasurementSaveService.IsSweepKind(kind))
            return CsvExportShape.Sweep;

        return CsvExportShape.Spectrum;
    }

    /// <summary>
    /// The requested part, or null when the name is not one of the three.
    ///
    /// <para>An absent or empty <c>part</c> is the default rather than an error: that is the plain
    /// link the history panel renders, and the query string only exists because one kind needs it.
    /// Case-insensitive, because this arrives typed into a URL as often as clicked.</para>
    /// </summary>
    internal static ImpulseCsvPart? ImpulsePart(string? part) => part?.ToLowerInvariant() switch
    {
        null or "" or "impulse" => ImpulseCsvPart.Impulse,
        "curves" => ImpulseCsvPart.Curves,
        "harmonics" => ImpulseCsvPart.Harmonics,
        _ => null,
    };

    private static string SanitizeFileName(string name)

    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    /// <summary>
    /// Internal so a test can pin the mapping rather than only the record's shape: adding a field
    /// to <see cref="MeasurementDetailResponse"/> and leaving this constructor call passing none
    /// of it compiles cleanly and answers null on the wire.
    /// </summary>
    internal static async Task<MeasurementDetailResponse?> BuildDetailResponseAsync(
        long resultId, MeasurementRepository repo, SpectrumPipeline pipeline, CancellationToken ct)
    {
        var detail = await repo.GetResultAsync(resultId, ct);
        if (detail is null)
            return null;

        var options = pipeline.Options;
        var channels = new List<MeasurementChannelResponse>();
        foreach (int channel in detail.AvailableChannels)
        {
            var spectrum = await repo.GetSpectrumAsync(detail.CaptureId, channel, ct);
            if (spectrum is null)
                continue;

            var columns = DecimateForDisplay(spectrum, options.DisplayLoFreqHz, options.DisplayColumns);
            var metrics = detail.MetricsByChannel.TryGetValue(channel, out var m) ? m : new Dictionary<string, double>();
            channels.Add(new MeasurementChannelResponse(channel, columns, metrics, spectrum.EnbwHz));
        }

        return new MeasurementDetailResponse(
            detail.ResultId, detail.MeasurementTypeId, detail.ProjectName, detail.MeasurementTypeName,
            detail.Kind,
            detail.Name, detail.Notes, detail.TimestampUtc, detail.ParamsJson,
            detail.SampleRateHz, detail.FftSize, detail.Channels,
            detail.InRangeDbv, detail.OutRangeDbv, detail.GenJson, channels.ToArray(),
            detail.Source, detail.PcmSampleRateHz);
    }

    /// <summary>
    /// Server-side min/max decimation of a stored full-resolution trace, to the same column budget
    /// and low-frequency edge the live chart uses. Reusing <see cref="SpectrumDecimator"/> is what
    /// makes a stored trace's columns come out built exactly the way the pipeline's own display
    /// frames are, so the chart's existing column-handling code works unchanged on stored traces.
    ///
    /// <para><paramref name="window"/> narrows it to a zoom: the span is sliced at the window's
    /// top bin and the decimator spreads the same column budget across what is left, so a shorter
    /// span is a denser one — the identical trick <c>SpectrumPipeline.BuildSpectrumFrame</c> plays
    /// for the live chart, and the reason a saved trace can sharpen on zoom at all. Every bin is
    /// in the database (<c>SpectrumCodec</c>, 2 bytes each); it is only this step that was
    /// throwing the detail away, once, over the whole span. Null is the whole span, and must stay
    /// byte-for-byte what it always produced — the detail endpoint still calls it that way.</para>
    ///
    /// <para>Note this min/maxes dBV where the pipeline min/maxes power and converts afterwards.
    /// Equivalent, because the conversion is monotone; worth knowing before anyone "aligns" them.</para>
    /// </summary>
    internal static MeasurementColumnResponse[] DecimateForDisplay(
        StoredSpectrum spectrum, double displayLoFreqHz, int displayColumns, SpectrumViewport? window = null)
    {
        int specLen = spectrum.Dbv.Length;
        if (specLen < 3)
            return [];

        int overviewLoBin = Math.Clamp((int)Math.Round(displayLoFreqHz / spectrum.BinSizeHz), 1, specLen - 2);

        int loBin = overviewLoBin;
        int hiBin = specLen - 1;
        if (window is not null)
        {
            loBin = Math.Clamp((int)Math.Floor(window.LoFreqHz / spectrum.BinSizeHz), overviewLoBin, specLen - 2);
            hiBin = Math.Clamp((int)Math.Ceiling(window.HiFreqHz / spectrum.BinSizeHz), loBin + 1, specLen - 1);
        }

        // Never more columns than there are bins to put in them — past that point extra columns
        // repeat single bins. This is also the floor a tight zoom reaches: one bin per column, no
        // min/max band left, the raw FFT.
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, displayColumns);

        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        SpectrumDecimator.DecimateLogWithCenters(spectrum.Dbv.AsSpan(0, hiBin + 1), columns, centerBins, loBin);

        var result = new MeasurementColumnResponse[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            result[i] = new MeasurementColumnResponse(
                (float)(centerBins[i] * spectrum.BinSizeHz), (float)columns[i].Min, (float)columns[i].Max);
        }

        return result;
    }

    private static MeasurementSummaryResponse ToSummaryResponse(MeasurementSummary summary) => new(
        summary.ResultId, summary.ProjectName, summary.MeasurementTypeName, summary.Name, summary.Notes,
        summary.TimestampUtc, summary.SampleRateHz, summary.FftSize, summary.Channels, summary.ChannelHeadlines,
        summary.Source);
}

/// <param name="ChartAnnotations">
/// The notes on the chart at the moment of the save, which the result keeps as its own copy.
/// <b>Null means none were sent</b>, in which case the result takes whatever its measurement
/// type has stored -- see <c>MeasurementSaveService.SaveAsync</c> for why this one
/// client-staged field travels with the save rather than only being read back from the type.
/// </param>
public sealed record SaveMeasurementRequest(
    long MeasurementTypeId,
    string Name,
    string? Notes,
    IReadOnlyList<JsonElement>? ChartAnnotations = null);

/// <param name="ChartAnnotations">
/// This result's own annotations. <b>Null means do not touch them</b> and an <b>empty list
/// means cleared</b>: the Edit measurement dialog saves a name and notes for every kind, and a
/// request that simply does not carry this field must not wipe what a result was saved with.
/// Refused with a 409 on any kind but FFTSpectrum, rather than quietly ignored.
/// </param>
public sealed record UpdateMeasurementRequest(
    string Name,
    string? Notes,
    IReadOnlyList<JsonElement>? ChartAnnotations = null);

public sealed record MeasurementListResponse(MeasurementSummaryResponse[] Items);

public sealed record MeasurementSummaryResponse(
    long ResultId, string ProjectName, string MeasurementTypeName, string Name, string? Notes,
    DateTimeOffset TimestampUtc, double SampleRateHz, int FftSize, int Channels,
    IReadOnlyDictionary<int, ChannelHeadline> ChannelHeadlines,
    /// <summary>
    /// <c>measurement_result.source</c>: null for a measured run, <c>'simulation'</c> for an
    /// imported one. <b>On the response record and not only on the domain record</b> -- threading
    /// a field through the repository and forgetting the endpoint's own DTO is a silent default
    /// rather than an error, and it showed up as every imported simulation rendering in the
    /// history list as though it had been measured.
    /// </summary>
    string? Source = null);

public sealed record MeasurementDetailResponse(
    long ResultId, long MeasurementTypeId, string ProjectName, string MeasurementTypeName,
    /// <summary>The owning type's kind. A 'ThdVsFrequency' result has no ChannelsData -- its points come from /sweep-points instead.</summary>
    string Kind,
    string Name, string? Notes, DateTimeOffset TimestampUtc, string ParamsJson,
    double SampleRateHz, int FftSize, int Channels,
    double? InRangeDbv, double? OutRangeDbv, string? GenJson,
    MeasurementChannelResponse[] ChannelsData,
    /// <summary>See <see cref="MeasurementSummaryResponse.Source"/>.</summary>
    string? Source = null,
    /// <summary>
    /// The rate of this capture's stored samples, when it has any. Non-null is what says the result
    /// can be re-analysed at a different window or FFT size without the source file.
    /// </summary>
    double? PcmSampleRateHz = null);

/// <param name="EnbwHz">The stored window's equivalent noise bandwidth at the capture's sample rate and FFT size — what converts the columns' dBV to dBV/√Hz on the client, per result, since an overlay's window need not match the live one's.</param>
public sealed record MeasurementChannelResponse(int Channel, MeasurementColumnResponse[] Columns, IReadOnlyDictionary<string, double> Metrics, double EnbwHz = 0.0);

/// <summary>
/// A saved measurement's decimated columns on their own, for a chart that has been zoomed. A
/// wrapper rather than a bare array so one AppJsonContext entry covers it — a collection return
/// type needs its own registration there, and a missing one fails only in the trimmed publish.
/// </summary>
public sealed record MeasurementSpectrumResponse(MeasurementChannelResponse[] ChannelsData);

public sealed record MeasurementColumnResponse(float FreqHz, float MinDbv, float MaxDbv);
