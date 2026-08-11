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
    /// </summary>
    private const double TargetInFlightSeconds = 0.064;

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

    /// <summary>
    /// The pump for the currently-running stream, or null when nothing is streaming. Every register
    /// write has to go through it while it is non-null — see <see cref="SetInputRangeAsync"/>.
    /// </summary>
    private volatile TransferPump? _activePump;

    /// <summary>Drops from streams that have already ended. Live drops come off <see cref="_activePump"/> — see <see cref="DroppedRxChunks"/>.</summary>
    private long _droppedRxChunks;

    public event EventHandler? DeviceLost;

    public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

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
    /// undone within ~10 ms of the stream running: <see cref="Qa40xAutoRanger.Evaluate"/> treats
    /// anything below <see cref="Qa40xAutoRanger.UnderRangeThresholdFraction"/> (-24 dBFS) as
    /// under-range, so any normal signal — or no signal at all — walked the range back down to the
    /// most sensitive setting one step per 3 chunks. Audible on real hardware as the 24 dB
    /// attenuator relay clicking in on selection and straight back out. That was a coordination bug
    /// between this class's own <c>_inputRangeDbv</c> (which autorange steps) and
    /// <c>DeviceSessionService</c>'s cached view of the range — if clicking or a stuck-looking range
    /// selector reappears, check that those two are still staying in sync.
    ///
    /// Steps are plumbed back to <see cref="InputRangeDbv"/>, which the app's status endpoint reads,
    /// so the UI's range selector follows the hardware when this is on.
    /// </summary>
    public bool AutoRangeEnabled { get; set; } = true;

    /// <inheritdoc />
    public double InputRangeDbv => Volatile.Read(ref _inputRangeDbv);

    /// <inheritdoc />
    public long DroppedRxChunks => Interlocked.Read(ref _droppedRxChunks) + (_activePump?.DroppedChunks ?? 0);

    /// <summary>Transfers kept in flight per direction at a given sample rate — see <see cref="TargetInFlightSeconds"/>.</summary>
    internal static int QueueDepthFor(double sampleRateHz) => Math.Clamp(
        (int)Math.Ceiling(TargetInFlightSeconds * sampleRateHz / TransferChunkFrames), MinQueueDepth, MaxQueueDepth);

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

            var calibration = await Qa40xCalibration.LoadAsync(transport, ct).ConfigureAwait(false);

            // Tier-2 registers (docs/protocol.md §2.4: sourced from qa40x-rs-main's own USB-traffic
            // observations only, not cross-checked against PyQa40x/qa40x-baremetal). Read-only, so
            // worst case is a cosmetic string, not a protocol violation.
            uint firmwareRaw = await transport.ReadRegisterAsync(Qa40xRegisters.FirmwareVersion, ct).ConfigureAwait(false);
            uint serialRaw = await transport.ReadRegisterAsync(Qa40xRegisters.SerialNumber, ct).ConfigureAwait(false);

            _transport = transport;
            _calibration = calibration;
            _autoRanger = new Qa40xAutoRanger(Capabilities.InputRangesDbv);
            _connected = true;

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

        await transport.WriteRegisterAsync(Qa40xRegisters.InputRange, Qa40xRegisters.InputRangeDbvToRegister(dbv), ct).ConfigureAwait(false);
        Volatile.Write(ref _inputRangeDbv, dbv);
        _autoRanger?.Reset();
    }

    /// <summary>Same EP1-while-EP2-is-busy hazard as <see cref="SetInputRangeAsync"/> — routed through the pump for the same reason.</summary>
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

        await transport.WriteRegisterAsync(Qa40xRegisters.OutputRange, Qa40xRegisters.OutputRangeDbvToRegister(dbv), ct).ConfigureAwait(false);
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
            () => _generatorConfig, () => Volatile.Read(ref _outputRangeDbv),
            // A Func, not the captured bool: the auto/manual checkbox has to take effect on a
            // running stream, not only on the next one.
            Volatile.Read(ref _inputRangeDbv), () => AutoRangeEnabled, config.SampleRateHz, () => _stopRequested,
            // Keep the device's own view of the input range in step with what autoranging actually
            // programmed, so a later SetInputRangeAsync/status read isn't working from a stale value.
            stepped => Volatile.Write(ref _inputRangeDbv, stepped));

        float[]? blockBuffer = null;
        int framesInBlock = 0;
        long sequenceNumber = 0;
        int settleFramesRemaining = 0;

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
            _activePump = pump;
            pump.Start(ct);

            // Deliberately *not* passing ct here. Cancellation is observed by the pump thread, which
            // then stops and completes the channel — so this loop ends by running out of chunks, the
            // same clean end-of-enumeration the pre-channel version had. Passing ct would instead make
            // ReadAllAsync throw OperationCanceledException out of StreamAsync, turning what every
            // caller treats as a normal stop into an exception, and would abandon chunks already
            // captured rather than flushing them.
            await foreach (var chunk in pump.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // A range step reports its settle window on the first chunk captured *after* the
                // register write, so this chunk's own samples are still valid at its stated range.
                if (chunk.SettleFrames > 0)
                    settleFramesRemaining = chunk.SettleFrames;

                int chunkFrameCount = chunk.Length / BytesPerFrame;
                for (int i = 0; i < chunkFrameCount; i++)
                {
                    if (settleFramesRemaining > 0)
                    {
                        settleFramesRemaining--;
                        continue;
                    }

                    blockBuffer ??= ArrayPool<float>.Shared.Rent(config.BlockFrameCount * config.ChannelCount);

                    // Deinterleaved and calibrated straight into the outgoing block — no intermediate
                    // per-chunk float arrays. There is deliberately **no channel swap here**. The
                    // QA403's swap is on the DAC only — PyQa40x states it as "On QA402 and QA403,
                    // *outputs* need to be swapped" (analyzer.py:113) and correspondingly reads its ADC
                    // data straight through, left = interleaved[0::2] / right = interleaved[1::2]
                    // (analyzer.py:174-175), applying its channel-0 ("left") calibration record to wire
                    // slot 0. TransferPump.BuildTxChunk already applies the swap on the way out, so
                    // applying it again here would swap twice and cross the channels: a tone generated
                    // on logical left would come back on logical right.
                    //
                    // This was measured on real hardware rather than inferred. Driving a raw sine on
                    // wire slot 1 alone put it at -6.00 dBFS on RX slot 0 with RX slot 1 at -94 dBFS,
                    // and driving slot 0 alone mirrored it exactly — TX slot N returns on RX slot 1-N.
                    // qa40x-baremetal does swap on receive (Acquisition.cs:193,
                    // FromByteStream(rxData, out r.Right, out r.Left)) and an earlier version of this
                    // code followed it, which is what crossed the channels; that reference sends 1 kHz
                    // left / 2 kHz right but only ever prints per-channel *peaks*, never which
                    // frequency arrived where, so a crossed pair is invisible to it.
                    int byteOffset = i * BytesPerFrame;
                    int slot0Raw = BinaryPrimitives.ReadInt32LittleEndian(chunk.Buffer.AsSpan(byteOffset));
                    int slot1Raw = BinaryPrimitives.ReadInt32LittleEndian(chunk.Buffer.AsSpan(byteOffset + 4));

                    int baseIndex = framesInBlock * config.ChannelCount;
                    blockBuffer[baseIndex] = (float)calibration.RawToVolts(slot0Raw, chunk.InputRangeDbv, 0);
                    if (config.ChannelCount > 1)
                        blockBuffer[baseIndex + 1] = (float)calibration.RawToVolts(slot1Raw, chunk.InputRangeDbv, 1);
                    framesInBlock++;

                    if (framesInBlock == config.BlockFrameCount)
                    {
                        yield return new PcmBlock(
                            blockBuffer, config.BlockFrameCount * config.ChannelCount, config.SampleRateHz,
                            config.ChannelCount, sequenceNumber++, Stopwatch.GetTimestamp());
                        blockBuffer = null;
                        framesInBlock = 0;
                    }
                }

                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }
        finally
        {
            if (blockBuffer is not null)
                ArrayPool<float>.Shared.Return(blockBuffer);

            // Cleared before the join, not after: once teardown has begun the pump can no longer
            // service queued register writes from its loop, so a concurrent SetInputRangeAsync must
            // fall through to writing directly rather than queueing onto a pump that is going away.
            _activePump = null;

            // Stops the pump thread and waits for it. The pump owns every EP2 transfer and the
            // StreamingStop write, and does both in its own finally — so by the time this returns the
            // data plane is quiet and the device has been told to stop.
            bool deviceLost = await pump.StopAndJoinAsync().ConfigureAwait(false);

            // Folded in only after the join, when the pump can no longer drop anything — reading it
            // while still running would double-count against the live term in DroppedRxChunks.
            long dropped = pump.DroppedChunks;
            if (dropped > 0)
            {
                Interlocked.Add(ref _droppedRxChunks, dropped);
                logger.LogWarning(
                    "QA403 stream dropped {Dropped} RX chunks ({Frames} frames) — the consumer fell behind, so FFT records "
                        + "over this run contain splices and any noise-floor reading from them is suspect",
                    dropped, dropped * TransferChunkFrames);
            }

            if (deviceLost)
            {
                logger.LogWarning("QA403 disconnected mid-stream");
                DeviceLost?.Invoke(this, EventArgs.Empty);
            }
        }
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
        ILogger logger, Func<GeneratorConfig> getGeneratorConfig, Func<double> getOutputRangeDbv,
        double initialInputRangeDbv, Func<bool> isAutoRangeEnabled, double sampleRateHz, Func<bool> isStopRequested,
        Action<double> onInputRangeStepped)
    {
        private readonly Queue<(Task<int> Task, byte[] Buffer)> _pendingReads = new();
        private readonly Queue<(Task<int> Task, byte[] Buffer)> _pendingWrites = new();
        private readonly int _queueDepth = QueueDepthFor(sampleRateHz);
        private readonly Qa40xGeneratorWaveform _waveform = new(new Random());

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

        /// <summary>Settle window owed to the consumer, attached to the next chunk emitted after a range step.</summary>
        private int _pendingSettleFrames;

        /// <summary>
        /// Register writes requested from outside while this pump owns the bus. They are applied on
        /// the pump thread between transfers, with EP2 quiesced first — never from the caller's
        /// thread. See <see cref="Qa40xUsbDevice.SetInputRangeAsync"/>.
        /// </summary>
        private readonly ConcurrentQueue<PendingRegisterWrite> _pendingRegisterWrites = new();

        private sealed record PendingRegisterWrite(byte Register, uint Value, double? InputRangeDbv, TaskCompletionSource Completion);

        public ChannelReader<RxChunk> Reader => _rx.Channel.Reader;

        /// <summary>RX chunks discarded because the consumer fell behind — see the itemDropped callback.</summary>
        public long DroppedChunks => Interlocked.Read(ref _rx.Drops.Count);

        /// <summary>Queues an input-range change and completes when the pump thread has actually programmed it.</summary>
        public Task RequestInputRangeAsync(double dbv, CancellationToken ct) =>
            EnqueueRegisterWriteAsync(Qa40xRegisters.InputRange, Qa40xRegisters.InputRangeDbvToRegister(dbv), dbv, ct);

        /// <summary>Queues an output-range change and completes when the pump thread has actually programmed it.</summary>
        public Task RequestOutputRangeAsync(double dbv, CancellationToken ct) =>
            EnqueueRegisterWriteAsync(Qa40xRegisters.OutputRange, Qa40xRegisters.OutputRangeDbvToRegister(dbv), null, ct);

        private Task EnqueueRegisterWriteAsync(byte register, uint value, double? inputRangeDbv, CancellationToken ct)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRegisterWrites.Enqueue(new PendingRegisterWrite(register, value, inputRangeDbv, completion));
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
                    QuiesceAndWriteRegister(pending.Register, pending.Value);
                    if (pending.InputRangeDbv is { } dbv)
                    {
                        Volatile.Write(ref _inputRangeDbv, dbv);
                        onInputRangeStepped(dbv);
                    }

                    pending.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    pending.Completion.TrySetException(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Stops EP2, writes one register, restarts EP2, and books the settle window. The device's
        /// USB stack does not tolerate a register write on EP1 with bulk transfers in flight on EP2
        /// (observed as a native transfer that never completes and a device that needs power-cycling),
        /// so this sequence — not a bare write — is the only way a register may be touched mid-stream.
        /// Shared by autoranging and by externally requested writes.
        /// </summary>
        private void QuiesceAndWriteRegister(byte register, uint value)
        {
            Drain();
            transport.WriteRegisterAsync(register, value, _ct).GetAwaiter().GetResult();
            Prime();
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

                while (!_stop && !_ct.IsCancellationRequested && !isStopRequested())
                {
                    // Complete the oldest read, then immediately put a read and a write back on the
                    // wire. Nothing between those two points may do meaningful work — that latency is
                    // the DAC's underrun margin.
                    var (readTask, readBuffer) = _pendingReads.Dequeue();
                    int transferLength = readTask.GetAwaiter().GetResult();

                    byte[] nextReadBuffer = ArrayPool<byte>.Shared.Rent(ChunkByteSize);
                    _pendingReads.Enqueue((transport.ReadDataAsync(nextReadBuffer, ChunkByteSize, _ct), nextReadBuffer));

                    var (writeTask, writeBuffer) = _pendingWrites.Dequeue();
                    writeTask.GetAwaiter().GetResult();
                    ArrayPool<byte>.Shared.Return(writeBuffer);
                    SubmitWrite();

                    double rangeForThisChunk = Volatile.Read(ref _inputRangeDbv);
                    int settleFrames = _pendingSettleFrames;
                    _pendingSettleFrames = 0;

                    // Externally requested register writes first, then autorange — both quiesce EP2,
                    // and both must land after rangeForThisChunk is latched above so this chunk keeps
                    // the range its samples were captured at.
                    ApplyPendingRegisterWrites();

                    if (isAutoRangeEnabled())
                        StepRangeIfNeeded(readBuffer, transferLength, rangeForThisChunk);

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
                        if (pending.InputRangeDbv is { } dbv)
                            onInputRangeStepped(dbv);

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
            var decision = autoRanger.Evaluate(currentRangeDbv, ScanPeakAbsRaw(buffer, transferLength));
            if (decision == RangeStepDecision.Hold)
                return;

            double nextRangeDbv = autoRanger.NextRangeDbv(currentRangeDbv, decision);

            QuiesceAndWriteRegister(Qa40xRegisters.InputRange, Qa40xRegisters.InputRangeDbvToRegister(nextRangeDbv));

            Volatile.Write(ref _inputRangeDbv, nextRangeDbv);
            onInputRangeStepped(nextRangeDbv);
            logger.LogDebug(
                "Input range {Decision} to {RangeDbv} dBV, discarding {Frames} settle frames",
                decision, nextRangeDbv, _pendingSettleFrames);
        }

        /// <summary>Largest absolute raw ADC sample in the chunk — a plain int scan, cheap enough to keep on the pump thread (autoranging needs it before the chunk is handed off).</summary>
        private static int ScanPeakAbsRaw(byte[] buffer, int transferLength)
        {
            long peak = 0;
            int frameCount = transferLength / BytesPerFrame;
            for (int frame = 0; frame < frameCount; frame++)
            {
                int byteOffset = frame * BytesPerFrame;
                int slot0Raw = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(byteOffset));
                int slot1Raw = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(byteOffset + 4));
                peak = Math.Max(peak, Math.Max(Math.Abs((long)slot0Raw), Math.Abs((long)slot1Raw)));
            }

            return (int)Math.Min(peak, int.MaxValue);
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
            double outputRangeDbv = getOutputRangeDbv();
            var waveform = _waveform;

            for (int frame = 0; frame < TransferChunkFrames; frame++)
            {
                double t = (startFrameIndex + frame) / sampleRateHz;
                double slot0Volts = waveform.ComputeSample(generatorConfig.Right, t);
                double slot1Volts = waveform.ComputeSample(generatorConfig.Left, t);
                int slot0Raw = calibration.VoltsToRaw(slot0Volts, outputRangeDbv, channel: 0);
                int slot1Raw = calibration.VoltsToRaw(slot1Volts, outputRangeDbv, channel: 1);

                int byteOffset = frame * BytesPerFrame;
                BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(byteOffset), slot0Raw);
                BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(byteOffset + 4), slot1Raw);
            }
        }
    }
}
