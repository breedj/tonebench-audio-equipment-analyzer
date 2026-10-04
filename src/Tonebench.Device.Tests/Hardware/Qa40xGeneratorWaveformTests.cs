using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>TX per-sample synthesis — see Qa40xGeneratorWaveform's type remarks for why this is standalone from Simulation.SignalSynthesizer.</summary>
public class Qa40xGeneratorWaveformTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>The frame that lands on <paramref name="t"/> seconds, since ComputeSample is indexed by frame.</summary>
    private static long Frame(double t) => (long)Math.Round(t * SampleRateHz);

    [Fact]
    public void ComputeSample_OffKind_IsSilent()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]) { Kind = GeneratorKind.Off };

        Assert.Equal(0.0, waveform.ComputeSample(channel, Frame(0.1234)));
    }

    [Fact]
    public void ComputeSample_Disabled_IsSilentRegardlessOfKind()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = new ChannelGeneratorConfig(false, [new GeneratorTone(1000.0, 0.0)]);

        Assert.Equal(0.0, waveform.ComputeSample(channel, Frame(0.1234)));
    }

    [Fact]
    public void ComputeSample_Tones_IsABareCosineAtPeakVolts_NoAddedHarmonics()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 0.0)]);
        const long frame = 8;
        const double t = frame / SampleRateHz;

        double expected = Math.Sqrt(2.0) * Math.Cos(2.0 * Math.PI * 1000.0 * t);
        Assert.Equal(expected, waveform.ComputeSample(channel, frame), precision: 9);
    }

    [Fact]
    public void ComputeSample_Tones_SumsMultipleTones()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        GeneratorTone[] tones = [new(1000.0, 0.0), new(5000.0, -6.0)];
        var channel = new ChannelGeneratorConfig(true, tones);
        const long frame = 8;
        const double t = frame / SampleRateHz;

        double expected = Math.Sqrt(2.0) * Math.Cos(2.0 * Math.PI * 1000.0 * t)
            + Math.Sqrt(2.0) * Math.Pow(10.0, -6.0 / 20.0) * Math.Cos(2.0 * Math.PI * 5000.0 * t);
        Assert.Equal(expected, waveform.ComputeSample(channel, frame), precision: 9);
    }

    [Fact]
    public void ComputeSample_Multitone_SumsTheEffectiveToneListIdenticallyToTones()
    {
        // ChannelGeneratorConfig.Tones already holds the effective (derived) comb for Multitone —
        // both kinds reduce to the same "sum of configured tones" formula on real hardware.
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        GeneratorTone[] comb = [new(200.0, -10.0), new(2000.0, -14.0), new(9000.0, -18.0)];
        var channel = new ChannelGeneratorConfig(true, comb) { Kind = GeneratorKind.Multitone };
        const long frame = 149;
        const double t = frame / SampleRateHz;

        double expected = comb.Sum(tone => Math.Sqrt(2.0) * Math.Pow(10.0, tone.AmplitudeDbv / 20.0) * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * t));
        Assert.Equal(expected, waveform.ComputeSample(channel, frame), precision: 9);
    }

    [Fact]
    public void ComputeSample_WhiteNoise_HasTheConfiguredBroadbandRms()
    {
        const double levelDbv = -20.0;
        var waveform = new Qa40xGeneratorWaveform(new Random(7), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.WhiteNoise, Noise = new WhiteNoiseSettings { LevelDbv = levelDbv } };

        const int n = 20_000;
        double sumSquares = 0.0;
        for (int i = 0; i < n; i++)
        {
            double sample = waveform.ComputeSample(channel, i);
            sumSquares += sample * sample;
        }

        double rms = Math.Sqrt(sumSquares / n);
        double measuredDbv = 20.0 * Math.Log10(rms);
        Assert.True(Math.Abs(measuredDbv - levelDbv) < 1.0, $"Expected ~{levelDbv} dBV, measured {measuredDbv} dBV");
    }

    [Fact]
    public void ComputeSample_PinkNoise_HasTheSameConfiguredBroadbandRmsAsWhite()
    {
        // The whole point of normalising the shaper: switching kind changes the spectrum and not the
        // loudness, so OutputRangeSelector can go on deriving a range from the configured level.
        // A longer block than the white test's, because 1/f noise concentrates its power at the low
        // end and a short block is one draw of a slow random walk rather than an average.
        const double levelDbv = -20.0;
        var waveform = new Qa40xGeneratorWaveform(new Random(7), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.PinkNoise, Noise = new WhiteNoiseSettings { LevelDbv = levelDbv } };

        const int n = 200_000;
        double sumSquares = 0.0;
        for (int i = 0; i < n; i++)
        {
            double sample = waveform.ComputeSample(channel, i);
            sumSquares += sample * sample;
        }

        double measuredDbv = 20.0 * Math.Log10(Math.Sqrt(sumSquares / n));
        Assert.True(Math.Abs(measuredDbv - levelDbv) < 1.0, $"Expected ~{levelDbv} dBV, measured {measuredDbv} dBV");
    }

    [Fact]
    public void ComputeSample_PinkNoise_IsIdenticalOnBothChannels()
    {
        // A stimulus and the reference it is measured against cannot be independent draws. The
        // impedance measurement's whole premise is that both input channels see the same drive, and
        // the simulator models its reference channel as the right generator — so two channels
        // pulling successive samples from one RNG would leave it measuring the ratio of two
        // unrelated noises. See PinkNoiseShaper.WhiteAt.
        var waveform = new Qa40xGeneratorWaveform(new Random(5), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.PinkNoise, Noise = new WhiteNoiseSettings { LevelDbv = -20.0 } };

        for (long frame = 0; frame < 5_000; frame++)
        {
            double left = waveform.ComputeSample(channel, frame, channelIndex: 0);
            double right = waveform.ComputeSample(channel, frame, channelIndex: 1);
            Assert.Equal(left, right);
        }
    }

    [Fact]
    public void ComputeSample_PinkNoise_ShapesEachChannelOnItsOwnFilter()
    {
        // One shared filter fed alternately from the two channels would return an interleaving of
        // two half-rate streams: still noise, still roughly the right level, and neither channel's
        // spectrum. The tell is the sample-to-sample correlation, which 1/f shaping makes strongly
        // positive and interleaving destroys.
        var waveform = new Qa40xGeneratorWaveform(new Random(11), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.PinkNoise, Noise = new WhiteNoiseSettings { LevelDbv = 0.0 } };

        const int n = 20_000;
        var left = new double[n];
        for (int i = 0; i < n; i++)
        {
            left[i] = waveform.ComputeSample(channel, i, channelIndex: 0);

            // The right channel is pulled in step, exactly as BuildTxChunk pulls it.
            waveform.ComputeSample(channel, i, channelIndex: 1);
        }

        double numerator = 0.0;
        double denominator = 0.0;
        for (int i = 1; i < n; i++)
        {
            numerator += left[i] * left[i - 1];
            denominator += left[i] * left[i];
        }

        double correlation = numerator / denominator;
        Assert.True(correlation > 0.5, $"Consecutive shaped samples correlated at {correlation:0.000}, which is not a pink stream.");
    }

    [Fact]
    public void ComputeSample_EchoChirp_StartsAtZeroAndSweepsUpward()
    {
        const double startHz = 100.0;
        const double endHz = 10_000.0;
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = new EchoChirpSettings { AmplitudeDbv = 0.0, StartFrequencyHz = startHz, EndFrequencyHz = endHz },
        };

        Assert.Equal(0.0, waveform.ComputeSample(channel, 0L), precision: 9);

        var samples = new float[4096];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)waveform.ComputeSample(channel, i);

        Assert.Contains(samples, s => Math.Abs(s) > 0.1);

        int half = samples.Length / 2;
        double firstHalfHz = ZeroCrossingRateHz(samples[..half], SampleRateHz) / 2.0;
        double secondHalfHz = ZeroCrossingRateHz(samples[half..], SampleRateHz) / 2.0;
        Assert.True(secondHalfHz > firstHalfHz, $"Expected an upward sweep, got {firstHalfHz} Hz then {secondHalfHz} Hz");
    }

    private static double ZeroCrossingRateHz(ReadOnlySpan<float> samples, double sampleRateHz)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0.0f) != (samples[i] < 0.0f))
                crossings++;
        }

        return crossings * sampleRateHz / samples.Length;
    }
}
