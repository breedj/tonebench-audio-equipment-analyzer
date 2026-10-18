namespace Tonebench.App.Services;

/// <summary>
/// Which measurement a runner's finished result belongs to, and the results of the measurements
/// switched away from — the sweep runners' half of the rule
/// <see cref="Streaming.SpectrumPipeline.SwitchRetentionOwner"/> states for the retained spectrum.
///
/// <para>There is one THD sweep runner and one IMD sweep runner for the whole app, so two
/// measurements of the same kind share it. The client used to answer "two sweep measurements must
/// not show each other's points" by <b>clearing</b> the runner on every switch between them (see
/// measurements/liveOwner.ts), which is also what lost the first one's run: measure a THD vs
/// amplitude sweep, select any other sweep measurement, come back, and the chart was empty
/// (reported 2026-10-17). Parking it under its own measurement is what lets the switch be
/// reversible — and it has to be the <i>server's</i> copy, because <b>Save sweep result</b> reads
/// <c>ThdSweepRunner.Status</c> and not what the browser is drawing, so a client-side restore over
/// somebody else's runner would store the wrong sweep.</para>
///
/// <para>Generic because the two sweep runners keep different state and the bookkeeping is the
/// whole of what they share. Not thread-safe on its own: every caller holds its own state lock
/// across <see cref="Switch"/>, the same lock that serialises the state being parked.</para>
/// </summary>
/// <typeparam name="TState">The runner's own snapshot of everything a switch has to carry.</typeparam>
public sealed class RunParking<TState>(int capacity) where TState : class
{
    private readonly Dictionary<string, TState> _parked = [];

    /// <summary>Parked owners, least recently parked first — the eviction order.</summary>
    private readonly List<string> _order = [];

    /// <summary>The measurement the live slot belongs to, or null when nothing has claimed it.</summary>
    public string? Owner { get; private set; }

    /// <summary>What a <see cref="Switch"/> leaves the caller to do.</summary>
    /// <param name="Changed">False when the owner was already this one and nothing moved.</param>
    /// <param name="Cleared">
    /// True when the live slot has been parked (or dropped) and must now be emptied. False when the
    /// outgoing owner was null, where the state on hand belongs to nobody and is adopted as it
    /// stands — a fresh process, or a sweep started from a second tab before any measurement
    /// claimed it.
    /// </param>
    /// <param name="Adopted">The arriving owner's parked state, or null if it has none.</param>
    public readonly record struct Outcome(bool Changed, bool Cleared, TState? Adopted);

    /// <summary>
    /// Points the runner at <paramref name="owner"/>: parks <paramref name="current"/> under the
    /// owner it belonged to and hands back whatever <paramref name="owner"/> had parked.
    ///
    /// <para>A null <paramref name="current"/> means there is nothing worth keeping — an empty
    /// runner, or one that has just been cleared. That <b>drops</b> the outgoing owner's parked
    /// entry rather than leaving it: a Clear is a deliberate "throw this away" and must not be
    /// undone by switching measurement and back.</para>
    /// </summary>
    public Outcome Switch(string? owner, TState? current)
    {
        if (Owner == owner)
            return new Outcome(Changed: false, Cleared: false, Adopted: null);

        // Taken out *before* the outgoing owner's is put in, and that order is the rule: at
        // capacity, parking first would evict the oldest entry -- which can be the very one this
        // switch is arriving to collect.
        TState? adopted = null;
        if (owner is not null && _parked.Remove(owner, out var held))
        {
            _order.Remove(owner);
            adopted = held;
        }

        bool cleared = false;
        if (Owner is { } previous)
        {
            cleared = true;
            if (current is not null)
            {
                if (!_parked.ContainsKey(previous))
                    _order.Add(previous);
                _parked[previous] = current;

                while (_order.Count > capacity)
                {
                    _parked.Remove(_order[0]);
                    _order.RemoveAt(0);
                }
            }
            else if (_parked.Remove(previous))
            {
                _order.Remove(previous);
            }
        }

        Owner = owner;
        return new Outcome(Changed: true, cleared, adopted);
    }

    /// <summary>
    /// Drops everything held for <paramref name="owner"/>, for a measurement that has stopped
    /// existing — the trash's permanent delete, Empty trash, and the retention purge.
    ///
    /// <para>Two things go, and the second is the one that is about correctness rather than
    /// memory. The <b>parked</b> entry would otherwise sit here until it was evicted or the
    /// process ended, holding a run nothing can ever ask for again (a parked impulse response is
    /// an <c>FftSize</c> array of doubles, so this is megabytes, not bookkeeping). And if the
    /// destroyed measurement is the <b>live</b> owner, the slot is released: an owned slot is
    /// nobody's business but its owner's, while an <i>unowned</i> one is adopted by the next
    /// measurement to arrive (see <see cref="Switch"/>) — so leaving a destroyed measurement's run
    /// in place with its owner still attached would hand it to whichever measurement was selected
    /// next, and Save would store it there. That is the 2026-09-27 fault by a new route.</para>
    /// </summary>
    /// <returns>True when the live slot belonged to <paramref name="owner"/> and must now be emptied.</returns>
    public bool Forget(string owner)
    {
        if (_parked.Remove(owner))
            _order.Remove(owner);

        if (Owner != owner)
            return false;

        Owner = null;
        return true;
    }
}
