using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// Who chooses the input range, and how the session decides it.
///
/// <para>Three properties here used to answer "does this device range itself?" with
/// <c>_device is SimulatedDevice</c> — a device-shaped question answered by naming one device. The
/// device declares it now (<see cref="IAnalyzerDevice.HasInputAutoRanging"/>), and the eight
/// runners ask <see cref="DeviceSessionService.RunnerOwnsInputRange"/> instead of re-spelling the
/// same pair of clauses.</para>
/// </summary>
public class DeviceRangingCapabilityTests
{
    private static DeviceSessionService SessionFor(IAnalyzerDevice device) => new(
        device,
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        NullLogger<DeviceSessionService>.Instance);

    [Fact]
    public async Task AutoRangeEnabled_OnADeviceThatDeclaresNoRangerOfItsOwn_IsNull()
    {
        var session = SessionFor(new RangingStub { HasInputAutoRanging = false, AutoRangeEnabled = true });
        await session.ConnectAsync(CancellationToken.None);

        Assert.Null(session.AutoRangeEnabled);
        Assert.Null(session.AutoRangeStepDownDelayMs);
        Assert.Null(session.AutoRangeStepDownEnabled);
    }

    /// <summary>
    /// Not null, and not a cached setpoint either: it is whatever the device says right now.
    /// Connecting applies the session's ranging mode to the device, so the flag follows the mode.
    /// </summary>
    [Fact]
    public async Task AutoRangeEnabled_OnADeviceThatDeclaresOne_IsTheDevicesOwnFlag()
    {
        var device = new RangingStub { HasInputAutoRanging = true };
        var session = SessionFor(device);
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.AutoRangeEnabled);
        Assert.True(device.AutoRangeEnabled);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);

        Assert.False(session.AutoRangeEnabled);
        Assert.False(device.AutoRangeEnabled);
        Assert.NotNull(session.AutoRangeStepDownDelayMs);
        Assert.NotNull(session.AutoRangeStepDownEnabled);
    }

    /// <summary>
    /// The behaviour the three type tests used to produce, pinned so replacing them stays a
    /// refactor rather than a change.
    /// </summary>
    [Fact]
    public async Task TheSimulator_StillReportsNoRangerOfItsOwn()
    {
        var session = SessionFor(new SimulatedDevice());
        await session.ConnectAsync(CancellationToken.None);

        Assert.Null(session.AutoRangeEnabled);
        Assert.Null(session.AutoRangeStepDownDelayMs);
        Assert.Null(session.AutoRangeStepDownEnabled);
    }

    /// <summary>
    /// <see cref="IAnalyzerDevice.HasInputAutoRanging"/> defaults to <c>true</c>, and that default
    /// is load-bearing: the app's test doubles wrap a <see cref="SimulatedDevice"/> while standing
    /// in for hardware that ranges, and they are the only way a runner's "take over the input range"
    /// path gets exercised at all. Putting the flag anywhere they proxy — on
    /// <see cref="DeviceCapabilities"/>, say, which they forward from the inner device — would take
    /// that path out of eight runner suites without failing anything.
    /// </summary>
    [Fact]
    public async Task AWrappingTestDouble_KeepsTheInterfaceDefault_AndStillRanges()
    {
        var session = SessionFor(new OnePoleLowPassDevice(new SimulatedDevice(), cornerHz: 2_000.0));
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.AutoRangeEnabled);
        Assert.True(session.RunnerOwnsInputRange);
    }

    /// <summary>
    /// The change this property exists for. A device with one input full scale — a sound card —
    /// has nothing to predict, nothing to probe and nothing to ratchet, and a runner that takes the
    /// range over spends a whole extra capture in its range probe concluding that the only range is
    /// the only range. Fails against the old <c>Count &gt; 0</c> predicate.
    /// </summary>
    [Fact]
    public async Task RunnerOwnsInputRange_WithASingleRung_IsFalse()
    {
        var session = SessionFor(new RangingStub
        {
            HasInputAutoRanging = false,
            Ranges = SingleRung,
        });
        await session.ConnectAsync(CancellationToken.None);

        Assert.Single(session.Capabilities.InputRangesDbv);
        Assert.False(session.RunnerOwnsInputRange);
    }

    [Fact]
    public async Task RunnerOwnsInputRange_OnALadderUnderAuto_IsTrue()
    {
        var session = SessionFor(new RangingStub { HasInputAutoRanging = true, AutoRangeEnabled = true });
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.RunnerOwnsInputRange);
    }

    /// <summary>Manual means manual: the user pinned a range, and a measurement must not move it.</summary>
    [Fact]
    public async Task RunnerOwnsInputRange_UnderManualRanging_IsFalse()
    {
        var session = SessionFor(new RangingStub { HasInputAutoRanging = true, AutoRangeEnabled = true });
        await session.ConnectAsync(CancellationToken.None);
        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);

        Assert.False(session.RunnerOwnsInputRange);
    }

    private static readonly DeviceCapabilities SingleRung = new(
        SampleRatesHz: [48_000],
        InputRangesDbv: [0.0],
        OutputRangesDbv: [0.0],
        MaxChannelCount: 2);

    /// <summary>
    /// Bare enough to isolate the decision: it connects, holds two ranges and declares whether it
    /// ranges itself. It never streams, so nothing here depends on the simulator's signal.
    /// </summary>
    private sealed class RangingStub : IAnalyzerDevice
    {
        private double _inputRangeDbv;

        public DeviceCapabilities Ranges { get; init; } = DeviceCapabilities.Qa403;

        public DeviceCapabilities Capabilities => Ranges;

        public bool HasInputAutoRanging { get; init; } = true;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; } = 1_000.0;

        public bool AutoRangeStepDownEnabled { get; set; } = true;

        public double InputRangeDbv => _inputRangeDbv;

        public long DroppedRxChunks => 0;

        public long DroppedBlocks => 0;

        public event EventHandler? DeviceLost { add { } remove { } }

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("Stub", "STUB-1", "stub-1.0", IsSimulated: true));

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

        public int StimulusLatencyFrames(double sampleRateHz) => 0;

        public Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            _inputRangeDbv = dbv;
            return Task.CompletedTask;
        }

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct) =>
            AsyncEnumerable.Empty<PcmBlock>();

        public Task StopAsync() => Task.CompletedTask;
    }
}
