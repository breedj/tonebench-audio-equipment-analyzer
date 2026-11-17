using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// Cutting a record down for the wire: the overview the zoom strip is drawn from, and the floor at
/// which a tight window stops being summarised and becomes the waveform itself.
/// </summary>
public class ScopeFrameBuilderTests
{
    private const double SampleRateHz = 192_000.0;

    /// <summary>A record of <paramref name="frames"/> frames whose left channel is a ramp of frame indices.</summary>
    private static ScopeRecord Record(int frames = 192_000, int preTrigger = 96_000)
    {
        var left = new double[frames];
        var right = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = i;
            right[i] = -i;
        }

        return new ScopeRecord(
            [left, right],
            SampleRateHz,
            FirstIndex: 0,
            TriggerPosition: preTrigger,
            Epoch: 0,
            Spliced: false,
            Forced: false);
    }

    [Fact]
    public void TheOverviewIsAlwaysColumns_NeverTheWholeRecord()
    {
        // One second of 192 kS/s stereo is 1.5 MB as f32 and the hub's per-client channel is 64
        // frames deep. The answer is not a deeper channel but never putting a megabyte in it.
        var slice = ScopeFrameBuilder.Overview(Record(), channel: 0);

        Assert.False(slice.IsRaw);
        Assert.NotNull(slice.Columns);
        Assert.Equal(ScopeFrameBuilder.DefaultMaxColumns, slice.Columns!.Length);

        // 1600 columns of (min, max) f32 is 12.8 kB a channel -- the figure the design rests on.
        Assert.Equal(12_800, slice.Columns.Length * 2 * sizeof(float));
    }

    [Fact]
    public void TheOverviewSpansTheWholeRecord_WithTheTriggerAtZero()
    {
        var record = Record();
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Equal(record.TimeAt(0), slice.X0, 9);
        Assert.True(slice.X0 < 0.0, "half the record is pre-trigger, so it starts before t = 0");
        Assert.Equal(0, slice.FirstFrame);
        Assert.Equal(record.FrameCount, slice.FrameCount);

        // The last column ends at the end of the record.
        double end = slice.X0 + (slice.Columns!.Length * slice.Dx);
        Assert.Equal(record.TimeAt(record.FrameCount - 1), end, 4);
    }

    [Fact]
    public void MinAndMaxAreBothKept_SoNothingNarrowerThanAColumnCanHide()
    {
        // A single-sample spike. A mean would flatten it into the trace around it and erase the one
        // thing the scope is being pointed at.
        var left = new double[100_000];
        var right = new double[100_000];
        left[54_321] = 9.0;

        var record = new ScopeRecord([left, right], SampleRateHz, 0, 50_000, 0, false, false);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Contains(slice.Columns!, c => c.Max >= 9.0);
    }

    [Fact]
    public void ATightWindowIsServedRaw()
    {
        // The floor that makes edge and timing work believable: a rise time read off a min/max
        // column pair is a reading of the decimation, not of the waveform.
        var record = Record();

        // 1 ms at 192 kS/s is 192 samples -- far under the column budget.
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0.0, toS: 0.001);

        Assert.True(slice.IsRaw);
        Assert.Null(slice.Columns);
        Assert.Equal(1.0 / SampleRateHz, slice.Dx, 12);
        Assert.InRange(slice.Samples!.Length, 190, 195);
    }

    [Fact]
    public void AWideWindowIsStillColumns()
    {
        var record = Record();
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS: -0.2, toS: 0.2);

        Assert.False(slice.IsRaw);
        Assert.Equal(ScopeFrameBuilder.DefaultMaxColumns, slice.Columns!.Length);
    }

    [Fact]
    public void TheRawFloorIsWhereDecimationStopsSayingAnything()
    {
        // Exactly at the boundary and just past it, so the step is pinned rather than assumed.
        var record = Record();
        int budget = ScopeFrameBuilder.DefaultMaxColumns * ScopeFrameBuilder.RawSamplesPerColumn;

        var atFloor = ScopeFrameBuilder.Window(record, 0, 0.0, budget / SampleRateHz);
        var pastFloor = ScopeFrameBuilder.Window(record, 0, 0.0, (budget * 4) / SampleRateHz);

        Assert.True(atFloor.IsRaw);
        Assert.False(pastFloor.IsRaw);
    }

    [Fact]
    public void AWindowCutsTheSamplesItNames()
    {
        // The left channel is a ramp of frame indices, so the first sample of a window says exactly
        // which frame it came from.
        var record = Record();
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0.0, toS: 0.0005);

        Assert.True(slice.IsRaw);

        // t = 0 is frame 96000 (the trigger), so that is the first sample in the window.
        Assert.Equal(96_000, slice.FirstFrame);
        Assert.Equal(96_000f, slice.Samples![0]);
        Assert.Equal(0.0, slice.X0, 9);
    }

    [Fact]
    public void AWindowReachingOutsideTheRecord_IsClampedToIt()
    {
        var record = Record();
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS: -10.0, toS: 10.0);

        Assert.Equal(0, slice.FirstFrame);
        Assert.Equal(record.FrameCount, slice.FrameCount);
    }

    [Theory]
    [InlineData(0.1, 0.1)]
    [InlineData(0.2, 0.1)]
    [InlineData(double.NaN, 0.1)]
    public void AWindowThatSelectsNothing_FallsBackToTheWholeRecord(double fromS, double toS)
    {
        // An empty chart is never the useful answer, and a NaN must not reach the encoder.
        var record = Record();
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS, toS);

        Assert.Equal(0, slice.FirstFrame);
        Assert.Equal(record.FrameCount, slice.FrameCount);
        Assert.True(double.IsFinite(slice.X0));
        Assert.True(double.IsFinite(slice.Dx));
    }

    [Fact]
    public void EachChannelIsCutIndependently()
    {
        var record = Record();
        var left = ScopeFrameBuilder.Window(record, channel: 0, 0.0, 0.0005);
        var right = ScopeFrameBuilder.Window(record, channel: 1, 0.0, 0.0005);

        Assert.Equal(96_000f, left.Samples![0]);
        Assert.Equal(-96_000f, right.Samples![0]);
        Assert.Equal(left.X0, right.X0, 12);
    }

    [Fact]
    public void AnOverviewEncodesAndDecodesBackToTheSameColumns()
    {
        var record = Record();
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        byte[] frame = StreamFrame.EncodeScopeRecord(
            StreamChannel.Left, seq: 7, (float)slice.X0, (float)slice.Dx, slice.Columns!, StreamFlags.ScopeSpliced);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.Equal(StreamMsgType.ScopeRecord, header.MsgType);
        Assert.True(header.Flags.HasFlag(StreamFlags.Decimated));
        Assert.True(header.Flags.HasFlag(StreamFlags.ScopeSpliced));
        Assert.Equal((uint)slice.Columns!.Length, header.NPoints);

        var columns = StreamFrame.DecodeScopeColumns(frame);
        Assert.Equal(slice.Columns.Length, columns.Length);
        Assert.Equal(slice.Columns[0].Max, columns[0].Max, 3);
        Assert.Equal(slice.Columns[^1].Max, columns[^1].Max, 3);
    }

    [Fact]
    public void ARawWindowEncodesAndDecodesBackToTheSameSamples()
    {
        var record = Record();
        var slice = ScopeFrameBuilder.Window(record, channel: 0, 0.0, 0.0005);

        byte[] frame = StreamFrame.EncodeScopeSamples(
            StreamChannel.Left, seq: 7, (float)slice.X0, (float)slice.Dx, slice.Samples!, StreamFlags.ScopeZoomWindow);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.False(header.Flags.HasFlag(StreamFlags.Decimated));
        Assert.True(header.Flags.HasFlag(StreamFlags.ScopeZoomWindow));

        Assert.Equal(slice.Samples!, StreamFrame.DecodeScopeRecordSamples(frame));
    }

    [Fact]
    public void AScopeRecordCannotSmuggleAnotherFramesMeaning()
    {
        // The counterpart of RequireOriginFlag, and it throws at run time for the same reason: a
        // new flag that forgets to be listed fails on the first encode rather than silently
        // meaning something to a decoder that reads it.
        var columns = new MinMax[4];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamFrame.EncodeScopeRecord(StreamChannel.Left, 0, 0f, 1f, columns, StreamFlags.Crosstalk));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamFrame.EncodeScopeRecord(StreamChannel.Left, 0, 0f, 1f, columns, StreamFlags.Zoomed));
    }

    [Fact]
    public void ARawPayloadMayNotClaimToBeDecimated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamFrame.EncodeScopeSamples(StreamChannel.Left, 0, 0f, 1f, new float[4], StreamFlags.Decimated));
    }

    [Fact]
    public void TheTwoPayloadsRefuseToBeReadAsEachOther()
    {
        var columns = StreamFrame.EncodeScopeRecord(StreamChannel.Left, 0, 0f, 1f, new MinMax[4]);
        var raw = StreamFrame.EncodeScopeSamples(StreamChannel.Left, 0, 0f, 1f, new float[4]);

        Assert.Throws<ArgumentException>(() => StreamFrame.DecodeScopeRecordSamples(columns));
        Assert.Throws<ArgumentException>(() => StreamFrame.DecodeScopeColumns(raw));
    }

    [Fact]
    public void TheMeanIsOfTheSamples_NotOfTheColumnsTheyAreDrawnAs()
    {
        // The reading the decimation cannot carry, and the reason it is measured here at all. A
        // sine of amplitude A about an offset has a sample mean of exactly the offset; the mean of
        // its column *maxima* is about 0.64 A above that (the envelope's own average over a
        // window holding many periods per column). So a client working this out from what arrives
        // on the wire would be confidently wrong rather than coarse.
        const int frames = 192_000;
        const double offsetV = 0.25;
        const double amplitudeV = 1.0;

        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = offsetV + (amplitudeV * Math.Sin(2 * Math.PI * 1_000.0 * i / SampleRateHz));

        var record = new ScopeRecord(
            [samples], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);

        var slice = ScopeFrameBuilder.Overview(record, channel: 0, maxColumns: 600);
        Assert.False(slice.IsRaw);
        Assert.Equal(offsetV, slice.Measurements.MeanV, 4);

        // What a reader would have got from the picture, so the test says what it is protecting
        // against rather than only what is right.
        double fromTheColumns = slice.Columns!.Average(c => c.Max);
        Assert.True(fromTheColumns - offsetV > 0.5, $"columns mean {fromTheColumns} was expected to sit well above {offsetV}");
    }

    [Fact]
    public void AWindowsMeanIsOfThatWindow_AndSurvivesBeingServedRaw()
    {
        // The same figure whichever side of the raw floor the window falls, because it is the same
        // samples either way -- and it follows the window rather than the record, so it is a
        // reading of what is on screen.
        var record = Record(frames: 192_000, preTrigger: 0);

        // The ramp is the frame index, so the mean of frames [a, b) is their midpoint.
        var raw = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0, toS: 1_000 / SampleRateHz, maxColumns: 600);
        Assert.True(raw.IsRaw);
        Assert.Equal((raw.FirstFrame + raw.FrameCount - 1) / 2.0, raw.Measurements.MeanV, 3);

        var decimated = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0, toS: 0.5, maxColumns: 600);
        Assert.False(decimated.IsRaw);
        Assert.Equal(
            decimated.FirstFrame + ((decimated.FrameCount - 1) / 2.0), decimated.Measurements.MeanV, 3);
    }
}
