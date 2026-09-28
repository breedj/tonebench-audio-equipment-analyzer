using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Qa403.Device.Hardware;

/// <summary>
/// <see cref="IAnalyzerDevice"/> backed by a real QA403 over LibUsbDotNet 3.x. Ties together
/// <see cref="Qa40xTransport"/> (raw USB), <see cref="Qa40xCalibration"/> (the single raw-counts
/// &lt;-&gt; volts application point), <see cref="Qa40xGeneratorWaveform"/> (TX synthesis) and
/// <see cref="Qa40xAutoRanger"/> (input range stepping). See docs/protocol.md for every wire-level
/// fact this class relies on — each is cited at its point of use below.
///
/// <para><b>Threading.</b> The USB transfer loop (<see cref="TransferPump"/>) runs on its own
/// dedicated thread and does nothing but keep transfers in flight; all DSP-ish work (calibration,
/// block assembly) happens on the consumer side of a <see cref="Channel"/>. See
/// <see cref="TransferPump"/>'s remarks for why that separation is what keeps the DAC gap-free.</para>
/// </summary>
public sealed class Qa40xUsbDevice(ILogger<Qa40xUsbDevice> logger) : IAnalyzerDevice, IDisposable
{
    /// <summary>
    /// One bulk transfer's frame count: 512 frames = 4096 bytes/direction. Matches
    /// qa40x-baremetal's <c>usbBufSize = 4096</c> exactly (Acquisition.cs:98:
    /// <c>usbBufSize = (int)Math.Pow(2, 12)</c>) rather than PyQa40x's larger 16384-byte chunk —
    /// baremetal is the proven-in-production reference for this exact LibUsbDotNet/WinUSB
    /// combination on Windows, so its tuning is the safer bet here.
    /// </summary>
    private const int TransferChunkFrames = 512;

    /// <summary>4 bytes left + 4 bytes right per frame, always — the wire is stereo regardless of the caller's requested <see cref="AcquisitionConfig.ChannelCount"/> (Acquisition.cs:211-235).</summary>
    private const int BytesPerFrame = 8;

    /// <summary>Bytes in one TX/RX chunk — the rented buffer size for both directions in <see cref="TransferPump"/>.</summary>
    private const int ChunkByteSize = TransferChunkFrames * BytesPerFrame;

    /// <summary>Output buffer sizes must be a multiple of 64 samples or the device emits garbled audio (task requirement; docs/qa403-app-stack-spec.md §2.1).</summary>
    private const int RequiredBlockFrameMultiple = 64;

    /// <summary>
    /// How much audio time to keep submitted on EP2 in each direction. The queue is what absorbs any
    /// host-side hiccup (a GC pause, a thread-pool scheduling delay, one slow spectrum tick) without
    /// the DAC running dry, so the thing that must stay constant is <em>time</em>, not transfer count.
    ///
    /// A fixed transfer count is what the earlier "QueueDepth = 2" (and then 4) actually was, and it
    /// is why DAC dropouts got strictly worse as the sample rate went up: 4 × 512 frames is 42 ms of
    /// slack at 48 kHz but only 10.7 ms at 192 kHz, so the same hiccup that was harmless at 48 kHz
    /// underran the DAC at 192 kHz. Scaling by sample rate removes that dependence entirely.
    ///
    /// 64 ms is chosen to sit between the two references rather than being invented: qa40x-baremetal
    /// queues 2 × 4096-byte transfers (Acquisition.cs:136-141) but does zero per-chunk work — it only
    /// <c>Array.Copy</c>s into a preallocated buffer — whereas PyQa40x queues up to 5 × 16384-byte
    /// buffers (stream.py:27, <c>dacQueue = queue.Queue(maxsize=5)</c>), i.e. 20480 frames ≈ 107 ms at
    /// 192 kHz. This app does more per chunk than baremetal, so baremetal's depth is not enough; 64 ms
    /// stays comfortably under PyQa40x's ceiling.
    ///
    /// <para><b>Raised from 0.064 to 0.128 on measured evidence.</b> 64 ms was chosen against the
    /// references, but the thing it has to survive is a pump iteration that blocks longer than the
    /// buffer it is protecting, and those were measured on this app once the waveform was captured:
    /// pump stalls of 46.5, 81.0, 92.7, 94.4 and 125.3 ms, every one of them spent in
    /// <c>read wait</c> — waiting for a LibUsbDotNet RX completion delivered through the thread
    /// pool. A stall longer than the queue drains the DAC by construction, so at 64 ms four of
    /// those five had to produce a gap no matter how rare they are. The captured generator output
    /// shows exactly that: held-constant DC spans of 0.3-3.6 ms, each lifting one 341 ms FFT
    /// record's broadband floor by ~70 dB.
    ///
    /// <para>128 ms covers the worst stall seen with margin. It is above PyQa40x's ~107 ms, which is
    /// the highest of the references, so it is no longer bracketed by them — but the references do
    /// not do per-chunk work on the consumer side and this app does. <see cref="MaxQueueDepth"/> is
    /// 48, which is exactly 128 ms at 192 kHz, so this is also the most the clamp allows there.
    /// The cost is latency, not throughput: <c>StimulusLatencyFrames</c> doubles, and every
    /// stimulus-driven flush already derives its discard from <see cref="QueueDepthFor"/>, so that
    /// follows automatically.</para>
    /// </summary>
    private const double TargetInFlightSeconds = 0.128;

    private const int MinQueueDepth = 4;
    private const int MaxQueueDepth = 48;

    /// <summary>
    /// Chunks buffered between the transfer pump and the calibrating consumer. Generous on purpose —
    /// at 192 kHz this is ~680 ms of headroom, so a consumer stalled behind one slow 64K-point FFT
    /// tick still never pushes back on the pump. Overflow drops oldest rather than blocking, because
    /// blocking the pump is precisely the DAC underrun this design exists to prevent.
    /// </summary>
    private const int RxChannelCapacity = 256;

    private Qa40xTransport? _transport;
    private Qa40xCalibration? _calibration;
    private Qa40xAutoRanger? _autoRanger;

    // Bookkeeping-only defaults (mirrors SimulatedDevice): the app always calls Set*RangeAsync
    // immediately after ConnectAsync (DeviceSessionService.ConnectAsync), so these values are
    // never relied on to reflect the device's actual power-on register-5/6 state.
    private double _inputRangeDbv = 12.0;
    private double _outputRangeDbv = 8.0;

    private GeneratorConfig _generatorConfig = GeneratorConfig.Off;
    private volatile bool _stopRequested;
    private bool _connected;

    /// <summary>0/1 latch so <see cref="DeviceLost"/> is raised once per connection, however many operations trip over the same unplug. Reset by <see cref="ConnectAsync"/>.</summary>
    private int _deviceLostRaised;

    /// <summary>
    /// The pump for the currently-running stream, or null when nothing is streaming. Every register
    /// write has to go through it while it is non-null — see <see cref="SetInputRangeAsync"/>.
    /// </summary>
    private volatile TransferPump? _activePump;

    /// <summary>
    /// The assembler owning the current stream, for the same reason <see cref="_activePump"/> is
    /// held: <see cref="DroppedBlocks"/> has to report drops as they happen, not only once the
    /// stream has ended and its total has been folded in.
    /// </summary>
    private volatile BlockAssembler? _activeAssembler;

    /// <summary>Drops from an ended stream, kept until the next one starts and zeroes them. Live drops come off <see cref="_activePump"/> — see <see cref="DroppedRxChunks"/>.</summary>
    private long _droppedRxChunks;

    /// <summary>Block drops from an ended stream, kept until the next one starts and zeroes them. Live drops come off <see cref="_activeAssembler"/> — see <see cref="DroppedBlocks"/>.</summary>
    private long _droppedBlocks;

    /// <summary>
    /// Last telemetry reading, or null until the first one lands (<see cref="ConnectAsync"/> takes
    /// it, <see cref="PingAsync"/> refreshes it). Written and read from different threads — a
    /// request thread behind a status poll, and whichever thread connected — so the reference is
    /// published atomically and the record is immutable; readers get either the previous reading or
    /// the new one, never a half-updated set of four numbers.
    /// </summary>
    private volatile DeviceTelemetry? _telemetry;

    /// <summary>
    /// Cleared the first time a telemetry read fails for a reason other than the device being gone,
    /// and not tried again until the next <see cref="ConnectAsync"/>. These are tier-2 registers
    /// (see <see cref="Qa40xRegisters.TelemetryUsbVoltage"/>): a firmware that does not implement
    /// them may simply not answer, and four one-second timeouts on every status poll would turn a
    /// cosmetic readout into a stalled status bar.
    /// </summary>
    private volatile bool _telemetryAvailable = true;

    public event EventHandler? DeviceLost;

    public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

    /// <inheritdoc/>
    public DeviceTelemetry? Telemetry => _telemetry;

    /// <summary>
    /// Whether <see cref="StreamAsync"/> is allowed to step the input range on its own (clip /
    /// under-range detection via <see cref="Qa40xAutoRanger"/>). Not part of <see cref="IAnalyzerDevice"/>
    /// — this is a hardware-only knob, same pattern as <c>SimulatedDevice.SignalProfile</c>.
    ///
    /// Defaults to <c>true</c> — the app's Auto checkbox (moved to the Acquisition panel) reflects
    /// this directly, and auto-ranging is the expected default for a fresh measurement.
    ///
    /// Historical note: this used to default to <c>false</c>, because back when the app had no real
    /// Auto control, leaving it on meant an explicit <see cref="SetInputRangeAsync"/> got silently
    /// undone within ~10 ms of the stream running: <see cref="Qa40xAutoRanger.Evaluate"/> treats a
    /// peak more than <see cref="Qa40xAutoRanger.HeadroomDb"/> dB below the next lower range's full
    /// scale as under-range, so any normal signal — or no signal at all — walked the range back down
    /// to the most sensitive setting one step per <see cref="Qa40xAutoRanger.StepDownDelayMs"/>.
    /// Audible on real hardware as the 24 dB
    /// attenuator relay clicking in on selection and straight back out. That was a coordination bug
    /// between this class's own <c>_inputRangeDbv</c> (which autorange steps) and
    /// <c>DeviceSessionService</c>'s cached view of the range — if clicking or a stuck-looking range
    /// selector reappears, check that those two are still staying in sync.
    ///
    /// Steps are plumbed back to <see cref="InputRangeDbv"/>, which the app's status endpoint reads,
    /// so the UI's range selector follows the hardware when this is on.
    /// </summary>
    public bool AutoRangeEnabled { get; set; } = true;

