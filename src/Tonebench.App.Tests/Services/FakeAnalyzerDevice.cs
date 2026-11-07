using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// A <see cref="SimulatedDevice"/> wrapper that can also be absent or unplugged — the two states the
/// simulator has no way to be, and the two the app now has to survive. Real hardware signals both
/// through exceptions from deep inside LibUsbDotNet, so what matters here is only the shape the app
/// sees: a failing <see cref="ConnectAsync"/>, and a <see cref="Qa40xDeviceLostException"/> preceded
/// by <see cref="DeviceLost"/> — the order <see cref="Qa40xUsbDevice"/> itself uses, since it raises
/// the event on its way out of the failing operation.
/// </summary>
internal class FakeAnalyzerDevice : IAnalyzerDevice
{
    private readonly SimulatedDevice _inner = new();
    private EventHandler? _deviceLost;

    /// <summary>Thrown by the next <see cref="ConnectAsync"/> when set; null lets it succeed.</summary>
    public Exception? ConnectFailure { get; set; }

    public int ConnectAttempts { get; private set; }

    public int DeviceLostSubscribers { get; private set; }

    /// <summary>Set to simulate the cable being pulled: every device operation then fails as lost, and the ping answers false.</summary>
    public bool Unplugged { get; set; }

    public event EventHandler? DeviceLost
    {
        add { _deviceLost += value; DeviceLostSubscribers++; }
        remove { _deviceLost -= value; DeviceLostSubscribers--; }
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct)
    {
        ConnectAttempts++;
        if (ConnectFailure is { } failure)
            return Task.FromException<DeviceInfo>(failure);

        Unplugged = false;
        return _inner.ConnectAsync(ct);
    }

    public virtual Task<bool> PingAsync(CancellationToken ct)
    {
        if (!Unplugged)
            return _inner.PingAsync(ct);

        RaiseDeviceLost();
        return Task.FromResult(false);
    }

    public virtual Task SetInputRangeAsync(double dbv, CancellationToken ct) =>
        Unplugged ? FailAsLost("input range write") : _inner.SetInputRangeAsync(dbv, ct);

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct) =>
        Unplugged ? FailAsLost("output range write") : _inner.SetOutputRangeAsync(dbv, ct);

    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) =>
        Unplugged ? FailAsLost("generator config") : _inner.ConfigureGeneratorAsync(config, ct);

    /// <summary>
    /// The simulator's own knobs, so a test can give the signal a loud hum or turn the noise off and
    /// still have a device with a <i>ranger</i> — which the simulator on its own does not have, and
    /// which is the whole reason the ranging tests go through this wrapper.
    /// </summary>
    public SimulatedSignalProfile SignalProfile
    {
        get => _inner.SignalProfile;
        set => _inner.SignalProfile = value;
    }

    public DeviceCapabilities Capabilities => _inner.Capabilities;

    public double InputRangeDbv => _inner.InputRangeDbv;

    /// <summary>
    /// Autoranging state is held here rather than delegated to <see cref="_inner"/>: the simulator
    /// hardwires <see cref="SimulatedDevice.AutoRangeEnabled"/> to false because it has no ranger,
    /// while this stands in for a device that does — and callers key off that flag to decide whether
    /// they may touch the input range at all (see <c>ThdSweepRunner</c>). Defaults match
    /// <see cref="Qa40xUsbDevice"/>'s.
    /// </summary>
    public bool AutoRangeEnabled { get; set; } = true;

    // The hardware default, spelled out: Qa40xAutoRanger is internal to Tonebench.Device.
    public double AutoRangeStepDownDelayMs { get; set; } = 1000.0;

    public virtual bool AutoRangeStepDownEnabled { get; set; } = true;

    public long DroppedRxChunks => _inner.DroppedRxChunks;

    public long DroppedBlocks => _inner.DroppedBlocks;

    public int StimulusLatencyFrames(double sampleRateHz) => _inner.StimulusLatencyFrames(sampleRateHz);

    public IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct) => _inner.StreamAsync(config, ct);

    public Task StopAsync() => _inner.StopAsync();

    private Task FailAsLost(string phase)
    {
        RaiseDeviceLost();
        return Task.FromException(new Qa40xDeviceLostException($"QA40x {phase} failed with Io — device appears disconnected."));
    }

    private void RaiseDeviceLost() => _deviceLost?.Invoke(this, EventArgs.Empty);
}
