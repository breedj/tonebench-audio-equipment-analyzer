using System.Runtime.CompilerServices;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// A simulated DUT whose impulse response is exactly what the test says it is: the simulator's
/// signal convolved, on the left channel only, with a given set of FIR taps.
///
/// <para>Modelled on <see cref="OnePoleLowPassDevice"/>, and carrying its own ranging state for the
/// same reason — <see cref="SimulatedDevice"/> hard-returns <c>AutoRangeEnabled</c> false with a
/// no-op setter, so a runner's "take over the input range" path is otherwise never entered.</para>
///
/// <para>Three things here are easy to get wrong and each one silently invalidates the test that
/// uses it. The history buffer advances once per <b>frame</b>, not once per sample, because samples
/// are interleaved. Only interleave slot 0 is filtered, so the right channel stays a clean copy of
/// the stimulus and a run using it as the reference is not quietly testing itself against itself.
/// And the state carries across blocks, so the convolution is continuous rather than restarting
/// every 512 frames — a per-block reset would put a step at every block boundary and read as
/// broadband noise.</para>
///
/// <para><b>Keep the taps short.</b> This is a direct convolution: <c>taps.Length</c> multiplies per
/// frame, so a few hundred taps at 48 kHz is comfortable and a few thousand at 192 kHz will not keep
/// up with the stream. Claims about accuracy belong in <c>Tonebench.Dsp.Tests</c>, where there is no
/// device and the length is free; this class is for proving the runner's plumbing.</para>
/// </summary>
internal sealed class KnownImpulseResponseDevice(SimulatedDevice inner, double[] taps) : IAnalyzerDevice
{
    private readonly double[] _history = new double[taps.Length];
    private int _next;

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

    /// <summary>The convolution of two tap sets — for cascading a stand-in analyzer chain with a DUT.</summary>
    public static double[] Convolve(double[] a, double[] b)
    {
        var result = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                result[i + j] += a[i] * b[j];
        return result;
    }

    public async IAsyncEnumerable<PcmBlock> StreamAsync(
        AcquisitionConfig config,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var block in inner.StreamAsync(config, ct).WithCancellation(ct))
        {
            for (int i = 0; i < block.SampleCount; i += block.ChannelCount)
            {
                _history[_next] = block.Samples[i];

                double sum = 0.0;
                int index = _next;
                for (int tap = 0; tap < taps.Length; tap++)
                {
                    sum += taps[tap] * _history[index];
                    index = index == 0 ? _history.Length - 1 : index - 1;
                }

                block.Samples[i] = (float)sum;
                _next = _next == _history.Length - 1 ? 0 : _next + 1;
            }

            yield return block;
        }
    }
}
