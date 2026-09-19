using Dapper;
using Microsoft.Data.Sqlite;

namespace Qa403.Data;

/// <summary>
/// Measurement results: Dapper for <c>measurement_result</c>/<c>capture</c>/<c>metric</c>, raw
/// <see cref="SqliteCommand"/> + <see cref="SqliteBlob"/> for the <c>spectrum.mag_db</c> BLOB.
///
/// A spectrum result owns exactly one <c>capture</c> row, at <c>seq_index = 0</c>; a sweep result
/// owns one per frequency, ordered by <c>seq_index</c> ascending in frequency -- which is what that
/// column was put there for, so no schema change was needed when sweeps landed. Anything reading
/// "the" capture of a result therefore has to say which one it means: see
/// <see cref="SelectHeadlineCapture"/> for the list view's answer.
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
            VALUES (@MeasurementTypeId, @Name, @Notes, @Ts, @ParamsJson, @DeviceName, @Serial, @FwVer, @IsSimulated)
            RETURNING id;
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
            VALUES (@ResultId, 0, @Ts, @Fs, @N, @InRangeDbv, @OutRangeDbv, @GenJson, @Channels)
            RETURNING id;
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

    /// <summary>
    /// Saves one sweep: a result row plus one <c>capture</c> row per frequency, at ascending
    /// <c>seq_index</c>, each carrying that point's per-channel metrics. One transaction, per the
    /// stack spec's "wrap sweep writes in a single transaction" note -- 31 points x 2 channels x 10
    /// metrics is ~600 inserts, and outside a transaction SQLite would fsync on every one.
    ///
    /// A capture's <c>n</c> column holds the FFT size and <c>fs</c> the sample rate, same as a
    /// spectrum capture; the point's own frequency lives in a <c>fundamental_hz</c> metric rather
    /// than a new column, so the existing metric reader and CSV exporter need no special case.
    ///
    /// Deliberately a separate method from <see cref="SaveResultAsync"/> rather than a
    /// generalisation of it: that path is the working FFT save, and widening it to a list of
    /// captures would put every existing measurement through new code for no gain.
    /// </summary>
    public async Task<long> SaveSweepResultAsync(SweepSaveRequest request, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        long resultId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO measurement_result
                (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated)
            VALUES (@MeasurementTypeId, @Name, @Notes, @Ts, @ParamsJson, @DeviceName, @Serial, @FwVer, @IsSimulated)
            RETURNING id;
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

        string capturedAt = Qa403Timestamp.Now();

        foreach (var capture in request.Captures)
        {
            long captureId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO capture
                    (measurement_result_id, seq_index, ts, fs, n, in_range_dbv, out_range_dbv, gen_json, channels)
                VALUES (@ResultId, @SeqIndex, @Ts, @Fs, @N, @InRangeDbv, @OutRangeDbv, NULL, @Channels)
                RETURNING id;
                """,
                new
                {
                    ResultId = resultId,
                    capture.SeqIndex,
                    Ts = capturedAt,
                    Fs = request.SampleRateHz,
                    N = request.FftSize,
                    request.InRangeDbv,
                    request.OutRangeDbv,
                    request.Channels,
                },
                transaction);

            foreach (var metric in capture.Metrics)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO metric (capture_id, channel, name, value, unit) VALUES (@CaptureId, @Channel, @Name, @Value, @Unit);",
                    new { CaptureId = captureId, metric.Channel, metric.Name, metric.Value, metric.Unit },
                    transaction);
            }

            if (capture.Previews is not null)
            {
                foreach (var preview in capture.Previews)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO capture_preview (capture_id, channel, kind, x, min, max)
                        VALUES (@CaptureId, @Channel, @Kind, @X, @Min, @Max);
                        """,
                        new
                        {
                            CaptureId = captureId,
                            preview.Channel,
                            preview.Kind,
                            X = PreviewCodec.Encode(preview.X),
                            Min = PreviewCodec.Encode(preview.Min),
                            Max = PreviewCodec.Encode(preview.Max),
                        },
                        transaction);
                }
            }
        }

        await transaction.CommitAsync(ct);
        return resultId;
    }

    /// <summary>
    /// Saves a frequency-response measurement: one result row, one capture, and its curves.
    ///
    /// <para>Its own method rather than a branch inside <see cref="SaveSweepResultAsync"/>, for the
    /// same reason that one is separate from <see cref="SaveResultAsync"/>: the row shapes have
    /// almost nothing in common (one capture holding continuous curves, versus N captures holding
    /// scalars), and widening a working save path to cover both would put every existing
    /// measurement through new code for no gain.</para>
    /// </summary>
    public async Task<long> SaveResponseResultAsync(ResponseSaveRequest request, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        long resultId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO measurement_result
                (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated)
            VALUES (@MeasurementTypeId, @Name, @Notes, @Ts, @ParamsJson, @DeviceName, @Serial, @FwVer, @IsSimulated)
            RETURNING id;
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
            VALUES (@ResultId, 0, @Ts, @Fs, @N, @InRangeDbv, @OutRangeDbv, NULL, @Channels)
            RETURNING id;
            """,
            new
            {
                ResultId = resultId,
                Ts = Qa403Timestamp.Now(),
                Fs = request.SampleRateHz,
                N = request.FftSize,
                request.InRangeDbv,
                request.OutRangeDbv,
                request.Channels,
            },
            transaction);

        foreach (var trace in request.Traces)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO response_trace
                    (capture_id, kind, start_hz, end_hz, peak_db, peak_hz,
                     bw1_lower_hz, bw1_upper_hz, bw1_lower_clamped, bw1_upper_clamped,
                     bw3_lower_hz, bw3_upper_hz, bw3_lower_clamped, bw3_upper_clamped,
                     freq_hz, mag_min_db, mag_max_db, phase_deg, coherence,
                     reference_db, reference_mode)
                VALUES
                    (@CaptureId, @Kind, @StartHz, @EndHz, @PeakDb, @PeakHz,
                     @Bw1LowerHz, @Bw1UpperHz, @Bw1LowerClamped, @Bw1UpperClamped,
                     @Bw3LowerHz, @Bw3UpperHz, @Bw3LowerClamped, @Bw3UpperClamped,
                     @FreqHz, @MagMinDb, @MagMaxDb, @PhaseDeg, @Coherence,
                     @ReferenceDb, @ReferenceMode);
                """,
                new
                {
                    CaptureId = captureId,
                    trace.Kind,
                    trace.StartHz,
                    trace.EndHz,
                    trace.PeakDb,
                    trace.PeakHz,
                    Bw1LowerHz = trace.OneDb.LowerHz,
                    Bw1UpperHz = trace.OneDb.UpperHz,
                    Bw1LowerClamped = trace.OneDb.LowerClamped ? 1 : 0,
                    Bw1UpperClamped = trace.OneDb.UpperClamped ? 1 : 0,
                    Bw3LowerHz = trace.ThreeDb.LowerHz,
                    Bw3UpperHz = trace.ThreeDb.UpperHz,
                    Bw3LowerClamped = trace.ThreeDb.LowerClamped ? 1 : 0,
                    Bw3UpperClamped = trace.ThreeDb.UpperClamped ? 1 : 0,
                    FreqHz = PreviewCodec.Encode(trace.FreqHz),
                    MagMinDb = PreviewCodec.Encode(trace.MagnitudeMinDb),
                    MagMaxDb = PreviewCodec.Encode(trace.MagnitudeMaxDb),
                    PhaseDeg = PreviewCodec.Encode(trace.PhaseDeg),
                    Coherence = PreviewCodec.Encode(trace.Coherence),
                    trace.ReferenceDb,
                    trace.ReferenceMode,
                },
                transaction);
        }

        await transaction.CommitAsync(ct);
        return resultId;
    }

    /// <summary>Every curve of a saved frequency-response measurement, for redrawing or overlaying it.</summary>
    public async Task<IReadOnlyList<StoredResponseTrace>> GetResponseTracesAsync(long resultId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var rows = await connection.QueryAsync<ResponseTraceRow>(
            """
            SELECT rt.kind AS Kind, rt.start_hz AS StartHz, rt.end_hz AS EndHz,
                   rt.peak_db AS PeakDb, rt.peak_hz AS PeakHz,
                   rt.bw1_lower_hz AS Bw1LowerHz, rt.bw1_upper_hz AS Bw1UpperHz,
                   rt.bw1_lower_clamped AS Bw1LowerClamped, rt.bw1_upper_clamped AS Bw1UpperClamped,
                   rt.bw3_lower_hz AS Bw3LowerHz, rt.bw3_upper_hz AS Bw3UpperHz,
                   rt.bw3_lower_clamped AS Bw3LowerClamped, rt.bw3_upper_clamped AS Bw3UpperClamped,
                   rt.freq_hz AS FreqHz, rt.mag_min_db AS MagMinDb, rt.mag_max_db AS MagMaxDb,
                   rt.phase_deg AS PhaseDeg, rt.coherence AS Coherence,
                   rt.reference_db AS ReferenceDb, rt.reference_mode AS ReferenceMode
            FROM response_trace rt
            JOIN capture c ON c.id = rt.capture_id
            WHERE c.measurement_result_id = @ResultId
            ORDER BY rt.id;
            """,
            new { ResultId = resultId });

        return
        [
            .. rows.Select(row => new StoredResponseTrace(
                row.Kind,
                row.StartHz,
                row.EndHz,
                row.PeakDb,
                row.PeakHz,
                new ResponseBandwidth(row.Bw1LowerHz, row.Bw1UpperHz, row.Bw1LowerClamped != 0, row.Bw1UpperClamped != 0),
                new ResponseBandwidth(row.Bw3LowerHz, row.Bw3UpperHz, row.Bw3LowerClamped != 0, row.Bw3UpperClamped != 0),
                PreviewCodec.Decode(row.FreqHz),
                PreviewCodec.Decode(row.MagMinDb),
                PreviewCodec.Decode(row.MagMaxDb),
                PreviewCodec.Decode(row.PhaseDeg),
                PreviewCodec.Decode(row.Coherence),
                row.ReferenceDb,
                row.ReferenceMode)),
        ];
    }

    /// <summary>
    /// Flat Dapper row for <see cref="GetResponseTracesAsync"/>. A class with settable properties
    /// rather than a positional record: Dapper matches a positional record's constructor by
    /// position, so a column list that drifts out of order binds silently and wrongly.
    /// </summary>
    private sealed class ResponseTraceRow
    {
        public string Kind { get; set; } = "";
        public double StartHz { get; set; }
        public double EndHz { get; set; }
        public double PeakDb { get; set; }
        public double PeakHz { get; set; }
        public double Bw1LowerHz { get; set; }
        public double Bw1UpperHz { get; set; }
        public long Bw1LowerClamped { get; set; }
        public long Bw1UpperClamped { get; set; }
        public double Bw3LowerHz { get; set; }
        public double Bw3UpperHz { get; set; }
        public long Bw3LowerClamped { get; set; }
        public long Bw3UpperClamped { get; set; }
        public double? ReferenceDb { get; set; }
        public string? ReferenceMode { get; set; }
        public byte[] FreqHz { get; set; } = [];
        public byte[] MagMinDb { get; set; } = [];
        public byte[] MagMaxDb { get; set; } = [];
        public byte[] PhaseDeg { get; set; } = [];
        public byte[] Coherence { get; set; } = [];
    }

    /// <summary>
    /// Saves an impulse-response measurement: one result, one capture, and three kinds of child row
    /// beneath it.
    ///
    /// <para>The gated magnitude and phase curves go into <c>response_trace</c> rather than a table
    /// of their own. They are the same curves a frequency response produces — same deconvolution,
    /// same decimation, same corner readings — so reusing the rows means reusing the reader, the CSV
    /// export and the overlay path rather than growing second copies of all three.</para>
    /// </summary>
    public async Task<long> SaveImpulseResultAsync(ImpulseSaveRequest request, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        long resultId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO measurement_result
                (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated)
            VALUES (@MeasurementTypeId, @Name, @Notes, @Ts, @ParamsJson, @DeviceName, @Serial, @FwVer, @IsSimulated)
            RETURNING id;
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
            VALUES (@ResultId, 0, @Ts, @Fs, @N, @InRangeDbv, @OutRangeDbv, NULL, @Channels)
            RETURNING id;
            """,
            new
            {
                ResultId = resultId,
                Ts = Qa403Timestamp.Now(),
                Fs = request.SampleRateHz,
                N = request.FftSize,
                request.InRangeDbv,
                request.OutRangeDbv,
                request.Channels,
            },
            transaction);

        foreach (var trace in request.Traces)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO response_trace
                    (capture_id, kind, start_hz, end_hz, peak_db, peak_hz,
                     bw1_lower_hz, bw1_upper_hz, bw1_lower_clamped, bw1_upper_clamped,
                     bw3_lower_hz, bw3_upper_hz, bw3_lower_clamped, bw3_upper_clamped,
                     freq_hz, mag_min_db, mag_max_db, phase_deg, coherence,
                     reference_db, reference_mode)
                VALUES
                    (@CaptureId, @Kind, @StartHz, @EndHz, @PeakDb, @PeakHz,
                     @Bw1LowerHz, @Bw1UpperHz, @Bw1LowerClamped, @Bw1UpperClamped,
                     @Bw3LowerHz, @Bw3UpperHz, @Bw3LowerClamped, @Bw3UpperClamped,
                     @FreqHz, @MagMinDb, @MagMaxDb, @PhaseDeg, @Coherence,
                     @ReferenceDb, @ReferenceMode);
                """,
                new
                {
                    CaptureId = captureId,
                    trace.Kind,
                    trace.StartHz,
                    trace.EndHz,
                    trace.PeakDb,
                    trace.PeakHz,
                    Bw1LowerHz = trace.OneDb.LowerHz,
                    Bw1UpperHz = trace.OneDb.UpperHz,
                    Bw1LowerClamped = trace.OneDb.LowerClamped ? 1 : 0,
                    Bw1UpperClamped = trace.OneDb.UpperClamped ? 1 : 0,
                    Bw3LowerHz = trace.ThreeDb.LowerHz,
                    Bw3UpperHz = trace.ThreeDb.UpperHz,
                    Bw3LowerClamped = trace.ThreeDb.LowerClamped ? 1 : 0,
                    Bw3UpperClamped = trace.ThreeDb.UpperClamped ? 1 : 0,
                    FreqHz = PreviewCodec.Encode(trace.FreqHz),
                    MagMinDb = PreviewCodec.Encode(trace.MagnitudeMinDb),
                    MagMaxDb = PreviewCodec.Encode(trace.MagnitudeMaxDb),
                    PhaseDeg = PreviewCodec.Encode(trace.PhaseDeg),
                    Coherence = PreviewCodec.Encode(trace.Coherence),
                    trace.ReferenceDb,
                    trace.ReferenceMode,
                },
                transaction);
        }

        foreach (var impulse in request.Impulses)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO impulse_result
                    (capture_id, kind, sample_rate_hz, peak_index, peak_volts,
                     gate_pre_ms, gate_post_ms, gate_window, resolution_limit_hz,
                     reference_mode, reference_name, latency_samples, ir_samples, ir)
                VALUES
                    (@CaptureId, @Kind, @SampleRateHz, @PeakIndex, @PeakVolts,
                     @GatePreMs, @GatePostMs, @GateWindow, @ResolutionLimitHz,
                     @ReferenceMode, @ReferenceName, @LatencySamples, @IrSamples, @Ir);
                """,
                new
                {
                    CaptureId = captureId,
                    impulse.Kind,
                    impulse.SampleRateHz,
                    impulse.PeakIndex,
                    impulse.PeakVolts,
                    impulse.GatePreMs,
                    impulse.GatePostMs,
                    impulse.GateWindow,
                    impulse.ResolutionLimitHz,
                    impulse.ReferenceMode,
                    impulse.ReferenceName,
                    impulse.LatencySamples,
                    IrSamples = impulse.Ir.Length,
                    Ir = PreviewCodec.Encode(impulse.Ir),
                },
                transaction);
        }

        foreach (var harmonic in request.Harmonics)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO impulse_harmonic (capture_id, ord, upper_valid_hz, fundamental_hz, level_dbc)
                VALUES (@CaptureId, @Ord, @UpperValidHz, @FundamentalHz, @LevelDbc);
                """,
                new
                {
                    CaptureId = captureId,
                    Ord = harmonic.Order,
                    harmonic.UpperValidHz,
                    FundamentalHz = PreviewCodec.Encode(harmonic.FundamentalHz),
                    LevelDbc = PreviewCodec.Encode(harmonic.LevelDbc),
                },
                transaction);
        }

        await transaction.CommitAsync(ct);
        return resultId;
    }

    /// <summary>The impulse responses and harmonic curves of a saved measurement. Null when the result has none.</summary>
    public async Task<StoredImpulseResult?> GetImpulseResultAsync(long resultId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var impulseRows = await connection.QueryAsync<ImpulseResultRow>(
            """
            SELECT ir.kind AS Kind, ir.sample_rate_hz AS SampleRateHz, ir.peak_index AS PeakIndex,
                   ir.peak_volts AS PeakVolts, ir.gate_pre_ms AS GatePreMs, ir.gate_post_ms AS GatePostMs,
                   ir.gate_window AS GateWindow, ir.resolution_limit_hz AS ResolutionLimitHz,
                   ir.reference_mode AS ReferenceMode, ir.reference_name AS ReferenceName,
                   ir.latency_samples AS LatencySamples, ir.ir AS Ir
            FROM impulse_result ir
            JOIN capture c ON c.id = ir.capture_id
            WHERE c.measurement_result_id = @ResultId
            ORDER BY ir.id;
            """,
            new { ResultId = resultId });

        var impulses = impulseRows.ToList();
        if (impulses.Count == 0)
            return null;

        var harmonicRows = await connection.QueryAsync<HarmonicCurveRow>(
            """
            SELECT h.ord AS Ord, h.upper_valid_hz AS UpperValidHz,
                   h.fundamental_hz AS FundamentalHz, h.level_dbc AS LevelDbc
            FROM impulse_harmonic h
            JOIN capture c ON c.id = h.capture_id
            WHERE c.measurement_result_id = @ResultId
            ORDER BY h.ord;
            """,
            new { ResultId = resultId });

        return new StoredImpulseResult(
            [
                .. impulses.Select(row => new StoredImpulseTrace(
                    row.Kind, row.SampleRateHz, (int)row.PeakIndex, row.PeakVolts,
                    row.GatePreMs, row.GatePostMs, row.GateWindow, row.ResolutionLimitHz,
                    row.ReferenceMode, row.ReferenceName, row.LatencySamples,
                    PreviewCodec.Decode(row.Ir))),
            ],
            [
                .. harmonicRows.Select(row => new StoredHarmonicCurve(
                    (int)row.Ord, row.UpperValidHz,
                    PreviewCodec.Decode(row.FundamentalHz), PreviewCodec.Decode(row.LevelDbc))),
            ]);
    }

    /// <summary>Flat Dapper rows. Classes with settable properties, never positional records — see <see cref="ResponseTraceRow"/>.</summary>
    private sealed class ImpulseResultRow
    {
        public string Kind { get; set; } = "";
        public double SampleRateHz { get; set; }
        public long PeakIndex { get; set; }
        public double PeakVolts { get; set; }
        public double GatePreMs { get; set; }
        public double GatePostMs { get; set; }
        public string GateWindow { get; set; } = "";
        public double ResolutionLimitHz { get; set; }
        public string ReferenceMode { get; set; } = "";
        public string? ReferenceName { get; set; }
        public double? LatencySamples { get; set; }
        public byte[] Ir { get; set; } = [];
    }

    private sealed class HarmonicCurveRow
    {
        public long Ord { get; set; }
        public double UpperValidHz { get; set; }
        public byte[] FundamentalHz { get; set; } = [];
        public byte[] LevelDbc { get; set; } = [];
    }

    /// <summary>
    /// Every point of a saved sweep, ascending by <c>seq_index</c>. Reads all the metrics in one
    /// query joined through <c>capture</c> rather than one query per point -- a 50-point stereo
    /// sweep is 50 round trips otherwise.
    /// </summary>
    public async Task<IReadOnlyList<StoredSweepPoint>> GetSweepPointsAsync(long resultId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        var rows = await connection.QueryAsync<SweepMetricRowRaw>(
            """
            SELECT c.seq_index AS SeqIndex, m.channel AS Channel, m.name AS Name, m.value AS Value
            FROM capture c
            JOIN metric m ON m.capture_id = c.id
            WHERE c.measurement_result_id = @resultId
            ORDER BY c.seq_index;
            """,
            new { resultId });

        // A second query rather than folding into the one above: previews are one row per
        // (capture, channel, kind) with three BLOB columns each, and joining that against the
        // metric rows would multiply every metric row by up to two preview rows for nothing --
        // the two are stitched back together by seq_index/channel below instead.
        var previewRows = await connection.QueryAsync<SweepPreviewRowRaw>(
            """
            SELECT c.seq_index AS SeqIndex, cp.channel AS Channel, cp.kind AS Kind, cp.x AS X, cp.min AS Min, cp.max AS Max
            FROM capture c
            JOIN capture_preview cp ON cp.capture_id = c.id
            WHERE c.measurement_result_id = @resultId
            ORDER BY c.seq_index;
            """,
            new { resultId });

        var previewsBySeq = previewRows
            .GroupBy(r => (int)r.SeqIndex)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<int, IReadOnlyDictionary<string, StoredPreviewSeries>>)g
                    .GroupBy(r => (int)r.Channel)
                    .ToDictionary(
                        c => c.Key,
                        c => (IReadOnlyDictionary<string, StoredPreviewSeries>)c.ToDictionary(
                            r => r.Kind,
                            r => new StoredPreviewSeries(PreviewCodec.Decode(r.X), PreviewCodec.Decode(r.Min), PreviewCodec.Decode(r.Max)))));

        return rows
            .GroupBy(r => (int)r.SeqIndex)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var byChannel = g
                    .GroupBy(r => (int)r.Channel)
                    .ToDictionary(
                        c => c.Key,
                        c => (IReadOnlyDictionary<string, double>)c.ToDictionary(r => r.Name, r => r.Value));

                // The point's frequency is stored as a metric (see SaveSweepResultAsync); take it
                // from whichever channel is present, since both record the same value.
                double freqHz = byChannel.Values
                    .Select(m => m.TryGetValue("fundamental_hz", out double hz) ? hz : 0.0)
                    .FirstOrDefault(hz => hz > 0.0);

                return new StoredSweepPoint(g.Key, freqHz, byChannel, previewsBySeq.GetValueOrDefault(g.Key));
            })
            .ToList();
    }

    private sealed record SweepMetricRowRaw(long SeqIndex, long Channel, string Name, double Value);

    private sealed record SweepPreviewRowRaw(long SeqIndex, long Channel, string Kind, byte[] X, byte[] Min, byte[] Max);

    private static async Task InsertSpectrumAsync(
        SqliteConnection connection, SqliteTransaction transaction, long captureId, StoredSpectrumInput spectrum, CancellationToken ct)
    {
        int byteLength = SpectrumCodec.ByteLength(spectrum.Dbv.Length);

        long spectrumId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO spectrum (capture_id, channel, window, s1, s2, enbw_hz, bin_hz, norm, mag_db)
            VALUES (@CaptureId, @Channel, @Window, @S1, @S2, @EnbwHz, @BinSizeHz, @Norm, zeroblob(@ByteLength))
            RETURNING id;
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

        // Which capture supplies the headline numbers is a choice, not a given, once a result owns
        // more than one: a sweep's captures run low frequency first, so seq_index 0 is its 10 or
        // 20 Hz point -- the worst THD on the curve and not a figure anyone quotes a DUT by. The
        // list therefore reads the metrics off the sweep's 1 kHz point instead (see
        // SelectHeadlineCapture); a single-capture result is unaffected, since its one capture is
        // the only candidate whatever its frequency.
        long[] resultIds = rows.Select(r => r.ResultId).ToArray();
        var captureRows = await connection.QueryAsync<HeadlineCaptureRowRaw>(
            """
            -- IFNULL, not a bare subquery and not CAST(... AS REAL): SQLite types a column from the
            -- values it actually returns, so a correlated subquery that is NULL for every row of a
            -- result set arrives with no type at all, Dapper materializes it as byte[], and binding
            -- the record fails with "a parameterless default constructor ... is required" -- an error
            -- naming nothing that is wrong. generator_dbv is NULL for every single-spectrum result,
            -- which is most of them, so this is the common path rather than an edge case.
            -- NoGeneratorLevelDbv stands in for "this capture recorded no level"; see its remarks.
            --
            -- fundamental_hz needs exactly the same guard, and got it only once a kind arrived whose
            -- results have no metric rows at all. Until then every result set contained at least one
            -- capture that had recorded a fundamental, so the column always came back typed and the
            -- bare subquery looked safe. Filtering the list to a frequency-response type made every
            -- row NULL and turned the whole history list into a 500.
            SELECT c.measurement_result_id AS ResultId, c.id AS CaptureId,
                   IFNULL((SELECT MIN(m.value) FROM metric m
                            WHERE m.capture_id = c.id AND m.name = 'fundamental_hz'), @noFundamental) AS FundamentalHz,
                   IFNULL((SELECT MIN(m.value) FROM metric m
                            WHERE m.capture_id = c.id AND m.name = 'generator_dbv'), @noLevel) AS GeneratorDbv,
                   c.seq_index AS SeqIndex
            FROM capture c
            WHERE c.measurement_result_id IN @resultIds;
            """,
            new { resultIds, noLevel = NoGeneratorLevelDbv, noFundamental = NoFundamentalHz });

        var capturesByResult = captureRows.ToLookup(c => c.ResultId);
        var headlineCaptureIds = rows.ToDictionary(
            r => r.ResultId,
            r => SelectHeadlineCapture(capturesByResult[r.ResultId]) ?? r.CaptureId);

        long[] captureIds = headlineCaptureIds.Values.Distinct().ToArray();
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
            long headlineCaptureId = headlineCaptureIds[r.ResultId];
            var channels = new Dictionary<int, ChannelHeadline>();
            foreach (var group in byCaptureChannel.Where(g => g.Key.CaptureId == headlineCaptureId))
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
                   p.name AS ProjectName, mt.name AS MeasurementTypeName, mt.kind AS Kind,
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
            row.ResultId, row.MeasurementTypeId, row.ProjectName, row.MeasurementTypeName, row.Kind,
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

    public async Task<bool> UpdateResultAsync(long resultId, string name, string? notes, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        int affected = await connection.ExecuteAsync(
            "UPDATE measurement_result SET name = @name, notes = @notes WHERE id = @resultId;",
            new { resultId, name, notes });
        return affected > 0;
    }

    /// <summary>
    /// Deletes the result's captures (and their spectra, metrics, previews and response traces) then
    /// the result itself.
    ///
    /// <para>Every child table has to be listed here by hand: foreign keys are off (see
    /// <c>SqliteConnectionFactory</c>), so a table missed from this cascade is orphaned silently and
    /// then collides with a future capture that reuses the freed rowid. That has now happened twice
    /// — <c>capture_preview</c> (migration 005) and <c>response_trace</c> (007) — both times because
    /// the table was added after this method was written. A new child of <c>capture</c> belongs in
    /// this list, in <c>ProjectRepository.DeleteTypeAsync</c>, and in a repair migration.</para>
    /// </summary>
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
            await connection.ExecuteAsync("DELETE FROM capture_preview WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
            await connection.ExecuteAsync("DELETE FROM response_trace WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
            await connection.ExecuteAsync("DELETE FROM impulse_result WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
            await connection.ExecuteAsync("DELETE FROM impulse_harmonic WHERE capture_id IN @captureIds;", new { captureIds }, transaction);
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
        long ResultId, long MeasurementTypeId, string ProjectName, string MeasurementTypeName, string Kind,
        string Name, string? Notes, string TimestampUtc, string ParamsJson,
        long CaptureId, long SampleRateHz, long FftSize, long Channels,
        double? InRangeDbv, double? OutRangeDbv, string? GenJson);

    /// <summary>
    /// The fundamental a headline THD figure is quoted at. 1 kHz because that is what every
    /// amplifier datasheet, and QuantAsylum's own software, reports THD at.
    /// </summary>
    private const double HeadlineFundamentalHz = 1000.0;

    /// <summary>
    /// Stands in for "this capture recorded no <c>generator_dbv</c> metric" in the headline query.
    /// A sentinel rather than NULL because of the Dapper/SQLite typing trap documented at the query
    /// itself; well below any level a generator can be asked for, so it can never be mistaken for a
    /// real reading.
    /// </summary>
    private const double NoGeneratorLevelDbv = -1.0e9;

    /// <summary>
    /// Stands in for "this capture recorded no <c>fundamental_hz</c> metric", for the same reason as
    /// <see cref="NoGeneratorLevelDbv"/>. Negative, so it can never be confused with a frequency.
    /// </summary>
    private const double NoFundamentalHz = -1.0;

    /// <summary>
    /// Picks the capture whose metrics stand for the whole result in the history list: the lowest
    /// measured fundamental at or above <see cref="HeadlineFundamentalHz"/>, or -- for a sweep whose
    /// axis is the level rather than the frequency -- its highest level.
    /// </summary>
    /// <remarks>
    /// A sweep grid rarely lands exactly on 1 kHz, so "the first point at or above it" is the rule
    /// rather than an exact match -- one step of the grid above 1 kHz, which at the coarsest
    /// setting is a few hundred Hz away and still on the flat part of a THD curve. A sweep that
    /// ends below 1 kHz (say 20 Hz to 500 Hz) falls back to its highest point, the nearest thing it
    /// has, rather than reporting nothing.
    /// <para>
    /// An amplitude sweep measures every point at the same frequency, so that rule would silently
    /// return its <em>first</em> capture -- the 1 mV point, whose THD is the analyzer's own residual
    /// and not a figure anyone quotes a DUT by. Such a sweep is recognised by its captures carrying
    /// more than one distinct <c>generator_dbv</c> (a frequency sweep holds the level constant and a
    /// single spectrum has no such metric at all), and headlines its highest level instead.
    /// </para>
    /// <para>
    /// Returns null when no capture carries a <c>fundamental_hz</c> metric at all -- a spectrum
    /// saved with no fundamental found in band -- and the caller keeps the seq_index 0 capture it
    /// already has.
    /// </para>
    /// </remarks>
    private static long? SelectHeadlineCapture(IEnumerable<HeadlineCaptureRowRaw> captures)
    {
        var all = captures.ToList();

        var levels = all.Where(c => c.GeneratorDbv > NoGeneratorLevelDbv).ToList();
        if (levels.Select(c => c.GeneratorDbv).Distinct().Count() > 1)
            return levels.OrderBy(c => c.GeneratorDbv).ThenBy(c => c.SeqIndex).Last().CaptureId;

        var measured = all
            .Where(c => c.FundamentalHz > NoFundamentalHz)
            .OrderBy(c => c.FundamentalHz)
            .ToList();

        if (measured.Count == 0)
            return null;

        var atOrAbove = measured.FirstOrDefault(c => c.FundamentalHz >= HeadlineFundamentalHz);
        return (atOrAbove ?? measured[^1]).CaptureId;
    }

    private sealed record MetricRowRaw(long CaptureId, long Channel, string Name, double Value);

    // FundamentalHz and GeneratorDbv are correlated MIN()s over the metric rows, NULL for a capture
    // that recorded neither -- and both arrive as a sentinel rather than a null, because a column
    // that is NULL for every row of a result set has no SQLite type and Dapper then materializes it
    // as byte[]. See the query. Both channels of one capture measure the same frequency, so MIN()
    // collapses them without choosing between them.
    private sealed record HeadlineCaptureRowRaw(long ResultId, long CaptureId, double FundamentalHz, double GeneratorDbv, long SeqIndex);

    private sealed record SpectrumRowRaw(long Id, string Window, double S1, double S2, double EnbwHz, double BinSizeHz, string Norm, long ByteLength);
}
