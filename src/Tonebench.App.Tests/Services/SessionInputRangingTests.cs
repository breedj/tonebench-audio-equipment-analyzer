using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Who chooses the input range while an analyzer with no ranger of its own is acquiring — the
/// session, since 2026-10-09, and before that every attached browser tab.
///
/// <para>The regression these pin is "with the simulator it restarts averaging after 3
/// measurements". <c>App.vue</c> posted a range once a target had held for three meters frames,
/// every accepted range write flushes the analysis window, and a flush clears the averagers — so
/// with two windows open on one bench the two tabs wrote <b>18 and 12 dBV alternately, about once
/// a second</b> (measured on the simulator at 1 kHz / 7.5 dBV, peak 7.557 dBV) and a 15-average
/// FFT never reached 15. A ranger in the session cannot disagree with itself.</para>
/// </summary>
public class SessionInputRangingTests
{
    private static readonly AcquisitionConfig Config = new(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);

    /// <summary>The volts whose RMS-referenced peak is <paramref name="dbv"/> — the convention the whole ranging path speaks.</summary>
    private static float PeakVoltsFor(double dbv) => (float)(Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0));

    private static DeviceSessionService SessionFor(
        LevelDevice device, AutoRangeStepDownDelaySetting? stepDown = null) => new(
        AnalyzerDeviceFactory.Pinned(device),
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        new CaptureDelaySetting(),
        NullLogger<DeviceSessionService>.Instance,
        stepDownDelay: stepDown ?? new AutoRangeStepDownDelaySetting { Ms = 1000.0 });

    /// <summary>
    /// The fix, stated as the thing that was broken: a steady signal produces exactly <b>one</b>
    /// range change and then nothing, however long it runs. Twenty further blocks is about half a
    /// second of stream at this block size — on the old client-side ranger that was three or four
    /// more writes, each one a flushed record.
    /// </summary>
    [Fact]
    public async Task OnADeviceWithNoRangerOfItsOwn_ASteadySignal_IsRangedOnceAndThenLeftAlone()
    {
        var device = new LevelDevice { PeakVolts = PeakVoltsFor(7.5) };
        var session = SessionFor(device);
        await session.ConnectAsync(CancellationToken.None);
        device.RangeWrites.Clear();

        await session.StartAcquisitionAsync(Config, CancellationToken.None);
        await device.PushAndDrainAsync(Config, blocks: 4);

        Assert.Equal([18.0], device.RangeWrites);

        await device.PushAndDrainAsync(Config, blocks: 20);

        Assert.Equal([18.0], device.RangeWrites);
        Assert.Equal(18.0, session.GetStatus().InputRangeDbv);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// A device that ranges inside itself is left to it. Two rangers on one range is the hunting
    /// this was written to end, not twice the coverage — and on a QA403 a register write from this
    /// thread, mid-stream, is the documented way to wedge the device.
    /// </summary>
    [Fact]
    public async Task OnADeviceThatRangesItself_TheSessionWritesNoRangeAtAll()
    {
        var device = new LevelDevice { PeakVolts = PeakVoltsFor(7.5), HasInputAutoRanging = true };
        var session = SessionFor(device);
        await session.ConnectAsync(CancellationToken.None);
        device.RangeWrites.Clear();

        await session.StartAcquisitionAsync(Config, CancellationToken.None);
        await device.PushAndDrainAsync(Config, blocks: 10);

        Assert.Empty(device.RangeWrites);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// Manual means manual. With the ranges pinned the user chose one deliberately, and the whole
    /// point of the control is that nothing walks away from it — which is also the workaround this
    /// fix replaces, so it has to keep working.
    /// </summary>
    [Fact]
    public async Task WithTheRangesPinnedToManual_TheSessionWritesNoRange()
    {
        var device = new LevelDevice { PeakVolts = PeakVoltsFor(7.5) };
        var session = SessionFor(device);
        await session.ConnectAsync(CancellationToken.None);
        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        device.RangeWrites.Clear();

        await session.StartAcquisitionAsync(Config, CancellationToken.None);
        await device.PushAndDrainAsync(Config, blocks: 10);

        Assert.Empty(device.RangeWrites);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// A signal that goes quiet is followed down, once, after the step-down delay — and then left
    /// alone again. The delay is the hardware ranger's own setting, so a bench that has tuned it
    /// means the same thing by it here.
    /// </summary>
    [Fact]
    public async Task ASignalThatGoesQuiet_StepsDownOnce_AfterTheDelay()
    {
        var device = new LevelDevice { PeakVolts = PeakVoltsFor(7.5) };
        var session = SessionFor(device, new AutoRangeStepDownDelaySetting { Ms = 1.0 });
        await session.ConnectAsync(CancellationToken.None);
        device.RangeWrites.Clear();

        await session.StartAcquisitionAsync(Config, CancellationToken.None);
        await device.PushAndDrainAsync(Config, blocks: 4);
        Assert.Equal([18.0], device.RangeWrites);

        device.PeakVolts = PeakVoltsFor(-30.0);
        await device.PushAndDrainAsync(Config, blocks: 20);

        Assert.Equal([18.0, 0.0], device.RangeWrites);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// A scripted device whose blocks carry a chosen level, so a test can say what the analyzer is
    /// seeing and then watch what the session does about it. Not the simulator: its level comes
    /// from a generator configuration, and what matters here is only the number in the samples.
    /// </summary>
    private sealed class LevelDevice : IAnalyzerDevice
    {
        public event EventHandler? DeviceLost { add { } remove { } }

        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        /// <summary>False like the simulator and the sound card; the one test that wants the other answer sets it.</summary>
        public bool HasInputAutoRanging { get; init; }

        /// <summary>The peak volts every sample of every pushed block carries, on both channels.</summary>
        public volatile float PeakVolts;

        /// <summary>Every range the session wrote, in order — the whole subject of these tests.</summary>
        public List<double> RangeWrites { get; } = [];

        public int StimulusLatencyFrames(double sampleRateHz) => 0;

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("QA403", "SIM-0001", "1.0", IsSimulated: true));

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

        public double InputRangeDbv { get; private set; }

        public long DroppedRxChunks => 0;

        public long DroppedBlocks => 0;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; }

        public bool AutoRangeStepDownEnabled { get; set; } = true;

        public Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            InputRangeDbv = dbv;
            lock (RangeWrites)
                RangeWrites.Add(dbv);
            return Task.CompletedTask;
        }

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        private readonly Channel<PcmBlock> _scripted = Channel.CreateUnbounded<PcmBlock>();
        private int _yielded;

        /// <summary>
        /// Queues <paramref name="blocks"/> blocks at the current level and waits until the session
        /// has taken every one of them. Scripted rather than free-running so the assertions are
        /// about a known number of blocks rather than about however many a timer produced.
        /// </summary>
        public async Task PushAndDrainAsync(AcquisitionConfig config, int blocks)
        {
            int target = Volatile.Read(ref _yielded) + blocks;
            for (int i = 0; i < blocks; i++)
            {
                int sampleCount = config.BlockFrameCount * config.ChannelCount;
                float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
                Array.Fill(buffer, PeakVolts, 0, sampleCount);
                _scripted.Writer.TryWrite(new PcmBlock(
                    buffer, sampleCount, config.SampleRateHz, config.ChannelCount, i, 0));
            }

            for (int i = 0; i < 500 && Volatile.Read(ref _yielded) < target; i++)
                await Task.Delay(10);

            Assert.True(Volatile.Read(ref _yielded) >= target, "the session never consumed the scripted blocks");

            // The ranging for the last block runs after it was handed over, so give that its moment
            // before anything asserts on what was written.
            await Task.Delay(50);
        }

        public async IAsyncEnumerable<PcmBlock> StreamAsync(
            AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
        {
            while (await _scripted.Reader.WaitToReadAsync(ct))
            {
                while (_scripted.Reader.TryRead(out var block))
                {
                    Interlocked.Increment(ref _yielded);
                    yield return block;
                }
            }
        }
    }
}
