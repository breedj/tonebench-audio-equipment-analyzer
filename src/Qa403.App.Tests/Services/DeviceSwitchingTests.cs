using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Hardware;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// Switching the analyzer between the real QA403 and the simulator at runtime, which used to be a
/// command-line flag and a restart.
///
/// <para>These run against a factory that builds simulators for <em>both</em> kinds, because the
/// question here is the session's own bookkeeping — teardown, state reset, refusal rules — and not
/// whether a QA403 is plugged into the build agent.</para>
/// </summary>
public class DeviceSwitchingTests
{
    /// <summary>
    /// A factory whose "hardware" is a second simulator, optionally one that refuses to connect.
    ///
    /// <para><b>Never a real <c>Qa40xUsbDevice</c></b>: constructing one on a machine that happens to
    /// have a QA403 attached would claim the interface out from under whatever else is using it, and
    /// would make the result of these tests depend on what is plugged into the bench. What is under
    /// test is the session's own bookkeeping — teardown, state reset, refusal rules — none of which
    /// is about USB.</para>
    /// </summary>
    private sealed class FakeFactory(DeviceKind initialKind, bool hardwareIsMissing = false)
        : AnalyzerDeviceFactory(NullLoggerFactory.Instance, initialKind)
    {
        public List<DeviceKind> Built { get; } = [];

        public override IAnalyzerDevice Create(DeviceKind kind)
        {
            Built.Add(kind);
            return kind == DeviceKind.Hardware && hardwareIsMissing
                ? new MissingDevice()
                : new SimulatedDevice();
        }
    }

    /// <summary>
    /// Stands in for a QA403 that is not plugged in: it exists, and connecting to it throws exactly
    /// what the real transport throws when the bus has no such device.
    /// </summary>
    private sealed class MissingDevice : IAnalyzerDevice
    {
        public event EventHandler? DeviceLost { add { } remove { } }

        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        public int StimulusLatencyFrames(double sampleRateHz) => 3584;

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            throw new Qa40xDeviceNotFoundException("No QA403 found on USB.");

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(false);

        public double InputRangeDbv => 0.0;

        public long DroppedRxChunks => 0;

        public long DroppedBlocks => 0;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; }

        public bool AutoRangeStepDownEnabled { get; set; } = true;

        public Task SetInputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct) =>
            AsyncEnumerable.Empty<PcmBlock>();

        public Task StopAsync() => Task.CompletedTask;
    }

    private static DeviceSessionService CreateSession(AnalyzerDeviceFactory? factory = null) =>
        new(factory ?? new FakeFactory(DeviceKind.Simulated),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

    /// <summary>
    /// The kind is reported whether or not anything is connected. <c>IsSimulated</c> cannot answer
    /// this — it describes a <i>connected</i> device, so it reads false in exactly the state the UI
    /// most needs the answer in: the simulator selected but not yet up.
    /// </summary>
    [Fact]
    public void GetStatus_ReportsTheSelectedKind_EvenBeforeConnecting()
    {
        var session = CreateSession();

        var status = session.GetStatus();

        Assert.Equal("Simulated", status.DeviceKind);
        Assert.False(status.Connected);
        Assert.False(status.IsSimulated);
    }

    /// <summary>Switching to the kind already selected is a no-op, not a disconnect and reconnect.</summary>
    [Fact]
    public async Task SwitchDeviceAsync_ToTheSameKind_LeavesTheSessionAlone()
    {
        var session = CreateSession();
        await session.ConnectAsync(CancellationToken.None);
        string serial = session.GetStatus().SerialNumber;

        await session.SwitchDeviceAsync(DeviceKind.Simulated, CancellationToken.None);

        var status = session.GetStatus();
        Assert.True(status.Connected);
        Assert.Equal(serial, status.SerialNumber);
        Assert.Equal("Simulated", status.DeviceKind);
    }

    /// <summary>
    /// The switch connects the new device on the spot, so the session comes back usable rather than
    /// needing a separate Connect. Going the other way — to hardware, on a machine with no QA403 —
    /// must leave an ordinary disconnected session rather than throwing: that is the same state the
    /// app starts in with nothing plugged in, and the Connect button is the way out of it.
    /// </summary>
    [Fact]
    public async Task SwitchDeviceAsync_ToHardwareWithNoDeviceAttached_LeavesAnOrdinaryDisconnectedSession()
    {
        var session = CreateSession(new FakeFactory(DeviceKind.Simulated, hardwareIsMissing: true));
        await session.ConnectAsync(CancellationToken.None);

        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);

        var status = session.GetStatus();
        Assert.Equal("Hardware", status.DeviceKind);
        Assert.False(status.Connected);

        // And back again, which must reconnect without a Connect call.
        await session.SwitchDeviceAsync(DeviceKind.Simulated, CancellationToken.None);
        Assert.True(session.GetStatus().Connected);
    }

    /// <summary>
    /// Refused rather than obeyed while a measurement is running. Every runner in the app holds the
    /// session across its whole sweep, so pulling the device out from under one would surface as an
    /// unexplained mid-run failure rather than as the deliberate act it was.
    /// </summary>
    [Fact]
    public async Task SwitchDeviceAsync_WhileAcquiring_IsRefused()
    {
        var session = CreateSession();
        await session.ConnectAsync(CancellationToken.None);
        await session.StartAcquisitionAsync(new AcquisitionConfig(48_000, 2, 1024), CancellationToken.None);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None));
            Assert.Contains("Stop the running measurement", ex.Message);

            // And it really was refused, not half-applied.
            Assert.Equal("Simulated", session.GetStatus().DeviceKind);
            Assert.True(session.GetStatus().Connected);
        }
        finally
        {
            await session.StopAcquisitionAsync();
        }
    }

    /// <summary>
    /// The simulator's own knobs belong to the device, not to the session, so a switch must not
    /// carry them across — a fresh device starts on its own defaults.
    /// </summary>
    [Fact]
    public async Task SwitchDeviceAsync_StartsTheNewDeviceOnItsOwnDefaults()
    {
        var session = CreateSession();
        await session.ConnectAsync(CancellationToken.None);
        session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: false, dut: SimulatedDutModel.Loudspeaker);
        Assert.Equal(SimulatedDutModel.Loudspeaker, session.SimulatedDut);

        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);
        await session.SwitchDeviceAsync(DeviceKind.Simulated, CancellationToken.None);

        Assert.Equal(SimulatedDutModel.StraightWire, session.SimulatedDut);
        Assert.True(session.SimulatedNoiseEnabled);
    }

    /// <summary>
    /// A session built around one already-constructed device — which is how every other test in this
    /// project builds one — has nothing to switch to, and says so rather than silently handing the
    /// same device back under a new name.
    /// </summary>
    [Fact]
    public async Task SwitchDeviceAsync_OnAPinnedSession_IsRefused()
    {
        var session = new DeviceSessionService(
            new SimulatedDevice(),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None));

        Assert.Contains("pinned", ex.Message);
    }
}
