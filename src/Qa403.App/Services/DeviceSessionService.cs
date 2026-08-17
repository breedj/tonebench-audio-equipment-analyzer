using Qa403.App.Endpoints;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>
/// Owns the single <see cref="IAnalyzerDevice"/> for the app's lifetime: connects once at
/// startup, tracks the range/generator/acquisition state the device itself doesn't expose
/// getters for, and drains acquisition blocks into the <see cref="SpectrumPipeline"/> for the
/// duration of each acquisition run.
/// </summary>
public sealed class DeviceSessionService(IAnalyzerDevice device, SpectrumPipeline spectrumPipeline, ILogger<DeviceSessionService> logger)
{
    /// <summary>Real QA403 hardware queue depth (docs/qa403-app-stack-spec.md §2.1) — also the floor enforced on <see cref="StartAcquisitionAsync"/>'s <c>BlockFrameCount</c>.</summary>
    public const int MinBlockFrameCount = 1024;

    private readonly object _streamLock = new();

    private DeviceInfo? _info;
    private double _inputRangeDbv;
    private double _outputRangeDbv;

    /// <summary>The effective generator config — tone frequencies already snapped, if snapping is on. This is what the device is playing and what status reports.</summary>
    private GeneratorConfig _generatorConfig = GeneratorConfig.Off;

    /// <summary>
    /// The unsnapped config exactly as the client asked for it. Kept so <see cref="ReapplyGeneratorAsync"/>
    /// can re-snap from the original request when the FFT size or sample rate changes — re-snapping a
    /// previously snapped frequency would let it drift a bin at a time.
    /// </summary>
    private GeneratorConfig _requestedGeneratorConfig = GeneratorConfig.Off;

    /// <summary>Last sample rate the client told us about, for snapping tones while no stream is running.</summary>
    private double _lastSampleRateHz = 48000.0;

    /// <summary>
    /// The device's <see cref="IAnalyzerDevice.StreamAsync"/> loop and the spectrum-facing "Acquiring" state
    /// are deliberately two different things. The QA403's DAC only clocks samples out while the same USB
    /// stream that carries ADC data is running (<see cref="IAnalyzerDevice.ConfigureGeneratorAsync"/> alone
    /// changes nothing on the wire — see <see cref="Hardware.Qa40xUsbDevice.ChunkPump.BuildTxChunk"/>), so
    /// the generator needs this stream running independently of whether the UI has pressed "Start
    /// Acquisition". <see cref="_streamCts"/>/<see cref="_streamTask"/> track that underlying stream;
    /// <see cref="_isAcquiring"/> tracks only whether received blocks are being forwarded to
    /// <see cref="spectrumPipeline"/>. <see cref="EnsureStreamRunningAsync"/> starts (or restarts, on a
    /// config change) the stream; <see cref="StopStreamIfRunningAsync"/> tears it down. Whichever of
    /// acquisition/generator asked for the stream last "owns" its <see cref="AcquisitionConfig"/> — see
    /// <see cref="StartAcquisitionAsync"/> and <see cref="ConfigureGeneratorAsync"/>.
    /// </summary>
    private CancellationTokenSource? _streamCts;

    private Task? _streamTask;
    private AcquisitionConfig? _streamConfig;
    private volatile bool _isAcquiring;
    private volatile bool _deviceLost;

    public DeviceCapabilities Capabilities => device.Capabilities;

    public bool Acquiring => _isAcquiring;

    /// <summary>Null until <see cref="ConnectAsync"/> completes.</summary>
    public DeviceInfo? Info => _info;

    public double InputRangeDbv => device.InputRangeDbv;

    public double OutputRangeDbv => _outputRangeDbv;

    /// <summary>The effective (already bin-snapped, if snapping is on) generator config — what the device is actually playing.</summary>
    public GeneratorConfig EffectiveGeneratorConfig => _generatorConfig;

    /// <summary>The acquisition config currently driving the device stream, or null when nothing has started one.</summary>
    public AcquisitionConfig? StreamConfig => _streamConfig;

