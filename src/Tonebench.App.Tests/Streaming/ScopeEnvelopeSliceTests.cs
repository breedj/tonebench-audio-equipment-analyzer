using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// An envelope as the chart receives it: a band rather than a waveform, and the things that assume
/// a waveform refusing on one.
///
/// <para>The arithmetic of accumulating is <c>RecordAccumulatorTests</c>'. What is tested here is
/// the <b>seam</b> — that the two outlines reach the slice as its min/max columns with the right one
/// in each half, that the readings are the band's own extremes and nothing else, and that the math
/// channel, the X–Y figure and the eye all answer null rather than plausibly.</para>
/// </summary>
public class ScopeEnvelopeSliceTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>A record whose upper outline is <paramref name="high"/> and lower is <paramref name="low"/>.</summary>
    private static ScopeRecord Envelope(double[] low, double[] high, double[]? lowB = null, double[]? highB = null)
    {
        double[][] upper = highB is null ? [high] : [high, highB];
        double[][] lower = lowB is null ? [low] : [low, lowB];

        return new ScopeRecord(
            upper, SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0,
            Spliced: false, Forced: false, Rolled: false, Lower: lower, AveragesTaken: 7);
    }

    private static double[] Ramp(int frames, double scale)
    {
        var channel = new double[frames];
        for (int i = 0; i < frames; i++)
            channel[i] = i * scale;
        return channel;
    }

    [Fact]
    public void ARecordWithALowerOutline_IsAnEnvelope()
    {
        var record = Envelope([0.0, 0.0], [1.0, 1.0]);

        Assert.True(record.IsEnvelope);
        Assert.Equal(7, record.AveragesTaken);

        // And an ordinary capture is not, by the same one field.
        var plain = new ScopeRecord(
            [[0.0, 1.0]], SampleRateHz, 0, 0, 0, Spliced: false, Forced: false);
        Assert.False(plain.IsEnvelope);
        Assert.Equal(1, plain.AveragesTaken);
    }

    [Fact]
    public void TheSliceIsAlwaysColumns_WithTheOutlinesInTheRightHalves()
    {
        // Short enough that a waveform would be served raw -- an envelope must not be, because the
        // pair of outlines IS the min/max per column the chart draws and a raw slice has nowhere to
        // put the second one. A single outline served as a waveform would be read as one.
        var record = Envelope([-1.0, -2.0, -3.0, -4.0], [1.0, 2.0, 3.0, 4.0]);

        var slice = ScopeFrameBuilder.Overview(record, channel: 0, maxColumns: 4);

        Assert.False(slice.IsRaw);
        Assert.Null(slice.Samples);
        Assert.NotNull(slice.Columns);
        Assert.Equal(4, slice.Columns!.Length);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(-(i + 1.0), slice.Columns[i].Min, 9);
            Assert.Equal(i + 1.0, slice.Columns[i].Max, 9);
        }
    }

    [Fact]
    public void AColumnTakesTheLowestOfTheLowerAndTheHighestOfTheUpper()
    {
        // The reason the reducer is its own loop rather than two passes of DecimateLinear: which
        // extreme comes from which array. Decimating the upper outline alone -- the obvious
        // shortcut, since it is what sits in Channels -- would draw the band's top edge twice and
        // lose the bottom one.
        var record = Envelope(Ramp(8, -1.0), Ramp(8, 1.0));

        var slice = ScopeFrameBuilder.Overview(record, channel: 0, maxColumns: 2);

        Assert.NotNull(slice.Columns);
        Assert.Equal(2, slice.Columns!.Length);
        // First half: lower runs 0..-3, upper 0..3.
        Assert.Equal(-3.0, slice.Columns[0].Min, 9);
        Assert.Equal(3.0, slice.Columns[0].Max, 9);
        // Second half: -4..-7 and 4..7.
        Assert.Equal(-7.0, slice.Columns[1].Min, 9);
        Assert.Equal(7.0, slice.Columns[1].Max, 9);
    }

    [Fact]
    public void TheReadingsAreTheBandsOwnExtremes_AndNothingElse()
    {
        // Absent rather than wrong, and this is the place with the most opportunities to break it:
        // a mean, an RMS or a crest factor of an outline is arithmetic on something that was never
        // a signal, and a period or a rise time is a reading of the accumulation.
        var record = Envelope([-0.5, -2.0, -0.5], [0.5, 3.0, 0.5]);

        var m = ScopeFrameBuilder.Overview(record, channel: 0).Measurements;

        Assert.Equal(3.0, m.MaxV, 9);
        Assert.Equal(-2.0, m.MinV, 9);
        Assert.Equal(5.0, m.PeakToPeakV, 9);

        Assert.Null(m.CrestFactor);
        Assert.Null(m.TopV);
        Assert.Null(m.BaseV);
        Assert.Null(m.PeriodS);
        Assert.Null(m.FrequencyHz);
        Assert.Null(m.DutyPercent);
        Assert.Null(m.RiseTimeS);
        Assert.Null(m.FallTimeS);
        Assert.Null(m.OvershootPercent);
        // The three that are not nullable read zero rather than a figure of an outline.
        Assert.Equal(0.0, m.MeanV);
        Assert.Equal(0.0, m.RmsV);
        Assert.Equal(0.0, m.AcRmsV);
    }

    [Fact]
    public void TheMathChannelRefusesAnEnvelope()
    {
        // Combining two upper outlines is arithmetic on two things that were never signals, and
        // A - B of them would be read as a differential measurement.
        var record = Envelope([-1.0, -1.0], [1.0, 1.0], [-2.0, -2.0], [2.0, 2.0]);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Null(ScopeFrameBuilder.MathTrace(record, slice, 1, WaveformOperation.AMinusB, 0, 1));
        Assert.Null(ScopeFrameBuilder.MathGate(record, WaveformOperation.AMinusB, 0, 1, 0.0, 1.0));
    }

    [Fact]
    public void TheXyFigureRefusesAnEnvelope()
    {
        // Pairing the upper outlines of two channels is not pairing two signals, and the figure it
        // would draw is a shape nothing was ever at.
        var record = Envelope([-1.0, -1.0], [1.0, 1.0], [-2.0, -2.0], [2.0, 2.0]);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Null(ScopeFrameBuilder.XyFigure(record, slice));
    }

    [Fact]
    public void TheEyeAndTheResidualRefuseAnEnvelope()
    {
        // Both need a measured frequency, which an envelope has none of -- but the refusal is
        // stated on the outlines rather than left to the missing figure.
        var low = new double[480];
        var high = new double[480];
        for (int i = 0; i < 480; i++)
        {
            low[i] = -Math.Sin(2.0 * Math.PI * i / 48.0);
            high[i] = Math.Sin(2.0 * Math.PI * i / 48.0);
        }

        var record = Envelope(low, high);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Null(ScopeFrameBuilder.EyeFigure(record, channel: 0, slice));
        Assert.Null(ScopeFrameBuilder.Residual(record, channel: 0, slice));
    }

    [Fact]
    public void AGatedReadingOfAnEnvelope_IsStillTheBandsExtremes()
    {
        // The gate is a window of the band, and the honest figures over it are the same three.
        var record = Envelope([0.0, -5.0, 0.0, 0.0], [0.0, 5.0, 0.0, 0.0]);

        var gated = ScopeFrameBuilder.Measure(record, channel: 0, fromS: 0.0, toS: 2.5 / SampleRateHz);

        Assert.NotNull(gated);
        Assert.Equal(5.0, gated!.MaxV, 9);
        Assert.Equal(-5.0, gated.MinV, 9);
        Assert.Null(gated.PeriodS);
    }

    [Fact]
    public void AnOscilloscopeRequest_RefusesAnAccumulationTheModeCannotProduce()
    {
        // Single holds one record, so there is never a second to combine with; Roll has no record
        // boundary at all. Refused by name rather than silently ignored.
        var single = Request(ScopeRunMode.Single, RecordAccumulation.Average);
        var roll = Request(ScopeRunMode.Roll, RecordAccumulation.Envelope);
        var auto = Request(ScopeRunMode.Auto, RecordAccumulation.Average);

        Assert.Throws<ArgumentException>(() => single.Validate());
        Assert.Throws<ArgumentException>(() => roll.Validate());
        auto.Validate();
    }

    [Fact]
    public void AnOscilloscopeRequest_RefusesACountOutsideTheAccumulatorsBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Request(ScopeRunMode.Auto, RecordAccumulation.Average, count: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Request(ScopeRunMode.Auto, RecordAccumulation.Average, count: RecordAccumulator.MaxCount + 1).Validate());

        // And it is not checked at all under Normal, where nothing reads it.
        Request(ScopeRunMode.Auto, RecordAccumulation.Normal, count: 99_999).Validate();
    }

    private static OscilloscopeRequest Request(
        ScopeRunMode mode, RecordAccumulation accumulation, int count = 16) =>
        new(
            SampleRateHz: SampleRateHz,
            RecordSeconds: 0.02,
            PreTriggerFraction: 0.25,
            Mode: mode,
            TriggerKind: TriggerKind.Edge,
            TriggerSource: TriggerSource.ChannelA,
            TriggerSlope: TriggerSlope.Rising,
            TriggerLevelV: 0.0,
            Accumulation: accumulation,
            AverageCount: count);
}
