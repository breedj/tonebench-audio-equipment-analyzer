namespace Tonebench.App.Streaming;

/// <summary>What the coupling control does to the trace before anything else sees it.</summary>
public enum ScopeCoupling
{
    /// <summary>Nothing. The trace is what the analyzer captured, offset and all.</summary>
    Dc,

    /// <summary>The record's own mean subtracted — see <see cref="ScopeSignalPath"/> for why that, and not a filter.</summary>
    Ac,
}

/// <summary>
/// The display path, applied to a record before it is sliced, measured, combined or plotted against
/// itself.
///
/// <para><b>It is applied to the whole record once, and everything downstream works from the
/// result.</b> Not per slice: a window moved along the record would otherwise be processed from a
/// different starting state each time, so the trace would change as it was scrolled; and the
/// readings, the math channel and the X–Y figure would be of the processed trace or the raw one
/// depending on which of them happened to do the work.</para>
///
/// <para><b>What is stored is never processed.</b> The samples go into <c>capture.pcm</c> as the
/// analyzer saw them, because the display path is a way of <i>looking</i> at a capture rather than
/// a property of it: a saved result can be reopened and read a different way, or raw, which a
/// processed-at-save record could not be.</para>
///
/// <para><b>The in-path filter was removed on 2026-11-18</b> (low-pass / high-pass / band-pass /
/// notch, with its own order, Q and response preview), to be rebuilt properly later. It was
/// zero-phase — run forward and then backward, so it added no delay and an edge stayed where it
/// happened — and that is what made its one real fault unavoidable rather than a bug: filtering a
/// sample needs signal on both sides of it, a record has none outside itself, and the mirror pad
/// that stood in for the missing signal left the first and last period of the corner reading up to
/// a third of full scale wrong. Measured on a tone sitting on the corner: the first millisecond
/// overshot the settled amplitude by 69 %. What would remove that rather than bound it is filtering
/// <i>in context</i> — keeping a margin of real samples either side of the record when it is taken
/// — which is a change to the capture path and the reason the rebuild is its own piece of work. The
/// arithmetic is in git: <c>Tonebench.Dsp/WaveformFilters.cs</c> and its 18 tests, deleted in the
/// same commit as this note.</para>
/// </summary>
/// <param name="Coupling">DC, or the mean removed.</param>
public readonly record struct ScopeSignalPath(ScopeCoupling Coupling)
{
    public static ScopeSignalPath Direct { get; } = new(ScopeCoupling.Dc);

    /// <summary>Whether this path would change the record at all.</summary>
    public bool Changes => Coupling == ScopeCoupling.Ac;

    /// <summary>
    /// The record as the screen sees it. The same instance when nothing is asked for, so a DC-coupled
    /// fetch costs exactly what it did before this existed.
    ///
    /// <para><b>AC coupling is the record's mean subtracted, not a high-pass</b>, and on this
    /// hardware that is the more correct operation rather than the cheaper one. Both the QA403 and
    /// a sound card are AC-coupled in hardware with a corner near 0.2 Hz, so there is no DC left to
    /// block: what reaches a capture is the front end's own standing offset — measured at about
    /// −4.5 mV on the left input — which is a constant, and subtracting the mean removes a constant
    /// <i>exactly</i>, with no corner to choose, no settling and nothing to ring. A software
    /// high-pass would do the same job approximately and add a transient to the start of the record,
    /// which is where a pre-trigger view is read.</para>
    ///
    /// <para>The mean is the <b>record's</b> and not the window's, so scrolling a window along the
    /// record does not move the trace up and down: the offset being removed is a property of the
    /// input, not of what is being looked at.</para>
    /// </summary>
    public ScopeRecord Apply(ScopeRecord record)
    {
        if (!Changes || record.FrameCount <= 0)
            return record;

        var channels = new double[record.ChannelCount][];
        for (int ch = 0; ch < record.ChannelCount; ch++)
        {
            var source = record.Channels[ch];
            var target = new double[source.Length];

            double sum = 0.0;
            for (int i = 0; i < source.Length; i++)
                sum += source[i];
            double mean = sum / source.Length;
            for (int i = 0; i < source.Length; i++)
                target[i] = source[i] - mean;

            channels[ch] = target;
        }

        return record with { Channels = channels };
    }
}
