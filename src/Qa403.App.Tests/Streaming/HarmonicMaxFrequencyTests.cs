using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// <see cref="SpectrumPipelineOptions.HarmonicMaxFreqHz"/> — the option a THD-vs-frequency sweep
/// needs so its upper half is a measurement rather than a cliff into the floor.
/// </summary>
public class HarmonicMaxFrequencyTests
{
    private const double SampleRateHz = 192_000.0;

    /// <summary>
    /// High enough that H2 (24 kHz) is already outside the default 20 kHz measurement band, but far
    /// enough below Nyquist (96 kHz) that H2..H8 are all really there to be counted.
    /// </summary>
    private const double FundamentalHz = 12_000.0;

    [Fact]
    public async Task Meters_HarmonicsBoundedByTheMeasurementBand_ReportZeroThdForAToneAboveHalfTheBand()
    {
        // This is the behaviour the live FFT view wants (QuantAsylum's /ThdDb/{freq}/{stopHz}
        // convention) and exactly what makes it wrong for a sweep: nothing is broken, there simply
        // are no in-band harmonics to sum.
        var meters = await MetersForAsync(harmonicMaxFreqHz: null);

        Assert.Equal(0.0f, meters.ThdPercent);
    }

    [Fact]
    public async Task Meters_HarmonicMaxAtNyquist_CountsHarmonicsAboveTheMeasurementBand()
    {
        // The simulated DUT synthesizes H2 at -46 dBc (0.5%) and falling, so with the harmonics
        // counted to Nyquist the same tone reports real distortion instead of none.
        var meters = await MetersForAsync(harmonicMaxFreqHz: SampleRateHz / 2.0);

        Assert.True(meters.ThdPercent > 0.1f, $"expected THD near 0.5%, got {meters.ThdPercent}");
        Assert.True(meters.ThdPercent < 2.0f, $"expected THD near 0.5%, got {meters.ThdPercent}");

        // H2 at 24 kHz is above MeasurementHiFreqHz and must be among the reported harmonics.
        Assert.Contains(meters.Harmonics, h => Math.Abs(h.FreqHz - (2.0 * FundamentalHz)) < 200.0);
    }

    private static async Task<DecodedMeters> MetersForAsync(double? harmonicMaxFreqHz)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(FundamentalHz, -6.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = 32768,
                TargetFrameRateHz = 40,
                DisplayColumns = 200,
                Averaging = Qa403.Dsp.AveragingMode.Off,
                HarmonicMaxFreqHz = harmonicMaxFreqHz,
            },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(SampleRateHz, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                byte[] frame = await reader.ReadAsync(timeout.Token);
                if (StreamFrame.DecodeHeader(frame).MsgType == StreamMsgType.Meters)
                    return StreamFrame.DecodeMeters(frame);
            }
        }
        finally
        {
            streamCts.Cancel();
            await device.StopAsync();
            try
            {
                await drainTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await pipeline.StopAsync();
        }
    }
}
