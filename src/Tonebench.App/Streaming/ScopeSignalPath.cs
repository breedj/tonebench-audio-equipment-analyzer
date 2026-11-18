using Tonebench.Dsp;

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
/// The display path: the coupling and the in-path filter, applied to a record before it is sliced,
/// measured, combined or plotted against itself.
///
/// <para><b>It is applied to the whole record once, and everything downstream works from the
/// result.</b> Not per slice: a window moved along the record would otherwise be filtered from a
/// different starting state each time, so the trace would change as it was scrolled; and the
/// readings, the math channel and the X–Y figure would be of the filtered trace or the raw one
/// depending on which of them happened to do the filtering.</para>
///
/// <para><b>It changes what every reading says, and nothing hides that.</b> A low-pass lowers a
/// peak-to-peak and lengthens a rise time; a notch removes a component the RMS was counting. The
/// figures are of the trace on screen, which is the only arrangement that is not a lie — and the
/// panel says a filter is in the path.</para>
///
/// <para><b>What is stored is never filtered.</b> The samples go into <c>capture.pcm</c> as the
/// analyzer saw them, because a filter is a way of <i>looking</i> at a capture rather than a
/// property of it: a saved result can be reopened and read through a different filter, or none,
/// which a filtered-at-save record could not be.</para>
/// </summary>
/// <param name="Coupling">DC, or the mean removed.</param>
/// <param name="Filter">The in-path filter, or <see cref="ScopeFilterSpec.Off"/>.</param>
public readonly record struct ScopeSignalPath(ScopeCoupling Coupling, ScopeFilterSpec Filter)
{
    public static ScopeSignalPath Direct { get; } = new(ScopeCoupling.Dc, ScopeFilterSpec.Off);

    /// <summary>Whether this path would change the record at all.</summary>
    public bool Changes => Coupling == ScopeCoupling.Ac || Filter.Filters;

    /// <summary>
    /// The record as the screen sees it. The same instance when nothing is asked for, so an
    /// unfiltered fetch costs exactly what it did before this existed.
    ///
    /// <para><b>AC coupling is the record's mean subtracted, not a high-pass</b>, and on this
    /// hardware that is the more correct operation rather than the cheaper one. Both the QA403 and
    /// a sound card are AC-coupled in hardware with a corner near 0.2 Hz, so there is no DC left to
    /// block: what reaches a capture is the front end's own standing offset — measured at about
    /// −4.5 mV on the left input — which is a constant, and subtracting the mean removes a constant
    /// <i>exactly</i>, with no corner to choose, no settling and nothing to ring. A software
    /// high-pass would do the same job approximately and add a transient to the start of the record,
    /// which is where a pre-trigger view is read. A genuine low-frequency roll-off, for a drift or a
    /// rumble <i>within</i> the record, is what the high-pass filter is for and is a separate
    /// control.</para>
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

            if (Coupling == ScopeCoupling.Ac)
            {
                double sum = 0.0;
                for (int i = 0; i < source.Length; i++)
                    sum += source[i];
                double mean = sum / source.Length;
                for (int i = 0; i < source.Length; i++)
                    target[i] = source[i] - mean;
            }
            else
            {
                source.CopyTo(target, 0);
            }

            WaveformFilters.Apply(Filter, target, record.SampleRateHz, target);
            channels[ch] = target;
        }

        return record with { Channels = channels };
    }
}
