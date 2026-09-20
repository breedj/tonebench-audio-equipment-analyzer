using System.Runtime.CompilerServices;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// A simulated stereo DUT with a known, flat, possibly asymmetric coupling between its channels: a
/// copy of each channel's sample, scaled by the coupling, is added to the other. The two figures
/// differ so a test can tell which direction a pass measured. Carries its own ranging state for the
/// reason <see cref="OnePoleLowPassDevice"/> does.
/// </summary>
internal sealed class CrosstalkDevice(SimulatedDevice inner, double leftToRightDb, double rightToLeftDb) : IAnalyzerDevice
{
    private readonly float _kLeftToRight = (float)Math.Pow(10.0, leftToRightDb / 20.0);
    private readonly float _kRightToLeft = (float)Math.Pow(10.0, rightToLeftDb / 20.0);

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
            if (block.ChannelCount >= 2)
            {
                for (int i = 0; i < block.SampleCount; i += block.ChannelCount)
                {
                    float left = block.Samples[i];
                    float right = block.Samples[i + 1];
                    block.Samples[i + 1] = right + (left * _kLeftToRight);
                    block.Samples[i] = left + (right * _kRightToLeft);
                }
            }

            yield return block;
        }
    }
}
