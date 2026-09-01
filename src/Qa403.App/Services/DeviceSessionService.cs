using Qa403.App.Endpoints;
using Qa403.App.Diagnostics;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Hardware;
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

    /// <summary>
    /// Serializes every device-session-mutating operation (generator, input/output range,
    /// start/stop acquisition) against every other one. Without this, two concurrent requests
    /// could interleave: one enqueues a register write onto the currently active USB transfer
    /// pump (e.g. <see cref="SetOutputRangeAsync"/>'s pump-queued write) while another tears that
    /// same pump down (<see cref="StopStreamIfRunningAsync"/>), cancelling the pump's own token
    /// out from under the still-pending write — observed as <c>TaskCanceledException</c> out of
    /// <c>Qa40xTransport.WriteRegisterAsync</c> every time a Single capture auto-stops (the
    /// client's Stop call disables the generator right after stopping acquisition, and that
    /// disable's own output-range write must never race a concurrent teardown of the same pump).
    /// Each public entry point below acquires this once and delegates to a private "Core"
    /// implementation; internal cross-calls (e.g. <see cref="StartAcquisitionAsync"/> stopping a
    /// previous run) call the Core methods directly rather than re-entering the lock, since
    /// <see cref="SemaphoreSlim"/> is not reentrant.
    /// </summary>
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

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

    /// <summary>Whether <see cref="OnDeviceLost"/> is already subscribed — see <see cref="ConnectAsync"/>, which may run more than once.</summary>
    private bool _deviceLostHooked;

    /// <summary>
    /// Bumped by every successful <see cref="ConnectAsync"/>. <see cref="OnDeviceLost"/> hands its
    /// generation to the background teardown, which then does nothing if the user has already
    /// reconnected in the meantime — otherwise a teardown scheduled microseconds before a Connect
    /// could stop the *new* session's pipeline.
    /// </summary>
    private int _connectionGeneration;

    /// <summary>
    /// <see cref="Environment.TickCount64"/> of the last liveness ping, or -1 for "never". Throttles
    /// <see cref="GetStatusAsync"/>: several browser tabs polling at 2 s each must not turn into
    /// several register exchanges per second on the wire.
    /// </summary>
    private long _lastPingTicks = -1;

    /// <summary>Floor on the gap between liveness pings, just under the UI's own 2 s status poll so a single poller still gets one per poll.</summary>
    private const int LivenessPingIntervalMs = 1500;

    public DeviceCapabilities Capabilities => device.Capabilities;

    public bool Acquiring => _isAcquiring;

    /// <summary>Whether a device has been brought up. False from startup until <see cref="ConnectAsync"/> first succeeds — the app runs fine with no QA403 attached.</summary>
    public bool Connected => _info is not null;

    /// <summary>Null until <see cref="ConnectAsync"/> completes.</summary>
    public DeviceInfo? Info => _info;

    public double InputRangeDbv => device.InputRangeDbv;

    /// <summary>
    /// RX chunks the device has dropped since connect. Exposed so a caller that owns a measurement
    /// window can bracket it — <see cref="ThdSweepRunner"/> reads this either side of every point, so
    /// a hole in the sample stream can be attributed to the frequency it actually contaminated
    /// instead of to the run as a whole.
    /// </summary>
    public long DroppedRxChunks => device.DroppedRxChunks;

    /// <summary>
    /// Assembled blocks dropped after calibration because this service's own drain loop fell behind
    /// — see <see cref="IAnalyzerDevice.DroppedBlocks"/>. Reported alongside
    /// <see cref="DroppedRxChunks"/> rather than folded into it, because the two name different
    /// halves of the pipeline and only reporting one of them is what hid this for weeks.
    /// </summary>
    public long DroppedBlocks => device.DroppedBlocks;

    public double OutputRangeDbv => _outputRangeDbv;

    /// <summary>
    /// The device state to stamp on a diagnostic record, read at the instant the record is analyzed
    /// rather than pushed on change — see <see cref="Qa403.App.Diagnostics.DiagnosticContext"/>.
    /// Reads <see cref="IAnalyzerDevice.InputRangeDbv"/> off the device, not the
    /// <c>_inputRangeDbv</c> mirror, because autoranging steps the range inside the device and the
    /// mirror is exactly the value that goes stale when it does. Field reads only: this runs on the
    /// pipeline's DSP thread and must not take <see cref="_sessionLock"/>.
    /// </summary>
    public DiagnosticContext CaptureDiagnosticContext() => new(
        InputRangeDbv: device.InputRangeDbv,
        OutputRangeDbv: _outputRangeDbv,
        AutoRangeEnabled: AutoRangeEnabled ?? false,
        Generator: _generatorConfig,
        DroppedRxChunks: device.DroppedRxChunks,
        DroppedBlocks: device.DroppedBlocks);

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

    /// <summary>
    /// Brings the device up, throwing whatever <see cref="IAnalyzerDevice.ConnectAsync"/> throws
    /// when there is nothing to connect to.
    ///
    /// Called once at startup by <see cref="DeviceLifecycleHostedService"/> and again, on demand,
    /// by <c>POST /api/device/connect</c>: a QA403 that isn't plugged in yet must not stop the app
    /// from starting (it used to take host startup down with it, so <c>run.bat</c> with no device
    /// attached died on a stack trace), so this has to be retryable rather than one-shot.
    ///
    /// Retrying after a failure is safe: <see cref="Hardware.Qa40xUsbDevice.ConnectAsync"/> assigns
    /// none of its own state and disposes the transport it opened unless every step succeeded.
    /// Retrying after *success* is a no-op — opening a second transport over a live one would claim
    /// the interface twice.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_info is not null)
                return;

            // Once per process, not once per attempt: every failed attempt would otherwise leave
            // another subscription behind, and a later disconnect would then log once per attempt.
            if (!_deviceLostHooked)
            {
                device.DeviceLost += OnDeviceLost;
                _deviceLostHooked = true;
            }

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

            // A fresh handle: whatever was lost before belonged to the previous connection.
            _deviceLost = false;
            _connectionGeneration++;
            logger.LogInformation(
                "Connected to {Product} (serial {Serial}, fw {Firmware}, simulated={Simulated})",
                _info.ProductName, _info.SerialNumber, _info.FirmwareVersion, _info.IsSimulated);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Guards every operation that has to reach the hardware. The app runs perfectly well with no
    /// device — the UI, the measurement database and every stored result are all still usable — so
    /// "not connected" is an ordinary state a request can arrive in, not an impossible one. Without
    /// this the failure surfaced from three levels down: <c>Qa40xUsbDevice</c>'s own "Device is not
    /// connected" on a register write (a 500 and a dev exception page), or worse, silently — a
    /// <c>POST /acquisition/start</c> answering 200 with <c>acquiring: false</c> while the stream
    /// task faulted on a background thread, which reads to the user as a Run button that does
    /// nothing. <see cref="InvalidOperationException"/> because that is what the endpoints already
    /// map to 409 with the message shown in the status bar.
    /// </summary>
    private void EnsureConnected()
    {
        if (_info is null)
            throw new InvalidOperationException("No device connected. Attach the QA403 and press Connect in the status bar.");
    }

    /// <summary>
    /// Turns "the device vanished while we were talking to it" into the same
    /// <see cref="InvalidOperationException"/> the endpoints already answer with 409. Without it the
    /// unplug surfaced as a raw <c>UsbException: Input/Output Error</c> out of a register write — a
    /// 500 and a stack trace for something the user did on purpose. <see cref="OnDeviceLost"/> has
    /// already run by this point (the device raises it before the exception propagates), so the
    /// session is disconnected and the message is the actionable half.
    /// </summary>
    private static InvalidOperationException DeviceLostError(Qa40xDeviceLostException inner) =>
        new("The QA403 was disconnected. Plug it back in and press Connect in the status bar.", inner);

    /// <summary>
    /// Status, preceded by a throttled liveness check — what <c>GET /api/device</c> serves.
    ///
    /// An unplug is only self-announcing while a stream is running (the transfer pump notices within
    /// one transfer). Idle, nothing touches USB at all, so the status bar went on reporting a
    /// connected QA403 that had been unplugged minutes earlier, and the discovery came instead as a
    /// failed register write behind the user's next click. Since the UI already polls this endpoint
    /// every 2 s, the check rides along with it rather than adding a background timer.
    /// </summary>
    public async Task<DeviceStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        await CheckDeviceAliveAsync(ct).ConfigureAwait(false);
        return GetStatus();
    }

    private async Task CheckDeviceAliveAsync(CancellationToken ct)
    {
        if (_info is null || StreamRunning)
            return;

        long now = Environment.TickCount64;
        if (_lastPingTicks >= 0 && now - _lastPingTicks < LivenessPingIntervalMs)
            return;

        // Zero-timeout: if any other operation holds the session it is already talking to the device,
        // which answers the same question better than a ping would. Never block a status poll on it.
        if (!await _sessionLock.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            // Re-checked under the lock: both could have changed while waiting for it.
            if (_info is null || StreamRunning)
                return;

            _lastPingTicks = Environment.TickCount64;

            // The return value needs no handling: a false answer means the device already raised
            // DeviceLost, and OnDeviceLost has cleared _info by the time this returns — so the
            // GetStatus() that follows reports the disconnect on this very poll.
            await device.PingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Liveness ping threw; leaving the session connected");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool StreamRunning => _streamTask is { IsCompleted: false };

    public DeviceStatusResponse GetStatus()
    {
        var left = ToResponse(_generatorConfig.Left);
        var right = ToResponse(_generatorConfig.Right);

        if (_info is not { } info)
            return new DeviceStatusResponse(
                false, "", "", "", false, 0, 0, false, left, right, SimulatedNoiseEnabled, SimulatedHumEnabled,
                _deviceLost, AutoRangeEnabled, AutoRangeStepDownDelayMs, _generatorConfig.SnapToBin, device.DroppedRxChunks, device.DroppedBlocks);

        return new DeviceStatusResponse(
            true, info.ProductName, info.SerialNumber, info.FirmwareVersion, info.IsSimulated,
            device.InputRangeDbv, _outputRangeDbv, Acquiring, left, right, SimulatedNoiseEnabled, SimulatedHumEnabled,
            _deviceLost, AutoRangeEnabled, AutoRangeStepDownDelayMs, _generatorConfig.SnapToBin, device.DroppedRxChunks, device.DroppedBlocks);
    }

    /// <summary>
    /// Runs on whichever thread noticed the disconnect — the stream's pump thread, or the request
    /// thread behind a liveness ping or a register write.
    ///
    /// The two flags are set inline so the very status response being built around this reports the
    /// disconnect. Clearing <see cref="_info"/> (rather than only setting
    /// <see cref="DeviceStatusResponse.DeviceLost"/>, which is all this used to do) is what makes the
    /// session honest about it: status flips to disconnected, every device operation is refused with
    /// a message instead of failing inside a register write, and Connect can bring the replugged
    /// device back — <see cref="Hardware.Qa40xUsbDevice"/> releases the dead handle on its way
    /// through here, so there is nothing left claiming the interface.
    ///
    /// Everything that has to *await* is deferred to <see cref="TearDownAfterLossAsync"/>: this can be
    /// called from inside the stream task's own finally, where awaiting that task would deadlock.
    /// </summary>
    private void OnDeviceLost(object? sender, EventArgs e)
    {
        _deviceLost = true;
        _info = null;
        logger.LogWarning("QA403 disconnected — session marked device-less; press Connect once it is plugged back in");

        int generation = _connectionGeneration;
        _ = Task.Run(() => TearDownAfterLossAsync(generation));
    }

    /// <summary>
    /// Releases what the lost device left running: the spectrum worker (started by
    /// <see cref="StartAcquisitionCoreAsync"/> and otherwise leaked, since a second
    /// <see cref="SpectrumPipeline.Start"/> overwrites its task without cancelling it) and the stream
    /// bookkeeping. The stream task itself needs no cancelling — it ended when the device did — but
    /// its fields have to be cleared or the next run would reuse a completed one.
    /// </summary>
    private async Task TearDownAfterLossAsync(int generation)
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Reconnected while this was queued: that session owns the pipeline now, leave it alone.
            if (generation != _connectionGeneration)
                return;

            _isAcquiring = false;
            await spectrumPipeline.StopAsync().ConfigureAwait(false);
            await StopStreamIfRunningAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Teardown after a device disconnect did not complete cleanly");
        }
        finally
        {
            _sessionLock.Release();
        }
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

    /// <summary>
    /// Whether hardware autoranging may step to a *more sensitive* range on its own. Null with the
    /// same convention as <see cref="AutoRangeEnabled"/>.
    ///
    /// <para>Deliberately not on <see cref="DeviceStatus"/> and not exposed over the API: this is
    /// not a user setting but a scoped suppression <see cref="ThdSweepRunner"/> holds for the length
    /// of a sweep, restoring whatever it found. A checkbox for it would be a second, slower way to
    /// express what the Auto checkbox already covers, and one a sweep would then have to fight.</para>
    /// </summary>
    public bool? AutoRangeStepDownEnabled => device is SimulatedDevice ? null : device.AutoRangeStepDownEnabled;

    public void SetAutoRangeStepDownEnabled(bool enabled) => device.AutoRangeStepDownEnabled = enabled;

    public async Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetInputRangeCoreAsync(dbv, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task SetInputRangeCoreAsync(double dbv, CancellationToken ct)
    {
        EnsureConnected();

        await device.SetInputRangeAsync(dbv, ct);
        _inputRangeDbv = dbv;

        // A range change is a gain step in the analog path — same reasoning as the generator
        // change in ConfigureGeneratorAsync. (An *autorange* step originates inside the device and
        // so cannot be flushed from here; it announces itself in-band instead, on the first block
        // after the step — see PumpAsync and PcmBlock.StartsAfterDiscontinuity.)
        if (_isAcquiring)
            spectrumPipeline.Flush(StimulusLatencyFrames);
    }

    /// <summary>
    /// Frames of pre-change audio still in flight when a stimulus change returns — what every
    /// stimulus-driven <see cref="SpectrumPipeline.Flush"/> has to discard on top of emptying the
    /// buffers. Falls back to the last known sample rate when no stream is running, which only
    /// matters for a flush that is about to be followed by a restart anyway.
    /// </summary>
    private int StimulusLatencyFrames =>
        device.StimulusLatencyFrames(_streamConfig?.SampleRateHz ?? _lastSampleRateHz);

    /// <summary>
    /// Re-applies the last generator request. Needed after an FFT-size or sample-rate change, because
    /// which frequency is coherent depends on both — without this, snapping silently goes stale and
    /// the tone drifts back off-bin the moment the analyzer is reconfigured.
    /// </summary>
    public async Task ReapplyGeneratorAsync(CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReapplyGeneratorCoreAsync(ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private Task ReapplyGeneratorCoreAsync(CancellationToken ct) =>
        ConfigureGeneratorCoreAsync(_requestedGeneratorConfig, ct);

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
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetOutputRangeCoreAsync(dbv, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task SetOutputRangeCoreAsync(double dbv, CancellationToken ct)
    {
        EnsureConnected();

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
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConfigureGeneratorCoreAsync(config, ct, streamConfigHint);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task ConfigureGeneratorCoreAsync(GeneratorConfig config, CancellationToken ct, AcquisitionConfig? streamConfigHint = null)
    {
        _lastSampleRateHz = streamConfigHint?.SampleRateHz ?? _streamConfig?.SampleRateHz ?? _lastSampleRateHz;
        _requestedGeneratorConfig = config;

        config = SnapTones(config);

        // Silencing a generator there is no device to silence is a success, not a 409: deviceControl's
        // stopRun (Stop, and every measurement switch) disables both channels unconditionally, and
        // with no device attached that has nothing to do. Asking for *output* without one is the
        // error case, and only that one is worth telling the user about.
        if (_info is null)
        {
            if (config.Left.Enabled || config.Right.Enabled)
                EnsureConnected();

            _generatorConfig = config;
            return;
        }

        // The output range is derived from what the generator is about to play, never chosen by the
        // user — see OutputRangeSelector. Ordering matters: program a *larger* range before the new
        // config reaches the DAC (otherwise the louder signal spends a chunk clipping against the old
        // smaller full scale), and a *smaller* one after (so the old louder signal isn't the thing
        // being squeezed into it).
        double nextOutputRangeDbv = OutputRangeSelector.Select(config, Capabilities.OutputRangesDbv);
        bool widening = nextOutputRangeDbv > _outputRangeDbv;

        if (widening && nextOutputRangeDbv != _outputRangeDbv)
            await SetOutputRangeCoreAsync(nextOutputRangeDbv, ct);

        await device.ConfigureGeneratorAsync(config, ct);
        _generatorConfig = config;

        if (!widening && nextOutputRangeDbv != _outputRangeDbv)
            await SetOutputRangeCoreAsync(nextOutputRangeDbv, ct);

        if (_isAcquiring)
        {
            // Every sample already buffered predates this stimulus, so an FFT record spanning the
            // change would show the step as a broad skirt around the tone rather than the real
            // noise floor. And so does everything still queued for the DAC at this instant, which is
            // why this passes the stimulus latency rather than flushing bare. See
            // SpectrumPipeline.Flush.
            spectrumPipeline.Flush(StimulusLatencyFrames);
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

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartAcquisitionCoreAsync(config, ct);
        }
        catch (Qa40xDeviceLostException ex)
        {
            throw DeviceLostError(ex);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StartAcquisitionCoreAsync(AcquisitionConfig config, CancellationToken ct)
    {
        EnsureConnected();

        // A config change (sample rate, channel count, block size) while already acquiring restarts
        // the stream in place rather than making the caller Stop then Start — the UI applies these
        // changes live, with no explicit stop/restart step of its own.
        double previousSampleRateHz = _streamConfig?.SampleRateHz ?? _lastSampleRateHz;
        if (_isAcquiring)
            await StopAcquisitionCoreAsync();

        // Reuses an already-running generator-only stream if its config matches, otherwise restarts it
        // at this config — either way, by the time this returns the stream is up before spectrumPipeline
        // starts consuming it.
        await EnsureStreamRunningAsync(config, ct);
        spectrumPipeline.Start(config.SampleRateHz, config.ChannelCount);
        _isAcquiring = true;

        // Which frequency is coherent depends on the sample rate, so a rate change has to re-snap the
        // generator or its tones silently drift off-bin at the new rate.
        if (config.SampleRateHz != previousSampleRateHz)
            await ReapplyGeneratorCoreAsync(ct);
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
                {
                    // Autoranging steps the input range inside the device, and the quiesce that does
                    // it discards ~a queue depth of already-captured RX on top of the relay settle
                    // frames — a hole of order 110 ms that DroppedRxChunks does not count and the
                    // block sequence numbers do not show. Flushing here is the same reasoning as
                    // SetInputRangeCoreAsync's flush for a manual range change; the difference is
                    // that the news arrives on the block rather than from the caller.
                    //
                    // Bare, with no stimulus-latency discard: nothing changed on the way *out*, so
                    // there is no in-flight DAC audio to outrun. See PcmBlock.StartsAfterDiscontinuity.
                    if (block.StartsAfterDiscontinuity)
                    {
                        // Information, not Debug, and logged here rather than at the step itself:
                        // this is the one event that makes an otherwise inexplicable one-record
                        // noise-floor jump attributable, and the pump thread is the wrong place to
                        // do file I/O. Repeated lines roughly a second apart mean the ranger is
                        // hunting at a threshold rather than tracking a real level change.
                        logger.LogInformation(
                            "Input range stepped to {RangeDbv} dBV by autoranging — flushing the "
                                + "analysis window, since the step discarded the RX transfers in flight",
                            device.InputRangeDbv);

                        spectrumPipeline.Flush();
                    }

                    spectrumPipeline.Enqueue(block); // takes ownership — disposes the block itself
                }
                else
                {
                    block.Dispose();
                }
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
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAcquisitionCoreAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StopAcquisitionCoreAsync()
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
