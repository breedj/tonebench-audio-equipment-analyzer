namespace Tonebench.Dsp;

/// <summary>
/// A trigger that is fed the stream block by block and keeps its own state between calls.
///
/// <para>Resumable is the whole point, and it is not a convenience. The device delivers 4096-frame
/// blocks, so a qualifying crossing routinely begins in one block and completes in the next; a
/// scanner that is handed each block independently either misses that crossing or, worse, finds it
/// twice. Everything that makes a trigger a trigger rather than a search -- the arm/fire state, the
/// holdoff countdown, the sample before the first one of this block -- lives across the call
/// boundary. That is also why the scope cannot simply search inside an already-captured record:
/// pulse width, dropout and runt are state machines over the contiguous stream, and pre-trigger
/// means the event has to be found as it passes, not looked for afterwards.</para>
///
/// <para>Implementations are not thread-safe and are driven from one thread -- the consumer side of
/// <c>DeviceSessionService.PumpAsync</c>, which is where the blocks arrive.</para>
/// </summary>
public interface IScanTrigger
{
    /// <summary>
    /// Forgets everything: the previous sample, the arm state and the holdoff. Called when the
    /// stream becomes discontinuous (an input-range step punches a hole of order 110 ms, and a
    /// state machine carried across it is describing a signal that was never there) and whenever
    /// the engine re-arms.
    /// </summary>
    void Reset();

    /// <summary>
    /// Advances over <paramref name="samples"/>, whose first element is absolute sample index
    /// <paramref name="firstAbsoluteIndex"/>.
    ///
    /// <para>Returns true at the <b>first</b> qualifying event, leaving the rest of the span
    /// unexamined -- the caller commits a record and re-arms, so there is nothing to lose by
    /// stopping. Returns false having consumed the whole span.</para>
    /// </summary>
    bool Scan(ReadOnlySpan<double> samples, long firstAbsoluteIndex, out TriggerHit hit);
}
