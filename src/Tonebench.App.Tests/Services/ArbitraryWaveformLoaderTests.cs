using Tonebench.App.Services;
using Tonebench.Device;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The loader: one channel of a file, resampled to the analyzer's rate and normalised.
///
/// <para>Every expectation here is against the waveform the file was <b>built</b> from, not against
/// a previous run — a sine resampled 4:1 is still that sine, and that is a claim arithmetic can
/// check. The one that matters most is the loop joint: a stimulus that repeats has no ends, and a
/// resampler that thinks it does puts a click there once per period.</para>
/// </summary>
public class ArbitraryWaveformLoaderTests
{
    private static WaveFileContents Mono(double[] samples, int sampleRateHz) =>
        new([.. samples.Select(v => (float)v)], sampleRateHz, 1, 32, IsFloat: true);

    private static WaveFileContents Stereo(double[] left, double[] right, int sampleRateHz)
    {
        var interleaved = new float[left.Length * 2];
        for (int i = 0; i < left.Length; i++)
        {
            interleaved[i * 2] = (float)left[i];
            interleaved[(i * 2) + 1] = (float)right[i];
        }

        return new WaveFileContents(interleaved, sampleRateHz, 2, 32, IsFloat: true);
    }

    /// <summary>One whole cycle of a sine over <paramref name="frames"/> samples.</summary>
    private static double[] Cycle(int frames, double amplitude = 1.0, int cycles = 1) =>
        [.. Enumerable.Range(0, frames).Select(i => amplitude * Math.Sin(2.0 * Math.PI * cycles * i / frames))];

    /// <summary>
    /// At the analyzer's own rate nothing is resampled, so the table is the file — normalised to
    /// unit rms, which is what makes the level mean the same thing it means for every other kind.
    /// </summary>
    [Fact]
    public void AtTheSameRate_TheTableIsTheFileNormalisedToUnitRms()
    {
        var file = Mono(Cycle(64, amplitude: 0.25), 48_000);

        var table = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, 48_000);

        Assert.Equal(64, table.Frames);
        Assert.Equal(48_000, table.SampleRateHz);

        double rms = Math.Sqrt(table.Samples.Sum(v => (double)v * v) / table.Frames);
        Assert.Equal(1.0, rms, precision: 6);

