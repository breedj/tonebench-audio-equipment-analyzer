using System.Runtime.CompilerServices;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// A simulated DUT with a real corner: the simulator's signal, low-passed on the left channel only.
///
/// <para>Shared by the chirp and stepped-sine response tests. Both need it for the same second
/// reason as well — it carries <b>its own ranging state</b>, because
/// <see cref="SimulatedDevice"/> hard-returns <c>AutoRangeEnabled</c> false with a no-op setter
/// (it has no ranger to speak of), and a runner's "take over the input range and suppress
/// step-down" path is otherwise never entered at all.</para>
/// </summary>
internal sealed class OnePoleLowPassDevice(SimulatedDevice inner, double cornerHz) : IAnalyzerDevice
{
    private readonly double _alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / 48_000.0);
    private double _state;

    public DeviceCapabilities Capabilities => inner.Capabilities;

    public double InputRangeDbv => inner.InputRangeDbv;

    public long DroppedRxChunks => inner.DroppedRxChunks;

    public long DroppedBlocks => inner.DroppedBlocks;

    // Its own ranging state rather than the simulator's: SimulatedDevice hard-returns
    // AutoRangeEnabled false with a no-op setter, because it has no ranger to speak of. This
    // class stands in for hardware that does, which is the only way to exercise the runner's
    // "take over the input range and suppress step-down" path at all.
    public bool AutoRangeEnabled { get; set; } = true;

    public bool AutoRangeStepDownEnabled { get; set; } = true;

    public double AutoRangeStepDownDelayMs
    {
        get => inner.AutoRangeStepDownDelayMs;
        set => inner.AutoRangeStepDownDelayMs = value;
    }

    public event EventHandler? DeviceLost
    {
        add => inner.DeviceLost += value;
        remove => inner.DeviceLost -= value;
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);

    public Task<bool> PingAsync(CancellationToken ct) => inner.PingAsync(ct);

    public int StimulusLatencyFrames(double sampleRateHz) => inner.StimulusLatencyFrames(sampleRateHz);

    public Task SetInputRangeAsync(double dbv, CancellationToken ct) => inner.SetInputRangeAsync(dbv, ct);

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => inner.SetOutputRangeAsync(dbv, ct);

    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => inner.ConfigureGeneratorAsync(config, ct);

    public Task StopAsync() => inner.StopAsync();

    public async IAsyncEnumerable<PcmBlock> StreamAsync(
        AcquisitionConfig config,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var block in inner.StreamAsync(config, ct).WithCancellation(ct))
        {
            // Left is interleaved slot 0; filter it in place and pass the block straight on.
            for (int i = 0; i < block.SampleCount; i += block.ChannelCount)
            {
                _state += _alpha * (block.Samples[i] - _state);
                block.Samples[i] = (float)_state;
            }

            yield return block;
        }
    }
}
