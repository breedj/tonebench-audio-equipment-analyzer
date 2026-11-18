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

    /// <summary>
    /// The middle half of a channel, which is what a filtered reading is taken over here.
    ///
    /// <para>The first and last few cycles of a filter's corner are its own assumption about what
    /// lies outside the record — see <see cref="Tonebench.Dsp.WaveformFilters"/>, where the two
    /// paddings that were weighed are measured. Pinning a figure that includes them would be
    /// pinning that assumption rather than the filter.</para>
    /// </summary>
    private static ReadOnlySpan<double> Middle(double[] channel) =>
        channel.AsSpan(channel.Length / 4, channel.Length / 2);

    // -----------------------------------------------------------------------
    // Doing nothing
    // -----------------------------------------------------------------------

    /// <summary>
    /// A path that asks for nothing hands back the <b>same instance</b>, so an unfiltered fetch
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
        var path = new ScopeSignalPath(ScopeCoupling.Ac, ScopeFilterSpec.Off);

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
        var coupled = new ScopeSignalPath(ScopeCoupling.Ac, ScopeFilterSpec.Off).Apply(record);

        for (int i = 0; i < record.FrameCount; i += 97)
            Assert.Equal(record.Channels[0][i] - 1.0, coupled.Channels[0][i], 6);
    }

    [Fact]
    public void AcCoupling_TreatsEachChannelAsItsOwn()
    {
        var record = Record(i => Sine(i, 1_000.0) + 2.0, i => Sine(i, 1_000.0) - 5.0);
        var coupled = new ScopeSignalPath(ScopeCoupling.Ac, ScopeFilterSpec.Off).Apply(record);

        Assert.Equal(0.0, ScopeSliceMeasurements.Of(coupled.Channels[0], Fs).MeanV, 9);
        Assert.Equal(0.0, ScopeSliceMeasurements.Of(coupled.Channels[1], Fs).MeanV, 9);
    }

    // -----------------------------------------------------------------------
    // The filter
    // -----------------------------------------------------------------------

    /// <summary>
    /// A notch takes the hum out and leaves the rest.
    ///
    /// <para>Over a <b>one-second</b> record, which is not padding of the test: a notch rings down
    /// over about Q periods of its own centre, so a Q of 8 at 50 Hz needs some 160 ms to settle
    /// and the padding cannot invent more record than there is — <c>PadLength</c> is clamped to the
    /// record's own length, because there are no samples outside it to reflect. On a 200 ms record
    /// the same notch leaves a sixth of the hum behind, which is the filter being asked for
    /// something the capture cannot give it rather than a fault.</para>
    /// </summary>
    [Fact]
    public void ANotch_RemovesTheHumAndLeavesTheTone()
    {
        var record = Record(i => Sine(i, 5_000.0) + Sine(i, 50.0, 0.4), frames: 48_000);
        var path = new ScopeSignalPath(ScopeCoupling.Dc, new ScopeFilterSpec(ScopeFilterKind.Notch, 50.0, Q: 8.0));

        var before = ScopeSliceMeasurements.Of(Middle(record.Channels[0]), Fs);
        var after = ScopeSliceMeasurements.Of(Middle(path.Apply(record).Channels[0]), Fs);

        // 2.8 peak to peak before: a 1 V tone riding on 0.4 V of hum. 2.0 after: the tone alone.
        Assert.Equal(2.8, before.PeakToPeakV, 1);
        Assert.Equal(2.0, after.PeakToPeakV, 1);
    }

    /// <summary>
    /// Both channels go through it, so a two-channel reading — the math channel, the X–Y figure —
    /// is of one filtered pair rather than of one filtered and one raw.
    /// </summary>
    [Fact]
    public void BothChannelsGoThroughIt()
    {
        var record = Record(i => Sine(i, 8_000.0));
        var path = new ScopeSignalPath(ScopeCoupling.Dc, new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0, Order: 4));

        var filtered = path.Apply(record);

        foreach (var channel in filtered.Channels)
        {
            double pp = ScopeSliceMeasurements.Of(Middle(channel), Fs).PeakToPeakV;
            Assert.True(pp < 0.01, $"an 8 kHz tone has no business surviving a 1 kHz low-pass: {pp}");
        }
    }

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

        var filtered = new ScopeSignalPath(ScopeCoupling.Ac, new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0))
            .Apply(raw);

        Assert.Equal(raw.FirstIndex, filtered.FirstIndex);
        Assert.Equal(raw.TriggerPosition, filtered.TriggerPosition);
        Assert.Equal(raw.Epoch, filtered.Epoch);
        Assert.True(filtered.Spliced);
        Assert.True(filtered.Forced);
        Assert.Equal(raw.FrameCount, filtered.FrameCount);
        Assert.NotSame(raw.Channels, filtered.Channels);
    }

    /// <summary>
    /// The coupling runs before the filter, which is the order a bench instrument has them in and
    /// the only one that is well behaved: a filter fed a standing offset spends its first samples
    /// settling on it.
    /// </summary>
    [Fact]
    public void TheCouplingRunsFirst()
    {
        var record = Record(i => Sine(i, 1_000.0, 0.2) + 3.0);
        var path = new ScopeSignalPath(ScopeCoupling.Ac, new ScopeFilterSpec(ScopeFilterKind.LowPass, 4_000.0, Order: 4));

        var result = path.Apply(record);

        // The offset is gone and the tone is not, which only holds if the mean went first: a
        // low-pass passes 3 V of DC untouched.
        Assert.Equal(0.0, ScopeSliceMeasurements.Of(result.Channels[0], Fs).MeanV, 6);
        Assert.Equal(0.4, ScopeSliceMeasurements.Of(result.Channels[0], Fs).PeakToPeakV, 2);
    }

    // -----------------------------------------------------------------------
    // What everything downstream sees
    // -----------------------------------------------------------------------

    /// <summary>
    /// The path is applied once to the whole record and everything is cut from the result, so a
    /// window moved along it is filtered the same way wherever it sits — and the figures beside the
    /// picture are of the trace in it.
    /// </summary>
    [Fact]
    public void AWindowIsCutFromTheFilteredRecord_WhereverItSits()
    {
        var record = Record(i => Sine(i, 8_000.0) + Sine(i, 200.0), frames: 48_000);
        var filtered = new ScopeSignalPath(ScopeCoupling.Dc, new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0, Order: 4))
            .Apply(record);

        // Three windows from different parts of the record read the same amplitude, which they
        // would not if each were filtered from rest.
        double first = ScopeFrameBuilder.Window(filtered, 0, 0.2, 0.3).Measurements.PeakToPeakV;
        double middle = ScopeFrameBuilder.Window(filtered, 0, 0.4, 0.5).Measurements.PeakToPeakV;
        double last = ScopeFrameBuilder.Window(filtered, 0, 0.6, 0.7).Measurements.PeakToPeakV;

        Assert.Equal(first, middle, 2);
        Assert.Equal(first, last, 2);
        // And the 8 kHz component really is gone: a 200 Hz sine alone is 2 V peak to peak.
        Assert.Equal(2.0, first, 1);
    }

    /// <summary>
    /// What the filter leaves at the record's own ends is its assumption about what lies outside
    /// it, and it is <b>bounded</b> rather than absent.
    ///
    /// <para>Stated as a test because it is a property a reader meets rather than a defect to
    /// chase: the record is a window of a stream that has since moved on, so nothing in it says
    /// what came next, and every padding is a guess. See Tonebench.Dsp.WaveformFilters for the two
    /// that were weighed and why the mirror won on its worst case. A fifth of full scale is the
    /// bound; the middle of the record is exact.</para>
    /// </summary>
    [Fact]
    public void WhatItLeavesAtTheEnds_IsBoundedRatherThanAbsent()
    {
        var record = Record(i => Sine(i, 8_000.0));
        var filtered = new ScopeSignalPath(ScopeCoupling.Dc, new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0, Order: 4))
            .Apply(record);

        double middle = ScopeSliceMeasurements.Of(Middle(filtered.Channels[0]), Fs).PeakToPeakV;
        double whole = ScopeSliceMeasurements.Of(filtered.Channels[0], Fs).PeakToPeakV;

        Assert.True(middle < 1e-6, $"the middle of the record is the filter's own answer: {middle}");
        Assert.True(whole < 0.2, $"and what the ends assume is bounded by a fifth of full scale: {whole}");
    }

    /// <summary>
    /// A trace with nothing in it is still a record: an empty path on an empty record must not
    /// throw on a fetch path.
    /// </summary>
    [Fact]
    public void AnEmptyRecord_IsHandedBack()
    {
        var empty = new ScopeRecord([], Fs, 0, 0, 0, false, false);

        Assert.Same(empty, new ScopeSignalPath(ScopeCoupling.Ac, ScopeFilterSpec.Off).Apply(empty));
    }
}
