using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// <see cref="ILoopbackReferenceStore"/> over the configuration database — see
/// <see cref="LoopbackReferenceRepository"/> for why a calibration does not live in a project file.
///
/// <para>The only translation it does is between the DSP type and the row: the impulse response is
/// <c>double[]</c> in <see cref="LoopbackReference"/> and f32 in the database. That is not a loss
/// worth worrying about — a float carries about seven significant digits, three orders of magnitude
/// finer than the estimator's own regulariser — and it keeps the calibration in the same
/// <c>PreviewCodec</c> encoding as every other array the app stores.</para>
/// </summary>
public sealed class SqliteLoopbackReferenceStore(LoopbackReferenceRepository repository) : ILoopbackReferenceStore
{
    public async Task<IReadOnlyList<LoopbackReferenceInfo>> ListAsync(CancellationToken ct) =>
        [.. (await repository.ListLoopbackReferencesAsync(ct)).Select(ToInfo)];

    public async Task<(LoopbackReferenceInfo Info, LoopbackReference Reference)?> GetAsync(long id, CancellationToken ct)
    {
        var stored = await repository.GetLoopbackReferenceAsync(id, ct);
        if (stored is null)
            return null;

        var ir = new double[stored.Ir.Length];
        for (int i = 0; i < ir.Length; i++)
            ir[i] = stored.Ir[i];

        return (ToInfo(stored), new LoopbackReference(stored.SampleRateHz, ir, stored.PeakOffset, stored.RemovedDelaySamples));
    }

    public async Task<LoopbackReferenceInfo> SaveAsync(
        string name,
        LoopbackReference reference,
        double chirpStartHz,
        double chirpEndHz,
        int sourceFftSize,
        string? deviceSerial,
        bool isSimulated,
        CancellationToken ct)
    {
        var ir = new float[reference.Ir.Length];
        for (int i = 0; i < ir.Length; i++)
            ir[i] = (float)reference.Ir[i];

        var row = new StoredLoopbackReference(
            Id: 0, name, DateTimeOffset.UtcNow, reference.SampleRateHz, sourceFftSize,
            chirpStartHz, chirpEndHz, reference.RemovedDelaySamples, reference.PeakOffset,
            ir, deviceSerial, isSimulated);

        long id = await repository.SaveLoopbackReferenceAsync(row, ct);

        // Read back rather than returning the row as written: the save replaces any calibration with
        // the same name, so the id and the stored timestamp are the database's answer, not ours.
        var stored = await repository.GetLoopbackReferenceAsync(id, ct)
            ?? throw new InvalidOperationException($"The loopback reference '{name}' vanished immediately after being saved.");

        return ToInfo(stored);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct) => repository.DeleteLoopbackReferenceAsync(id, ct);

    private static LoopbackReferenceInfo ToInfo(StoredLoopbackReference stored) =>
        new(stored.Id, stored.Name, stored.TakenUtc.UtcDateTime, stored.SampleRateHz,
            stored.ChirpStartHz, stored.ChirpEndHz, stored.RemovedDelaySamples,
            stored.Ir.Length, stored.DeviceSerial, stored.IsSimulated);
}
