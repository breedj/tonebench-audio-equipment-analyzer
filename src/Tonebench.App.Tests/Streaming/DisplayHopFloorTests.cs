using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// <c>SpectrumPipeline.DisplayHopFloor</c> — the free-running display re-analyzes a channel only
/// once a quarter of a record of new signal has arrived, so its cost follows the record length
/// rather than the display tick.
///
/// <para>Without it, <see cref="SpectrumPipelineOptions.TargetFrameRateHz"/> paced the worker at
/// 20 Hz whatever the FFT size: measured against the simulator at 192 kHz with both channels,
/// 1,048,576 points ran 10.5 FFTs per record for 169 % of one core, each tick carrying 9.6 % new
/// signal — which a Hann window weights at 0.041 %, so ten of those eleven transforms per record
/// answered the same question to four decimal places.</para>
/// </summary>
public class DisplayHopFloorTests
{
    private const double SampleRateHz = 48_000.0;

    private static SpectrumPipelineOptions Options(double hop, bool singleShot) =>
        new() { AverageHopFraction = hop, SingleShot = singleShot };

    [Fact]
    public void AFreeRunningChannel_NeverHopsLessThanAQuarterOfARecord()
    {
        // The live FFT screen's own setting is 0 — "analyze on every display tick" — which is the
        // case the floor exists for.
        Assert.Equal(0.25, SpectrumPipeline.EffectiveHopFraction(Options(hop: 0.0, singleShot: false)));
        Assert.Equal(0.25, SpectrumPipeline.EffectiveHopFraction(Options(hop: 0.1, singleShot: false)));
    }

    [Fact]
    public void AHopLargerThanTheFloor_IsLeftAlone()
    {
        // The sweep runners ask for Welch's 50 %; the floor must not pull that down, and must not
        // round it up either.
        Assert.Equal(0.5, SpectrumPipeline.EffectiveHopFraction(Options(hop: 0.5, singleShot: false)));
        Assert.Equal(1.0, SpectrumPipeline.EffectiveHopFraction(Options(hop: 1.0, singleShot: true)));
    }

    [Fact]
    public void ASingleShotChannel_KeepsTheHopItAskedFor()
    {
        // Every runner sets SingleShot, which is what keeps the floor off every measurement path.
        // Four of them pin the hop to 0 (frequency response, impedance twice, impulse response) and
        // must go on taking a record the moment one is ready.
        Assert.Equal(0.0, SpectrumPipeline.EffectiveHopFraction(Options(hop: 0.0, singleShot: true)));
    }

    /// <summary>
    /// The same rule end to end. Bounded above rather than pinned: a machine too slow to keep up
    /// produces fewer analyses, never more, and it is the ceiling that is the point.
    /// </summary>
    [Fact]
    public async Task AFreeRunningPipeline_AnalyzesARecordAboutFourTimes_NotOncePerTick()
    {
        // 683 ms per record against a 16 ms tick: ungated this would be ~40 analyses per record.
        const int fftSize = 32768;
        double recordSeconds = fftSize / SampleRateHz;

        var device = new SimulatedDevice(randomSeed: 1);
        await device.ConnectAsync(CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = fftSize, TargetFrameRateHz = 60, DisplayColumns = 200,
                Averaging = AveragingMode.Off, ProcessLeft = true, ProcessRight = false,
                SingleShot = false,
            },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(SampleRateHz, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 1024);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            // Let the ring buffer fill before counting, or the wait for the first record is counted
            // as though it were a gated interval.
            await Task.Delay(TimeSpan.FromSeconds(recordSeconds + 0.5));

            var measured = TimeSpan.FromSeconds(recordSeconds * 4);
            using var readCts = new CancellationTokenSource(measured + TimeSpan.FromSeconds(20));
            var until = DateTime.UtcNow + measured;
            int spectra = 0;
            while (DateTime.UtcNow < until)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                if (StreamFrame.DecodeHeader(frame).MsgType == StreamMsgType.Spectrum)
                    spectra++;
            }

            double perRecord = spectra / (measured.TotalSeconds / recordSeconds);
            Assert.InRange(perRecord, 2.0, 6.0);
        }
        finally
        {
            await streamCts.CancelAsync();
            try { await drainTask; } catch (OperationCanceledException) { }
            await pipeline.StopAsync();
            await device.StopAsync();
        }
    }
}