    /// <summary>
    /// Whether the simulated DUT adds its own noise floor (the white floor plus the 1/f texture
    /// layered on it), or <c>null</c> when the device isn't the simulator. Deliberately reached by
    /// type test rather than through <see cref="IAnalyzerDevice"/>: real hardware has no such knob,
    /// so putting it on the interface would be a lie. See <see cref="SimulatedSignalProfile"/>.
    /// </summary>
    public bool? SimulatedNoiseEnabled => (device as SimulatedDevice)?.SignalProfile.NoiseEnabled;

    /// <summary>
    /// Whether the simulated DUT adds mains hum (a 50 Hz fundamental plus two harmonics 6 dB apart),
    /// or <c>null</c> when the device isn't the simulator. Independent of
    /// <see cref="SimulatedNoiseEnabled"/> — hum is a deterministic tone stack, not part of the
    /// noise path.
    /// </summary>
    public bool? SimulatedHumEnabled => (device as SimulatedDevice)?.SignalProfile.HumEnabled;

    /// <summary>
    /// Patches the simulated DUT's signal profile; <c>null</c> leaves a setting alone. Returns
    /// false when the device isn't the simulator, so the caller can answer with a 400 rather than
    /// silently accepting a setting that does nothing.
    /// </summary>
    public bool TrySetSimulatedProfile(bool? noiseEnabled, bool? humEnabled)
    {
        if (device is not SimulatedDevice simulated)
            return false;

        // Snapshot once: the property returns an immutable record, and re-reading it per field
        // could mix two different profiles into the one being written back.
        var profile = simulated.SignalProfile;
        simulated.SignalProfile = profile with
        {
            NoiseEnabled = noiseEnabled ?? profile.NoiseEnabled,
            HumEnabled = humEnabled ?? profile.HumEnabled,
        };

        return true;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        device.DeviceLost += OnDeviceLost;

        _info = await device.ConnectAsync(ct);

        // Most sensitive input range (best resolution for a fresh measurement), and the *largest*
        // output range — maximum generator headroom, so a default tone level can't sit above DAC
        // full scale and arrive pre-clipped before the user has touched anything.
        _inputRangeDbv = Capabilities.InputRangesDbv[0];
        _outputRangeDbv = Capabilities.OutputRangesDbv[^1];

        // Push both to the device rather than only caching them here. The device has its own field
        // defaults, and leaving the two unsynced means status reports one full scale while the
        // device actually clips at another — invisible until the ranges started doing something.
        await device.SetInputRangeAsync(_inputRangeDbv, ct);
        await device.SetOutputRangeAsync(_outputRangeDbv, ct);
        logger.LogInformation(
            "Connected to {Product} (serial {Serial}, fw {Firmware}, simulated={Simulated})",
            _info.ProductName, _info.SerialNumber, _info.FirmwareVersion, _info.IsSimulated);
    }

    public DeviceStatusResponse GetStatus()
    {
        var left = ToResponse(_generatorConfig.Left);
        var right = ToResponse(_generatorConfig.Right);

        if (_info is not { } info)
            return new DeviceStatusResponse(
                false, "", "", "", false, 0, 0, false, left, right, SimulatedNoiseEnabled, SimulatedHumEnabled,
                _deviceLost, AutoRangeEnabled, AutoRangeStepDownDelayMs, _generatorConfig.SnapToBin, device.DroppedRxChunks);

        return new DeviceStatusResponse(
            true, info.ProductName, info.SerialNumber, info.FirmwareVersion, info.IsSimulated,
            device.InputRangeDbv, _outputRangeDbv, Acquiring, left, right, SimulatedNoiseEnabled, SimulatedHumEnabled,
            _deviceLost, AutoRangeEnabled, AutoRangeStepDownDelayMs, _generatorConfig.SnapToBin, device.DroppedRxChunks);
    }

    /// <summary>
    /// Runs on whichever thread <see cref="IAnalyzerDevice.StreamAsync"/>'s enumerator was being
    /// pumped from when the transport detected the disconnect. Only flips a flag the polled
    /// <c>GET /api/device</c> status already surfaces (<see cref="DeviceStatusResponse.DeviceLost"/>)
    /// — no acquisition-stopping here, since <see cref="IAnalyzerDevice.StreamAsync"/> itself
    /// already ends its enumeration cleanly before raising this event.
    /// </summary>
    private void OnDeviceLost(object? sender, EventArgs e)
    {
        _deviceLost = true;
        logger.LogWarning("Device reported itself lost (disconnected).");
    }

