using Tonebench.Device.Simulation;

namespace Tonebench.Device.Tests;

/// <summary>
/// The simulator's channel coupling: its exact response, and that the streaming loop applies it to
/// the victim channel only when the knob is on.
/// </summary>
public class SimulatedCrosstalkTests
{
    private const double SampleRateHz = 48_000.0;

    [Fact]
    public void CouplingDb_ReadsTheFloorAt1kHz_AndRisesAt20kHz()
    {
        // The tutorial's shape: -70 dB through the middle of the band, climbing 6 dB/octave above
        // the corner. The exact sampled figure is what the runner tests compare against.
        // At 1 kHz the 6 kHz zero already lifts the floor by 10·log10(1 + (1/6)²) = 0.12 dB.
        Assert.InRange(SimulatedCrosstalk.CouplingDb(1000.0, 192_000.0), -70.0, -69.8);
        Assert.InRange(SimulatedCrosstalk.CouplingDb(100.0, 192_000.0), -70.1, -69.9);
        Assert.InRange(SimulatedCrosstalk.CouplingDb(20_000.0, 192_000.0), -61.0, -57.0);
        // Monotone rise between corner and the top of the band.
        Assert.True(SimulatedCrosstalk.CouplingDb(10_000.0, 192_000.0) > SimulatedCrosstalk.CouplingDb(5_000.0, 192_000.0));
    }

    [Fact]
    public async Task WithTheKnobOff_TheVictimChannelIsSilent()
    {
        var right = await CaptureAsync(crosstalk: false, channel: 1, driveLeft: true);
        Assert.True(Rms(right) < 1e-9, $"the right channel carried {Rms(right):E2} V with the coupling off");
    }

    [Fact]
    public async Task WithTheKnobOn_DrivingLeftOnly_TheRightInputCarriesTheModel()
    {
        double freqHz = 1000.0;
        var left = await CaptureAsync(crosstalk: true, channel: 0, driveLeft: true, freqHz: freqHz);
        var right = await CaptureAsync(crosstalk: true, channel: 1, driveLeft: true, freqHz: freqHz);

        double measuredDb = 20.0 * Math.Log10(Rms(right) / Rms(left));
        double expectedDb = SimulatedCrosstalk.CouplingDb(freqHz, SampleRateHz);
        Assert.InRange(measuredDb, expectedDb - 0.2, expectedDb + 0.2);
    }

    [Fact]
    public async Task TheCouplingIsSymmetric()
    {
        double freqHz = 8000.0;
        var aggressor = await CaptureAsync(crosstalk: true, channel: 1, driveLeft: false, freqHz: freqHz);
        var victim = await CaptureAsync(crosstalk: true, channel: 0, driveLeft: false, freqHz: freqHz);

        double measuredDb = 20.0 * Math.Log10(Rms(victim) / Rms(aggressor));
        double expectedDb = SimulatedCrosstalk.CouplingDb(freqHz, SampleRateHz);
        Assert.InRange(measuredDb, expectedDb - 0.2, expectedDb + 0.2);
    }

    private static double Rms(double[] samples)
    {
        double sum = 0.0;
        foreach (double s in samples)
            sum += s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    private static async Task<double[]> CaptureAsync(bool crosstalk, int channel, bool driveLeft, double freqHz = 1000.0)
    {
        var device = new SimulatedDevice(randomSeed: 7);
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(freqHz, -10.0)]);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(driveLeft ? tone : ChannelGeneratorConfig.Off, driveLeft ? ChannelGeneratorConfig.Off : tone) { SnapToBin = false },
            CancellationToken.None);

        device.SignalProfile = device.SignalProfile with
        {
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
            CrosstalkEnabled = crosstalk,
        };

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        // Skip the first block (filter settling), keep half a second.
        int wanted = (int)(0.5 * SampleRateHz);
        var samples = new List<double>(wanted);
        int blocks = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            if (blocks++ > 0)
            {
                for (int frame = 0; frame * config.ChannelCount < block.SampleCount; frame++)
                    samples.Add(block.Samples[(frame * config.ChannelCount) + channel]);
            }

            block.Dispose();
            if (samples.Count >= wanted)
                break;
        }

        await device.StopAsync();
        return [.. samples];
    }
}
