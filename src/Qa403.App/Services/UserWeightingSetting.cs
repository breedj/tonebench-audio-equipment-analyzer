using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// The live copy of the two user weighting tables (<see cref="UserWeightingTable"/>, config
/// documents <c>measurement.weighting.user1</c>/<c>user2</c>). A singleton in memory for the same
/// reason as <see cref="CaptureDelaySetting"/>: the reader is <see cref="Streaming.SpectrumPipeline"/>,
/// on its worker thread, which has no business doing database I/O. <c>UserWeightingService</c>
/// loads it at startup and writes it on import/clear.
///
/// <para><see cref="Version"/> increments on every change and is part of the pipeline's per-bin
/// gain cache key, so a table replaced while the same slot stays checked is picked up on the next
/// tick rather than silently keeping the old curve.</para>
/// </summary>
public sealed class UserWeightingSetting
{
    private readonly object _lock = new();
    private readonly UserWeightingTable?[] _tables = new UserWeightingTable?[2];
    private readonly TabulatedWeighting?[] _curves = new TabulatedWeighting?[2];
    private int _version;

    /// <summary>Bumped on every <see cref="Set"/>; read by the pipeline to invalidate its gain table.</summary>
    public int Version => Volatile.Read(ref _version);

    public UserWeightingTable? Get(int slot)
    {
        lock (_lock)
            return _tables[Index(slot)];
    }

    /// <summary>The slot's curve, or null when the slot is empty (a checked empty slot weighs nothing).</summary>
    public IWeightingCurve? CurveFor(int slot)
    {
        lock (_lock)
            return _curves[Index(slot)];
    }

    /// <summary>Replaces (or with null, clears) a slot. Builds the curve here so the pipeline never parses under its lock.</summary>
    public void Set(int slot, UserWeightingTable? table)
    {
        int i = Index(slot);
        var curve = table is null ? null : new TabulatedWeighting(table.Points);
        lock (_lock)
        {
            _tables[i] = table;
            _curves[i] = curve;
            Interlocked.Increment(ref _version);
        }
    }

    private static int Index(int slot)
    {
        if (slot is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "User weighting slots are 1 and 2.");
        return slot - 1;
    }
}