    /// <summary>
    /// Backing value for <see cref="AutoRangeStepDownDelayMs"/> before <see cref="_autoRanger"/>
    /// exists (i.e. before <see cref="ConnectAsync"/>), and the value <see cref="ConnectAsync"/>
    /// seeds a freshly created ranger with. Once connected, <see cref="_autoRanger"/>'s own property
    /// is the live source of truth — this field is kept in sync by the setter below purely so a
    /// caller reading it back before connecting sees what it just set.
    /// </summary>
    private double _autoRangeStepDownDelayMs = Qa40xAutoRanger.DefaultStepDownDelayMs;

    /// <inheritdoc />
    public double AutoRangeStepDownDelayMs
    {
        get => _autoRanger?.StepDownDelayMs ?? _autoRangeStepDownDelayMs;
        set
        {
            _autoRangeStepDownDelayMs = value;
            if (_autoRanger is not null)
                _autoRanger.StepDownDelayMs = value;
        }
    }

    /// <summary>
    /// Same before/after-connect arrangement as <see cref="_autoRangeStepDownDelayMs"/>: the live
    /// source of truth is the ranger's own property once one exists.
    /// </summary>
    private bool _autoRangeStepDownEnabled = true;

    /// <inheritdoc />
    public bool AutoRangeStepDownEnabled
    {
        get => _autoRanger?.StepDownEnabled ?? _autoRangeStepDownEnabled;
        set
        {
            _autoRangeStepDownEnabled = value;
            if (_autoRanger is not null)
                _autoRanger.StepDownEnabled = value;
        }
    }

    /// <inheritdoc />
    public double InputRangeDbv => Volatile.Read(ref _inputRangeDbv);

    /// <inheritdoc />
    public long DroppedRxChunks => Interlocked.Read(ref _droppedRxChunks) + (_activePump?.DroppedChunks ?? 0);

    /// <inheritdoc />
    public long DroppedBlocks => Interlocked.Read(ref _droppedBlocks) + (_activeAssembler?.DroppedBlocks ?? 0);

    /// <summary>Transfers kept in flight per direction at a given sample rate — see <see cref="TargetInFlightSeconds"/>.</summary>
    internal static int QueueDepthFor(double sampleRateHz) => Math.Clamp(
        (int)Math.Ceiling(TargetInFlightSeconds * sampleRateHz / TransferChunkFrames), MinQueueDepth, MaxQueueDepth);

    /// <summary>
    /// One extra chunk on top of the TX queue, covering the device's own output FIFO and the analog
    /// path — neither of which this app can observe, and both of which sit after the last transfer
    /// this side has handed over. A tuning constant, not a protocol fact.
    ///
    /// Sized against the measurement rather than guessed at: the observed latency ran 128-256 frames
    /// past <see cref="QueueDepthFor"/> × <see cref="TransferChunkFrames"/> at 48, 96 and 192 kHz, and
    /// that excess was itself the 128-frame resolution of the detector. One chunk is comfortably over
    /// it at every rate. Over-discarding is close to free (it delays the first post-change spectrum by
    /// ~10 ms, against a buffer refill measured in hundreds of ms) whereas under-discarding puts the
    /// discontinuity straight back into the first record.
    /// </summary>
    private const int StimulusLatencySlackFrames = TransferChunkFrames;

    /// <inheritdoc />
    public int StimulusLatencyFrames(double sampleRateHz) =>
        (QueueDepthFor(sampleRateHz) * TransferChunkFrames) + StimulusLatencySlackFrames;

