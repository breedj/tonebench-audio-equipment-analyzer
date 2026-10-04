using System.Runtime.CompilerServices;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// A simulated amplifier with a purely resistive output impedance and a load the test connects or
/// removes: with <see cref="LoadOhms"/> set, the left channel is scaled by the divider
/// <c>R_L / (R_L + R_out)</c>; with it null (open) the channel passes straight through. Resistive so
/// the divider is a scalar and exact at every frequency, which is what a magnitude-only test needs.
/// A reactive stage is the simulator's own <see cref="SimulatedOutputStage"/>. Carries its own
/// ranging state for the reason <see cref="OnePoleLowPassDevice"/> does.
/// </summary>
internal sealed class OutputStageDevice(SimulatedDevice inner, double outputOhms) : IAnalyzerDevice
{
    /// <summary>The load connected to the amplifier's output, or null for open. Read once per block, so a change lands on a block boundary.</summary>
    public double? LoadOhms { get; set; }

    public DeviceCapabilities Capabilities => inner.Capabilities;

    public double InputRangeDbv => inner.InputRangeDbv;

    public long DroppedRxChunks => inner.DroppedRxChunks;

    public long DroppedBlocks => inner.DroppedBlocks;

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
            if (LoadOhms is { } load)
            {
                float k = (float)(load / (load + outputOhms));
                for (int i = 0; i < block.SampleCount; i += block.ChannelCount)
                    block.Samples[i] *= k;
            }

            yield return block;
        }
    }
}
