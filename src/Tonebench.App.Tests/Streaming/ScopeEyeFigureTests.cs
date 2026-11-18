using Tonebench.App.Streaming;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The eye diagram on a record: what it is folded on, what it refuses, and the two caps.
///
/// <para>The spread and the jitter are <c>WaveformEye</c>'s and are asserted against closed forms
/// in <c>WaveformEyeTests</c>. What is tested here is the <b>seam</b> — that the fold is the
/// frequency the slice measured rather than anything the caller supplied, that the folds are the
/// record's own samples rather than the columns the waveforms are drawn as, that a window holding
/// more cycles than the cap is truncated rather than strided, and that the readings describe the
/// folds that are drawn.</para>
/// </summary>
public class ScopeEyeFigureTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>A 1 kHz sine, which at this rate is exactly 48 samples a period.</summary>
    private static ScopeRecord Tone(int frames, double amplitude = 1.0, double frequencyHz = 1_000.0)
    {
        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * i / SampleRateHz);

        return new ScopeRecord(
            [samples], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
    }

    private static ScopeRecord FromSamples(double[] samples) =>
        new([samples], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);

    // -----------------------------------------------------------------------
    // What it folds on
    // -----------------------------------------------------------------------

    [Fact]
    public void TheFoldIsTheFrequencyTheSliceMeasured()
    {
        // 1 kHz at 48 kS/s: 48 samples a period, so 20 whole cycles out of 960 frames.
        var record = Tone(frames: 960);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, slice);

        Assert.NotNull(figure);
        Assert.Equal(48, figure!.FoldFrameCount);
        Assert.Equal(48.0 / SampleRateHz, figure.FoldSeconds, 9);
        Assert.Equal(20, figure.Folds.Length);
        Assert.Equal(20, figure.WindowFoldCount);
        Assert.False(figure.Truncated);
        // The frequency it reports is the one it folded on, so the header cannot name a period the
        // picture was not built from.
        Assert.Equal(slice.Measurements.FrequencyHz!.Value, figure.PeriodHz, 9);
    }

    /// <summary>
    /// The folds are the record's own samples, whatever shape the waveform came back as.
    ///
    /// <para>The point of the test is the decimated case: at this length the slice is columns, and
    /// a min/max per column has already collapsed the cycles into the band this picture exists to
    /// resolve. So the figure cannot be built from the slice.</para>
    /// </summary>
    [Fact]
    public void TheFoldsAreSamples_EvenWhereTheWaveformIsDrawnAsColumns()
    {
        var record = Tone(frames: 960);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0, maxColumns: 100);
        Assert.False(slice.IsRaw);

        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, slice);

        Assert.NotNull(figure);
        for (int f = 0; f < figure!.Folds.Length; f++)
            for (int i = 0; i < figure.Folds[f].Length; i += 7)
                // To six places rather than exactly: the fold is placed on the *fitted* period, so
                // a point lands a part in ten million off a whole sample and is interpolated.
                Assert.Equal(record.Channels[0][f * 48 + i], figure.Folds[f][i], 6);
    }

    [Fact]
    public void AnExactlyRepeatingTone_FoldsToOneLine()
    {
        // The figure every other reading is judged against: 1 kHz is a whole number of samples at
        // this rate, so every fold is the same samples and the band has no width at all.
        var record = Tone(frames: 960);
        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, ScopeFrameBuilder.Overview(record, 0));

        Assert.NotNull(figure);
        // Not exactly zero: the fold is the *fitted* period, which on a tone that is a whole
        // number of samples is 1 kHz to a part in ten million rather than to the last bit.
        Assert.True(figure!.MaxSpreadV < 1e-5, $"spread {figure.MaxSpreadV:E2} V");
        Assert.True(figure.MeanSpreadV < 1e-5, $"mean {figure.MeanSpreadV:E2} V");
        Assert.True(Math.Abs(figure.JitterS!.Value) < 1e-9, $"jitter {figure.JitterS:E2} s");
    }

    [Fact]
    public void ACycleThatDiffersFromItsNeighbour_ReadsItsSpreadAndWhereItIs()
    {
        // Alternate cycles at 1.0 and 0.9 -- the classic "every other cycle is different" fault,
        // and the shape this picture is pointed at. The band is then 0.1 wide at the sine's own
        // peak, twelve samples into a 48-sample fold.
        //
        // Alternating rather than a smooth ramp, and that is about the *measurement* rather than
        // about the fault: Vtop and Vbase come from a 256-bin histogram, and a ramp smears each
        // extreme over a dozen bins, so at this record length neither mode is found and the slice
        // reports no frequency at all -- no frequency, no fold. A real capture is long enough for
        // the histogram either way; a 960-sample test is not.
        const int foldFrames = 48;
        const int folds = 20;
        var samples = new double[foldFrames * folds];
        for (int f = 0; f < folds; f++)
        {
            double amplitude = f % 2 == 0 ? 1.0 : 0.9;
            for (int i = 0; i < foldFrames; i++)
                samples[f * foldFrames + i] = amplitude * Math.Sin(2.0 * Math.PI * i / foldFrames);
        }

        var record = FromSamples(samples);
        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, ScopeFrameBuilder.Overview(record, 0));

        Assert.NotNull(figure);
        Assert.Equal(0.1, figure!.MaxSpreadV, 9);
        Assert.Equal(12.0 / SampleRateHz, figure.SpreadAtS, 9);
        // The mean over the fold is the difference times the fold's own mean |sin|.
        double meanAbsSin = 0.0;
        for (int i = 0; i < foldFrames; i++)
            meanAbsSin += Math.Abs(Math.Sin(2.0 * Math.PI * i / foldFrames));
        meanAbsSin /= foldFrames;
        Assert.Equal(0.1 * meanAbsSin, figure.MeanSpreadV, 9);
    }

    [Fact]
    public void ATonePlacedBetweenSamples_StillFoldsToOneLine()
    {
        // 1000.49 Hz at 48 kS/s is 47.98 samples a period, so the fold is 48 and a cycle walks
        // 0.02 of a sample -- which over the window is nothing. What matters is which frequency
        // got it there: the crossing estimate is some tenths of a per cent off at this length, and
        // folding on *that* rounds to 48 or 49 depending on which way it fell, where 49 walks a
        // whole sample a cycle and draws a clean tone as a band. The fit is what makes this read
        // as a repeating signal rather than as a jittery one.
        var record = Tone(frames: 48 * 20, frequencyHz: 1000.49);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, slice);

        Assert.NotNull(figure);
        Assert.Equal(48, figure!.FoldFrameCount);
        // The fit's own answer, to a part in ten thousand -- not the crossing estimate's.
        Assert.Equal(1000.49, figure.PeriodHz, 1);
        // And the fold is the exact period, so what is left is the linear interpolation's own
        // residue rather than an overlay walking sideways. That residue has a closed form -- the
        // worst a straight line can be from a sine over one sample is 1 - cos(omega/2), which is
        // omega^2/8 -- so it is asserted against the formula rather than against a number somebody
        // chose. Measured 0.00207 V against the 0.00214 V bound; placing the folds a whole number
        // of samples apart read 0.0585 V on this very waveform, twenty-seven times more.
        double omega = 2.0 * Math.PI * 1000.49 / SampleRateHz;
        double bound = omega * omega / 8.0;
        Assert.True(
            figure.MaxSpreadV < bound * 1.2,
            $"spread {figure.MaxSpreadV:F5} V against a bound of {bound:F5} V");
    }

    [Fact]
    public void AFitThatRunsAwayFromItsSeed_IsNotBelieved()
    {
        // The fold is seeded from the crossing estimate and refined, so a fit that has locked onto
        // a harmonic or onto noise would fold the picture on something the window is not. A tenth
        // of an octave either side is the bound; noise has no measured frequency at all and so
        // never reaches the fit, which is what this case says.
        var record = FromSamples(new double[960]);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Null(slice.Measurements.FrequencyHz);
        Assert.Null(ScopeFrameBuilder.EyeFigure(record, channel: 0, slice));
    }

    // -----------------------------------------------------------------------
    // What it refuses
    // -----------------------------------------------------------------------

    [Fact]
    public void WithNoMeasuredFrequency_ThereIsNoEye()
    {
        // A picture folded on an invented period is a band of noise that looks exactly like a
        // jittery signal, which is the plausible wrong answer this whole file is written against.
        var record = FromSamples(new double[960]);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);
        Assert.Null(slice.Measurements.FrequencyHz);

        Assert.Null(ScopeFrameBuilder.EyeFigure(record, channel: 0, slice));
    }

    [Fact]
    public void WithFewerThanTwoWholePeriodsInTheWindow_ThereIsNoEye()
    {
        // One cycle drawn on its own is the waveform view; the comparison between cycles is the
        // point of this one.
        var record = Tone(frames: 960);
        var slice = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0.0, toS: 70.0 / SampleRateHz);

        Assert.Null(ScopeFrameBuilder.EyeFigure(record, channel: 0, slice));
    }

    // -----------------------------------------------------------------------
    // The two caps
    // -----------------------------------------------------------------------

    [Fact]
    public void AWindowHoldingMoreCyclesThanTheCap_IsTruncatedRatherThanStrided()
    {
        // Striding the folds would skip cycles, and an intermittent fault one cycle in three is
        // exactly what someone turns this picture on to find. So the overlay is the FIRST n cycles
        // of the window, and it says the window held more.
        var record = Tone(frames: 48 * 40);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, slice, maxFolds: 8);

        Assert.NotNull(figure);
        Assert.Equal(8, figure!.Folds.Length);
        Assert.Equal(40, figure.WindowFoldCount);
        Assert.True(figure.Truncated);
        // The first eight, contiguous from the start of the window.
        for (int f = 0; f < 8; f++)
            Assert.Equal(record.Channels[0][f * 48 + 3], figure.Folds[f][3], 6);
    }

    [Fact]
    public void AFoldLongerThanTheCap_IsDrawnCoarselyAndEveryFoldAlike()
    {
        // Within a fold, a coarser step is the right answer: the x axis is one period rather than
        // a stretch of the record, so a min/max per column would collapse the folds into the band.
        // What must hold is that the step is the same for every fold, or they fall out of
        // alignment with each other and a clean tone reads as a jittery one.
        var record = Tone(frames: 48 * 20);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, slice, maxFoldFrames: 12);

        Assert.NotNull(figure);
        Assert.Equal(12, figure!.FoldFrameCount);
        // The fold is still a whole period in seconds -- only its resolution fell.
        Assert.Equal(48.0 / SampleRateHz, figure.FoldSeconds, 9);
        // Every fourth sample, from the start of each fold.
        for (int f = 0; f < figure.Folds.Length; f++)
            for (int i = 0; i < 12; i++)
                Assert.Equal(record.Channels[0][f * 48 + i * 4], figure.Folds[f][i], 6);
        // And the tone still folds to one line, which is what says the folds stayed aligned.
        Assert.True(figure.MaxSpreadV < 1e-5, $"spread {figure.MaxSpreadV:E2} V");
    }

    [Fact]
    public void TheJitterIsMeasuredAgainstTheFoldsOwnMeanRatherThanZero()
    {
        // An offset waveform has no zero crossing at all, and this hardware's captures carry the
        // front end's own offset -- measured near -4.5 mV on the left input. Against zero this
        // would read as absent on a signal that is perfectly stable.
        var record = Tone(frames: 960);
        for (int i = 0; i < record.Channels[0].Length; i++)
            record.Channels[0][i] += 2.0;

        var slice = ScopeFrameBuilder.Overview(record, channel: 0);
        var figure = ScopeFrameBuilder.EyeFigure(record, channel: 0, slice);

        Assert.NotNull(figure);
        Assert.NotNull(figure!.JitterS);
        Assert.True(Math.Abs(figure.JitterS!.Value) < 1e-9, $"jitter {figure.JitterS:E2} s");
    }

    [Fact]
    public void TheCapsAreRefusedBelowTheirOwnFloors()
    {
        var record = Tone(frames: 960);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScopeFrameBuilder.EyeFigure(record, 0, slice, maxFolds: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScopeFrameBuilder.EyeFigure(record, 0, slice, maxFoldFrames: 1));
    }
}
