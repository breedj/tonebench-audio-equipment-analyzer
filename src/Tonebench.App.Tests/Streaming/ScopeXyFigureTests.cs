using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Endpoints;
using Tonebench.App.Streaming;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The X–Y figure on a record: the pairs, the cap, and the two places it is deliberately absent.
///
/// <para>The three readings are <c>ChannelCorrelation</c>'s and are tested against closed forms in
/// <c>ChannelCorrelationTests</c>. What is tested here is the seam — that the figure is built from
/// the record's own <b>samples</b> rather than from the columns the waveforms are drawn as, that a
/// window too long for the wire is truncated rather than strided, and that the readings describe
/// the stretch that is drawn.</para>
/// </summary>
public class ScopeXyFigureTests
{
    private const double SampleRateHz = 48_000.0;

    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            return options.SerializerOptions;
        }
    }

    /// <summary>A pair of 1 kHz sines where the right input lags the left by <paramref name="phaseDeg"/>.</summary>
    private static ScopeRecord Pair(double phaseDeg, int frames = 4_800, double amplitudeB = 1.0)
    {
        var left = new double[frames];
        var right = new double[frames];
        double phase = phaseDeg * Math.PI / 180.0;
        for (int i = 0; i < frames; i++)
        {
            double t = 2.0 * Math.PI * 1_000.0 * i / SampleRateHz;
            left[i] = Math.Cos(t);
            right[i] = amplitudeB * Math.Cos(t - phase);
        }

        return new ScopeRecord(
            [left, right], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
    }

    // -----------------------------------------------------------------------
    // The pairs
    // -----------------------------------------------------------------------

    /// <summary>
    /// The figure is the record's own samples, in pairs, whatever shape the waveforms came back as.
    ///
    /// <para>The point of the test is the decimated case: at this length the slice is columns, and
    /// a min/max per column throws away which value of A went with which value of B — which is the
    /// whole of what the picture is. So the figure cannot be built from the slice.</para>
    /// </summary>
    [Fact]
    public void TheFigureIsPairsOfSamples_EvenWhereTheWaveformsAreDrawnAsColumns()
    {
        var record = Pair(0.0, frames: 4_800);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0, maxColumns: 200);
        Assert.False(slice.IsRaw);

        var figure = ScopeFrameBuilder.XyFigure(record, slice);

        Assert.NotNull(figure);
        Assert.Equal(4_800, figure!.FrameCount);
        Assert.Equal(figure.X.Length, figure.Y.Length);
        for (int i = 0; i < figure.X.Length; i += 97)
        {
            Assert.Equal((float)record.Channels[0][i], figure.X[i]);
            Assert.Equal((float)record.Channels[1][i], figure.Y[i]);
        }
    }

    [Fact]
    public void TheFigureFollowsTheWindow_NotTheWholeRecord()
    {
        var record = Pair(0.0, frames: 4_800);
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0.01, toS: 0.02);

        var figure = ScopeFrameBuilder.XyFigure(record, slice);

        Assert.NotNull(figure);
        Assert.Equal(slice.FirstFrame, figure!.FirstFrame);
        Assert.Equal(slice.FrameCount, figure.FrameCount);
        Assert.Equal((float)record.Channels[0][slice.FirstFrame], figure.X[0]);
    }

    // -----------------------------------------------------------------------
    // The cap
    // -----------------------------------------------------------------------

    /// <summary>
    /// A window longer than the cap is <b>truncated</b>, and the first pairs are what is kept.
    ///
    /// <para>Striding is the obvious alternative and it is the one that produces a plausible wrong
    /// picture: a stride near a whole number of periods collapses an ellipse onto a line, and a
    /// reader concludes the channels are in phase. The case below is exactly that — 1 kHz at
    /// 48 kS/s is 48 samples a period, so a stride of 48 would put every pair at the same point on
    /// the locus. A contiguous run is always a true figure of a real stretch.</para>
    /// </summary>
    [Fact]
    public void AWindowLongerThanTheCap_IsTruncatedRatherThanStrided()
    {
        var record = Pair(90.0, frames: 48_000);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var figure = ScopeFrameBuilder.XyFigure(record, slice, maxPoints: 480);

        Assert.NotNull(figure);
        Assert.True(figure!.Truncated);
        Assert.Equal(480, figure.FrameCount);
        Assert.Equal(48_000, figure.WindowFrameCount);

        // Contiguous from the start of the window: the pairs are the record's own, in order.
        for (int i = 0; i < figure.FrameCount; i++)
            Assert.Equal((float)record.Channels[0][i], figure.X[i]);

        // And the figure is still an ellipse: ten whole periods of a quarter-period-apart pair,
        // which a stride of one period would have collapsed onto a single point.
        Assert.Equal(0.0, figure.Correlation, 6);
    }

    [Fact]
    public void AWindowInsideTheCap_IsNotTruncated()
    {
        var record = Pair(0.0, frames: 4_800);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var figure = ScopeFrameBuilder.XyFigure(record, slice);

        Assert.NotNull(figure);
        Assert.False(figure!.Truncated);
        Assert.Equal(figure.FrameCount, figure.WindowFrameCount);
    }

    /// <summary>
    /// The readings are of the stretch drawn, not of the window — so the figures under the picture
    /// are figures of the picture.
    /// </summary>
    [Fact]
    public void TheReadingsAreOfTheStretchDrawn()
    {
        // In phase for the first 480 frames, antiphase after: one window, two different figures.
        var left = new double[4_800];
        var right = new double[4_800];
        for (int i = 0; i < left.Length; i++)
        {
            double t = 2.0 * Math.PI * 1_000.0 * i / SampleRateHz;
            left[i] = Math.Cos(t);
            right[i] = i < 480 ? Math.Cos(t) : -Math.Cos(t);
        }

        var record = new ScopeRecord(
            [left, right], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var drawn = ScopeFrameBuilder.XyFigure(record, slice, maxPoints: 480);
        var whole = ScopeFrameBuilder.XyFigure(record, slice);

        Assert.Equal(1.0, drawn!.Correlation, 6);
        Assert.True(whole!.Correlation < -0.5, $"the whole window is mostly antiphase, not {whole.Correlation}");
    }

    // -----------------------------------------------------------------------
    // The readings
    // -----------------------------------------------------------------------

    [Fact]
    public void AMatchedPairInPhase_ReadsOneAndZeroAndOne()
    {
        var figure = ScopeFrameBuilder.XyFigure(Pair(0.0), ScopeFrameBuilder.Overview(Pair(0.0), 0));

        Assert.NotNull(figure);
        Assert.Equal(1.0, figure!.Correlation, 6);
        Assert.Equal(0.0, figure.PhaseDeg!.Value, 3);
        Assert.Equal(1.0, figure.AmplitudeRatio!.Value, 6);
    }

    [Fact]
    public void AQuarterPeriodApart_ReadsNoCorrelationAndNinetyDegrees()
    {
        var record = Pair(90.0, amplitudeB: 0.5);
        var figure = ScopeFrameBuilder.XyFigure(record, ScopeFrameBuilder.Overview(record, 0));

        Assert.NotNull(figure);
        Assert.Equal(0.0, figure!.Correlation, 6);
        Assert.Equal(90.0, figure.PhaseDeg!.Value, 3);
        Assert.Equal(0.5, figure.AmplitudeRatio!.Value, 6);
    }

    /// <summary>
    /// Noise on both channels has a correlation and no phase — which is the honest answer, and the
    /// reason the correlation is the figure that is always shown.
    /// </summary>
    [Fact]
    public void NoiseHasACorrelationAndNoPhase()
    {
        var random = new Random(4_039);
        var left = new double[4_096];
        var right = new double[4_096];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = random.NextDouble() - 0.5;
            right[i] = random.NextDouble() - 0.5;
        }

        var record = new ScopeRecord(
            [left, right], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
        var figure = ScopeFrameBuilder.XyFigure(record, ScopeFrameBuilder.Overview(record, 0));

        Assert.NotNull(figure);
        Assert.True(Math.Abs(figure!.Correlation) < 0.1, $"two unrelated noises, not {figure.Correlation}");
        Assert.True(double.IsFinite(figure.Correlation));
    }

    // -----------------------------------------------------------------------
    // Absent rather than wrong
    // -----------------------------------------------------------------------

    /// <summary>
    /// One channel is no figure at all. Plotting it against an array of zeros would draw a
    /// horizontal line, which is what a perfectly silent right input looks like — and that is a
    /// measurement a reader would act on.
    /// </summary>
    [Fact]
    public void ARecordWithOneChannel_HasNoFigure()
    {
        var mono = new ScopeRecord(
            [new double[4_096]], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);

        Assert.Null(ScopeFrameBuilder.XyFigure(mono, ScopeFrameBuilder.Overview(mono, 0)));
    }

    [Fact]
    public void AnEmptyWindow_HasNoFigure()
    {
        var record = Pair(0.0);
        var empty = ScopeFrameBuilder.Overview(record, 0) with { FrameCount = 0 };

        Assert.Null(ScopeFrameBuilder.XyFigure(record, empty));
    }

    /// <summary>Nothing it can produce truncates a response — the rule the whole scope's wire is built around.</summary>
    [Fact]
    public void TheSliceSerializes_IncludingWithEveryReadingRefused()
    {
        var silent = new ScopeRecord(
            [new double[2_048], new double[2_048]],
            SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
        var figure = ScopeFrameBuilder.XyFigure(silent, ScopeFrameBuilder.Overview(silent, 0));

        Assert.NotNull(figure);
        Assert.Null(figure!.PhaseDeg);

        var wire = new ScopeXySlice(
            figure.X,
            figure.Y,
            figure.FrameCount,
            figure.WindowFrameCount,
            figure.FrameCount / silent.SampleRateHz,
            figure.Correlation,
            figure.PhaseDeg,
            figure.AmplitudeRatio);

        string json = JsonSerializer.Serialize(wire, SerializerOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("phaseDeg").ValueKind);
        Assert.Equal(2_048, document.RootElement.GetProperty("frameCount").GetInt32());
    }
}
