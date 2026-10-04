using System.Numerics;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.Device.Tests;

/// <summary>
/// End-to-end check that <see cref="SimulatedDevice"/>'s signal synthesis and
/// <c>Tonebench.Dsp</c>'s scaling agree: capture from the simulator, analyze with the real DSP
/// pipeline, and confirm the measured numbers match what was configured. This is the
/// regression harness referenced by the Phase 3 spec for everything built on top of it.
/// </summary>
public class SimulatedDeviceDspRoundTripTests
{
    private const double SampleRateHz = 48_000.0;

    [Fact]
    public async Task MeasuredThd_MatchesConfiguredHarmonicProfile_Within0_1Db()
    {
        const int fftSize = 65536;
        var coherent = CoherentFrequency.Nearest(1000.0, SampleRateHz, fftSize);

        var profile = new SimulatedSignalProfile
        {
            FundamentalFrequencyHz = coherent.FrequencyHz,
            FundamentalAmplitudeDbv = 0.0,
            HarmonicsEnabled = true,
            HumEnabled = false,
            NoiseEnabled = false,
        };

        var device = new SimulatedDevice();
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = profile;

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        double[] samples = await CaptureSamplesAsync(device, config, fftSize);

        var window = Window.Get(WindowType.Hann, fftSize);
        var windowed = new double[fftSize];
        Window.Apply(samples, window, windowed);

        var spectrum = new Complex[fftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);

        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, window.Sums, psdBuffer);

        var harmonics = new Harmonic[1 + profile.HarmonicDbc.Count];
        HarmonicAnalyzer.Analyze(psd, coherent.Bin, harmonics.Length, harmonics);

        double measuredThdDb = SpectrumMetrics.ThdDb(harmonics);
        double expectedThdDb = ExpectedThdDb(profile.HarmonicDbc);

        Assert.True(
            Math.Abs(measuredThdDb - expectedThdDb) < 0.1,
            $"expected THD {expectedThdDb:F4} dB (from configured dBc profile), measured {measuredThdDb:F4} dB");
    }

    [Fact]
    public async Task MeasuredNoiseFloor_MatchesConfiguredDensity_Within0_5Db()
    {
        const int fftSize = 1024;
        const int segments = 64;
        const double noiseDensityDbvPerRootHz = -150.0;

        var profile = new SimulatedSignalProfile
        {
            FundamentalAmplitudeDbv = -300.0, // effectively silent
            HarmonicsEnabled = false,
            HumEnabled = false,
            NoiseEnabled = true,
            PinkNoiseEnabled = false, // isolate the flat white floor this test verifies
            NoiseDensityDbvPerRootHz = noiseDensityDbvPerRootHz,
        };

        var device = new SimulatedDevice(randomSeed: 2026);
        await device.ConnectAsync(CancellationToken.None);
        device.SignalProfile = profile;

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        double[] samples = await CaptureSamplesAsync(device, config, fftSize * segments);

        // Rectangular window: like Tonebench.Dsp.Tests golden test 5, Parseval holds without any
        // window-energy correction, so the raw block feeds the FFT directly.
        var window = Window.Get(WindowType.Rectangular, fftSize);
        var backend = new FftSharpBackend();
        var spectrum = new Complex[fftSize / 2 + 1];
        var psdBuffer = new double[fftSize / 2 + 1];
        var accumulated = new double[fftSize / 2 + 1];

        for (int s = 0; s < segments; s++)
        {
            var segment = samples.AsSpan(s * fftSize, fftSize);
            backend.Forward(segment, spectrum);
            var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, window.Sums, psdBuffer);
            for (int k = 0; k < accumulated.Length; k++)
                accumulated[k] += psd.ValuesV2PerHz[k];
        }

        // Average over segments (reduces per-bin estimator variance), then over interior bins
        // (independent white-noise bins average out remaining scatter). Skip a few edge bins.
        double averagePsd = accumulated.Skip(4).SkipLast(4).Select(sum => sum / segments).Average();

        double densityLinear = Math.Pow(10.0, noiseDensityDbvPerRootHz / 20.0);
        double theoreticalPsd = densityLinear * densityLinear;

        double errorDb = 10.0 * Math.Log10(averagePsd / theoreticalPsd);
        Assert.True(Math.Abs(errorDb) < 0.5, $"noise floor level error {errorDb:F3} dB exceeds 0.5 dB");
    }

    private static double ExpectedThdDb(IReadOnlyList<double> harmonicDbc)
    {
        double sumSquares = harmonicDbc.Sum(dbc => Math.Pow(10.0, dbc / 10.0));
        return 10.0 * Math.Log10(sumSquares);
    }

    private static async Task<double[]> CaptureSamplesAsync(IAnalyzerDevice device, AcquisitionConfig config, int frameCount)
    {
        var samples = new double[frameCount];
        int filled = 0;

        using var cts = new CancellationTokenSource();
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            using (block)
            {
                var blockSamples = block.Samples;
                for (int frame = 0; frame < block.FrameCount && filled < frameCount; frame++)
                    samples[filled++] = blockSamples[frame * block.ChannelCount];
            }

            if (filled >= frameCount)
            {
                cts.Cancel();
                break;
            }
        }

        return samples;
    }
}
