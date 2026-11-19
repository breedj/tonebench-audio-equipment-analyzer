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
    /// Forgets the stream state: the previous sample and the arm state. Called when the stream
    /// becomes discontinuous (an input-range step punches a hole of order 110 ms, and a state
    /// machine carried across it is describing a signal that was never there) and whenever the
    /// engine re-arms after a record.
    ///
    /// <para><b>The holdoff is the one thing that may outlive a re-arm, and
    /// <paramref name="keepHoldoff"/> is what says so.</b> A holdoff is a dead time measured from
    /// the trigger <i>event</i>, and the engine re-arms as soon as the record it triggered has
    /// filled — so a reset that cleared it there threw the holdoff away before it had ever bitten,
    /// the fill being the only thing that ever delayed the next trigger. That was true of every
    /// scanner but <see cref="StimulusTrigger"/>, whose own <c>Reset</c> keeps its holdoff and says
    /// why: it is a count on the same clock, and the clock did not stop. Passing <c>true</c> gives
    /// the other five the same behaviour; the discontinuity path passes <c>false</c>, because
    /// across a hole the index has advanced by fewer frames than the wall clock has and a holdoff
    /// carried over it would hold for longer than it was set to.</para>
    /// </summary>
    void Reset(bool keepHoldoff = false);

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
