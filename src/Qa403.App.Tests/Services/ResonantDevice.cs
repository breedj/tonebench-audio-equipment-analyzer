using System.Runtime.CompilerServices;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// A DUT with a peak <b>above</b> its passband: a one-pole roll-off with a resonance sitting on the
/// shoulder of it, tall enough to be the whole trace's maximum.
///
/// <para>This is an output transformer, in miniature. Its leakage inductance resonates against the
/// winding capacitance somewhere above the audio band, and on a real amplifier that peak is
/// routinely several dB above the passband — which is what makes a peak-referenced bandwidth figure
/// measure the resonance instead of the amplifier. Scaled down here because the test harness runs at
/// a 24 kHz span: the passband corner is at 10 kHz and the resonance at 9 kHz, which reproduces the
/// same geometry inside a 48 kHz sample rate.</para>
///
/// <para>Left channel only, like <see cref="OnePoleLowPassDevice"/> — the right input stays the
/// straight reference the "right channel is reference" mode assumes — and carrying its own ranging
/// state for the same reason that class does.</para>
/// </summary>
internal sealed class ResonantDevice : IAnalyzerDevice
{
    private const double SampleRateHz = 48_000.0;

    private readonly SimulatedDevice _inner;
    private readonly double _alpha;

    // Direct form I, which needs no normalisation gymnastics and is exact enough for a test fixture.
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _lowPassState;
    private double _x1, _x2, _y1, _y2;

    public ResonantDevice(SimulatedDevice inner, double cornerHz, double resonanceHz, double resonanceQ = 3.5, double resonanceDb = 6.0)
    {
        _inner = inner;
        _alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / SampleRateHz);

        // RBJ peaking EQ.
        double a = Math.Pow(10.0, resonanceDb / 40.0);
        double w0 = 2.0 * Math.PI * resonanceHz / SampleRateHz;
        double alpha = Math.Sin(w0) / (2.0 * resonanceQ);
        double cosW0 = Math.Cos(w0);

        double a0 = 1.0 + (alpha / a);
        _b0 = (1.0 + (alpha * a)) / a0;
        _b1 = -2.0 * cosW0 / a0;
        _b2 = (1.0 - (alpha * a)) / a0;
        _a1 = -2.0 * cosW0 / a0;
        _a2 = (1.0 - (alpha / a)) / a0;
    }

    public DeviceCapabilities Capabilities => _inner.Capabilities;

    public double InputRangeDbv => _inner.InputRangeDbv;

    public long DroppedRxChunks => _inner.DroppedRxChunks;

    public long DroppedBlocks => _inner.DroppedBlocks;

    public bool AutoRangeEnabled { get; set; } = true;

    public bool AutoRangeStepDownEnabled { get; set; } = true;

    public double AutoRangeStepDownDelayMs
    {
        get => _inner.AutoRangeStepDownDelayMs;
        set => _inner.AutoRangeStepDownDelayMs = value;
    }

    public event EventHandler? DeviceLost
    {
        add => _inner.DeviceLost += value;
        remove => _inner.DeviceLost -= value;
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);

    public Task<bool> PingAsync(CancellationToken ct) => _inner.PingAsync(ct);

    public int StimulusLatencyFrames(double sampleRateHz) => _inner.StimulusLatencyFrames(sampleRateHz);

    public Task SetInputRangeAsync(double dbv, CancellationToken ct) => _inner.SetInputRangeAsync(dbv, ct);

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => _inner.SetOutputRangeAsync(dbv, ct);

    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => _inner.ConfigureGeneratorAsync(config, ct);

    public Task StopAsync() => _inner.StopAsync();

    public async IAsyncEnumerable<PcmBlock> StreamAsync(
        AcquisitionConfig config,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var block in _inner.StreamAsync(config, ct).WithCancellation(ct))
        {
            // Left is interleaved slot 0; filter it in place and pass the block straight on.
            for (int i = 0; i < block.SampleCount; i += block.ChannelCount)
            {
                _lowPassState += _alpha * (block.Samples[i] - _lowPassState);

                double x = _lowPassState;
                double y = (_b0 * x) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
                _x2 = _x1;
                _x1 = x;
                _y2 = _y1;
                _y1 = y;

                block.Samples[i] = (float)y;
            }

            yield return block;
        }
    }
}