    /// <summary>Also used by <see cref="MeasurementSaveService.ApplyAsync"/> to describe a measurement type's stored generator config in the same wire shape as live device status.</summary>
    internal static GeneratorChannelResponse ToResponse(ChannelGeneratorConfig config)
    {
        var tones = config.Tones.Select(t => new GeneratorToneResponse(t.FrequencyHz, t.AmplitudeDbv)).ToArray();

        // Fill unset per-kind blocks with the settings records' own defaults rather than sending
        // null: the UI seeds every one of its parameter forms from this response, and a null block
        // would leave whichever kind the user hasn't touched yet with no values to show.
        var multitone = config.Multitone ?? new MultitoneSettings();
        var noise = config.Noise ?? new WhiteNoiseSettings();
        var chirp = config.Chirp ?? new EchoChirpSettings();

        return new GeneratorChannelResponse(
            config.Enabled,
            tones,
            config.Kind,
            new MultitoneResponse(multitone.TonesPerOctave, multitone.TotalRmsDbv, multitone.RolloffDbPerOctave),
            new WhiteNoiseResponse(noise.LevelDbv),
            new EchoChirpResponse(chirp.AmplitudeDbv, chirp.StartFrequencyHz, chirp.EndFrequencyHz, chirp.Smoothing));
    }

    /// <summary>
    /// Whether the device is autoranging its input. Null when the device has no autoranging of its
    /// own (the simulator), so the UI can hide the checkbox — same null convention as
    /// <see cref="SimulatedNoiseEnabled"/>.
    /// </summary>
    public bool? AutoRangeEnabled => device is SimulatedDevice ? null : device.AutoRangeEnabled;

    public void SetAutoRangeEnabled(bool enabled) => device.AutoRangeEnabled = enabled;

    /// <summary>
    /// How long a peak must stay under-range before hardware autoranging steps to a more sensitive
    /// range, in milliseconds. Null with the same convention as <see cref="AutoRangeEnabled"/> — the
    /// simulator has no hardware ranger to configure.
    /// </summary>
    public double? AutoRangeStepDownDelayMs => device is SimulatedDevice ? null : device.AutoRangeStepDownDelayMs;

    public void SetAutoRangeStepDownDelayMs(double delayMs) => device.AutoRangeStepDownDelayMs = delayMs;

    public async Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        await device.SetInputRangeAsync(dbv, ct);
        _inputRangeDbv = dbv;