    public async Task<DeviceInfo> ConnectAsync(CancellationToken ct)
    {
        var transport = new Qa40xTransport
        {
            TraceTeardown = step => logger.LogInformation("QA403 teardown: {Step}", step),
        };

        try
        {
            transport.Open();
            logger.LogInformation("QA403 USB interface claimed");

            // Recovery *before* the connection verify, not after. The QA403 keeps streaming across a
            // host process going away: register 8 is device state, and nothing on the USB side
            // (interface release, handle close, re-enumeration) clears it. A host that died without
            // writing StreamingStop — hard kill, crash, console window closed past the OS grace
            // period — leaves the device pushing EP2 data forever, and previously only a physical
            // power cycle recovered it, for QuantAsylum's app as much as this one.
            //
            // This ran after the verify in the first attempt at the fix, which is why that attempt
            // did nothing: against an already-wedged device the verify is itself what fails (its
            // register readback races a device still mid-stream), so the recovery was never reached.
            // Nothing here depends on the device answering correctly, so it is safe to do first.
            await transport.WriteRegisterAsync(Qa40xRegisters.StreamingEnable, Qa40xRegisters.StreamingStop, ct).ConfigureAwait(false);
            int flushed = await transport.DrainDataEndpointAsync(ct).ConfigureAwait(false);
            if (flushed > 0)
                logger.LogWarning("QA403 was still streaming on connect — flushed {Bytes} stale bytes off EP2", flushed);

            // Connection verify: write a random uint32 to register 0, read it back (Usb.cs:189-204).
            // Retried once: the first exchange after the recovery above can still pick up a stale
            // response that was already in flight when StreamingStop landed.
            if (!await TryVerifyAsync(transport, ct).ConfigureAwait(false)
                && !await TryVerifyAsync(transport, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("QA403 connection verification failed (register 0 readback did not match).");
            }

            // Front-panel I2S expansion port off, in the reference's own position: after the stream
            // stop, before the calibration read. It is the same kind of leftover state as register 8
            // — a vendor-app session that left the port playing keeps playing across that process
            // exiting and across this one opening the device — and this app never drives it, so
            // without this write it could only ever be silenced by someone else's software.
            //
            // After the verify rather than beside the StreamingStop above: that recovery is placed
            // where it is because it must reach a wedged device, and this write has no such claim on
            // it. Failures are swallowed for the reason the telemetry reads are (tier-2, sourced from
            // one project's USB capture of a QA402) — a firmware that does not implement 0x0A must
            // not be a firmware this app refuses to connect to.
            try
            {
                await transport.WriteRegisterAsync(Qa40xRegisters.I2sControl, Qa40xRegisters.I2sStop, ct).ConfigureAwait(false);

                // Logged on the way through, at Debug, because there is no other way to see this
                // happen: the port is write-only, nothing on screen reflects it, and a write that was
                // swallowed above looks exactly like one that was never reached.
                logger.LogDebug("QA403 front-panel I2S stopped at connect (register 0x0A = 0)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not Qa40xDeviceLostException)
            {
                logger.LogDebug(ex, "QA403 did not accept the front-panel I2S stop at connect — continuing");
            }

            var calibration = await Qa40xCalibration.LoadAsync(transport, ct).ConfigureAwait(false);

            // Tier-2 registers (docs/protocol.md §2.4: sourced from qa40x-rs-main's own USB-traffic
            // observations only, not cross-checked against PyQa40x/qa40x-baremetal). Read-only, so
            // worst case is a cosmetic string, not a protocol violation.
            uint firmwareRaw = await transport.ReadRegisterAsync(Qa40xRegisters.FirmwareVersion, ct).ConfigureAwait(false);
            uint serialRaw = await transport.ReadRegisterAsync(Qa40xRegisters.SerialNumber, ct).ConfigureAwait(false);

            // Taken here as well as on the ping so the status bar has numbers from the first poll
            // rather than from the first ping 1.5 s later. A replug may be a different unit, so the
            // "don't ask again" latch is reset with the connection it belongs to.
            _telemetryAvailable = true;
            await RefreshTelemetryAsync(transport, ct).ConfigureAwait(false);

            _transport = transport;
            _calibration = calibration;
            _autoRanger = new Qa40xAutoRanger(Capabilities.InputRangesDbv)
            {
                StepDownDelayMs = _autoRangeStepDownDelayMs,
                StepDownEnabled = _autoRangeStepDownEnabled,
            };
            _connected = true;

            // This instance is reusable across an unplug/replug: HandleDeviceLost releases the dead
            // handle rather than leaving it claimed, so a later connect opens a fresh transport into
            // the same object. Both latches belong to the *previous* connection and have to clear
            // with it, or the reconnected device would never report a second loss and would stream
            // straight into a stale stop request.
            _deviceLostRaised = 0;
            _stopRequested = false;

            var info = new DeviceInfo("QA403", serialRaw.ToString(), firmwareRaw.ToString(), IsSimulated: false);
            logger.LogInformation(
                "Connected to QA403 (serial {Serial}, fw {Firmware})", info.SerialNumber, info.FirmwareVersion);
            return info;
        }
        catch
        {
            // Nothing above assigns _transport until every step has succeeded, so on any failure
            // (device not found, verify mismatch, calibration read failure...) this local instance
            // is the only reference to the underlying UsbContext. Dispose it explicitly — an
            // undisposed UsbContext left for the GC to finalize can crash the process outright
            // (observed: NullReferenceException inside LibUsbDotNet's own finalizer).
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sets the input range. While a stream is running this is *handed to the transfer pump* rather
    /// than written here, for two independent reasons — both of which were live bugs:
    ///
    /// <para>Writing register 5 from the caller's thread puts a register write on EP1 while bulk
    /// transfers are in flight on EP2, which is precisely the thing <c>TransferPump.StepRangeIfNeeded</c>
    /// documents as wedging this device's USB stack — observed as a native transfer that never
    /// completes, leaving the QA403 unreachable to every app until it was power-cycled. Autoranging
    /// always quiesced EP2 first; a manual range change did not.</para>
    ///
    /// <para>And the running pump keeps its own <c>_inputRangeDbv</c>, which is what stamps each
    /// <c>RxChunk</c> and therefore what <see cref="Qa40xCalibration.RawToVolts"/> compensates with.
    /// Writing only this class's field left the pump converting at the range the stream *started*
    /// on, so selecting 24 dBV attenuated the signal in hardware while the spectrum was still scaled
    /// for 0 dBV — the whole 24 dB landing on screen as a level drop. Autoranging hid this by
    /// updating the pump's copy on every step; with it off, the range simply never reached the
    /// conversion.</para>
    /// </summary>
    public async Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(Capabilities.InputRangesDbv, dbv, nameof(dbv));
        var transport = RequireTransport();

        if (_activePump is { } pump)
        {
            await pump.RequestInputRangeAsync(dbv, ct).ConfigureAwait(false);
            _autoRanger?.Reset();
            return;
        }

        try
        {
            await transport.WriteRegisterAsync(Qa40xRegisters.InputRange, Qa40xRegisters.InputRangeDbvToRegister(dbv), ct).ConfigureAwait(false);
        }
        catch (Qa40xDeviceLostException)
        {
            HandleDeviceLost("input range write");
            throw;
        }

        Volatile.Write(ref _inputRangeDbv, dbv);
        _autoRanger?.Reset();
    }

    /// <summary>
    /// Same EP1-while-EP2-is-busy hazard as <see cref="SetInputRangeAsync"/> — routed through the pump
    /// for the same reason, and for the second reason too: the range that scales a TX chunk has to flip
    /// at exactly the same instant the attenuator does.
    ///
    /// <para>The field written below is a bookkeeping mirror (it seeds the *next* pump), not what
    /// <c>TransferPump.BuildTxChunk</c> scales by. It used to be both, and because it was only written
    /// after the pump had already programmed register 6, the <c>Prime()</c> that immediately follows the
    /// register write filled a whole queue's worth of TX chunks with the *old* range's scaling while the
    /// analog attenuator was already on the new one. Measured in loopback at 48 kHz: a -30 dBV tone came
    /// out at -0.1 dBV for ~30 ms across a -12 -> 18 dBV change (30 dB too loud, i.e. the whole range
    /// step) and at -39.5 dBV across an 18 -> 8 dBV one. A burst like that inside an FFT record is a
    /// broadband smear — it reads as the noise floor jumping — and on a widening step it is a real 10-30 dB
    /// overshoot into whatever the generator is driving.</para>
    /// </summary>
    public async Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(Capabilities.OutputRangesDbv, dbv, nameof(dbv));
        var transport = RequireTransport();

        if (_activePump is { } pump)
        {
            await pump.RequestOutputRangeAsync(dbv, ct).ConfigureAwait(false);
            Volatile.Write(ref _outputRangeDbv, dbv);
            return;
        }

        try
        {
            await transport.WriteRegisterAsync(Qa40xRegisters.OutputRange, Qa40xRegisters.OutputRangeDbvToRegister(dbv), ct).ConfigureAwait(false);
        }
        catch (Qa40xDeviceLostException)
        {
            HandleDeviceLost("output range write");
            throw;
        }

        Volatile.Write(ref _outputRangeDbv, dbv);
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
        if (config.BlockFrameCount % RequiredBlockFrameMultiple != 0)
        {
            throw new ArgumentException(
                $"BlockFrameCount must be a multiple of {RequiredBlockFrameMultiple} samples (QA403 output buffer "
                    + $"constraint, docs/qa403-app-stack-spec.md §2.1) — got {config.BlockFrameCount}. A non-multiple "
                    + "would emit garbled audio rather than a clear error, so this is enforced up front.",
                nameof(config));
        }

        var transport = RequireTransport();
        var calibration = _calibration ?? throw new InvalidOperationException("Device is not connected. Call ConnectAsync first.");
        var autoRanger = _autoRanger ?? throw new InvalidOperationException("Device is not connected. Call ConnectAsync first.");

        _stopRequested = false;

        // Sample rate register + settle (reference/PyQa40x-main/PyQa40x-main/src/PyQa40x/control.py:40-49).
        await transport.WriteRegisterAsync(Qa40xRegisters.SampleRate, Qa40xRegisters.SampleRateHzToRegister(config.SampleRateHz), ct).ConfigureAwait(false);
        await Task.Delay(Qa40xRegisters.SampleRateSettleMs, ct).ConfigureAwait(false);

        // Start streaming before sending any data (Acquisition.cs:126-129).
        await transport.WriteRegisterAsync(Qa40xRegisters.StreamingEnable, Qa40xRegisters.StreamingStart, ct).ConfigureAwait(false);

        var pump = new TransferPump(
            transport, calibration, autoRanger, logger,
            // The generator config is a Func (it changes with no register write, so the pump has no
            // point at which to latch it), but the output range is passed by value: from here on the
            // pump owns it, and flips it at the instant it programs register 6 — see SetOutputRangeAsync.
            () => _generatorConfig, Volatile.Read(ref _outputRangeDbv),
            // A Func, not the captured bool: the auto/manual checkbox has to take effect on a
            // running stream, not only on the next one.
            Volatile.Read(ref _inputRangeDbv), () => AutoRangeEnabled, config.SampleRateHz, () => _stopRequested,
            // Keep the device's own view of the input range in step with what autoranging actually
            // programmed, so a later SetInputRangeAsync/status read isn't working from a stale value.
            stepped => Volatile.Write(ref _inputRangeDbv, stepped));

        // Calibration and block assembly run on their own thread, not on thread-pool continuations —
        // see BlockAssembler. Measured on real hardware before this existed: a 100 s sweep dropped 75
        // RX chunks, i.e. the reader of the pump's 683 ms channel went unscheduled for longer than
        // that, repeatedly, while the FFT worker had the pool.
        var assembler = new BlockAssembler(pump.Reader, calibration, config, logger);
        long reportedChunkDrops = 0;
        long reportedBlockDrops = 0;

        // Everything from pump start to pump join must sit inside this try/finally. A consumer that
        // leaves the `await foreach` early — `break`, an exception, or just not enumerating to the
        // end — disposes the iterator, and iterator disposal resumes the state machine in dispose
        // mode: it runs `finally` handlers and nothing else. Teardown placed *after* the loop is
        // simply never executed on that path. That is the normal way this stream ends (both hardware
        // tests `break` out of it), and skipping teardown breaks the one invariant qa40x-baremetal
        // never breaks — it collects every in-flight buffer before returning, on the cancellation
        // path too (Acquisition.cs:168-181). Leaving libusb transfers submitted while Dispose() goes
        // on to call libusb_close is undefined behaviour, and in practice wedges the process:
        // LibUsbDotNet's UsbDevice.Close() signals its event-handling thread to stop *before* closing
        // the device handle (UsbDevice.cs:600-617), and libusb_close then waits — with no timeout —
        // for an acknowledgement from an event thread that may already have exited. That wait is a
        // native, uninterruptible block: no CancellationToken or C#-level timeout can reach it, and
        // the process has to be killed. Whether the race is lost depends on transfer timing, which is
        // why the hang was intermittent rather than constant.
        try
        {
            // Both counters start again with the stream, so what they report is always "drops in the
            // acquisition you are looking at" rather than a total since the device was plugged in.
            //
            // That distinction is the whole use of them. A drop is a hole in the sample stream, which
            // splices the next FFT record — so the only question anyone asks of these numbers is
            // whether *this* measurement can be trusted, and a running total cannot answer it. It
            // actively misleads: a stale 8 from an earlier run reads exactly like 8 that just
            // happened, and the only way to tell was to note the number, run again, and subtract.
            // Every runner starts its own acquisition, so a measurement gets a clean count for free.
            Interlocked.Exchange(ref _droppedRxChunks, 0);
            Interlocked.Exchange(ref _droppedBlocks, 0);

            _activePump = pump;
            _activeAssembler = assembler;
            pump.Start(ct);
            assembler.Start();

            // Deliberately *not* passing ct here. Cancellation is observed by the pump thread, which
            // stops and completes its channel; the assembler follows and completes this one — so this
            // loop ends by running out of blocks, the same clean end-of-enumeration the pre-channel
            // version had. Passing ct would instead make ReadAllAsync throw OperationCanceledException
            // out of StreamAsync, turning what every caller treats as a normal stop into an exception,
            // and would abandon blocks already captured rather than flushing them.
            await foreach (var block in assembler.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Logged here rather than where any of it is measured: this loop is off both the pump
                // thread and the assembler thread, so the file/console I/O behind an ILogger call
                // cannot land between one transfer completing and the next being submitted.
                LogPumpStalls(pump.Diagnostics);

                // Reported as it happens, not just as a total at stream end. A drop total printed once
                // the run is over says a measurement was invalid but not *which* part of it — and the
                // whole question a sweep raises is whether the holes clustered on one point or were
                // spread across all of them.
                long chunkDrops = pump.DroppedChunks;
                long blockDrops = assembler.DroppedBlocks;
                if (chunkDrops != reportedChunkDrops || blockDrops != reportedBlockDrops)
                {
                    LogDropBurst(
                        chunkDrops - reportedChunkDrops, blockDrops - reportedBlockDrops,
                        chunkDrops, blockDrops, config);
                    reportedChunkDrops = chunkDrops;
                    reportedBlockDrops = blockDrops;
                }

                yield return block;
            }
        }
        finally
        {
            // Cleared before the join, not after: once teardown has begun the pump can no longer
            // service queued register writes from its loop, so a concurrent SetInputRangeAsync must
            // fall through to writing directly rather than queueing onto a pump that is going away.
            _activePump = null;

            // Cleared alongside the pump, and before the join for the same reason: past this point
            // the live term must stop contributing so the fold-in below cannot double-count it.
            _activeAssembler = null;

            // Strictly before the pump's join, and that order is load-bearing: StopAndJoinAsync ends
            // by draining whatever RxChunks are still sitting in the channel and returning their
            // buffers to the pool. An assembler still reading that same channel would be returning
            // the same buffers concurrently, and a double return to ArrayPool hands one array to two
            // owners. Stopping the assembler first makes the pump the sole owner of that channel.
            assembler.StopAndJoin();

            // Stops the pump thread and waits for it. The pump owns every EP2 transfer and the
            // StreamingStop write, and does both in its own finally — so by the time this returns the
            // data plane is quiet and the device has been told to stop.
            bool deviceLost = await pump.StopAndJoinAsync().ConfigureAwait(false);

            // After the join, so nothing is still being recorded: whatever the loop noticed on its
            // last iterations gets logged rather than dropped, and the summary is final.
            LogPumpStalls(pump.Diagnostics);
            LogPumpSummary(pump.Diagnostics, config.SampleRateHz);

            // Folded in only after the join, when the pump can no longer drop anything — reading it
            // while still running would double-count against the live term in DroppedRxChunks.
            long dropped = pump.DroppedChunks;
            long droppedBlocks = assembler.DroppedBlocks;
            if (dropped > 0 || droppedBlocks > 0)
            {
                Interlocked.Add(ref _droppedRxChunks, dropped);
                Interlocked.Add(ref _droppedBlocks, droppedBlocks);

                // The two counters answer different questions, which is the entire reason they are
                // separate. RX chunks drop when the *assembler* could not keep up with the pump;
                // blocks drop when whoever consumes this stream could not keep up with the assembler.
                // A single combined number would have said "something fell behind" and left the next
                // person to guess which half — exactly the guess this split exists to remove.
                logger.LogWarning(
                    "QA403 stream lost data: {Chunks} RX chunks ({ChunkFrames} frames) dropped before calibration, "
                        + "{Blocks} assembled blocks ({BlockFrames} frames) dropped after it — FFT records over this run "
                        + "contain splices and any noise-floor reading from them is suspect",
                    dropped, dropped * TransferChunkFrames,
                    droppedBlocks, droppedBlocks * config.BlockFrameCount);
            }

            // _activePump was cleared above and the pump has joined, so this releases the handle here
            // rather than leaving it claimed by a device that is no longer on the bus.
            if (deviceLost)
                HandleDeviceLost("mid-stream");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A register read (firmware version — read-only, so it changes nothing) is the cheapest honest
    /// answer to "is it still there?": it is the same EP1 exchange every other control operation
    /// uses, so it fails exactly when they would.
    ///
    /// <para>Skipped entirely while a stream is running, and not because it would be wasteful: a
    /// register exchange on EP1 with EP2 transfers in flight is the documented way to wedge this
    /// device's USB stack. The running pump is the better detector there anyway — it reads EP2
    /// continuously and reports a loss through <see cref="DeviceLost"/> within one transfer.</para>
    ///
    /// <para>Only a device-lost failure answers false. A timeout or a short read is a hiccup on a
    /// device that is still present, and reporting those as a disconnect would tear down a working
    /// session over one slow exchange.</para>
    /// </remarks>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        if (!_connected)
            return false;

        if (_activePump is not null)
            return true;

        var transport = _transport;
        if (transport is null)
            return false;

        try
        {
            await transport.ReadRegisterAsync(Qa40xRegisters.FirmwareVersion, ct).ConfigureAwait(false);

            // Rides along with the liveness exchange rather than running on a timer of its own: it
            // needs exactly the conditions this method has already established — connected, no
            // pump, throttled by the caller — and a second path onto the control endpoint would be
            // a second chance to get it wrong.
            await RefreshTelemetryAsync(transport, ct).ConfigureAwait(false);
            return true;
        }
        catch (Qa40xDeviceLostException)
        {
            HandleDeviceLost("liveness ping");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "QA403 liveness ping failed without a disconnect signal — treating the device as still present");
            return true;
        }
    }

    /// <summary>
    /// The single reaction to "this device is gone", from wherever it was noticed: a register write,
    /// the liveness ping, or the transfer pump.
    ///
    /// Releasing the handle is the point of it. A removed device leaves a claimed interface behind,
    /// and holding that means the replugged QA403 cannot be opened by this process (or any other)
    /// without a restart — the same "needs a power cycle" symptom a wedged process produces. With the
    /// transport disposed and the fields cleared, <see cref="ConnectAsync"/> works again as-is.
    ///
    /// <para>Ordering matters when a stream is running: the pump owns every in-flight EP2 transfer,
    /// and disposing the transport under it puts <c>libusb_close</c> in exactly the position
    /// <see cref="StreamAsync"/>'s remarks warn about. So a loss noticed while the pump is live only
    /// asks it to stop; the stream's own finally joins it and calls back in here, by which point
    /// <see cref="_activePump"/> is null and the release is safe.</para>
    /// </summary>
    /// <summary>
    /// Logs whatever the pump has recorded since the last call. Warning level on purpose: the app
    /// ships at Information, and a DAC gap that only shows up under Debug is a DAC gap nobody sees.
    /// </summary>
    private void LogPumpStalls(TransferPumpDiagnostics diagnostics)
    {
        if (!diagnostics.TryDrain(out var stalls))
            return;

        foreach (var stall in stalls)
        {
            logger.LogWarning(
                "QA403 DAC starvation [{Cause}] iter {Iteration}: iteration {IterationMs:0.0} ms "
                    + "(read wait {ReadWaitMs:0.0}, write wait {WriteWaitMs:0.0}, submit {SubmitMs:0.0}, "
                    + "register+prime {RegisterMs:0.0}), TX slack now {SlackMs:0.0} ms of {PrimedMs:0.0}; "
                    + "GC pause {GcPauseMs:0.0} ms total (gen0 {Gen0}/gen1 {Gen1}/gen2 {Gen2}), "
                    + "pool {ThreadPoolThreads} threads / {ThreadPoolPending} queued"
                    + "{MissingNote}",
                stall.Cause, stall.Iteration, stall.IterationMs,
                stall.ReadWaitMs, stall.WriteWaitMs, stall.SubmitMs, stall.RegisterMs,
                stall.SlackMs, diagnostics.PrimedSlackMs,
                stall.GcPauseMs, stall.Gen0, stall.Gen1, stall.Gen2,
                stall.ThreadPoolThreads, stall.ThreadPoolPending,
                MissingBytesNote(stall));
        }
    }

    /// <summary>
    /// The tail of a stall line, naming what the event actually lost. The two directions fail
    /// differently and must not read alike: TX bytes never sent are a gap in the generator output,
    /// RX bytes never delivered are a splice in the analysis.
    /// </summary>
    private static string MissingBytesNote(in PumpStall stall) => stall switch
    {
        { MissingBytes: <= 0 } => string.Empty,
        { Cause: PumpStallCause.ShortWrite } => $"; {stall.MissingBytes} TX bytes never sent",
        { Cause: PumpStallCause.ShortRead } =>
            $"; {stall.MissingBytes} RX bytes never delivered — any FFT record spanning this instant is spliced",
        _ => $"; {stall.MissingBytes} bytes lost",
    };

    /// <summary>
    /// One burst of lost data, logged the moment the consumer notices the counters moved rather than
    /// as a total once the run is over. A sweep point spans a known wall-clock window, so a timestamped
    /// burst is what makes "this point's THD is contaminated" answerable instead of "some point's was".
    /// </summary>
    private void LogDropBurst(long chunkDelta, long blockDelta, long chunkTotal, long blockTotal, AcquisitionConfig config)
    {
        logger.LogWarning(
            "QA403 lost data now: +{ChunkDelta} RX chunks ({ChunkFrames} frames, before calibration), "
                + "+{BlockDelta} blocks ({BlockFrames} frames, after it); run totals {ChunkTotal}/{BlockTotal}. "
                + "Any FFT record spanning this instant is spliced",
            chunkDelta, chunkDelta * TransferChunkFrames,
            blockDelta, blockDelta * config.BlockFrameCount,
            chunkTotal, blockTotal);
    }

    /// <summary>
    /// End-of-stream verdict. <c>PrimedSlackMs - MinSlackMs</c> is the total DAC time the host failed
    /// to cover over the whole run — the sum of every flat span a scope would have shown — so a clean
    /// run reports a number near zero and a run with gaps reports their total directly.
    /// </summary>
    private void LogPumpSummary(TransferPumpDiagnostics diagnostics, double sampleRateHz)
    {
        double lostMs = diagnostics.PrimedSlackMs - diagnostics.MinSlackMs;

        // A few ms is host-vs-device clock drift over a long run, not starvation — see the slack
        // metric's remarks. Report at Warning only once it is past anything drift can explain.
        var level = lostMs > 2.0 || diagnostics.ShortWrites > 0 || diagnostics.ShortReads > 0
            ? Microsoft.Extensions.Logging.LogLevel.Warning
            : Microsoft.Extensions.Logging.LogLevel.Information;

        logger.Log(
            level,
            "QA403 transfer summary at {SampleRateHz:0} Hz: {Iterations} iterations, worst {WorstMs:0.0} ms, "
                + "TX slack floor {MinSlackMs:0.0} ms of {PrimedMs:0.0} (≈{LostMs:0.0} ms of DAC time lost), "
                + "{ShortWrites} short writes ({ShortWriteBytes} bytes), "
                + "{ShortReads} short reads ({ShortReadBytes} bytes), "
                + "{GcPauseMs:0.0} ms GC pause over gen0 {Gen0}/gen1 {Gen1}/gen2 {Gen2}"
                + "{Suppressed}",
            sampleRateHz, diagnostics.Iterations, diagnostics.WorstIterationMs,
            diagnostics.MinSlackMs, diagnostics.PrimedSlackMs, lostMs,
            diagnostics.ShortWrites, diagnostics.ShortWriteBytes,
            diagnostics.ShortReads, diagnostics.ShortReadBytes,
            diagnostics.GcPauseDuringStream.TotalMilliseconds,
            diagnostics.Gen0Collections, diagnostics.Gen1Collections, diagnostics.Gen2Collections,
            diagnostics.SuppressedStalls > 0 ? $", {diagnostics.SuppressedStalls} further events not logged" : string.Empty);
    }

    /// <summary>
    /// Reads the telemetry registers and publishes the decoded reading. Callers must already
    /// hold the conditions that make a control exchange safe — connected, and no transfer pump
    /// running (see <see cref="PingAsync"/>).
    ///
    /// <para>A device-lost failure is rethrown so the caller reacts to it the way it reacts to any
    /// other dead exchange. Anything else is swallowed and latches the readout off for this
    /// connection: telemetry is a status-bar convenience, and no reading at all is a better outcome
    /// than a status poll that spends three timeouts every 1.5 s discovering the same thing.</para>
    /// </summary>
    private async Task RefreshTelemetryAsync(Qa40xTransport transport, CancellationToken ct)
    {
        if (!_telemetryAvailable)
            return;

        try
        {
            // Three reads, not four: 0x13 is skipped because a QA403 does not answer it with a
            // measurement — see Qa40xRegisters.TelemetryIsoCurrent.
            uint usbVoltageRaw = await transport.ReadRegisterAsync(Qa40xRegisters.TelemetryUsbVoltage, ct).ConfigureAwait(false);
            uint usbCurrentRaw = await transport.ReadRegisterAsync(Qa40xRegisters.TelemetryUsbCurrent, ct).ConfigureAwait(false);
            uint temperatureRaw = await transport.ReadRegisterAsync(Qa40xRegisters.TelemetryTemperature, ct).ConfigureAwait(false);

            _telemetry = Qa40xRegisters.DecodeTelemetry(usbVoltageRaw, usbCurrentRaw, temperatureRaw);
        }
        catch (Qa40xDeviceLostException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _telemetryAvailable = false;
            logger.LogDebug(ex, "QA403 telemetry registers did not answer — not asking again on this connection");
        }
    }

    private void HandleDeviceLost(string context)
    {
        _connected = false;

        // Sensor readings from a device that is no longer there. Dropped rather than left to go
        // quietly stale, so nothing can put a plausible rail voltage beside a disconnected unit.
        _telemetry = null;

        if (_activePump is not null)
        {
            _stopRequested = true;
            RaiseDeviceLostOnce(context);
            return;
        }

        var transport = _transport;
        _transport = null;
        _calibration = null;
        _autoRanger = null;

        if (transport is not null)
        {
            try
            {
                transport.Dispose();
            }
            catch (Exception ex)
            {
                // Nothing left to salvage: the device is gone either way, and the next connect opens
                // a new transport regardless of how this one ended.
                logger.LogWarning(ex, "QA403 transport dispose failed after disconnect");
            }
        }

        RaiseDeviceLostOnce(context);
    }

    private void RaiseDeviceLostOnce(string context)
    {
        if (Interlocked.Exchange(ref _deviceLostRaised, 1) == 1)
            return;

        logger.LogWarning("QA403 disconnected ({Context}) — handle released; reconnect the device and press Connect", context);
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Last-ditch device quiesce plus handle release. Runs when the DI container disposes this
    /// singleton, after <c>DeviceLifecycleHostedService.StopAsync</c> has already stopped any stream.
    ///
    /// The StreamingStop write here is a backstop for the paths where the pump's own finally never
    /// ran — it never started, or it faulted. Writing register 8 twice is harmless; leaving the
    /// device streaming is not, because that state survives the process and nothing on the USB side
    /// clears it. Wrapped in its own try: if the register write throws, the handle must still be
    /// released below, or the *next* process cannot even claim the interface.
    /// </summary>
    public void Dispose()
    {
        var transport = _transport;
        _transport = null;
        if (transport is null)
            return;

        try
        {
            transport.WriteRegisterAsync(Qa40xRegisters.StreamingEnable, Qa40xRegisters.StreamingStop, CancellationToken.None)
                .GetAwaiter().GetResult();
            logger.LogInformation("QA403 teardown: StreamingStop written");
        }
        catch (Exception ex)
        {
            logger.LogWarning("QA403 teardown: final StreamingStop failed ({Error}) — releasing the handle anyway", ex.Message);
        }

        transport.Dispose();
    }

    private static async Task<bool> TryVerifyAsync(Qa40xTransport transport, CancellationToken ct)
    {
        uint verifyValue = unchecked((uint)Random.Shared.Next());
        await transport.WriteRegisterAsync(Qa40xRegisters.ConnectionVerify, verifyValue, ct).ConfigureAwait(false);
        try
        {
            return await transport.ReadRegisterAsync(Qa40xRegisters.ConnectionVerify, ct).ConfigureAwait(false) == verifyValue;
        }
        catch (InvalidOperationException)
        {
            // "read returned no data" — a wedged device's first exchange. Let the caller retry.
            return false;
        }
    }

    private Qa40xTransport RequireTransport() =>
        _transport ?? throw new InvalidOperationException("Device is not connected. Call ConnectAsync first.");

    private static void RequireSupportedRange(IReadOnlyList<double> supported, double value, string paramName)
    {
        if (!supported.Contains(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"Unsupported range. Supported values: {string.Join(", ", supported)} dBV.");
        }
    }

    /// <summary>
    /// One completed RX transfer, handed from the pump thread to the calibrating consumer.
    /// <see cref="Buffer"/> is rented from <see cref="ArrayPool{T}"/> and becomes the consumer's to
    /// return; only the first <see cref="Length"/> bytes are valid. <see cref="InputRangeDbv"/> is the
    /// range that was actually in effect when these samples were captured — carried per chunk rather
    /// than read from a field, so a range step mid-stream can't calibrate a chunk at the wrong range.
    /// </summary>
    private readonly record struct RxChunk(byte[] Buffer, int Length, double InputRangeDbv, int SettleFrames);

    /// <summary>
    /// Turns raw <see cref="RxChunk"/>s into calibrated <see cref="PcmBlock"/>s, on its own thread.
    ///
    /// <para><b>Why a thread, and not an <c>await foreach</c>.</b> This work used to live directly in
    /// <see cref="StreamAsync"/>'s iterator, which meant it ran as thread-pool continuations: one per
    /// chunk, competing with the FFT worker in <c>SpectrumPipeline</c> for the same pool. That is the
    /// same starvation the pump was given a dedicated thread to escape, and it was still costing real
    /// samples — measured on a QA403 over a 100 s sweep at 192 kHz, 75 RX chunks dropped, i.e. the
    /// reader of the pump's 683 ms channel went unscheduled for longer than 683 ms, repeatedly. Per
    /// <c>SampleRingBuffer</c>'s contract those holes are not skipped display frames: the stream is
    /// concatenated, so each one splices an FFT record, smears the tone into a skirt and lifts the
    /// broadband floor — a spurious THD reading, which is exactly the symptom that was reported.</para>
    ///
    /// <para>The pool round-trip does not disappear, it moves: this thread hands finished blocks to a
    /// second channel and <see cref="StreamAsync"/> yields from that. So a consumer that cannot keep
    /// up now backs up <em>behind</em> calibration instead of in front of it, and
    /// <see cref="DroppedBlocks"/> counts that case separately from the pump's own drops. The two
    /// numbers name which half fell behind, which one combined number never could.</para>
    /// </summary>
    private sealed class BlockAssembler
    {
        private readonly ChannelReader<RxChunk> _source;
        private readonly Qa40xCalibration _calibration;
        private readonly AcquisitionConfig _config;
        private readonly ILogger _logger;
        private readonly Channel<PcmBlock> _output;
        private readonly CancellationTokenSource _stop = new();
        private long _droppedBlocks;
        private Thread? _thread;

        public BlockAssembler(
            ChannelReader<RxChunk> source, Qa40xCalibration calibration, AcquisitionConfig config, ILogger logger)
        {
            _source = source;
            _calibration = calibration;
            _config = config;
            _logger = logger;

            // Roughly a second of assembled audio. Drop-oldest for the same reason the RX channel uses
            // it — blocking here would push back onto this thread, which would stop draining the RX
            // channel, which is the drop this class exists to prevent. Losing the oldest block is the
            // lesser of the two, and it is counted rather than silent.
            int capacity = Math.Max(8, (int)Math.Ceiling(config.SampleRateHz / config.BlockFrameCount));
            _output = Channel.CreateBounded<PcmBlock>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                },
                itemDropped: dropped =>
                {
                    dropped.Dispose();
                    Interlocked.Increment(ref _droppedBlocks);
                });
        }

        public ChannelReader<PcmBlock> Reader => _output.Reader;

        /// <summary>Blocks discarded because whoever consumes this stream fell behind calibration — see the type remarks.</summary>
        public long DroppedBlocks => Interlocked.Read(ref _droppedBlocks);

        public void Start()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "QA403 block assembler",

                // Above the pool's normal priority, below the pump's: this must not be preempted by
                // the DSP work it exists to be independent of, and must not preempt transfer refill.
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>
        /// Stops the thread and waits for it. Must complete before the pump drains its channel — see
        /// the call site: both would otherwise be returning the same rented buffers.
        /// </summary>
        public void StopAndJoin()
        {
            _stop.Cancel();

            if (_thread is { } thread && !thread.Join(TimeSpan.FromSeconds(2)))
                _logger.LogWarning("QA403 block assembler did not exit within 2 s");

            _stop.Dispose();

            while (_output.Reader.TryRead(out var leftover))
                leftover.Dispose();
        }

        private void Run()
        {
            float[]? blockBuffer = null;
            int framesInBlock = 0;
            long sequenceNumber = 0;
            int settleFramesRemaining = 0;
            bool startsAfterDiscontinuity = false;

            try
            {
                // Blocking waits, on a dedicated thread, for the same reason TransferPump blocks on
                // its transfers: it is not a pool thread, so parking it costs nothing that matters.
                while (_source.WaitToReadAsync(_stop.Token).AsTask().GetAwaiter().GetResult())
                {
                    while (_source.TryRead(out var chunk))
                    {
                        // A range step reports its settle window on the first chunk captured *after*
                        // the register write, so this chunk's own samples are still valid at its
                        // stated range.
                        if (chunk.SettleFrames > 0)
                        {
                            settleFramesRemaining = chunk.SettleFrames;

                            // The step is a hole in the stream, not just a level change: the quiesce
                            // that performed it drained and discarded every completed RX transfer
                            // (~one queue depth, ~64 ms at 192 kHz) before these settle frames were
                            // even captured. Whatever is half-assembled here is pre-step audio with
                            // post-step audio about to be appended straight onto it, so the block it
                            // would become is spliced at an arbitrary interior point. Drop it and
                            // start clean; the flag rides the next completed block out to the
                            // consumer, which flushes rather than analyzing across the hole. See
                            // PcmBlock.StartsAfterDiscontinuity.
                            if (blockBuffer is not null)
                            {
                                ArrayPool<float>.Shared.Return(blockBuffer);
                                blockBuffer = null;
                                framesInBlock = 0;
                            }

                            // Not assigned: a second step landing before the flagged block has been
                            // emitted must not clear the flag the first one raised.
                            startsAfterDiscontinuity = true;
                        }

                        int chunkFrameCount = chunk.Length / BytesPerFrame;
                        for (int i = 0; i < chunkFrameCount; i++)
                        {
                            if (settleFramesRemaining > 0)
                            {
                                settleFramesRemaining--;
                                continue;
                            }

                            blockBuffer ??= ArrayPool<float>.Shared.Rent(_config.BlockFrameCount * _config.ChannelCount);

                            // Deinterleaved and calibrated straight into the outgoing block — no
                            // intermediate per-chunk float arrays. There is deliberately **no channel
                            // swap here**. The QA403's swap is on the DAC only — PyQa40x states it as
                            // "On QA402 and QA403, *outputs* need to be swapped" (analyzer.py:113) and
                            // correspondingly reads its ADC data straight through, left =
                            // interleaved[0::2] / right = interleaved[1::2] (analyzer.py:174-175),
                            // applying its channel-0 ("left") calibration record to wire slot 0.
                            // TransferPump.BuildTxChunk already applies the swap on the way out, so
                            // applying it again here would swap twice and cross the channels: a tone
                            // generated on logical left would come back on logical right.
                            //
                            // This was measured on real hardware rather than inferred. Driving a raw
                            // sine on wire slot 1 alone put it at -6.00 dBFS on RX slot 0 with RX slot
                            // 1 at -94 dBFS, and driving slot 0 alone mirrored it exactly — TX slot N
                            // returns on RX slot 1-N. qa40x-baremetal does swap on receive
                            // (Acquisition.cs:193, FromByteStream(rxData, out r.Right, out r.Left)) and
                            // an earlier version of this code followed it, which is what crossed the
                            // channels; that reference sends 1 kHz left / 2 kHz right but only ever
                            // prints per-channel *peaks*, never which frequency arrived where, so a
                            // crossed pair is invisible to it.
                            int byteOffset = i * BytesPerFrame;
                            int slot0Raw = BinaryPrimitives.ReadInt32LittleEndian(chunk.Buffer.AsSpan(byteOffset));
                            int slot1Raw = BinaryPrimitives.ReadInt32LittleEndian(chunk.Buffer.AsSpan(byteOffset + 4));

                            int baseIndex = framesInBlock * _config.ChannelCount;
                            blockBuffer[baseIndex] = (float)_calibration.RawToVolts(slot0Raw, chunk.InputRangeDbv, 0);
                            if (_config.ChannelCount > 1)
                                blockBuffer[baseIndex + 1] = (float)_calibration.RawToVolts(slot1Raw, chunk.InputRangeDbv, 1);
                            framesInBlock++;

                            if (framesInBlock == _config.BlockFrameCount)
                            {
                                var block = new PcmBlock(
                                    blockBuffer, _config.BlockFrameCount * _config.ChannelCount, _config.SampleRateHz,
                                    _config.ChannelCount, sequenceNumber++, Stopwatch.GetTimestamp(),
                                    startsAfterDiscontinuity);

                                // Cleared only once it has actually been handed to a block, so a step
                                // that lands mid-block still reaches the consumer.
                                startsAfterDiscontinuity = false;

                                // DropOldest never blocks, so a stalled consumer can never stall this
                                // thread and therefore can never stall the RX drain. TryWrite reports
                                // false only on a completed channel, where nothing will read this.
                                if (!_output.Writer.TryWrite(block))
                                    block.Dispose();

                                blockBuffer = null;
                                framesInBlock = 0;
                            }
                        }

                        ArrayPool<byte>.Shared.Return(chunk.Buffer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QA403 block assembler faulted");
            }
            finally
            {
                if (blockBuffer is not null)
                    ArrayPool<float>.Shared.Return(blockBuffer);

                _output.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Owns the queued bulk TX/RX pipeline for one <see cref="StreamAsync"/> run, on a dedicated
    /// thread, and hands completed RX buffers to the consumer over a <see cref="Channel"/>.
    ///
    /// <para><b>Why a dedicated thread, and why this does no DSP.</b> The DAC underruns — audible and
    /// scope-visible gaps in the generator output, several times a second and worse at higher sample
    /// rates — whenever a fresh TX transfer isn't submitted before the queued ones drain. The previous
    /// design ran the transfer loop as an <c>async</c> method whose continuations went to the thread
    /// pool, and put per-chunk calibration, block assembly and a full <c>yield return</c> round-trip to
    /// the spectrum pipeline *between* one transfer completing and the next being submitted. That put
    /// TX refill behind: thread-pool scheduling (contended with the 20 Hz × 64K-point FFT worker in
    /// <c>SpectrumPipeline</c>, whose thread injection is deliberately slow — roughly one thread per
    /// 0.5-1 s once starved, which is exactly a "gaps a few times a second" signature), GC pauses from
    /// the same DSP work, and the consumer's own latency.</para>
    ///
    /// <para>So this class does nothing but transfer management: complete a read, immediately resubmit
    /// a read and a write, hand the raw bytes off, loop. Calibration happens on the consumer side of
    /// the channel, where being late costs display latency instead of DAC samples. Running on a real
    /// thread rather than the pool means the loop cannot be starved by DSP work at all, and blocking on
    /// each transfer's <see cref="Task"/> is fine there precisely because it is not a pool thread.</para>
    ///
    /// <para>The 1 write per 1 completed read ratio is kept exactly as qa40x-baremetal has it —
    /// "Everytime we get some RX data, we'll send another block of TX data. This is how we maintain
    /// timing with the hardware" (Acquisition.cs:144-146). Only the latency of that refill is
    /// changed here, never the ratio.</para>
    /// </summary>
    private sealed class TransferPump(
        Qa40xTransport transport, Qa40xCalibration calibration, Qa40xAutoRanger autoRanger,
        ILogger logger, Func<GeneratorConfig> getGeneratorConfig, double initialOutputRangeDbv,
        double initialInputRangeDbv, Func<bool> isAutoRangeEnabled, double sampleRateHz, Func<bool> isStopRequested,
        Action<double> onInputRangeStepped)
    {
        private readonly Queue<(Task<int> Task, byte[] Buffer)> _pendingReads = new();
        private readonly Queue<(Task<int> Task, byte[] Buffer)> _pendingWrites = new();
        private readonly int _queueDepth = QueueDepthFor(sampleRateHz);
        private readonly Qa40xGeneratorWaveform _waveform = new(new Random(), sampleRateHz);

        /// <summary>Wall-clock duration of one <see cref="TransferChunkFrames"/> chunk at this stream's sample rate — what <see cref="StepRangeIfNeeded"/> feeds <see cref="Qa40xAutoRanger.Evaluate"/> as its under-range hysteresis tick.</summary>
        private readonly double _blockDurationMs = TransferChunkFrames * 1000.0 / sampleRateHz;

        /// <summary>
        /// Mutable box for the dropped-chunk count. The itemDropped callback below has to increment
        /// something, and a field initializer may not touch <c>this</c> — so the counter is a local
        /// the static factory closes over, not a field of the pump.
        /// </summary>
        private sealed class DropCounter
        {
            public long Count;
        }

        private readonly (Channel<RxChunk> Channel, DropCounter Drops) _rx = CreateRxChannel();

        private static (Channel<RxChunk>, DropCounter) CreateRxChannel()
        {
            var drops = new DropCounter();
            var channel = Channel.CreateBounded<RxChunk>(
                new BoundedChannelOptions(RxChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                },
                itemDropped: dropped =>
                {
                    ArrayPool<byte>.Shared.Return(dropped.Buffer);

                    // A dropped chunk is not a dropped *display frame* — it is a hole punched in the
                    // middle of the sample stream. SampleRingBuffer just concatenates what arrives, so
                    // the next FFT record contains a splice, which smears a tone into a skirt and lifts
                    // the whole broadband floor. DropOldest is still right (blocking the pump underruns
                    // the DAC), but it was completely silent, and CLAUDE.md already notes that
                    // SustainedCapture_60SecondsAt192kHz cannot detect this. Count it so a raised noise
                    // floor can be attributed instead of guessed at.
                    Interlocked.Increment(ref drops.Count);
                });

            return (channel, drops);
        }

        private Thread? _thread;
        private CancellationToken _ct;
        private volatile bool _stop;
        private volatile bool _deviceLost;
        private long _frameIndex;
        private double _inputRangeDbv = initialInputRangeDbv;

        /// <summary>
        /// DAC-starvation detector. Pump-thread-only writes, drained and logged by the consumer —
        /// see <see cref="TransferPumpDiagnostics"/> for the slack metric it is built around.
        /// </summary>
        private readonly TransferPumpDiagnostics _diagnostics =
            new(sampleRateHz, QueueDepthFor(sampleRateHz), TransferChunkFrames);

        /// <summary>Running total of milliseconds spent inside <see cref="QuiesceAndWriteRegister"/>, so the loop can tell a deliberate drain from a stall it did not ask for.</summary>
        private double _quiesceMs;

        /// <summary>Milliseconds between two <see cref="Stopwatch.GetTimestamp"/> readings.</summary>
        private static double Elapsed(long from, long to) =>
            (to - from) * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// The range <see cref="BuildTxChunk"/> scales by, and the authoritative copy while this pump
        /// runs. Only ever written from <see cref="QuiesceAndWriteRegister"/>'s
        /// <c>applyBeforePrime</c> hook, i.e. after register 6 has been programmed and before the TX
        /// queue is refilled, so no chunk is ever built for a range the attenuator is not already on.
        /// </summary>
        private double _outputRangeDbv = initialOutputRangeDbv;

        /// <summary>Settle window owed to the consumer, attached to the next chunk emitted after a range step.</summary>
        private int _pendingSettleFrames;

        /// <summary>
        /// Register writes requested from outside while this pump owns the bus. They are applied on
        /// the pump thread between transfers, with EP2 quiesced first — never from the caller's
        /// thread. See <see cref="Qa40xUsbDevice.SetInputRangeAsync"/>.
        /// </summary>
        private readonly ConcurrentQueue<PendingRegisterWrite> _pendingRegisterWrites = new();

        /// <summary>
        /// One queued register write. <see cref="InputRangeDbv"/>/<see cref="OutputRangeDbv"/> are the
        /// value this write puts into effect, for whichever of the two ranges it programs — applied by
        /// the pump at the write itself, never by the caller, so the range the pump *calibrates and
        /// scales with* can never disagree with the range the hardware is on.
        /// </summary>
        private sealed record PendingRegisterWrite(
            byte Register, uint Value, double? InputRangeDbv, double? OutputRangeDbv, TaskCompletionSource Completion);

        public ChannelReader<RxChunk> Reader => _rx.Channel.Reader;

        /// <summary>DAC-starvation measurements for this stream. Drained and logged by the consumer, never by the pump.</summary>
        public TransferPumpDiagnostics Diagnostics => _diagnostics;

        /// <summary>RX chunks discarded because the consumer fell behind — see the itemDropped callback.</summary>
        public long DroppedChunks => Interlocked.Read(ref _rx.Drops.Count);

        /// <summary>Queues an input-range change and completes when the pump thread has actually programmed it.</summary>
        public Task RequestInputRangeAsync(double dbv, CancellationToken ct) =>
            EnqueueRegisterWriteAsync(
                Qa40xRegisters.InputRange, Qa40xRegisters.InputRangeDbvToRegister(dbv), dbv, null, ct);

        /// <summary>Queues an output-range change and completes when the pump thread has actually programmed it.</summary>
        public Task RequestOutputRangeAsync(double dbv, CancellationToken ct) =>
            EnqueueRegisterWriteAsync(
                Qa40xRegisters.OutputRange, Qa40xRegisters.OutputRangeDbvToRegister(dbv), null, dbv, ct);

        private Task EnqueueRegisterWriteAsync(
            byte register, uint value, double? inputRangeDbv, double? outputRangeDbv, CancellationToken ct)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRegisterWrites.Enqueue(
                new PendingRegisterWrite(register, value, inputRangeDbv, outputRangeDbv, completion));
            return completion.Task.WaitAsync(ct);
        }

        /// <summary>
        /// Applies every queued register write, quiescing EP2 around each one. Called from the pump
        /// loop *after* the current chunk's range has been latched, so that chunk is still stamped
        /// with the range its samples were actually captured at.
        /// </summary>
        private void ApplyPendingRegisterWrites()
        {
            while (_pendingRegisterWrites.TryDequeue(out var pending))
            {
                try
                {
                    QuiesceAndWriteRegister(pending.Register, pending.Value, () => ApplyRange(pending));
                    pending.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    pending.Completion.TrySetException(ex);
                    throw;
                }
            }
        }

        /// <summary>Puts a queued write's range into effect on this pump. Runs between the register write and <see cref="Prime"/> — see <paramref name="pending"/>'s type.</summary>
        private void ApplyRange(PendingRegisterWrite pending)
        {
            if (pending.InputRangeDbv is { } inputDbv)
            {
                Volatile.Write(ref _inputRangeDbv, inputDbv);
                onInputRangeStepped(inputDbv);
            }

            if (pending.OutputRangeDbv is { } outputDbv)
                Volatile.Write(ref _outputRangeDbv, outputDbv);
        }

        /// <summary>
        /// Stops EP2, writes one register, restarts EP2, and books the settle window. The device's
        /// USB stack does not tolerate a register write on EP1 with bulk transfers in flight on EP2
        /// (observed as a native transfer that never completes and a device that needs power-cycling),
        /// so this sequence — not a bare write — is the only way a register may be touched mid-stream.
        /// Shared by autoranging and by externally requested writes.
        ///
        /// <para><paramref name="applyBeforePrime"/> runs in the one window where the hardware is on the
        /// new range but no TX chunk has been built for it yet. Anything that must change *with* the
        /// register goes here rather than after this method returns: <see cref="Prime"/> immediately
        /// refills the whole TX queue, and <see cref="BuildTxChunk"/> scales those chunks by
        /// <see cref="_outputRangeDbv"/>, so updating that afterwards puts a queue's worth of audio out
        /// at the wrong level — see <see cref="SetOutputRangeAsync"/> for what that measured.</para>
        /// </summary>
        private void QuiesceAndWriteRegister(byte register, uint value, Action? applyBeforePrime = null)
        {
            // Timed because this is a *known* DAC gap, not a suspected one: Drain() waits for every
            // outstanding TX transfer to complete, which is precisely the device having consumed all
            // the audio the host had queued. From that moment until Prime() has rebuilt the queue the
            // DAC has nothing to play and emits silence. Only the register write and the rebuild are
            // charged here — the drain itself is covered by audio that is still playing — so this
            // number is the width of the flat span a scope sees on the generator output.
            Drain();

            long tRegisterStart = Stopwatch.GetTimestamp();
            transport.WriteRegisterAsync(register, value, _ct).GetAwaiter().GetResult();
            applyBeforePrime?.Invoke();
            Prime();
            _quiesceMs += Elapsed(tRegisterStart, Stopwatch.GetTimestamp());

            _pendingSettleFrames = Qa40xAutoRanger.SettleFrameCount(sampleRateHz);
        }

        public void Start(CancellationToken ct)
        {
            _ct = ct;
            _thread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "QA403 USB transfer pump",

                // Above the thread pool's normal priority so a saturated pool — which is the state
                // this whole design is defending against — cannot preempt transfer refill.
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>Signals the pump to stop, waits for it to finish its own teardown, and drains any buffers left in the channel. Returns whether the device was lost mid-stream.</summary>
        public async Task<bool> StopAndJoinAsync()
        {
            _stop = true;

            var thread = _thread;
            if (thread is not null)
            {
                // Bounded regardless of what the device does: the outstanding transfers were all
                // submitted in parallel, so their 1000 ms data timeouts elapse concurrently rather
                // than in series, and the drain needs roughly one of them, not _queueDepth of them.
                //
                // 2 s, not 10: this join sits inside the host's 3 s ShutdownTimeout, which in turn
                // sits inside the shutdown watchdog's 3.5 s. A 10 s join could not complete within
                // either, so it would be abandoned mid-way and disposal would then call libusb_close
                // with transfers still in flight — the precise thing that hangs. Better to give up
                // here, loudly, while the ordering is still intact.
                await Task.Run(() =>
                {
                    if (!thread.Join(TimeSpan.FromSeconds(2)))
                        logger.LogWarning("QA403 transfer pump did not exit within 2 s — disposal may block in libusb_close");
                }).ConfigureAwait(false);
            }

            while (_rx.Channel.Reader.TryRead(out var leftover))
                ArrayPool<byte>.Shared.Return(leftover.Buffer);

            return _deviceLost;
        }

        private void PumpLoop()
        {
            try
            {
                Prime();
                _diagnostics.Start();

                while (!_stop && !_ct.IsCancellationRequested && !isStopRequested())
                {
                    // Timestamps only — see TransferPumpDiagnostics on why measuring here is safe and
                    // logging here would not be. The four spans below are the whole iteration, split
                    // so a stall can be blamed on the thing that actually took the time.
                    long tIterationStart = Stopwatch.GetTimestamp();

                    // Complete the oldest read, then immediately put a read and a write back on the
                    // wire. Nothing between those two points may do meaningful work — that latency is
                    // the DAC's underrun margin.
                    var (readTask, readBuffer) = _pendingReads.Dequeue();
                    int transferLength = readTask.GetAwaiter().GetResult();
                    long tAfterRead = Stopwatch.GetTimestamp();

                    // Checked for the same reason the write below is, and it was the *only* one of
                    // the two that wasn't. A short bulk read is reported as a length, not an error
                    // (a timeout is deliberately not fatal — see Qa40xTransport.ReadDataAsync), and
                    // the assembler appends exactly the frames it is handed. So the missing ones are
                    // a silent hole in the sample stream: DroppedRxChunks does not count them, the
                    // block sequence numbers stay contiguous across them, and the only trace is a
                    // spliced FFT record.
                    if (transferLength != ChunkByteSize)
                        _diagnostics.RecordShortRead(_frameIndex, ChunkByteSize, transferLength);

                    byte[] nextReadBuffer = ArrayPool<byte>.Shared.Rent(ChunkByteSize);
                    _pendingReads.Enqueue((transport.ReadDataAsync(nextReadBuffer, ChunkByteSize, _ct), nextReadBuffer));

                    var (writeTask, writeBuffer) = _pendingWrites.Dequeue();
                    int writtenBytes = writeTask.GetAwaiter().GetResult();
                    long tAfterWrite = Stopwatch.GetTimestamp();

                    // Checked, not discarded. A partial bulk write is reported as a length rather
                    // than an error (Qa40xTransport.WriteDataAsync says so explicitly, and a timeout
                    // is deliberately not fatal), so the frames it failed to move are simply absent
                    // from the DAC's stream — a gap on a scope, and nothing at all in a log.
                    if (writtenBytes != ChunkByteSize)
                        _diagnostics.RecordShortWrite(_frameIndex, ChunkByteSize, writtenBytes);

                    ArrayPool<byte>.Shared.Return(writeBuffer);
                    SubmitWrite();
                    long tAfterSubmit = Stopwatch.GetTimestamp();

                    double rangeForThisChunk = Volatile.Read(ref _inputRangeDbv);
                    int settleFrames = _pendingSettleFrames;
                    _pendingSettleFrames = 0;

                    // Externally requested register writes first, then autorange — both quiesce EP2,
                    // and both must land after rangeForThisChunk is latched above so this chunk keeps
                    // the range its samples were captured at.
                    double quiesceMsBefore = _quiesceMs;
                    ApplyPendingRegisterWrites();

                    if (isAutoRangeEnabled())
                        StepRangeIfNeeded(readBuffer, transferLength, rangeForThisChunk);

                    long tIterationEnd = Stopwatch.GetTimestamp();
                    _diagnostics.EndIteration(
                        _frameIndex,
                        Elapsed(tIterationStart, tIterationEnd),
                        Elapsed(tIterationStart, tAfterRead),
                        Elapsed(tAfterRead, tAfterWrite),
                        Elapsed(tAfterWrite, tAfterSubmit),
                        _quiesceMs - quiesceMsBefore,
                        quiesced: _quiesceMs != quiesceMsBefore);

                    // DropOldest never blocks, so a stalled consumer can never stall the DAC. TryWrite
                    // only reports false on a completed channel, in which case nothing will read this.
                    if (!_rx.Channel.Writer.TryWrite(new RxChunk(readBuffer, transferLength, rangeForThisChunk, settleFrames)))
                        ArrayPool<byte>.Shared.Return(readBuffer);
                }
            }
            catch (Qa40xDeviceLostException)
            {
                _deviceLost = true;
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "QA403 transfer pump faulted");
            }
            finally
            {
                // Drain before stopping, not after. Every EP2 transfer must be collected while the
                // device is still streaming, so the pending reads complete with data instead of sitting
                // until their 1000 ms timeout; and it keeps the StreamingStop register write on EP1 off
                // the wire until the data plane is quiet, the same quiesce rule the range step follows.
                Drain();

                // Anything queued that the loop never got to. EP2 is quiet now, so these are safe to
                // write directly. Their callers are awaiting the completion source — leaving it unset
                // would hang whoever asked for the range change until their CancellationToken fired.
                while (_pendingRegisterWrites.TryDequeue(out var pending))
                {
                    try
                    {
                        transport.WriteRegisterAsync(pending.Register, pending.Value, CancellationToken.None).GetAwaiter().GetResult();
                        ApplyRange(pending);
                        pending.Completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        pending.Completion.TrySetException(ex);
                    }
                }

                try
                {
                    transport.WriteRegisterAsync(Qa40xRegisters.StreamingEnable, Qa40xRegisters.StreamingStop, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Qa40xDeviceLostException)
                {
                    // Already gone — nothing left to stop.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "QA403 StreamingStop write failed during teardown");
                }

                _rx.Channel.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Evaluates this chunk's peak against the autoranger and, if it says so, quiesces EP2, writes
        /// register 5 and re-primes. The register write has to happen here on the pump thread rather
        /// than on the consumer: this device's simple USB stack appears not to tolerate a register
        /// write on EP1 while bulk transfers are in flight on EP2 — observed as the whole connection
        /// wedging (a native transfer that never completes) when a register write landed mid-stream —
        /// so the thread that owns the transfers has to be the one that quiesces them.
        /// </summary>
        private void StepRangeIfNeeded(byte[] buffer, int transferLength, double currentRangeDbv)
        {
            var decision = autoRanger.Evaluate(currentRangeDbv, ScanPeakDbv(buffer, transferLength, currentRangeDbv), _blockDurationMs);
            if (decision == RangeStepDecision.Hold)
                return;

            double nextRangeDbv = autoRanger.NextRangeDbv(currentRangeDbv, decision);

            QuiesceAndWriteRegister(
                Qa40xRegisters.InputRange, Qa40xRegisters.InputRangeDbvToRegister(nextRangeDbv),
                () =>
                {
                    Volatile.Write(ref _inputRangeDbv, nextRangeDbv);
                    onInputRangeStepped(nextRangeDbv);
                });

            logger.LogDebug(
                "Input range {Decision} to {RangeDbv} dBV, discarding {Frames} settle frames",
                decision, nextRangeDbv, _pendingSettleFrames);
        }

        /// <summary>
        /// Largest calibrated peak level in the chunk (either channel), in dBV — converted through
        /// the same peak-to-RMS-equivalent step <c>SpectrumPipeline.ComputeTimeDomainLevels</c> uses
        /// for its "Pk dBV" meter (<c>peakVrms = peakVpk / sqrt(2)</c>), not a bare peak-volts log.
        /// <see cref="Qa40xAutoRanger.HeadroomDb"/> (ported from the previous app's
        /// <c>QaLibrary.DetermineAttenuation</c> / <c>attenuation.ts</c>) was calibrated against that
        /// same RMS-equivalent "Pk dBV" reading, not a true-peak one — a true-peak dBV would read
        /// 20*log10(sqrt(2)) ≈ 3.01 dB hotter than the level the rest of the app (and a signal
        /// generator's own dBV setpoint) reports for the same sine, which would eat into the
        /// documented -5 dBV headroom margin. Cheap enough to keep on the pump thread, since
        /// autoranging needs it before the chunk is handed off.
        /// </summary>
        private double ScanPeakDbv(byte[] buffer, int transferLength, double currentRangeDbv)
        {
            long peakRaw0 = 0, peakRaw1 = 0;
            int frameCount = transferLength / BytesPerFrame;
            for (int frame = 0; frame < frameCount; frame++)
            {
                int byteOffset = frame * BytesPerFrame;
                int slot0Raw = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(byteOffset));
                int slot1Raw = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(byteOffset + 4));
                peakRaw0 = Math.Max(peakRaw0, Math.Abs((long)slot0Raw));
                peakRaw1 = Math.Max(peakRaw1, Math.Abs((long)slot1Raw));
            }

            double volts0 = calibration.RawToVolts((int)Math.Min(peakRaw0, int.MaxValue), currentRangeDbv, 0);
            double volts1 = calibration.RawToVolts((int)Math.Min(peakRaw1, int.MaxValue), currentRangeDbv, 1);
            double peakVrms = Math.Max(volts0, volts1) / Math.Sqrt(2.0);

            // Silence has no dBV; -Infinity compares fine against Evaluate's finite thresholds and
            // reads as unambiguously under range, which is what true silence is.
            return peakVrms > 0 ? 20.0 * Math.Log10(peakVrms) : double.NegativeInfinity;
        }

        private void Prime()
        {
            for (int i = 0; i < _queueDepth; i++)
            {
                byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ChunkByteSize);
                _pendingReads.Enqueue((transport.ReadDataAsync(readBuffer, ChunkByteSize, _ct), readBuffer));
            }

            for (int i = 0; i < _queueDepth; i++)
                SubmitWrite();
        }

        private void SubmitWrite()
        {
            byte[] txChunk = ArrayPool<byte>.Shared.Rent(ChunkByteSize);
            BuildTxChunk(_frameIndex, txChunk);
            _frameIndex += TransferChunkFrames;
            _pendingWrites.Enqueue((transport.WriteDataAsync(txChunk, ChunkByteSize, _ct), txChunk));
        }

        /// <summary>Awaits and discards every still-outstanding transfer, swallowing errors — best-effort. Every rented buffer is returned regardless of outcome.</summary>
        private void Drain()
        {
            while (_pendingReads.Count > 0)
            {
                var (task, buffer) = _pendingReads.Dequeue();
                try { task.GetAwaiter().GetResult(); }
                catch { /* best-effort drain */ }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            while (_pendingWrites.Count > 0)
            {
                var (task, buffer) = _pendingWrites.Dequeue();
                try { task.GetAwaiter().GetResult(); }
                catch { /* best-effort drain */ }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }

        /// <summary>
        /// Builds one TX chunk. Hardware swaps L/R (docs/protocol.md §6); corrected the same way
        /// qa40x-baremetal's caller does it (Form1.cs:113-114: "Left and right DAC outputs are
        /// swapped in hardware. Fix by re-ordering args") and PyQa40x does it before calibration
        /// (analyzer.py:115-121): wire slot 0 carries the *logical right* channel's generator
        /// output, wire slot 1 carries *logical left*. Calibration channel index follows wire
        /// position, not logical identity — PyQa40x applies its "left" calibration record
        /// (channel 0) to slot 0 post-swap (analyzer.py:121,127), so that's what
        /// <see cref="Qa40xCalibration.VoltsToRaw"/> receives here too.
        /// </summary>
        private void BuildTxChunk(long startFrameIndex, byte[] chunk)
        {
            var generatorConfig = getGeneratorConfig();
            double outputRangeDbv = Volatile.Read(ref _outputRangeDbv);
            var waveform = _waveform;

            for (int frame = 0; frame < TransferChunkFrames; frame++)
            {
                long frameIndex = startFrameIndex + frame;
                // The channel index names the *generator* channel, not the wire slot: the L/R swap
                // here is the DAC's, and a stateful kind has to keep its stream tied to the channel
                // whose settings it is playing.
                double slot0Volts = waveform.ComputeSample(generatorConfig.Right, frameIndex, channelIndex: 1);
                double slot1Volts = waveform.ComputeSample(generatorConfig.Left, frameIndex, channelIndex: 0);
                int slot0Raw = calibration.VoltsToRaw(slot0Volts, outputRangeDbv, channel: 0);
                int slot1Raw = calibration.VoltsToRaw(slot1Volts, outputRangeDbv, channel: 1);

                int byteOffset = frame * BytesPerFrame;
                BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(byteOffset), slot0Raw);
                BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(byteOffset + 4), slot1Raw);
            }
        }
    }
}
