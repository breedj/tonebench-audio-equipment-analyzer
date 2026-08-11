using Dapper;
using Microsoft.Data.Sqlite;

namespace Qa403.Data;

/// <summary>
/// Measurement results: Dapper for <c>measurement_result</c>/<c>capture</c>/<c>metric</c>, raw
/// <see cref="SqliteCommand"/> + <see cref="SqliteBlob"/> for the <c>spectrum.mag_db</c> BLOB.
///
/// Every result owns exactly one <c>capture</c> row today, at <c>seq_index = 0</c>. That column
/// exists so a future time-series measurement is just "more capture rows under the same result,"
/// ordered by <c>seq_index</c> -- no schema change needed when that lands.
/// </summary>
public sealed class MeasurementRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task<long> SaveResultAsync(MeasurementSaveRequest request, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        long resultId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO measurement_result
                (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated)
            VALUES (@MeasurementTypeId, @Name, @Notes, @Ts, @ParamsJson, @DeviceName, @Serial, @FwVer, @IsSimulated);
            SELECT last_insert_rowid();
            """,
            new
            {
                request.MeasurementTypeId,
                request.Name,
                request.Notes,
                Ts = Qa403Timestamp.Now(),
                request.ParamsJson,
                request.DeviceName,
                request.Serial,
                request.FwVer,
                request.IsSimulated,
            },
            transaction);

        long captureId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO capture
                (measurement_result_id, seq_index, ts, fs, n, in_range_dbv, out_range_dbv, gen_json, channels)
            VALUES (@ResultId, 0, @Ts, @Fs, @N, @InRangeDbv, @OutRangeDbv, @GenJson, @Channels);
            SELECT last_insert_rowid();
            """,
            new
            {
                ResultId = resultId,
                Ts = Qa403Timestamp.Now(),
                Fs = request.SampleRateHz,
                N = request.FftSize,
                request.InRangeDbv,
                request.OutRangeDbv,
                request.GenJson,
                request.Channels,
            },
            transaction);

        foreach (var spectrum in request.Spectra)
            await InsertSpectrumAsync(connection, transaction, captureId, spectrum, ct);

        foreach (var metric in request.Metrics)
        {
            await connection.ExecuteAsync(
                "INSERT INTO metric (capture_id, channel, name, value, unit) VALUES (@CaptureId, @Channel, @Name, @Value, @Unit);",
                new { CaptureId = captureId, metric.Channel, metric.Name, metric.Value, metric.Unit },
                transaction);
        }

        await transaction.CommitAsync(ct);
        return resultId;
    }

    private static async Task InsertSpectrumAsync(
        SqliteConnection connection, SqliteTransaction transaction, long captureId, StoredSpectrumInput spectrum, CancellationToken ct)
    {
        int byteLength = SpectrumCodec.ByteLength(spectrum.Dbv.Length);

        long spectrumId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO spectrum (capture_id, channel, window, s1, s2, enbw_hz, bin_hz, norm, mag_db)
            VALUES (@CaptureId, @Channel, @Window, @S1, @S2, @EnbwHz, @BinSizeHz, @Norm, zeroblob(@ByteLength));
            SELECT last_insert_rowid();
            """,
            new
            {
                CaptureId = captureId,
                spectrum.Channel,
                Window = spectrum.Window.ToString(),
                S1 = spectrum.Sums.S1,
                S2 = spectrum.Sums.S2,
                EnbwHz = spectrum.Sums.EnbwHz(spectrum.SampleRateHz),
                spectrum.BinSizeHz,
                Norm = NormToText(spectrum.Norm),
                ByteLength = byteLength,
            },
            transaction);

        byte[] encoded = new byte[byteLength];
        SpectrumCodec.EncodeTo(spectrum.Dbv, encoded);

        using var blob = new SqliteBlob(connection, "spectrum", "mag_db", spectrumId);
        await blob.WriteAsync(encoded, ct);
    }

    public async Task<IReadOnlyList<MeasurementSummary>> ListResultsAsync(MeasurementFilter filter, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        if (filter.ProjectId is { } projectId)
        {
            conditions.Add("p.id = @projectId");
            parameters.Add("projectId", projectId);
        }
        if (filter.MeasurementTypeId is { } typeId)
        {
            conditions.Add("mt.id = @typeId");
            parameters.Add("typeId", typeId);
        }
        if (filter.From is { } from)
        {
            conditions.Add("mr.ts >= @from");
            parameters.Add("from", Qa403Timestamp.FormatUtc(from));
        }
        if (filter.To is { } to)
        {
            conditions.Add("mr.ts < @to");
            parameters.Add("to", Qa403Timestamp.FormatUtc(to));
        }

        string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        parameters.Add("take", filter.Take);
        parameters.Add("skip", filter.Skip);

        var rows = (await connection.QueryAsync<ResultSummaryRowRaw>(
            $"""
            SELECT mr.id AS ResultId, p.name AS ProjectName, mt.name AS MeasurementTypeName,
                   mr.name AS Name, mr.notes AS Notes, mr.ts AS TimestampUtc,
                   c.id AS CaptureId, c.fs AS SampleRateHz, c.n AS FftSize, c.channels AS Channels
            FROM measurement_result mr
            JOIN measurement_type mt ON mt.id = mr.measurement_type_id
            JOIN project p ON p.id = mt.project_id
            JOIN capture c ON c.measurement_result_id = mr.id AND c.seq_index = 0
            {whereClause}
            ORDER BY mr.ts DESC
            LIMIT @take OFFSET @skip;
            """,
            parameters)).ToList();

        if (rows.Count == 0)
            return [];

        var captureIds = rows.Select(r => r.CaptureId).ToArray();
        var metricRows = await connection.QueryAsync<MetricRowRaw>(
            """
            SELECT capture_id AS CaptureId, channel AS Channel, name AS Name, value AS Value
            FROM metric
            WHERE capture_id IN @captureIds AND name IN ('thd_percent', 'thd_n_percent', 'snr_db');
            """,
            new { captureIds });

        var byCaptureChannel = metricRows.ToLookup(m => (m.CaptureId, m.Channel));

        return rows.Select(r =>
        {
            var channels = new Dictionary<int, ChannelHeadline>();
            foreach (var group in byCaptureChannel.Where(g => g.Key.CaptureId == r.CaptureId))
            {
                var values = group.ToDictionary(m => m.Name, m => m.Value);
                channels[(int)group.Key.Channel] = new ChannelHeadline(
                    values.GetValueOrDefault("thd_percent"),
                    values.GetValueOrDefault("thd_n_percent"),
                    values.GetValueOrDefault("snr_db"));
            }

            return new MeasurementSummary(
                r.ResultId, r.ProjectName, r.MeasurementTypeName, r.Name, r.Notes,
                Qa403Timestamp.Parse(r.TimestampUtc), r.SampleRateHz, (int)r.FftSize, (int)r.Channels, channels);
        }).ToList();
    }

    public async Task<MeasurementDetail?> GetResultAsync(long resultId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<ResultDetailRowRaw>(
            """
            SELECT mr.id AS ResultId, mr.measurement_type_id AS MeasurementTypeId,
                   p.name AS ProjectName, mt.name AS MeasurementTypeName,
                   mr.name AS Name, mr.notes AS Notes, mr.ts AS TimestampUtc, mr.params_json AS ParamsJson,
                   c.id AS CaptureId, c.fs AS SampleRateHz, c.n AS FftSize, c.channels AS Channels,
                   c.in_range_dbv AS InRangeDbv, c.out_range_dbv AS OutRangeDbv, c.gen_json AS GenJson
            FROM measurement_result mr
            JOIN measurement_type mt ON mt.id = mr.measurement_type_id
            JOIN project p ON p.id = mt.project_id
            JOIN capture c ON c.measurement_result_id = mr.id AND c.seq_index = 0
            WHERE mr.id = @resultId;
            """,
            new { resultId });

        if (row is null)
            return null;

        var availableChannels = (await connection.QueryAsync<int>(
            "SELECT channel FROM spectrum WHERE capture_id = @captureId ORDER BY channel;",
            new { captureId = row.CaptureId })).ToList();

        var metricRows = await connection.QueryAsync<MetricRowRaw>(
            "SELECT capture_id AS CaptureId, channel AS Channel, name AS Name, value AS Value FROM metric WHERE capture_id = @captureId;",
            new { captureId = row.CaptureId });

        var metricsByChannel = metricRows
            .GroupBy(m => (int)m.Channel)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, double>)g.ToDictionary(m => m.Name, m => m.Value));

        return new MeasurementDetail(
            row.ResultId, row.MeasurementTypeId, row.ProjectName, row.MeasurementTypeName,
            row.Name, row.Notes, Qa403Timestamp.Parse(row.TimestampUtc), row.ParamsJson,
            row.CaptureId, row.SampleRateHz, (int)row.FftSize, (int)row.Channels,
            row.InRangeDbv, row.OutRangeDbv, row.GenJson,
            availableChannels, metricsByChannel);
    }

    public async Task<StoredSpectrum?> GetSpectrumAsync(long captureId, int channel, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var row = await connection.QuerySingleOrDefaultAsync<SpectrumRowRaw>(
            """
            SELECT id AS Id, window AS Window, s1 AS S1, s2 AS S2, enbw_hz AS EnbwHz, bin_hz AS BinSizeHz,
                   norm AS Norm, length(mag_db) AS ByteLength
            FROM spectrum WHERE capture_id = @captureId AND channel = @channel;
            """,
            new { captureId, channel });

        if (row is null)
            return null;

        byte[] buffer = new byte[(int)row.ByteLength];
        using (var blob = new SqliteBlob(connection, "spectrum", "mag_db", row.Id, readOnly: true))
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await blob.ReadAsync(buffer.AsMemory(totalRead), ct);
                if (read == 0)
                    break;
                totalRead += read;
            }
        }

        var dbv = new double[row.ByteLength / SpectrumCodec.ByteLength(1)];
        SpectrumCodec.DecodeTo(buffer, dbv);

        return new StoredSpectrum(
            channel, Enum.Parse<Qa403.Dsp.WindowType>(row.Window), row.S1, row.S2, row.EnbwHz, row.BinSizeHz,
            NormFromText(row.Norm), dbv);
    }

    /// <summary>Deletes the result's captures (and their spectra/metrics) then the result itself.</summary>
    public async Task<bool> DeleteResultAsync(long resultId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        var captureIds = (await connection.QueryAsync<long>(
            "SELECT id FROM capture WHERE measurement_result_id = @resultId;", new { resultId }, transaction)).ToList();

        if (captureIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM metric WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
            await connection.ExecuteAsync("DELETE FROM spectrum WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
            await connection.ExecuteAsync("DELETE FROM capture WHERE id IN @captureIds;", new { captureIds }, transaction);
        }

        int affected = await connection.ExecuteAsync("DELETE FROM measurement_result WHERE id = @resultId;", new { resultId }, transaction);

        await transaction.CommitAsync(ct);
        return affected > 0;
    }

    private static string NormToText(SpectrumNorm norm) => norm switch
    {
        SpectrumNorm.Amplitude => "amplitude",
        SpectrumNorm.Psd => "psd",
        _ => throw new ArgumentOutOfRangeException(nameof(norm), norm, null),
    };

    private static SpectrumNorm NormFromText(string text) => text switch
    {
        "amplitude" => SpectrumNorm.Amplitude,
        "psd" => SpectrumNorm.Psd,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unrecognised spectrum.norm value."),
    };

    // Numeric fields backed by an INTEGER-affinity column (fs, n, channels, channel, and the
    // length() of a BLOB) are declared `long` here, not the narrower domain type -- SQLite's
    // dynamic typing returns Int64 for every INTEGER-affinity column regardless of what CLR type
    // wrote it, and Dapper's record/constructor binding requires an exact type match (unlike its
    // single-column scalar path, which is more lenient). Narrowed to `int` at the call site.
    private sealed record ResultSummaryRowRaw(
        long ResultId, string ProjectName, string MeasurementTypeName, string Name, string? Notes,
        string TimestampUtc, long CaptureId, long SampleRateHz, long FftSize, long Channels);

    private sealed record ResultDetailRowRaw(
        long ResultId, long MeasurementTypeId, string ProjectName, string MeasurementTypeName,
        string Name, string? Notes, string TimestampUtc, string ParamsJson,
        long CaptureId, long SampleRateHz, long FftSize, long Channels,
        double? InRangeDbv, double? OutRangeDbv, string? GenJson);

    private sealed record MetricRowRaw(long CaptureId, long Channel, string Name, double Value);

    private sealed record SpectrumRowRaw(long Id, string Window, double S1, double S2, double EnbwHz, double BinSizeHz, string Norm, long ByteLength);
}
