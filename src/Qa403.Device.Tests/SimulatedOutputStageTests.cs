using System.Numerics;
using Qa403.Device.Simulation;

namespace Qa403.Device.Tests;

/// <summary>
/// The simulator's amplifier output stage: its exact divider, the oracle a load-step measurement is
/// checked against, and — the guard every other simulated measurement relies on — that the default
/// knobs change nothing at all.
/// </summary>
public class SimulatedOutputStageTests
{
    private const double SampleRateHz = 48_000.0;

    [Fact]
    public void AnalogZout_IsTheSeriesElements()
    {
        var z = SimulatedOutputStage.AnalogZoutAt(SimulatedOutputStageModel.SolidState, 20_000.0);
        Assert.Equal(SimulatedOutputStage.SolidStateOhms, z.Real, 12);
        Assert.Equal(2.0 * Math.PI * 20_000.0 * SimulatedOutputStage.SolidStateHenry, z.Imaginary, 9);
        // The BC-1's own numbers: 0.028 ohm at LF, its 1.5 uH coil worth 0.19 ohm at 20 kHz.
        Assert.InRange(z.Magnitude, 0.185, 0.195);
        Assert.Equal(Complex.Zero, SimulatedOutputStage.AnalogZoutAt(SimulatedOutputStageModel.None, 1000.0));
    }

    [Fact]
    public void SampledZout_MatchesTheAnalogFigure_WhereTheBilinearMapIsStraight()
    {
        // At 1 kHz against 192 kHz the warping is invisible; the sampled oracle and the design figure agree.
        var sampled = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.SolidState, 8.0, 1000.0, 192_000.0);
        var analog = SimulatedOutputStage.AnalogZoutAt(SimulatedOutputStageModel.SolidState, 1000.0);
        Assert.InRange(sampled.Real, analog.Real * 0.999, analog.Real * 1.001);
        Assert.InRange(sampled.Imaginary, analog.Imaginary * 0.99, analog.Imaginary * 1.01);

        // The tube model: 2 ohm resistive dominates at 1 kHz (0.3 mH is 1.9 ohm there — comparable, so both show).
        var tube = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, 100.0, 192_000.0);
        Assert.InRange(tube.Real, 1.99, 2.01);
        Assert.InRange(tube.Imaginary, 0.18, 0.20);
    }

    [Fact]
    public void TheDivider_IsTheSampledOracleInverted()
    {
        // Process a tone through the stage and compare the level drop with what ZoutAt implies:
        // |H| = |R_L / (R_L + Zout)|.
        double freqHz = 5000.0;
        var stage = new SimulatedOutputStage(SimulatedOutputStageModel.TubeAmp, SimulatedLoad.EightOhms, SampleRateHz);
        Assert.False(stage.IsUnity);

        double input = 0.0, output = 0.0;
        int n = (int)SampleRateHz;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Sin(2.0 * Math.PI * freqHz * i / SampleRateHz);
            double y = stage.Process(x);
            if (i >= n / 2)
            {
                input += x * x;
                output += y * y;
            }
        }

        double measuredDb = 10.0 * Math.Log10(output / input);
        var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, freqHz, SampleRateHz);
        double expectedDb = 20.0 * Math.Log10((8.0 / (8.0 + z)).Magnitude);
        Assert.InRange(measuredDb, expectedDb - 0.02, expectedDb + 0.02);
        // And it is a real drop: 2 ohm + 9.4 ohm of reactance against 8 ohm is several dB.
        Assert.True(measuredDb < -3.0);
    }

    [Theory]
    [InlineData(SimulatedOutputStageModel.None, SimulatedLoad.EightOhms)]
    [InlineData(SimulatedOutputStageModel.SolidState, SimulatedLoad.Open)]
    [InlineData(SimulatedOutputStageModel.None, SimulatedLoad.Open)]
    public void NoModelOrNoLoad_IsUnity(SimulatedOutputStageModel model, SimulatedLoad load)
    {
        var stage = new SimulatedOutputStage(model, load, SampleRateHz);
        Assert.True(stage.IsUnity);
        Assert.Equal(0.123456, stage.Process(0.123456));
    }

    [Fact]
    public async Task TheDefaultKnobs_LeaveTheStreamBitIdentical()
    {
        // The guard for every other simulated measurement: with the stage at its defaults the device
        // produces exactly the samples it produced before the stage existed — which, since the stage
        // is unity, is the same as forcing it off.
        var untouched = await CaptureAsync(SimulatedOutputStageModel.None, SimulatedLoad.Open);
        var loadWithoutAStage = await CaptureAsync(SimulatedOutputStageModel.None, SimulatedLoad.EightOhms);
        var stageWithoutALoad = await CaptureAsync(SimulatedOutputStageModel.SolidState, SimulatedLoad.Open);
        Assert.Equal(untouched.Left, loadWithoutAStage.Left);
        Assert.Equal(untouched.Left, stageWithoutALoad.Left);
        Assert.Equal(untouched.Right, stageWithoutALoad.Right);
    }

    [Fact]
    public async Task ALoadedStage_DividesTheLeftChannelOnly()
    {
        double freqHz = 1000.0;
        var open = await CaptureAsync(SimulatedOutputStageModel.TubeAmp, SimulatedLoad.Open, freqHz);
        var loaded = await CaptureAsync(SimulatedOutputStageModel.TubeAmp, SimulatedLoad.EightOhms, freqHz);

        double leftDb = 20.0 * Math.Log10(Rms(loaded.Left) / Rms(open.Left));
        double rightDb = 20.0 * Math.Log10(Rms(loaded.Right) / Rms(open.Right));

        var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, freqHz, SampleRateHz);
        double expectedDb = 20.0 * Math.Log10((8.0 / (8.0 + z)).Magnitude);
        Assert.InRange(leftDb, expectedDb - 0.05, expectedDb + 0.05);
        // The right channel is the generator tap: untouched.
        Assert.InRange(rightDb, -0.001, 0.001);
    }

    private static double Rms(double[] samples)
    {
        double sum = 0.0;
        foreach (double s in samples)
            sum += s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    private static async Task<(double[] Left, double[] Right)> CaptureAsync(
        SimulatedOutputStageModel model, SimulatedLoad load, double freqHz = 1000.0)
    {
        var device = new SimulatedDevice(randomSeed: 7);
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(freqHz, -10.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone) { SnapToBin = false }, CancellationToken.None);

        device.SignalProfile = device.SignalProfile with
        {
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
            OutputStage = model,
            Load = load,
        };

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        int wanted = (int)(0.5 * SampleRateHz);
        var left = new List<double>(wanted);
        var right = new List<double>(wanted);
        int blocks = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            if (blocks++ > 0)
            {
                for (int frame = 0; frame * config.ChannelCount < block.SampleCount; frame++)
                {
                    left.Add(block.Samples[frame * config.ChannelCount]);
                    right.Add(block.Samples[(frame * config.ChannelCount) + 1]);
                }
            }

            block.Dispose();
            if (left.Count >= wanted)
                break;
        }

        await device.StopAsync();
        return ([.. left], [.. right]);
    }
}
