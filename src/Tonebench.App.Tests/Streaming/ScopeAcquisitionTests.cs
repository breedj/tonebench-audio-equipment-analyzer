using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The oscilloscope's capture path driven end to end through a real <see cref="DeviceSessionService"/>
/// and the simulator — the seam itself, rather than the engine in isolation.
///
/// <para>This is the test that would have caught the capture being wired in the wrong place: the
/// scope has to see the blocks <b>before</b> <c>SpectrumPipeline.Enqueue</c> takes ownership of
/// them and disposes them, and it has to see them with the external gain already applied, because
/// everything downstream of the pipeline's door draws the DUT's volts rather than the
/// analyzer's.</para>
/// </summary>
public class ScopeAcquisitionTests
{
    private const double SampleRateHz = 48_000.0;
    private const double ToneHz = 1_000.0;

    private static ScopeCaptureSpec Spec(ScopeRunMode mode = ScopeRunMode.Single, double levelV = 0.0) =>
        new(
            TriggerSpec.Edge(levelV, TriggerSlope.Rising),
            TriggerChannel: 0,
            RecordFrames: 4096,
            PreTriggerFrames: 1024,
            Mode: mode,
            SampleRateHz: SampleRateHz,
            AutoTimeoutFrames: 0,
            DiscontinuityHoldoffFrames: 0);

    private static async Task<ScopeRecord?> CaptureAsync(
        ScopeCaptureSpec spec, ExternalGainSetting? gain = null, double toneDbv = -6.0)
    {
        var device = new SimulatedDevice();
        var buffer = new ScopeCaptureBuffer(2, 8192);
        var engine = new ScopeTriggerEngine(buffer);
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance, externalGain: gain);
        var session = new DeviceSessionService(
            device,
            pipeline,
            new CaptureDelaySetting { Ms = 0 },
            NullLogger<DeviceSessionService>.Instance,
            gain,
            engine);

        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: (int)SampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneHz, toneDbv)]),
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneHz, toneDbv)])),
            CancellationToken.None,
            config);

        engine.Arm(spec, droppedTotal: 0);
        await session.StartAcquisitionAsync(config, CancellationToken.None);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested)
            {
                if (engine.TakeRecord() is { } record)
                    return record;

                await Task.Delay(20, CancellationToken.None);
            }

            return null;
        }
        finally
        {
            await session.StopAcquisitionAsync();
            await session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AnArmedScope_CapturesTheStreamItWasPointedAt()
    {
        var record = await CaptureAsync(Spec());

        Assert.NotNull(record);
        Assert.Equal(4096, record!.FrameCount);
        Assert.Equal(2, record.ChannelCount);
        Assert.Equal(SampleRateHz, record.SampleRateHz, 6);
        Assert.False(record.Forced);

        // A clean simulated stream, so nothing should have been dropped under it.
        Assert.False(record.Spliced);
    }

    [Fact]
    public async Task TheCapturedRecordIsTheToneThatWasPlaying()
    {
        // -6 dBV is 0.5 Vrms, so 0.7071 V peak. Measured off the record rather than asserted
        // loosely, because the whole point of the seam is that these are the DUT's own volts.
        var record = await CaptureAsync(Spec());

        Assert.NotNull(record);
        var levels = WaveformMeasure.Measure(record!.Channels[0]);

        Assert.Equal(0.5, levels.AcRmsV, 2);
        Assert.Equal(Math.Sqrt(2.0), levels.CrestFactor!.Value, 1);
    }

    [Fact]
    public async Task TheTriggerLandsWhereTheSignalCrossesTheLevel()
    {
        // Rising through zero, 1024 frames of pre-trigger. The sample at the trigger is at or above
        // the level and the one before it is below -- which is the whole of what an edge trigger
        // promises, and what makes successive captures sit still on top of each other.
        var record = await CaptureAsync(Spec());

        Assert.NotNull(record);

        // t = 0 is the crossing, and the record is anchored on the NEAREST sample to it -- which
        // may be the one just below the level, not the one just above. So the straddling pair is
        // found from the time axis rather than assumed to sit at the pre-trigger index.
        int k = 0;
        for (int i = 0; i < record!.FrameCount - 1; i++)
        {
            if (record.TimeAt(i) <= 0.0 && record.TimeAt(i + 1) > 0.0)
            {
                k = i;
                break;
            }
        }

        Assert.InRange(k, 1023, 1024);
        Assert.True(record.Channels[0][k] <= 0.0, $"the sample below t = 0 was {record.Channels[0][k]:0.####}");
        Assert.True(record.Channels[0][k + 1] >= 0.0, $"the sample above t = 0 was {record.Channels[0][k + 1]:0.####}");

        // And the anchor is never further than half a sample from the crossing.
        Assert.InRange(Math.Abs(record.TimeAt(1024)), 0.0, 0.5 / SampleRateHz);
    }

    [Fact]
    public async Task TheExternalGainReachesTheScope_SoItDrawsTheDutsVolts()
    {
        // A 10:1 divider in front of the input is MINUS 20 dB, and then the DUT is ten times what
        // the analyzer sees -- the sign convention ExternalGainSetting documents, and the one a x10
        // probe will be labelled with. The scope has to agree with every other reading in the app
        // about it, which is why the gain is applied at this seam and not left to the client.
        var plain = await CaptureAsync(Spec());
        var divided = await CaptureAsync(Spec(levelV: 0.0), gain: new ExternalGainSetting { LeftDb = -20.0, RightDb = -20.0 });

        Assert.NotNull(plain);
        Assert.NotNull(divided);

        double plainRms = WaveformMeasure.Measure(plain!.Channels[0]).AcRmsV;
        double dividedRms = WaveformMeasure.Measure(divided!.Channels[0]).AcRmsV;

        Assert.Equal(10.0, dividedRms / plainRms, 1);
    }

    [Fact]
    public async Task ARunningScopeKeepsDelivering()
    {
        var device = new SimulatedDevice();
        var buffer = new ScopeCaptureBuffer(2, 8192);
        var engine = new ScopeTriggerEngine(buffer);
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            new CaptureDelaySetting { Ms = 0 },
            NullLogger<DeviceSessionService>.Instance,
            engine);

        await session.ConnectAsync(CancellationToken.None);
        var config = new AcquisitionConfig(SampleRateHz: (int)SampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneHz, -6.0)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None,
            config);

        engine.Arm(Spec(ScopeRunMode.Normal), droppedTotal: 0);
        await session.StartAcquisitionAsync(config, CancellationToken.None);

        try
        {
            int taken = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (taken < 3 && !cts.IsCancellationRequested)
            {
                if (engine.TakeRecord() is not null)
                    taken++;
                else
                    await Task.Delay(20, CancellationToken.None);
            }

            Assert.Equal(3, taken);
        }
        finally
        {
            await session.StopAcquisitionAsync();
            await session.ShutdownAsync();
        }
    }
}