        // A range change is a gain step in the analog path — same reasoning as the generator
        // change in ConfigureGeneratorAsync. (Autoranging steps the range inside the device without
        // telling us, so those transients can't be flushed this way.)
        if (_isAcquiring)
            spectrumPipeline.Flush();
    }

    /// <summary>
    /// Re-applies the last generator request. Needed after an FFT-size or sample-rate change, because
    /// which frequency is coherent depends on both — without this, snapping silently goes stale and
    /// the tone drifts back off-bin the moment the analyzer is reconfigured.
    /// </summary>
    public Task ReapplyGeneratorAsync(CancellationToken ct) =>
        ConfigureGeneratorAsync(_requestedGeneratorConfig, ct);

    /// <summary>
    /// Rewrites every tone frequency to the nearest coherent bin for the current FFT size and sample
    /// rate. No-op when <see cref="GeneratorConfig.SnapToBin"/> is off. Applies to the *effective*
    /// tone list, so a multitone comb gets snapped tone by tone as well.
    /// </summary>
    private GeneratorConfig SnapTones(GeneratorConfig config)
    {
        if (!config.SnapToBin)
            return config;

        int fftSize = spectrumPipeline.Options.FftSize;
        double sampleRateHz = _lastSampleRateHz;
        if (fftSize < 2 || sampleRateHz <= 0)
            return config;

        return config with { Left = SnapChannel(config.Left), Right = SnapChannel(config.Right) };

        ChannelGeneratorConfig SnapChannel(ChannelGeneratorConfig channel)
        {
            if (channel.Tones.Count == 0)
                return channel;

            var snapped = channel.Tones
                .Select(t => t with { FrequencyHz = CoherentFrequency.Nearest(t.FrequencyHz, sampleRateHz, fftSize).FrequencyHz })
                .ToArray();

            return channel with { Tones = snapped };
        }
    }

    public async Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        await device.SetOutputRangeAsync(dbv, ct);
        _outputRangeDbv = dbv;
    }

    /// <summary>
    /// <paramref name="streamConfigHint"/> is what to stream at if this call needs to bring the underlying
    /// USB stream up on its own — i.e. a channel just got enabled and no acquisition is currently running.
    /// Pass the UI's current (not-yet-started) Acquisition panel settings here so generator-only output
    /// uses the sample rate the user has actually selected, rather than an arbitrary default. Ignored
    /// while an acquisition is running: that call already owns the stream's configuration, and generator
    /// changes take effect on it automatically (<see cref="Hardware.Qa40xUsbDevice.ChunkPump.BuildTxChunk"/>
    /// reads the live generator config on every chunk).
    /// </summary>
    public async Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct, AcquisitionConfig? streamConfigHint = null)
    {
        _lastSampleRateHz = streamConfigHint?.SampleRateHz ?? _streamConfig?.SampleRateHz ?? _lastSampleRateHz;
        _requestedGeneratorConfig = config;

        config = SnapTones(config);

        // The output range is derived from what the generator is about to play, never chosen by the
        // user — see OutputRangeSelector. Ordering matters: program a *larger* range before the new
        // config reaches the DAC (otherwise the louder signal spends a chunk clipping against the old
        // smaller full scale), and a *smaller* one after (so the old louder signal isn't the thing
        // being squeezed into it).
        double nextOutputRangeDbv = OutputRangeSelector.Select(config, Capabilities.OutputRangesDbv);
        bool widening = nextOutputRangeDbv > _outputRangeDbv;

        if (widening && nextOutputRangeDbv != _outputRangeDbv)
            await SetOutputRangeAsync(nextOutputRangeDbv, ct);

        await device.ConfigureGeneratorAsync(config, ct);
        _generatorConfig = config;

        if (!widening && nextOutputRangeDbv != _outputRangeDbv)
            await SetOutputRangeAsync(nextOutputRangeDbv, ct);

        if (_isAcquiring)
        {
            // Every sample already buffered predates this stimulus, so an FFT record spanning the
            // change would show the step as a broad skirt around the tone rather than the real
            // noise floor. See SpectrumPipeline.Flush.
            spectrumPipeline.Flush();
            return;
        }

        bool anyChannelEnabled = config.Left.Enabled || config.Right.Enabled;
        if (anyChannelEnabled && streamConfigHint is { } hint)
            await EnsureStreamRunningAsync(hint, ct);
        else if (!anyChannelEnabled)
            await StopStreamIfRunningAsync();
    }

    public async Task StartAcquisitionAsync(AcquisitionConfig config, CancellationToken ct)
    {
        if (config.SampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "SampleRateHz must be positive.");
        if (config.ChannelCount < 1 || config.ChannelCount > Capabilities.MaxChannelCount)
            throw new ArgumentOutOfRangeException(nameof(config), $"ChannelCount must be between 1 and {Capabilities.MaxChannelCount}.");
        if (config.BlockFrameCount < MinBlockFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                $"BlockFrameCount must be at least {MinBlockFrameCount} (the real QA403's hardware queue depth, "
                    + "per docs/qa403-app-stack-spec.md §2.1) — smaller values turn the acquisition loop into a "
                    + "near-tight spin at the simulated sample rate and can starve the app under load.");
        }

        // A config change (sample rate, channel count, block size) while already acquiring restarts
        // the stream in place rather than making the caller Stop then Start — the UI applies these
        // changes live, with no explicit stop/restart step of its own.
        double previousSampleRateHz = _streamConfig?.SampleRateHz ?? _lastSampleRateHz;
        if (_isAcquiring)
            await StopAcquisitionAsync();

        // Reuses an already-running generator-only stream if its config matches, otherwise restarts it
        // at this config — either way, by the time this returns the stream is up before spectrumPipeline
        // starts consuming it.
        await EnsureStreamRunningAsync(config, ct);
        spectrumPipeline.Start(config.SampleRateHz, config.ChannelCount);
        _isAcquiring = true;

        // Which frequency is coherent depends on the sample rate, so a rate change has to re-snap the
        // generator or its tones silently drift off-bin at the new rate.
        if (config.SampleRateHz != previousSampleRateHz)
            await ReapplyGeneratorAsync(ct);
    }

    /// <summary>Starts (or restarts, if already running at a different config) the underlying device stream.</summary>
    private async Task EnsureStreamRunningAsync(AcquisitionConfig config, CancellationToken ct)
    {
        lock (_streamLock)
        {
            if (_streamTask is { IsCompleted: false } && _streamConfig is { } current && current.Equals(config))
                return;
        }

        await StopStreamIfRunningAsync();

        var cts = new CancellationTokenSource();

        // Capture the token by value into a local, not the field, before handing it to Task.Run: the
        // lambda doesn't actually run until the thread pool gets to it, and if StopStreamIfRunningAsync
        // races in first and nulls _streamCts, reading the field lazily inside the lambda throws a
        // NullReferenceException instead of observing an already-cancelled token like it should.
        var pumpCt = cts.Token;
        lock (_streamLock)
        {
            _streamCts = cts;
            _streamConfig = config;
            _streamTask = Task.Run(() => PumpAsync(config, pumpCt), CancellationToken.None);
        }
    }

    /// <summary>
    /// Pumps <see cref="IAnalyzerDevice.StreamAsync"/> for as long as either acquisition or the generator
    /// needs the stream up. Every block is drained regardless — the device requires a continuous reader on
    /// EP2 to keep the DAC clocking TX at all — but is only forwarded to <see cref="spectrumPipeline"/>
    /// while <see cref="_isAcquiring"/> is set; otherwise it's just disposed (returns its rented buffer).
    /// </summary>
    private async Task PumpAsync(AcquisitionConfig config, CancellationToken ct)
    {
        try
        {
            await foreach (var block in device.StreamAsync(config, ct).WithCancellation(ct))
            {
                if (_isAcquiring)
                    spectrumPipeline.Enqueue(block); // takes ownership — disposes the block itself
                else
                    block.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Device stream faulted");
        }
    }

    private async Task StopStreamIfRunningAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_streamLock)
        {
            cts = _streamCts;
            task = _streamTask;
            _streamCts = null;
            _streamTask = null;
            _streamConfig = null;
        }

        if (cts is null)
            return;

        cts.Cancel();
        await device.StopAsync();
        if (task is not null)
            await task;
        cts.Dispose();
    }

    public async Task StopAcquisitionAsync()
    {
        if (!_isAcquiring)
            return;

        _isAcquiring = false;
        await spectrumPipeline.StopAsync();

        // The generator may still need the stream even though acquisition is done with it.
        bool generatorActive = _generatorConfig.Left.Enabled || _generatorConfig.Right.Enabled;
        if (!generatorActive)
            await StopStreamIfRunningAsync();
    }

    /// <summary>
    /// Host-shutdown teardown. Deliberately *not* <see cref="StopAcquisitionAsync"/>, which was what
    /// the lifecycle hosted service used to call and which tears down nothing at all in the two
    /// states the app is usually left in: it returns immediately when acquisition was never started,
    /// and it deliberately keeps the stream up when the generator is enabled. Either way the transfer
    /// pump was still running when DI went on to dispose the device, so <c>TransferPump</c>'s
    /// <c>finally</c> — the only thing that writes <see cref="Hardware.Qa40xRegisters.StreamingStop"/>
    /// and collects the in-flight EP2 transfers — never ran before <c>libusb_close</c>. That leaves
    /// the device streaming with nobody reading it, which is what made the QA403 unreachable to every
    /// app until it was power-cycled.
    ///
    /// Unconditional and ordered: stop forwarding, stop the FFT worker, then bring the stream down
    /// and wait for the pump to actually join. Only after this returns is it safe for the DI
    /// container to dispose <see cref="IAnalyzerDevice"/> (release interface, close handle).
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Logged step by step because the failure this guards against is a *hang*, not an exception:
        // whichever line has no matching "done" in the log is the one that blocked.
        logger.LogInformation("QA403 teardown: shutdown begin (acquiring={Acquiring}, stream={StreamRunning})",
            _isAcquiring, _streamTask is { IsCompleted: false });

        _isAcquiring = false;
        await spectrumPipeline.StopAsync();
        logger.LogInformation("QA403 teardown: spectrum pipeline stopped");

        await StopStreamIfRunningAsync();
        logger.LogInformation("QA403 teardown: device stream stopped and pump joined");
    }
}
