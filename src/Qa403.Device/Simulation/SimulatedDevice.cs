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

    // The block size the running stream hands out, which is also this device's stimulus latency
    // (see StimulusLatencyFrames). Written by StreamAsync on its own thread, read by the session
    // on the caller's, hence volatile.
    private volatile int _activeBlockFrameCount = AcquisitionConfig.DefaultBlockFrameCount;
    private volatile bool _stopRequested;

    /// <summary>
    /// Where the generator's own output is copied while the stream recorder is armed, or null.
    /// Implemented here and not only on hardware because this is the device the recording itself is
    /// developed and tested against -- see <see cref="IStimulusTap"/>.
    /// </summary>
    private volatile IStimulusTap? _stimulusTap;
    private bool _connected;

    public SimulatedDevice(int? randomSeed = null) =>
        _random = randomSeed is { } seed ? new Random(seed) : new Random();

    /// <summary>
    /// Mirrors <c>Qa40xUsbDevice.Allow384kHz</c>, so Configuration's experimental checkbox, the
    /// sample-rate dropdown it feeds and the generator refusal below can all be exercised under
    /// <c>--simulate</c> with no QA403 attached — the "Testing without hardware" rule. A
    /// <see cref="Func{T}"/> for the same startup-ordering reason the hardware device uses one.
    /// </summary>
    public Func<bool>? Allow384kHz { get; init; }

    public DeviceCapabilities Capabilities =>
        Allow384kHz?.Invoke() == true ? DeviceCapabilities.Qa403Experimental : DeviceCapabilities.Qa403;

    /// <summary>
    /// 192 kHz, copying the hardware's limit although nothing here would actually misbehave above
    /// it. The simulator exists to stand in for the QA403, and a simulator that happily generates at
    /// 384 kHz would make the one screen this setting affects behave differently on the two devices —
    /// which is precisely the difference a user would then meet for the first time on real hardware,
    /// where the consequence is the output protection relay cycling.
    /// </summary>
    public double MaxGeneratorSampleRateHz => 192_000.0;

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
    /// One block. <see cref="StreamAsync"/> reads the profile and both ranges <i>once</i>, at the
    /// top of each block, and then synthesizes the whole block from that reading — so a change that
    /// lands while a block is being built is not in that block, and the block is still handed out
    /// after the change returned. That is a block of audio committed ahead of the change, which is
    /// exactly what this property describes for the real device (its DAC queue), just one block
    /// deep instead of a queue.
    ///
    /// <para>This used to return 0 on the argument that "a change is audible in the very next
    /// block", which is true and beside the point: the block in progress is the one that matters,
    /// and every stimulus-driven flush trusts this number to know how much of it to throw away.
    /// Reproduced on a THD-vs-frequency sweep under adaptive resolution, where the 4096-point
    /// records at the top of the sweep are one block long: roughly one point in five read the
    /// <i>previous</i> point's tone — the whole record was the stale block — and with four averages
    /// that diluted to a level 0.6–1.2 dB low with the SNR down 20–40 dB. Invisible at the fixed
    /// 131072-point size, where the same block is 3 % of a record under a window that weights its
    /// ends toward zero. Guarded by
    /// <c>SimulatedDeviceTests.ConfigureGeneratorAsync_StaleAudioAfterItReturns_IsCoveredByStimulusLatencyFrames</c>.</para>
    ///
    /// <para>The sample rate is irrelevant here — the latency is a block whatever the rate — and the
    /// block size is the running stream's, falling back to the default while idle, which only
    /// matters for a flush that precedes a restart anyway.</para>
    /// </summary>
    public int StimulusLatencyFrames(double sampleRateHz) => _activeBlockFrameCount;

    /// <summary>
    /// There is no ranger here, so the session reports <c>AutoRangeEnabled</c> as null rather than
    /// false and the UI hides the auto/manual control. Declared rather than inferred from the type:
    /// the app's test doubles wrap this device while standing in for hardware that <i>does</i>
    /// range, and they keep the interface's <c>true</c> default.
    /// </summary>
    public bool HasInputAutoRanging => false;

    /// <inheritdoc />
    public IStimulusTap? StimulusTap
    {
        get => _stimulusTap;
        set => _stimulusTap = value;
    }

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

    /// <summary>Stored and never read, for the same reason as <see cref="AutoRangeStepDownDelayMs"/> — there is no ranger here to suppress.</summary>
    public bool AutoRangeStepDownEnabled { get; set; } = true;

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
        _activeBlockFrameCount = config.BlockFrameCount;

        var synthesizer = new SignalSynthesizer(_random, config.SampleRateHz);

        // Built here rather than per block, and rebuilt below only when the model changes: the
        // filters are stateful, and a chain rebuilt every block would restart from rest 40 times a
        // second and never produce a decay at all.
        var dut = SimulatedDut.For(_profile.Dut, config.SampleRateHz);
        // Built per stream, at this stream's rate, and only while the knob is on: the unweighted
        // default path must not pay for a filter it never uses.
        SimulatedCrosstalk? crosstalk = _profile.CrosstalkEnabled ? new SimulatedCrosstalk(config.SampleRateHz) : null;
        // Likewise per stream: a divider whose state must carry across blocks, rebuilt only when a knob moves.
        var outputStage = new SimulatedOutputStage(_profile.OutputStage, _profile.Load, config.SampleRateHz);

        var period = TimeSpan.FromSeconds(config.BlockFrameCount / config.SampleRateHz);
        using var timer = new PeriodicTimer(period);

        long frameIndex = 0;
        long sequenceNumber = 0;

        // The profile as last seen and its harmonic-free twin, so the `with` below runs once per
        // profile change rather than once per block.
        SimulatedSignalProfile? distortingSource = null;
        SimulatedSignalProfile? distortionFree = null;

        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            // Real-time cadence, not "as fast as possible": the periodic timer is what makes
            // this exercise the streaming pipeline's backpressure like a real device would.
            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                yield break;

            // Read once per block, as are the two ranges below, and everything in this block follows
            // from that reading. A change made from another thread part-way through therefore misses
            // this block and lands in the next -- which is the one block of latency
            // StimulusLatencyFrames reports. Do not "fix" this by re-reading per frame: a block
            // whose tone changes mid-way is a step inside a record, which is the worse artefact, and
            // one no flush can be told about.
            var profile = _profile;

            // A model changed mid-acquisition starts from rest, which is right: it is a different
            // DUT, and carrying the previous one's state into it would ring for a moment with poles
            // that no longer exist.
            if (dut?.Model != profile.Dut)
                dut = SimulatedDut.For(profile.Dut, config.SampleRateHz);

            if (profile.CrosstalkEnabled != (crosstalk is not null))
                crosstalk = profile.CrosstalkEnabled ? new SimulatedCrosstalk(config.SampleRateHz) : null;

            // A load changed mid-acquisition is exactly what the load-step measurement asks for,
            // between its two passes; the new divider starts from rest like a rebuilt DUT does.
            if (outputStage.Model != profile.OutputStage || outputStage.Load != profile.Load)
                outputStage = new SimulatedOutputStage(profile.OutputStage, profile.Load, config.SampleRateHz);

            // A DUT that distorts on its own is measured on its own: the fixed harmonic stack the
            // synthesizer otherwise adds to the generator is level-independent, and under it every
            // level-dependent curve the nonlinear models exist to draw is a flat line at -46 dBc.
            // Dropping it also makes the right channel the clean generator tap a real bench has.
            if (dut is { SuppliesOwnDistortion: true } && profile.HarmonicsEnabled)
            {
                if (!ReferenceEquals(profile, distortingSource))
                {
                    distortingSource = profile;
                    distortionFree = profile with { HarmonicsEnabled = false };
                }

                profile = distortionFree!;
            }

            // Two independent clip points, one per end of the signal chain: the generator's own
            // DAC full scale, and the analyzer's ADC full scale. See the per-sample loop below.
            double outputClipPeakV = SignalSynthesizer.DbvToPeakV(Volatile.Read(ref _outputRangeDbv));
            double inputClipPeakV = SignalSynthesizer.DbvToPeakV(Volatile.Read(ref _inputRangeDbv));
            int sampleCount = config.BlockFrameCount * config.ChannelCount;
            float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);

            // Null unless the stream recorder is armed. Always two channels, whatever the stream's
            // channel count: a recording's second channel is the generator's right output, which
            // exists on a mono acquisition too.
            var tap = _stimulusTap;
            float[]? tapVolts = tap is null ? null : ArrayPool<float>.Shared.Rent(config.BlockFrameCount * 2);
            if (tapVolts is not null)
                Array.Clear(tapVolts, 0, config.BlockFrameCount * 2);

            Span<double> generatedByChannel = stackalloc double[Math.Max(2, config.ChannelCount)];

            for (int frame = 0; frame < config.BlockFrameCount; frame++)
            {
                long absoluteFrame = frameIndex + frame;
                double t = absoluteFrame / config.SampleRateHz;

                // Hum/noise are drawn once per frame and shared across channels (same as before);
                // only the generator tones differ per channel (see ComputeChannelSample).
                double sharedAdditive = synthesizer.ComputeSharedAdditive(profile, t, config.SampleRateHz);

                // Every channel's own output first, then the mix: the crosstalk model needs the
                // *other* channel's post-DUT output, which does not exist yet inside a single pass.
                for (int ch = 0; ch < config.ChannelCount; ch++)
                {
                    // Signal chain order matters: the generator's output is hard-clipped at the
                    // DAC's full scale first (asking for a level above the selected output range
                    // truncates the waveform, exactly as real hardware does), then it passes
                    // through the DUT, then the DUT's hum and noise are added downstream of it, and
                    // only then does the whole thing meet the ADC's soft input saturation. Hum and
                    // noise are not products of the DAC, so they deliberately sit outside the
                    // output clip; nor are they products of the DUT's filters, so they sit after
                    // those too — a DUT's own noise floor is not band-limited by its own response.
                    double generated = SignalSynthesizer.HardClip(
                        synthesizer.ComputeChannelSample(profile, absoluteFrame, ch), outputClipPeakV);

                    // The stimulus is what left the DAC: after the output clip (so a level above the
                    // output range is recorded truncated, as hardware records it) and before the DUT,
                    // the output stage and the crosstalk, none of which are the generator's.
                    if (tapVolts is not null && ch < 2)
                        tapVolts[(frame * 2) + ch] = (float)generated;

                    // One channel only — left for a DUT, right for an impedance fixture, the way a
                    // bench is wired for each (see SimulatedDutModelExtensions.MeasuredChannel).
                    // Filtering both would let any two-channel ratio divide the model straight
                    // back out.
                    if (dut is not null && ch == dut.Model.MeasuredChannel())
                        generated = dut.Process(generated);

                    // The amplifier's output terminals are the left channel: what the DUT model
                    // produced is the open-circuit output, and the load hangs on it here — after
                    // the DUT, before the coupling and the additive, which are not the stage's.
                    // Unity unless both knobs say otherwise, so this costs nothing by default.
                    if (ch == 0 && !outputStage.IsUnity)
                        generated = outputStage.Process(generated);

                    generatedByChannel[ch] = generated;
                }

                for (int ch = 0; ch < config.ChannelCount; ch++)
                {
                    double generated = generatedByChannel[ch];

                    // After the DUT (the victim carries a copy of the aggressor's *output*) and
                    // before the additive (the coupling is not a source of hum or noise). Two
                    // channels only; a mono stream has nothing to couple from.
                    if (crosstalk is not null && config.ChannelCount >= 2)
                    {
                        int other = ch == 0 ? 1 : 0;
                        generated += crosstalk.Couple(other, generatedByChannel[other]);
                    }

                    double sample = SignalSynthesizer.SoftClip(generated + sharedAdditive, inputClipPeakV);
                    buffer[(frame * config.ChannelCount) + ch] = (float)sample;
                }
            }

            if (tap is not null && tapVolts is not null)
            {
                tap.WriteStimulus(
                    tapVolts.AsSpan(0, config.BlockFrameCount * 2), channelCount: 2, config.SampleRateHz, frameIndex);
                ArrayPool<float>.Shared.Return(tapVolts);
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