        // And the shape is untouched: a quarter-amplitude sine normalised is the same sine.
        for (int i = 0; i < table.Frames; i++)
            Assert.Equal(Math.Sqrt(2.0) * Math.Sin(2.0 * Math.PI * i / 64), table.Samples[i], tolerance: 1e-5);
    }

    /// <summary>
    /// The crest factor is <b>measured</b>, not allowed for — the one stimulus here where it can
    /// be, since the samples are all in hand. A sine is 3.01 dB and a square is 0, and the output
    /// range reads this rather than a guess.
    /// </summary>
    [Fact]
    public void TheCrestFactorIsTheFilesOwn()
    {
        var sine = ArbitraryWaveformLoader.Build(Mono(Cycle(256), 48_000), ArbitraryChannel.Left, 48_000);
        Assert.Equal(Math.Sqrt(2.0), sine.PeakOfUnitRms, precision: 3);

        double[] square = [.. Enumerable.Range(0, 64).Select(i => i < 32 ? 1.0 : -1.0)];
        var flat = ArbitraryWaveformLoader.Build(Mono(square, 48_000), ArbitraryChannel.Left, 48_000);
        Assert.Equal(1.0, flat.PeakOfUnitRms, precision: 9);
    }

    /// <summary>
    /// Resampled 4:1 upward, a sine is still that sine — and the loop is a whole number of frames,
    /// which is what makes the stimulus periodic in the sampled domain at all.
    /// </summary>
    [Fact]
    public void ResampledUpward_ASineIsStillThatSine()
    {
        var file = Mono(Cycle(48), 48_000);

        var table = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, 192_000);

        Assert.Equal(192, table.Frames);
        Assert.Equal(48, table.SourceFrames);
        Assert.Equal(48_000, table.SourceSampleRateHz);

        // A tolerance rather than a decimal count: xUnit's `precision` rounds both sides, so a value
        // that lands either side of a rounding boundary fails on an error of 1e-4. The resampler's
        // own passband ripple is a few parts in 10,000 and that is what is being allowed for.
        for (int i = 0; i < table.Frames; i++)
            Assert.Equal(Math.Sqrt(2.0) * Math.Sin(2.0 * Math.PI * i / 192), table.Samples[i], tolerance: 5e-4);
    }

    /// <summary>
    /// <b>A loop has no ends, and the resampler has to be told so.</b>
    ///
    /// <para><c>Resampler.Decimate</c>'s kernel reaches hundreds of samples either side and at a
    /// buffer's edge renormalises by the weights it has — right for a record, wrong for a loop,
    /// where the samples before the first really are the ones after the last. Left alone it puts an
    /// artefact at the joint once per period: a click that is ours rather than the file's.</para>
    ///
    /// <para>Stated as a comparison rather than as a tolerance, because a tolerance loose enough
    /// for the un-wrapped version to pass would hide it: the error in the first and last few
    /// samples must be no worse than the error in the middle.</para>
    /// </summary>
    [Fact]
    public void TheLoopJoins_SoTheEndsAreNoWorseThanTheMiddle()
    {
        var table = ArbitraryWaveformLoader.Build(Mono(Cycle(64, cycles: 3), 48_000), ArbitraryChannel.Left, 192_000);

        double Error(int i) =>
            Math.Abs(table.Samples[i] - Math.Sqrt(2.0) * Math.Sin(2.0 * Math.PI * 3 * i / table.Frames));

        double worstEnd = Enumerable.Range(0, 8)
            .Concat(Enumerable.Range(table.Frames - 8, 8))
            .Max(Error);
        double worstMiddle = Enumerable.Range(table.Frames / 4, table.Frames / 2).Max(Error);

        Assert.True(
            worstEnd <= Math.Max(worstMiddle * 2.0, 1e-3),
            $"the joint is worse than the middle: {worstEnd:0.######} against {worstMiddle:0.######}");
    }

    /// <summary>
    /// Downward too, which is the direction that needs the anti-alias filter: a 192 kHz file played
    /// at 48 keeps what fits and the loop is a quarter the length.
    /// </summary>
    [Fact]
    public void ResampledDownward_TheLoopIsTheRightLength()
    {
        var table = ArbitraryWaveformLoader.Build(Mono(Cycle(256, cycles: 2), 192_000), ArbitraryChannel.Left, 48_000);

        Assert.Equal(64, table.Frames);
        for (int i = 0; i < table.Frames; i++)
            Assert.Equal(Math.Sqrt(2.0) * Math.Sin(2.0 * Math.PI * 2 * i / 64), table.Samples[i], tolerance: 5e-3);
    }

    /// <summary>
    /// A rate that does not divide is rounded to a whole frame, like <see cref="PeriodicWave"/>'s
    /// period and for the same reason — and the rounding is at most half a source sample.
    /// </summary>
    [Fact]
    public void AnAwkwardRatioStillGivesAWholeNumberOfFrames()
    {
        // 44.1 kHz to 48: 1000 frames becomes 1088.43..., i.e. 1088.
        var table = ArbitraryWaveformLoader.Build(Mono(Cycle(1000), 44_100), ArbitraryChannel.Left, 48_000);

        Assert.Equal(1088, table.Frames);
        Assert.Equal(48_000.0 / 1088, table.RepeatHz, precision: 6);
        Assert.Equal(1088 / 48_000.0, table.DurationSeconds, precision: 9);
    }

    /// <summary>
    /// One channel rather than a mixdown by default, because summing is the choice that can be
    /// silently wrong: anti-phase material cancels, and a file that is mostly difference comes out
    /// empty with nothing to say why. Here the two channels are exact opposites, so a mix is
    /// silence — which the loader refuses rather than plays.
    /// </summary>
    [Fact]
    public void TakesAChannelRatherThanMixing_AndAMixOfOppositesIsRefused()
    {
        var cycle = Cycle(64);
        var file = Stereo(cycle, [.. cycle.Select(v => -v)], 48_000);

        var left = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, 48_000);
        var right = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Right, 48_000);

        for (int i = 0; i < 64; i++)
            Assert.Equal(-left.Samples[i], right.Samples[i], precision: 5);

        var ex = Assert.Throws<WaveFileException>(
            () => ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Mix, 48_000));
        Assert.Contains("silence", ex.Message);
    }

    /// <summary>And a mix really is the mean, where that is what was asked for.</summary>
    [Fact]
    public void MixesWhenItIsAskedTo()
    {
        var file = Stereo(Cycle(64), Cycle(64, amplitude: 3.0), 48_000);

        var mix = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Mix, 48_000);
        var left = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, 48_000);

        // Both normalise to unit rms, so the mix of a 1 and a 3 is the same shape as either.
        for (int i = 0; i < 64; i++)
            Assert.Equal(left.Samples[i], mix.Samples[i], precision: 5);
    }

    /// <summary>A stereo setting left behind on a mono file plays the one channel there is.</summary>
    [Fact]
    public void AStereoChoiceOnAMonoFile_PlaysTheChannelThereIs()
    {
        var file = Mono(Cycle(64), 48_000);

        var right = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Right, 48_000);
        var left = ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, 48_000);

        Assert.Equal(left.Samples, right.Samples);
    }

    [Fact]
    public void RefusesDigitalSilenceRatherThanDividingByIt()
    {
        var ex = Assert.Throws<WaveFileException>(
            () => ArbitraryWaveformLoader.Build(Mono(new double[64], 48_000), ArbitraryChannel.Left, 48_000));

        Assert.Contains("silence", ex.Message);
    }

    /// <summary>
    /// A file too long to loop is refused by name and with the limit in seconds, not truncated: a
    /// stimulus silently cut to a fraction of itself is exactly the plausible wrong answer this
    /// whole path is written against.
    /// </summary>
    [Fact]
    public void RefusesALoopLongerThanItWillBuild()
    {
        // Short at its own rate, far too long at the analyzer's.
        var file = Mono(Cycle(64), 1);

        var ex = Assert.Throws<WaveFileException>(
            () => ArbitraryWaveformLoader.Build(file, ArbitraryChannel.Left, 192_000));

        Assert.Contains("the limit is", ex.Message);
    }

    /// <summary>
    /// The peak the output range is given is the level's rms times the file's own crest — so a
    /// 10 dB-crest file at 0 dBV asks for three times the headroom a square does, which is the
    /// whole reason this one is measured.
    /// </summary>
    [Fact]
    public void TheRangeSelectorIsGivenTheFilesOwnCrest()
    {
        var table = ArbitraryWaveformLoader.Build(Mono(Cycle(256), 48_000), ArbitraryChannel.Left, 48_000);
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.Arbitrary,
            Arbitrary = new ArbitrarySettings { Path = "x.wav", LevelDbv = 0.0, Table = table },
        };

        Assert.Equal(Math.Sqrt(2.0), OutputRangeSelector.RequiredPeakVolts(channel), precision: 3);

        // And nothing loaded asks for nothing, so a never-configured channel takes the quietest
        // range rather than the loudest.
        Assert.Equal(0.0, OutputRangeSelector.RequiredPeakVolts(channel with
        {
            Arbitrary = new ArbitrarySettings { Path = "x.wav" },
        }));
    }

    /// <summary>
    /// The synthesis itself: the table on a loop, scaled by the level. A modulo and a multiply, and
    /// the only thing worth pinning is that the loop lands on the same frame however long the
    /// stream has been running.
    /// </summary>
    [Fact]
    public void ThePlayedSamplesAreTheTableOnALoop()
    {
        var table = ArbitraryWaveformLoader.Build(Mono(Cycle(64), 48_000), ArbitraryChannel.Left, 48_000);
        var settings = new ArbitrarySettings { Path = "x.wav", LevelDbv = -6.0, Table = table };

        double scale = Math.Pow(10.0, -6.0 / 20.0);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(table.Samples[i % 64] * scale, ArbitraryWave.Sample(settings, i), precision: 9);

            // Exactly one loop later, to the bit.
            Assert.Equal(ArbitraryWave.Sample(settings, i), ArbitraryWave.Sample(settings, i + 64), precision: 15);
        }

        Assert.Equal(0.0, ArbitraryWave.Sample(new ArbitrarySettings { Path = "x.wav" }, 7));
        Assert.Equal(64, ArbitraryWave.PeriodFrames(settings));
    }
}
