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

    /// <summary>
    /// The margin the client's sin(x)/x reconstruction reaches into at the ends of a window.
    ///
    /// <para>The left channel is a ramp of frame indices, so a margin sample's own value says which
    /// frame it came from — which is what makes "immediately before" and "immediately after"
    /// checkable rather than merely present.</para>
    /// </summary>
    [Fact]
    public void AWindowInsideTheRecord_CarriesRealSamplesEitherSideOfIt()
    {
        var record = Record();

        var slice = ScopeFrameBuilder.Window(
            record, channel: 0, fromS: 0.0, toS: 0.001, maxColumns: 600, marginSamples: 16);

        Assert.True(slice.IsRaw);
        Assert.Equal(16, slice.LeadSamples!.Length);
        Assert.Equal(16, slice.TrailSamples!.Length);

        // The sixteen frames before the window, in order, ending one short of its first sample.
        Assert.Equal(slice.FirstFrame - 16, (int)slice.LeadSamples[0]);
        Assert.Equal(slice.FirstFrame - 1, (int)slice.LeadSamples[^1]);

        // And the sixteen after it, starting one past its last.
        Assert.Equal(slice.FirstFrame + slice.FrameCount, (int)slice.TrailSamples[0]);
        Assert.Equal(slice.FirstFrame + slice.FrameCount + 15, (int)slice.TrailSamples[^1]);
    }

    /// <summary>
    /// And the window itself does not move, which is the whole care in this.
    ///
    /// <para>Four things are built "on the slice's own geometry" from <c>FirstFrame</c> and
    /// <c>FrameCount</c> — the residual, the math trace, the X–Y figure and the eye — and
    /// <c>Measurements</c> is explicitly of the window. Widen that pair to carry the margin and all
    /// five silently change what they are of, which is a reading that is wrong rather than a picture
    /// that is coarse.</para>
    /// </summary>
    [Fact]
    public void TheMarginDoesNotMoveTheWindowItIsAMarginOf()
    {
        var record = Record();

        var plain = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0.0, toS: 0.001, maxColumns: 600);
        var padded = ScopeFrameBuilder.Window(
            record, channel: 0, fromS: 0.0, toS: 0.001, maxColumns: 600, marginSamples: 16);

        Assert.Equal(plain.FirstFrame, padded.FirstFrame);
        Assert.Equal(plain.FrameCount, padded.FrameCount);
        Assert.Equal(plain.X0, padded.X0);
        Assert.Equal(plain.Samples!.Length, padded.Samples!.Length);
        Assert.Equal(plain.Measurements.MaxV, padded.Measurements.MaxV);

        // And asking for none is exactly what it was before any of this existed.
        Assert.Null(plain.LeadSamples);
        Assert.Null(plain.TrailSamples);
    }

    /// <summary>
    /// At the record's own ends there is nothing outside to send, and that is not an error — it is
    /// the one case the client's kernel has to answer for itself.
    /// </summary>
    [Fact]
    public void AtTheRecordsOwnEnds_ThereIsNoMarginToGive()
    {
        var record = Record(frames: 2_000, preTrigger: 0);

        var atStart = ScopeFrameBuilder.Window(
            record, channel: 0, fromS: 0.0, toS: 100 / SampleRateHz, maxColumns: 600, marginSamples: 16);

        Assert.Null(atStart.LeadSamples);
        Assert.NotNull(atStart.TrailSamples);

        // The whole record is both ends at once.
        var whole = ScopeFrameBuilder.Window(
            record, channel: 0, fromS: -10.0, toS: 10.0, maxColumns: 4_000, marginSamples: 16);

        Assert.Null(whole.LeadSamples);
        Assert.Null(whole.TrailSamples);
    }

    /// <summary>
    /// A decimated slice gets none: there are no samples on it to reconstruct between, so the client
    /// falls back to lines there and a margin would be bytes nothing could read.
    /// </summary>
    [Fact]
    public void ADecimatedWindow_GetsNoMargin()
    {
        var record = Record();

        var slice = ScopeFrameBuilder.Window(
            record, channel: 0, fromS: 0.0, toS: 0.5, maxColumns: 600, marginSamples: 16);

        Assert.False(slice.IsRaw);
        Assert.Null(slice.LeadSamples);
        Assert.Null(slice.TrailSamples);
    }

    /// <summary>
    /// And the raw-or-columns decision is taken on the window that was asked for, never on the
    /// padded one — a margin is context for drawing and has no business deciding that.
    /// </summary>
    [Fact]
    public void TheMarginDoesNotPushAWindowOverTheRawFloor()
    {
        var record = Record();
        const int budget = 600;

        // Exactly at the floor: 1200 samples against 600 columns at two samples a column.
        double toS = (budget * ScopeFrameBuilder.RawSamplesPerColumn) / SampleRateHz;

        var padded = ScopeFrameBuilder.Window(
            record, channel: 0, fromS: 0.0, toS: toS, maxColumns: budget, marginSamples: 64);

        Assert.True(padded.IsRaw);
        Assert.Equal(64, padded.LeadSamples!.Length);
    }

    /// <summary>The overview is the whole record, so it has no outside and asks for none.</summary>
    [Fact]
    public void TheOverview_HasNoMargin()
    {
        var slice = ScopeFrameBuilder.Overview(Record(), channel: 0);

        Assert.Null(slice.LeadSamples);
        Assert.Null(slice.TrailSamples);
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

    // -----------------------------------------------------------------------------------------
    // The gate -- a window measured and not drawn, which is what a cursor pair asks for
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AGate_MeasuresTheWindowItNames_AndNotTheWholeRecord()
    {
        // The left channel is a ramp of frame indices, so a window's own max and min say exactly
        // which frames were measured -- which is the whole question a gated reading raises.
        var record = Record();
        double from = record.TimeAt(1_000);
        double to = record.TimeAt(2_000);

        var gated = ScopeFrameBuilder.Measure(record, channel: 0, from, to);

        // Half-open, like the slice: the window holds frames 1000..1999, the end being the frame
        // the time rounds up to rather than one inside it.
        Assert.NotNull(gated);
        Assert.Equal(1_000, gated!.MinV, 0);
        Assert.Equal(1_999, gated.MaxV, 0);
        Assert.Equal(1_499.5, gated.MeanV, 6);
    }

    [Fact]
    public void AGate_AgreesWithTheSliceDrawnForTheSameWindow()
    {
        // Two code paths naming the same frames: a gated reading of a window half a sample
        // different from the one on screen is exactly the plausible disagreement nothing would
        // flag, which is why both go through one FrameRange.
        var record = Record();
        double from = record.TimeAt(4_000);
        double to = record.TimeAt(9_000);

        var slice = ScopeFrameBuilder.Window(record, channel: 0, from, to);
        var gated = ScopeFrameBuilder.Measure(record, channel: 0, from, to);

        Assert.NotNull(gated);
        Assert.Equal(slice.Measurements.MinV, gated!.MinV, 9);
        Assert.Equal(slice.Measurements.MaxV, gated.MaxV, 9);
        Assert.Equal(slice.Measurements.RmsV, gated.RmsV, 9);
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(0.0, double.NaN)]
    [InlineData(0.01, 0.01)] // zero width
    [InlineData(0.02, 0.01)] // reversed
    public void AGate_ThatNamesNothing_IsRefusedRatherThanAnsweredWithTheWholeRecord(double from, double to)
    {
        // Window() falls back to the overview here, deliberately: a picture is always better drawn
        // than blank. A *gate* must not, or the panel quotes the whole record's figures under a
        // heading saying they are the cursors'.
        Assert.Null(ScopeFrameBuilder.Measure(Record(), channel: 0, from, to));
    }

    [Theory]
    [InlineData(-5.0, -4.0)]
    [InlineData(4.0, 5.0)]
    public void AGate_WhollyOutsideTheRecord_IsRefused(double from, double to)
    {
        // Clamped instead, this would measure the first or the last sample alone and read as a
        // measurement of a flat line.
        Assert.Null(ScopeFrameBuilder.Measure(Record(), channel: 0, from, to));
    }

    [Fact]
    public void AGate_ReachingPastTheRecord_MeasuresTheOverlap()
    {
        // Half in is a real window, unlike wholly out: the samples it names exist.
        var record = Record();
        var gated = ScopeFrameBuilder.Measure(record, channel: 0, record.TimeAt(0) - 1.0, record.TimeAt(10));

        Assert.NotNull(gated);
        Assert.Equal(0, gated!.MinV, 0);
        Assert.Equal(9, gated.MaxV, 0);
    }

    [Fact]
    public void AGate_ReadsItsOwnChannel()
    {
        var record = Record();
        double from = record.TimeAt(1_000);
        double to = record.TimeAt(2_000);

        // The right channel is the negated ramp, so a gate that read channel 0 for both would be
        // obvious here and nowhere else.
        Assert.Equal(1_999, ScopeFrameBuilder.Measure(record, 0, from, to)!.MaxV, 0);
        Assert.Equal(-1_000, ScopeFrameBuilder.Measure(record, 1, from, to)!.MaxV, 0);
    }

    // -----------------------------------------------------------------------------------------
    // The residual -- the window with its fundamental taken out
    // -----------------------------------------------------------------------------------------

    /// <summary>A record of a tone plus a third harmonic of a known size, which is what the residual must be left holding.</summary>
    private static ScopeRecord ToneRecord(
        double freqHz = 1_000.0, double ampV = 1.0, double h3 = 0.01, int frames = 19_200, double offsetV = 0.0)
    {
        var left = new double[frames];
        var right = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double t = i / SampleRateHz;
            left[i] = (ampV * Math.Sin(2.0 * Math.PI * freqHz * t))
                + (ampV * h3 * Math.Sin(2.0 * Math.PI * 3.0 * freqHz * t))
                + offsetV;
            right[i] = left[i];
        }

        return new ScopeRecord(
            [left, right], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
    }

    [Fact]
    public void TheResidual_IsWhatIsLeftWhenTheFundamentalIsTakenOut()
    {
        // A 1 % third harmonic on a 1 V tone: the residual's RMS must be that harmonic's own, and
        // the percentage must read 1 %. Both are figures the waveform was built with.
        var record = ToneRecord(h3: 0.01);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var residual = ScopeFrameBuilder.Residual(record, 0, slice);

        Assert.NotNull(residual);
        // Within a millihertz over a 171 ms fit window that also holds the harmonic being measured.
        Assert.Equal(1_000.0, residual!.FitFrequencyHz, 2);
        Assert.Equal(1.0, residual.FitAmplitudeVpk, 4);
        Assert.Equal(0.01 / Math.Sqrt(2.0), residual.ResidualRmsV, 5);
        Assert.NotNull(residual.ResidualPercent);
        Assert.Equal(1.0, residual.ResidualPercent!.Value, 3);
    }

    [Fact]
    public void TheResidual_SubtractsTheDcTermWithTheTone()
    {
        // The fit is four-parameter, so an offset rides out with the sine rather than being left in
        // the residual and read as distortion. Every input here is AC-coupled, but the analyzer's
        // own few millivolts are real and would otherwise dominate a quiet residual.
        var record = ToneRecord(h3: 0.01, offsetV: 0.25);
        var residual = ScopeFrameBuilder.Residual(record, 0, ScopeFrameBuilder.Overview(record, channel: 0));

        Assert.NotNull(residual);
        Assert.Equal(0.25, residual!.FitOffsetV, 4);
        Assert.Equal(0.01 / Math.Sqrt(2.0), residual.ResidualRmsV, 5);
    }

    [Fact]
    public void TheResidual_CancelsAToneThatIsNotOnAWholeNumberOfSamples()
    {
        // The whole reason SineFit refines its frequency. Half a bin is pi radians of drift end to
        // end, which reads as a fundamental-sized residual with nothing wrong.
        var record = ToneRecord(freqHz: 997.37, h3: 0.0);
        var residual = ScopeFrameBuilder.Residual(record, 0, ScopeFrameBuilder.Overview(record, channel: 0));

        Assert.NotNull(residual);
        // Better than -100 dB of the fundamental, over a window the fit covered only part of.
        Assert.True(residual!.ResidualPercent < 0.001, $"residual was {residual.ResidualPercent} %");
    }

    [Fact]
    public void TheResidual_IsRefusedWhereThereIsNoFundamentalToRemove()
    {
        // Digital silence has one settled level, so no period is measured and no seed exists. A fit
        // seeded with a guess would subtract an invented tone and call the difference a residual.
        var flat = new double[4_800];
        var record = new ScopeRecord(
            [flat, flat], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);

        var slice = ScopeFrameBuilder.Overview(record, channel: 0);
        Assert.Null(slice.Measurements.FrequencyHz);
        Assert.Null(ScopeFrameBuilder.Residual(record, 0, slice));
    }

    [Fact]
    public void TheResidual_HasTheSameShapeAsTheSliceItIsDrawnWith()
    {
        // Two traces on one x array: one raw and the other decimated would put the error where the
        // signal is not.
        var record = ToneRecord(frames: 192_000);

        var overview = ScopeFrameBuilder.Overview(record, channel: 0);
        var decimated = ScopeFrameBuilder.Residual(record, 0, overview);
        Assert.NotNull(decimated);
        Assert.Null(decimated!.Samples);
        Assert.Equal(overview.Columns!.Length, decimated.Columns!.Length);

        var tight = ScopeFrameBuilder.Window(record, 0, record.TimeAt(0), record.TimeAt(1_000));
        Assert.True(tight.IsRaw);
        var raw = ScopeFrameBuilder.Residual(record, 0, tight);
        Assert.NotNull(raw);
        Assert.Null(raw!.Columns);
        Assert.Equal(tight.Samples!.Length, raw.Samples!.Length);
    }

    [Fact]
    public void TheResidualsFitWindowIsBounded_AndStillCancelsAcrossTheWholeWindow()
    {
        // SineFit is O(n) per iteration and takes 2.65 s over a 1,048,576-point record, so the fit
        // is taken over a bounded, centred window and subtracted across the whole of it. What that
        // has to buy is cancellation at the *ends*, which is where a centred window's worst case is.
        var record = ToneRecord(freqHz: 1_013.7, h3: 0.0, frames: 192_000);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.True(slice.FrameCount > ScopeFrameBuilder.MaxResidualFitFrames, "the window must be longer than the fit");

        var residual = ScopeFrameBuilder.Residual(record, 0, slice);
        Assert.NotNull(residual);

        // Every column, both extremes -- so the ends are included rather than averaged away.
        double worst = residual!.Columns!.Max(c => Math.Max(Math.Abs(c.Min), Math.Abs(c.Max)));
        Assert.True(worst < 1e-3, $"worst residual sample was {worst} V against a 1 V fundamental");
    }

}
