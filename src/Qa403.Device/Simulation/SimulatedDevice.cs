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

    // GeneratorLeft/Right = Off (not the type's own null-means-legacy-demo-tone default — see
    // SimulatedSignalProfile.GeneratorLeft) so this device's out-of-the-box state genuinely
    // matches _generatorConfig's Off default below: no generator tone until ConfigureGeneratorAsync
    // says otherwise, same as the UI's "Generator enabled" checkbox starting unchecked.
    private volatile SimulatedSignalProfile _profile = new()
    {
        GeneratorLeft = ChannelGeneratorConfig.Off,
        GeneratorRight = ChannelGeneratorConfig.Off,
    };

    // Written from whichever thread calls Set*RangeAsync, read by the streaming loop on another.
    // C# forbids `volatile` on double, so these go through Volatile.Read/Write instead — without
    // that, nothing stops the JIT from hoisting the read out of the per-block loop and a range
    // change made during an acquisition would never take effect.
    private double _inputRangeDbv = 12.0;
    private double _outputRangeDbv = 8.0;
    private GeneratorConfig _generatorConfig = GeneratorConfig.Off;
    private volatile bool _stopRequested;
    private bool _connected;

    public SimulatedDevice(int? randomSeed = null) =>
        _random = randomSeed is { } seed ? new Random(seed) : new Random();

    public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

    /// <summary>Never raised: the simulator has no physical device to lose.</summary>
    public event EventHandler? DeviceLost { add { } remove { } }

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

    /// <summary>There is no cable to pull: the simulator is reachable for exactly as long as it has been connected.</summary>
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(_connected);

    /// <inheritdoc />
    public double InputRangeDbv => Volatile.Read(ref _inputRangeDbv);

    /// <summary>The simulator generates blocks on demand rather than off a bounded USB queue, so it has nothing to drop.</summary>
    public long DroppedRxChunks => 0;

    /// <inheritdoc />
    public long DroppedBlocks => 0;

    /// <summary>
    /// Zero: each block is synthesized from the generator config as it is handed out, so a change is
    /// audible in the very next block and nothing is ever committed ahead. This is precisely why the
    /// post-change skirt that real hardware shows cannot be reproduced against the simulator — see
    /// <see cref="IAnalyzerDevice.StimulusLatencyFrames"/>.
    /// </summary>
    public int StimulusLatencyFrames(double sampleRateHz) => 0;

    /// <summary>
    /// The simulator has no autoranging, so this stays false however it is set — the UI reads it to
    /// decide whether to offer the auto/manual checkbox at all.
    /// </summary>
    public bool AutoRangeEnabled { get => false; set { } }

    /// <summary>
    /// The simulator has no autoranging of its own (see <see cref="AutoRangeEnabled"/>), so this
    /// value is never read — stored rather than ignored purely so a caller reading it straight back
    /// after setting it doesn't see a surprising discard.
    /// </summary>
    public double AutoRangeStepDownDelayMs { get; set; } = Hardware.Qa40xAutoRanger.DefaultStepDownDelayMs;

    public Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(Capabilities.InputRangesDbv, dbv, nameof(dbv));
        Volatile.Write(ref _inputRangeDbv, dbv);
        return Task.CompletedTask;
    }

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(Capabilities.OutputRangesDbv, dbv, nameof(dbv));
        Volatile.Write(ref _outputRangeDbv, dbv);
        return Task.CompletedTask;
    }

    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
    {
        _generatorConfig = config ?? throw new ArgumentNullException(nameof(config));

        // Store the per-channel config as-is: the synthesizer honours Enabled and Kind itself.
        // Never null here — null means "never configured" and falls back to the fixed demo tone
        // (see SimulatedSignalProfile.GeneratorLeft).
        _profile = _profile with
        {
            GeneratorLeft = config.Left,
            GeneratorRight = config.Right,
        };

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

            // Two independent clip points, one per end of the signal chain: the generator's own
            // DAC full scale, and the analyzer's ADC full scale. See the per-sample loop below.
            double outputClipPeakV = SignalSynthesizer.DbvToPeakV(Volatile.Read(ref _outputRangeDbv));
            double inputClipPeakV = SignalSynthesizer.DbvToPeakV(Volatile.Read(ref _inputRangeDbv));
            int sampleCount = config.BlockFrameCount * config.ChannelCount;
            float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);

            for (int frame = 0; frame < config.BlockFrameCount; frame++)
            {
                double t = (frameIndex + frame) / config.SampleRateHz;

                // Hum/noise are drawn once per frame and shared across channels (same as before);
                // only the generator tones differ per channel (see ComputeChannelSample).
                double sharedAdditive = synthesizer.ComputeSharedAdditive(profile, t, config.SampleRateHz);

                for (int ch = 0; ch < config.ChannelCount; ch++)
                {
                    // Signal chain order matters: the generator's output is hard-clipped at the
                    // DAC's full scale first (asking for a level above the selected output range
                    // truncates the waveform, exactly as real hardware does), then the DUT's hum
                    // and noise are added downstream of it, and only then does the whole thing meet
                    // the ADC's soft input saturation. Hum/noise are not products of the DAC, so
                    // they deliberately sit outside the output clip.
                    double generated = SignalSynthesizer.HardClip(
                        synthesizer.ComputeChannelSample(profile, t, ch), outputClipPeakV);

                    double sample = SignalSynthesizer.SoftClip(generated + sharedAdditive, inputClipPeakV);
                    buffer[(frame * config.ChannelCount) + ch] = (float)sample;
                }
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

    private static void RequireSupportedRange(IReadOnlyList<double> supported, double value, string paramName)
    {
        if (!supported.Contains(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"Unsupported range. Supported values: {string.Join(", ", supported)} dBV.");
        }
    }
}
