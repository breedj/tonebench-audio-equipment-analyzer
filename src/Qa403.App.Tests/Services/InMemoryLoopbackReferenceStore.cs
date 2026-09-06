using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

/// <summary>
/// <see cref="ILoopbackReferenceStore"/> in a dictionary. The reason the interface exists: it lets a
/// test capture a calibration and then measure against it without a database, which is the only way
/// to exercise the loopback correction end to end.
/// </summary>
internal sealed class InMemoryLoopbackReferenceStore : ILoopbackReferenceStore
{
    private readonly Dictionary<long, (LoopbackReferenceInfo Info, LoopbackReference Reference)> _byId = [];
    private long _nextId = 1;

    public int Count => _byId.Count;

    public Task<IReadOnlyList<LoopbackReferenceInfo>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LoopbackReferenceInfo>>([.. _byId.Values.Select(v => v.Info)]);

    public Task<(LoopbackReferenceInfo Info, LoopbackReference Reference)?> GetAsync(long id, CancellationToken ct) =>
        Task.FromResult(_byId.TryGetValue(id, out var found) ? found : ((LoopbackReferenceInfo, LoopbackReference)?)null);

    public Task<LoopbackReferenceInfo> SaveAsync(
        string name,
        LoopbackReference reference,
        double chirpStartHz,
        double chirpEndHz,
        int sourceFftSize,
        string? deviceSerial,
        bool isSimulated,
        CancellationToken ct)
    {
        long id = _nextId++;
        var info = new LoopbackReferenceInfo(
            id, name, DateTime.UtcNow, reference.SampleRateHz, chirpStartHz, chirpEndHz,
            reference.RemovedDelaySamples, reference.Ir.Length, deviceSerial, isSimulated);

        _byId[id] = (info, reference);
        return Task.FromResult(info);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct) => Task.FromResult(_byId.Remove(id));
}
