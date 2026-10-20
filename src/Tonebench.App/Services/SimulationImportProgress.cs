namespace Tonebench.App.Services;

/// <summary>
/// How far through a multi-run import the service has got, for the dialog that is waiting on it.
///
/// <para>A 61-step file takes about a minute to import and a 220 MB one rather longer, and until
/// this existed the only thing on screen was a spinner saying how many runs there were — which a
/// user reasonably reads as a stall (Joost, 2026-10-20). The import is one POST that does not
/// answer until it is finished, so the count travels the other way: the service writes it here and
/// the dialog polls <c>GET /api/simulations/progress</c> for it, the way the status bar polls the
/// device.</para>
///
/// <para>A singleton holding one reading, deliberately: an import owns the file and the dialog, so
/// there is only ever one to report, and a second would be a different kind of bug. Nothing here is
/// durable — a reading left behind by a crashed import is replaced by the next one and ignored by
/// everything else, since the dialog only asks while its own request is in flight.</para>
/// </summary>
public sealed class SimulationImportProgress
{
    private readonly Lock _gate = new();
    private int _done;
    private int _total;

    /// <summary>Starts a run of <paramref name="total"/> steps, discarding whatever came before.</summary>
    public void Begin(int total)
    {
        lock (_gate)
        {
            _done = 0;
            _total = Math.Max(0, total);
        }
    }

    /// <summary>One more step finished.</summary>
    public void Advance()
    {
        lock (_gate)
        {
            if (_total > 0)
                _done = Math.Min(_done + 1, _total);
        }
    }

    /// <summary>Nothing is running. Called from a <c>finally</c>, so a refusal clears it too.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _done = 0;
            _total = 0;
        }
    }

    /// <summary>The current reading, or null when nothing multi-run is in flight.</summary>
    public (int Done, int Total)? Current
    {
        get
        {
            lock (_gate)
                return _total > 0 ? (_done, _total) : null;
        }
    }
}
