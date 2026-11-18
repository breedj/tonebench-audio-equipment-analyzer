using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The display path on a record: the coupling, the filter, and what they do to everything measured
/// from the result.
/// </summary>
public class ScopeSignalPathTests
{
    private const double Fs = 48_000.0;

    private static ScopeRecord Record(Func<int, double> left, Func<int, double>? right = null, int frames = 9_600)
    {
        var a = new double[frames];
        var b = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            a[i] = left(i);
            b[i] = (right ?? left)(i);
        }

        return new ScopeRecord(
            [a, b], Fs, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
    }

    private static double Sine(int i, double freqHz, double amplitude = 1.0) =>
        amplitude * Math.Sin(2.0 * Math.PI * freqHz * i / Fs);

    // -----------------------------------------------------------------------
    // Doing nothing
    // -----------------------------------------------------------------------

    /// <summary>
    /// A path that asks for nothing hands back the <b>same instance</b>, so a DC-coupled fetch
    /// costs exactly what it did before any of this existed.
    /// </summary>
    [Fact]
    public void ADirectPath_HandsBackTheRecordItself()
    {
        var record = Record(i => Sine(i, 1_000.0));

        Assert.False(ScopeSignalPath.Direct.Changes);
        Assert.Same(record, ScopeSignalPath.Direct.Apply(record));
    }

    // -----------------------------------------------------------------------
    // Coupling
    // -----------------------------------------------------------------------

    /// <summary>
    /// AC coupling is the record's mean subtracted, and on this hardware that is the more correct
    /// operation rather than the cheaper one: both the QA403 and a sound card already block DC, so
    /// what is left is the front end's own standing offset — a constant, which a mean removes
    /// <i>exactly</i>, with no corner to choose, no settling, and nothing to ring at the start of
    /// the record where a pre-trigger view is read.
    /// </summary>
    [Fact]
    public void AcCoupling_RemovesTheStandingOffsetExactly()
    {
        // A tone riding on the measured left-input offset.
        var record = Record(i => Sine(i, 1_000.0, 0.5) - 0.0045);
        var path = new ScopeSignalPath(ScopeCoupling.Ac);

        var coupled = path.Apply(record);

        var before = ScopeSliceMeasurements.Of(record.Channels[0], Fs);
        var after = ScopeSliceMeasurements.Of(coupled.Channels[0], Fs);

        Assert.Equal(-0.0045, before.MeanV, 6);
        Assert.Equal(0.0, after.MeanV, 9);
        // And nothing else about the trace moved: the AC RMS is what it was.
        Assert.Equal(before.AcRmsV, after.AcRmsV, 9);
    }

    /// <summary>
    /// The mean is the <b>record's</b> and not a window's, so scrolling a window along the record
    /// does not move the trace up and down — the offset being removed is a property of the input
    /// rather than of what is being looked at.
    /// </summary>
    [Fact]
    public void AcCoupling_SubtractsOneMeanForTheWholeRecord()
    {
        // A slow wave, so any window's own mean is far from the record's.
        var record = Record(i => Sine(i, 5.0) + 1.0);
        var coupled = new ScopeSignalPath(ScopeCoupling.Ac).Apply(record);

        for (int i = 0; i < record.FrameCount; i += 97)
            Assert.Equal(record.Channels[0][i] - 1.0, coupled.Channels[0][i], 6);
    }

    [Fact]
    public void AcCoupling_TreatsEachChannelAsItsOwn()
    {
        var record = Record(i => Sine(i, 1_000.0) + 2.0, i => Sine(i, 1_000.0) - 5.0);
        var coupled = new ScopeSignalPath(ScopeCoupling.Ac).Apply(record);

        Assert.Equal(0.0, ScopeSliceMeasurements.Of(coupled.Channels[0], Fs).MeanV, 9);
        Assert.Equal(0.0, ScopeSliceMeasurements.Of(coupled.Channels[1], Fs).MeanV, 9);
    }

    // -----------------------------------------------------------------------
    // The filter
    // -----------------------------------------------------------------------

    /// <summary>
    /// The record it hands back is the same record in every other respect — the trigger, the
    /// splice and the forced flags all belong to the capture and not to the way it is being looked
    /// at.
    /// </summary>
    [Fact]
    public void EverythingButTheSamples_IsTheRecordsOwn()
    {
        var raw = new ScopeRecord(
            [new double[1_024], new double[1_024]],
            Fs, FirstIndex: 7, TriggerPosition: 300.5, Epoch: 4, Spliced: true, Forced: true);

        var filtered = new ScopeSignalPath(ScopeCoupling.Ac).Apply(raw);

        Assert.Equal(raw.FirstIndex, filtered.FirstIndex);
        Assert.Equal(raw.TriggerPosition, filtered.TriggerPosition);
        Assert.Equal(raw.Epoch, filtered.Epoch);
        Assert.True(filtered.Spliced);
        Assert.True(filtered.Forced);
        Assert.Equal(raw.FrameCount, filtered.FrameCount);
        Assert.NotSame(raw.Channels, filtered.Channels);
    }

    // -----------------------------------------------------------------------
    // What everything downstream sees
    // -----------------------------------------------------------------------

    /// <summary>
    /// A trace with nothing in it is still a record: an empty path on an empty record must not
    /// throw on a fetch path.
    /// </summary>
    [Fact]
    public void AnEmptyRecord_IsHandedBack()
    {
        var empty = new ScopeRecord([], Fs, 0, 0, 0, false, false);

        Assert.Same(empty, new ScopeSignalPath(ScopeCoupling.Ac).Apply(empty));
    }
}
