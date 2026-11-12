using System.Buffers;
using System.Runtime.CompilerServices;

namespace Tonebench.Device.Tests.Split;

/// <summary>
/// A scriptable <see cref="IAnalyzerDevice"/> for the composite's tests: it records what was routed
/// to it and can be told how many blocks to yield, when to fault, and what to report.
///
/// <para>Two fakes rather than two simulators, because what is under test is <i>routing</i> — which
/// half received each call — and a <c>SimulatedDevice</c> answers plausibly for both sides, so a
/// member wired to the wrong one would still look right.</para>
/// </summary>
internal sealed class FakeAnalyzerDevice : IAnalyzerDevice, IDisposable
{
    private readonly string _name;

    public FakeAnalyzerDevice(string name) => _name = name;

    public DeviceCapabilities Capabilities { get; set; } = DeviceCapabilities.Qa403;

    public DeviceTelemetry? Telemetry { get; set; }

    public bool HasInputAutoRanging { get; set; } = true;

    public double MinStimulusFrequencyHz { get; set; }

    public double MaxGeneratorSampleRateHz { get; set; } = double.PositiveInfinity;

    public IStimulusTap? StimulusTap { get; set; }

    public double InputRangeDbv { get; set; }

    public bool AutoRangeEnabled { get; set; }

    public double AutoRangeStepDownDelayMs { get; set; }

    public bool AutoRangeStepDownEnabled { get; set; }

    public long DroppedRxChunks { get; set; }

    public long DroppedBlocks { get; set; }

    /// <summary>What <see cref="StimulusLatencyFrames"/> answers, at whatever rate it is asked.</summary>
    public int StimulusLatency { get; set; }

    /// <summary>What <see cref="StreamStartSettleFrames"/> answers, at whatever rate it is asked.</summary>
    public int SettleFrames { get; set; }

    // ---- what was routed here ----

    public int Connects { get; private set; }

    public int Pings { get; private set; }

    public int Stops { get; private set; }

    public bool Disposed { get; private set; }

    public List<GeneratorConfig> Generators { get; } = [];

    public List<double> InputRangeWrites { get; } = [];

    public List<double> OutputRangeWrites { get; } = [];

    public AcquisitionConfig? StreamedWith { get; private set; }

    public List<double> LatencyAskedAt { get; } = [];

    public List<double> SettleAskedAt { get; } = [];

    /// <summary>Every block this fake handed out, so a test can assert they were disposed.</summary>
    public List<PcmBlock> Produced { get; } = [];

    // ---- scripting ----

    public bool PingAnswer { get; set; } = true;

    /// <summary>What this half claims to be. The composite ANDs the two, so a test needs both.</summary>
    public bool IsSimulated { get; set; }

    public Exception? ConnectFault { get; set; }

    /// <summary>Thrown from the stream after <see cref="FaultAfterBlocks"/> blocks, as a device loss would be.</summary>
    public Exception? StreamFault { get; set; }

    public int FaultAfterBlocks { get; set; }

    /// <summary>How many blocks to hand out. Zero is a playback-only card, which yields none at all.</summary>
    public int BlocksToYield { get; set; }

    public int BlockFrames { get; set; } = 1024;

    /// <summary>Whether to stay open after the scripted blocks, the way a real stream does.</summary>
    public bool HoldOpen { get; set; } = true;

    /// <summary>
    /// Awaited before the first block, when set, so a test can decide the order two concurrent
    /// streams do things in rather than hope for it.
    /// </summary>
    public TaskCompletionSource? StartGate { get; set; }

    public event EventHandler? DeviceLost;

    public void RaiseDeviceLost() => DeviceLost?.Invoke(this, EventArgs.Empty);

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct)
    {
        Connects++;
        if (ConnectFault is not null)
            return Task.FromException<DeviceInfo>(ConnectFault);

        return Task.FromResult(new DeviceInfo($"{_name} product", $"{_name}-serial", $"{_name}-fw", IsSimulated));
    }

    public Task<bool> PingAsync(CancellationToken ct)
    {
        Pings++;
        return Task.FromResult(PingAnswer);
    }

    public int StimulusLatencyFrames(double sampleRateHz)
    {
        LatencyAskedAt.Add(sampleRateHz);
        return StimulusLatency;
    }

    public int StreamStartSettleFrames(double sampleRateHz)
    {
        SettleAskedAt.Add(sampleRateHz);
        return SettleFrames;
    }

    public Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        InputRangeWrites.Add(dbv);
        return Task.CompletedTask;
    }

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        OutputRangeWrites.Add(dbv);
        return Task.CompletedTask;
    }

    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
    {
        Generators.Add(config);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<PcmBlock> StreamAsync(
        AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        StreamedWith = config;

        if (StartGate is not null)
            await StartGate.Task.WaitAsync(ct).ConfigureAwait(false);

        for (int index = 0; index < BlocksToYield; index++)
        {
            if (StreamFault is not null && index == FaultAfterBlocks)
                throw StreamFault;

            int samples = BlockFrames * config.ChannelCount;
            var block = new PcmBlock(
                ArrayPool<float>.Shared.Rent(samples), samples, config.SampleRateHz,
                config.ChannelCount, index, index);

            Produced.Add(block);
            yield return block;
        }

        if (StreamFault is not null && FaultAfterBlocks >= BlocksToYield)
            throw StreamFault;

        if (HoldOpen)
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        Stops++;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}
