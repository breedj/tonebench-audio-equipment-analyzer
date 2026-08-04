using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Qa403.Device.Simulation;

/// <summary>
/// <see cref="IAnalyzerDevice"/> backed by a synthesized signal instead of real hardware. Good
/// enough to develop and test the rest of the app against: configurable fundamental/harmonics,
/// mains hum, white+1/f noise, soft input clipping and two-tone IMD. See
/// <see cref="SimulatedSignalProfile"/> for what's tunable and <see cref="SignalSynthesizer"/>
/// for the per-sample math.
/// </summary>
public sealed class SimulatedDevice : IAnalyzerDevice
{
    private readonly Random _random;
    private volatile SimulatedSignalProfile _profile = new();
    private double _inputRangeDbv = 12.0;
    private double _outputRangeDbv = 8.0;
    private GeneratorConfig _generatorConfig = GeneratorConfig.Off;
    private volatile bool _stopRequested;
    private bool _connected;

    public SimulatedDevice(int? randomSeed = null) =>
        _random = randomSeed is { } seed ? new Random(seed) : new Random();

    public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

    /// <summary>
    /// Shapes the simulated input signal. Not part of <see cref="IAnalyzerDevice"/> — real
    /// hardware has no equivalent knob. Reassigning it takes effect at the start of the next
    /// emitted block; in-flight blocks are unaffected.
    /// </summary>
    public SimulatedSignalProfile SignalProfile
    {
        get => _profile;
        set => _profile = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct)
    {
        _connected = true;
        return Task.FromResult(new DeviceInfo("QA403", "SIM-0001", "sim-1.0", IsSimulated: true));
    }

    public Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(Capabilities.InputRangesDbv, dbv, nameof(dbv));
        _inputRangeDbv = dbv;
        return Task.CompletedTask;
    }

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(Capabilities.OutputRangesDbv, dbv, nameof(dbv));
        _outputRangeDbv = dbv;
        return Task.CompletedTask;
    }

    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
    {
        _generatorConfig = config ?? throw new ArgumentNullException(nameof(config));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_connected)
            throw new InvalidOperationException("Device is not connected. Call ConnectAsync first.");
        if (config.SampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "SampleRateHz must be positive.");
        if (config.ChannelCount < 1 || config.ChannelCount > Capabilities.MaxChannelCount)
            throw new ArgumentOutOfRangeException(nameof(config), $"ChannelCount must be between 1 and {Capabilities.MaxChannelCount}.");
        if (config.BlockFrameCount < 1)
            throw new ArgumentOutOfRangeException(nameof(config), "BlockFrameCount must be positive.");

        _stopRequested = false;

        var synthesizer = new SignalSynthesizer(_random);
        var period = TimeSpan.FromSeconds(config.BlockFrameCount / config.SampleRateHz);
        using var timer = new PeriodicTimer(period);

        long frameIndex = 0;
        long sequenceNumber = 0;

        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            // Real-time cadence, not "as fast as possible": the periodic timer is what makes
            // this exercise the streaming pipeline's backpressure like a real device would.
            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                yield break;

            var profile = _profile;
            double clipPeakV = InputRangeToClipPeakV(_inputRangeDbv);
            int sampleCount = config.BlockFrameCount * config.ChannelCount;
            float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);

            for (int frame = 0; frame < config.BlockFrameCount; frame++)
            {
                double t = (frameIndex + frame) / config.SampleRateHz;
                double sample = synthesizer.NextSample(profile, t, config.SampleRateHz);
                sample = SignalSynthesizer.SoftClip(sample, clipPeakV);

                float sampleF = (float)sample;
                // The simulator models one input signal, duplicated across channels.
                // Per-channel differentiation is not needed to develop the UI in Phase 3.
                for (int ch = 0; ch < config.ChannelCount; ch++)
                    buffer[(frame * config.ChannelCount) + ch] = sampleF;
            }

            frameIndex += config.BlockFrameCount;

            yield return new PcmBlock(buffer, sampleCount, config.SampleRateHz, config.ChannelCount, sequenceNumber++, Stopwatch.GetTimestamp());
        }
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        return Task.CompletedTask;
    }

    private static double InputRangeToClipPeakV(double inputRangeDbv) => SignalSynthesizer.DbvToPeakV(inputRangeDbv);

    private static void RequireSupportedRange(IReadOnlyList<double> supported, double value, string paramName)
    {
        if (!supported.Contains(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"Unsupported range. Supported values: {string.Join(", ", supported)} dBV.");
        }
    }
}
