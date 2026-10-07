using Tonebench.App.Services;
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
                long resultId = await saveService.SaveAsync(request.MeasurementTypeId, request.Name, request.Notes, ct);
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

        group.MapPut("/{id:long}", async (long id, UpdateMeasurementRequest request, MeasurementRepository repo, CancellationToken ct) =>
        {
            bool updated = await repo.UpdateResultAsync(id, request.Name, request.Notes, ct);
            return updated ? Results.Ok() : Results.NotFound();
        });

        // Moves the result to the trash — see MeasurementRepository.TrashResultAsync, and
        // TrashEndpoints for what lists and restores it. Nothing is destroyed here; the permanent
        // delete is DELETE /api/trash/measurements/{id}, and the retention purge.
        group.MapDelete("/{id:long}", async (long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            bool trashed = await repo.TrashResultAsync(id, ct);
            return trashed ? Results.NoContent() : Results.NotFound();
        });

        group.MapGet("/{id:long}/export.csv", async (long id, MeasurementRepository repo, CancellationToken ct) =>
        {
            var detail = await repo.GetResultAsync(id, ct);
            if (detail is null)
                return Results.NotFound();

            // A sweep result stores metrics only, no spectrum rows, so it exports through its own
            // renderer -- one row per frequency. Branching on the stored kind rather than on
            // "found no spectra" keeps a genuinely empty FFT result a 404 instead of a 0-row CSV.
            string csv;
            if (MeasurementSaveService.IsImpedanceKind(detail.Kind) || MeasurementSaveService.IsOutputImpedanceKind(detail.Kind))
            {
                // Impedance stores its curves in the same response_trace rows but in different
                // units, so it is checked before the response branch rather than folded into it.
                // An output-impedance result is a Zout curve in those units plus two gain rows in
                // plain dB, which the impedance exporter writes as _db columns.
                var traces = await repo.GetResponseTracesAsync(detail.ResultId, ct);
                if (traces.Count == 0)
                    return Results.NotFound();

                csv = CsvExporter.ExportImpedance(detail, traces);
            }
            else if (MeasurementSaveService.IsFrequencyResponseKind(detail.Kind) || MeasurementSaveService.IsCrosstalkKind(detail.Kind))
            {
                // Crosstalk rows are response rows in plain dB, so the generic per-kind columns
                // (xtalk_l2r_min_db, ...) are exactly right for them.
                var traces = await repo.GetResponseTracesAsync(detail.ResultId, ct);
                if (traces.Count == 0)
                    return Results.NotFound();

                csv = CsvExporter.ExportResponse(detail, traces);
            }
            else if (MeasurementSaveService.IsSweepKind(detail.Kind))
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
            return Results.File(bytes, "text/csv", $"{SanitizeFileName(detail.Name)}.csv");
        });
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static async Task<MeasurementDetailResponse?> BuildDetailResponseAsync(
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
            detail.InRangeDbv, detail.OutRangeDbv, detail.GenJson, channels.ToArray());
    }

    /// <summary>
    /// Server-side min/max decimation of a stored full-resolution trace, to the same column budget
    /// and low-frequency edge the live chart uses. Reusing <see cref="SpectrumDecimator"/> is what
    /// makes a stored trace's columns come out built exactly the way the pipeline's own display
    /// frames are, so the chart's existing column-handling code works unchanged on stored traces.
    /// </summary>
    private static MeasurementColumnResponse[] DecimateForDisplay(StoredSpectrum spectrum, double displayLoFreqHz, int displayColumns)
    {
        int specLen = spectrum.Dbv.Length;
        int loBin = Math.Clamp((int)Math.Round(displayLoFreqHz / spectrum.BinSizeHz), 1, specLen - 2);
        int hiBin = specLen - 1;
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, displayColumns);

        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        SpectrumDecimator.DecimateLogWithCenters(spectrum.Dbv, columns, centerBins, loBin);

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
        summary.TimestampUtc, summary.SampleRateHz, summary.FftSize, summary.Channels, summary.ChannelHeadlines);
}

public sealed record SaveMeasurementRequest(long MeasurementTypeId, string Name, string? Notes);

public sealed record UpdateMeasurementRequest(string Name, string? Notes);

public sealed record MeasurementListResponse(MeasurementSummaryResponse[] Items);

public sealed record MeasurementSummaryResponse(
    long ResultId, string ProjectName, string MeasurementTypeName, string Name, string? Notes,
    DateTimeOffset TimestampUtc, double SampleRateHz, int FftSize, int Channels,
    IReadOnlyDictionary<int, ChannelHeadline> ChannelHeadlines);

public sealed record MeasurementDetailResponse(
    long ResultId, long MeasurementTypeId, string ProjectName, string MeasurementTypeName,
    /// <summary>The owning type's kind. A 'ThdVsFrequency' result has no ChannelsData -- its points come from /sweep-points instead.</summary>
    string Kind,
    string Name, string? Notes, DateTimeOffset TimestampUtc, string ParamsJson,
    double SampleRateHz, int FftSize, int Channels,
    double? InRangeDbv, double? OutRangeDbv, string? GenJson,
    MeasurementChannelResponse[] ChannelsData);

/// <param name="EnbwHz">The stored window's equivalent noise bandwidth at the capture's sample rate and FFT size — what converts the columns' dBV to dBV/√Hz on the client, per result, since an overlay's window need not match the live one's.</param>
public sealed record MeasurementChannelResponse(int Channel, MeasurementColumnResponse[] Columns, IReadOnlyDictionary<string, double> Metrics, double EnbwHz = 0.0);

public sealed record MeasurementColumnResponse(float FreqHz, float MinDbv, float MaxDbv);
