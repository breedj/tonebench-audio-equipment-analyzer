using Dapper;

namespace Qa403.Data;

/// <summary>
/// Loopback calibrations (the analyzer's own impulse response), in the <b>configuration</b> database.
///
/// <para>Over <see cref="ConfigConnectionFactory"/> rather than the project file on purpose: a
/// calibration describes this analyzer and its cables, not any one measurement or project. It moved
/// here (config migration 002) when projects became one file each — kept per project it would need
/// re-taking for every new file, and copied to another machine inside a project file it would silently
/// divide that machine's measurements by this bench's response. The empty <c>impulse_reference</c>
/// table migration 009 still creates in every project file is left alone and never read.</para>
/// </summary>
public sealed class LoopbackReferenceRepository(ConfigConnectionFactory connectionFactory)
{
    /// <summary>Stores a loopback calibration, replacing any with the same name.</summary>
    public async Task<long> SaveLoopbackReferenceAsync(StoredLoopbackReference reference, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO impulse_reference
                (name, taken_utc, sample_rate_hz, source_fft_size, chirp_start_hz, chirp_end_hz,
                 removed_delay_samples, peak_offset, ir_samples, ir, device_serial, is_simulated)
            VALUES
                (@Name, @TakenUtc, @SampleRateHz, @SourceFftSize, @ChirpStartHz, @ChirpEndHz,
                 @RemovedDelaySamples, @PeakOffset, @IrSamples, @Ir, @DeviceSerial, @IsSimulated)
            ON CONFLICT(name) DO UPDATE SET
                taken_utc = excluded.taken_utc,
                sample_rate_hz = excluded.sample_rate_hz,
                source_fft_size = excluded.source_fft_size,
                chirp_start_hz = excluded.chirp_start_hz,
                chirp_end_hz = excluded.chirp_end_hz,
                removed_delay_samples = excluded.removed_delay_samples,
                peak_offset = excluded.peak_offset,
                ir_samples = excluded.ir_samples,
                ir = excluded.ir,
                device_serial = excluded.device_serial,
                is_simulated = excluded.is_simulated
            RETURNING id;
            """,
            new
            {
                reference.Name,
                TakenUtc = Qa403Timestamp.Now(),
                reference.SampleRateHz,
                reference.SourceFftSize,
                reference.ChirpStartHz,
                reference.ChirpEndHz,
                reference.RemovedDelaySamples,
                reference.PeakOffset,
                IrSamples = reference.Ir.Length,
                Ir = PreviewCodec.Encode(reference.Ir),
                reference.DeviceSerial,
                IsSimulated = reference.IsSimulated ? 1 : 0,
            });
    }

    public async Task<IReadOnlyList<StoredLoopbackReference>> ListLoopbackReferencesAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var rows = await connection.QueryAsync<LoopbackReferenceRow>(LoopbackReferenceSelect + " ORDER BY r.name;");
        return [.. rows.Select(ToStoredReference)];
    }

    public async Task<StoredLoopbackReference?> GetLoopbackReferenceAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<LoopbackReferenceRow>(
            LoopbackReferenceSelect + " WHERE r.id = @Id;", new { Id = id });

        return row is null ? null : ToStoredReference(row);
    }

    public async Task<bool> DeleteLoopbackReferenceAsync(long id, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        int deleted = await connection.ExecuteAsync("DELETE FROM impulse_reference WHERE id = @Id;", new { Id = id });
        return deleted > 0;
    }

    private const string LoopbackReferenceSelect =
        """
        SELECT r.id AS Id, r.name AS Name, r.taken_utc AS TakenUtc, r.sample_rate_hz AS SampleRateHz,
               r.source_fft_size AS SourceFftSize, r.chirp_start_hz AS ChirpStartHz, r.chirp_end_hz AS ChirpEndHz,
               r.removed_delay_samples AS RemovedDelaySamples, r.peak_offset AS PeakOffset,
               r.ir AS Ir, r.device_serial AS DeviceSerial, r.is_simulated AS IsSimulated
        FROM impulse_reference r
        """;

    private static StoredLoopbackReference ToStoredReference(LoopbackReferenceRow row) =>
        new(row.Id, row.Name, Qa403Timestamp.Parse(row.TakenUtc), row.SampleRateHz, (int)row.SourceFftSize,
            row.ChirpStartHz, row.ChirpEndHz, row.RemovedDelaySamples, (int)row.PeakOffset,
            PreviewCodec.Decode(row.Ir), row.DeviceSerial, row.IsSimulated != 0);

    /// <summary>Flat Dapper row. A class with settable properties, never a positional record — see <c>MeasurementRepository.ResponseTraceRow</c>.</summary>
    private sealed class LoopbackReferenceRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string TakenUtc { get; set; } = "";
        public double SampleRateHz { get; set; }
        public long SourceFftSize { get; set; }
        public double ChirpStartHz { get; set; }
        public double ChirpEndHz { get; set; }
        public double RemovedDelaySamples { get; set; }
        public long PeakOffset { get; set; }
        public byte[] Ir { get; set; } = [];
        public string? DeviceSerial { get; set; }
        public long IsSimulated { get; set; }
    }
}
