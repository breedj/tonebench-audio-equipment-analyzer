using System.Buffers.Binary;
using System.Diagnostics;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace Tonebench.Device.Hardware;

/// <summary>
/// Raised internally when a transfer fails with an error that indicates the physical device is
/// gone (<see cref="Error.NoDevice"/>, or an IO/pipe error on the data plane consistent with an
/// unplug). Caught inside <see cref="Qa40xUsbDevice"/> and translated into
/// <see cref="IAnalyzerDevice.DeviceLost"/> plus a clean end of the stream — never allowed to
/// propagate out of <see cref="IAnalyzerDevice.StreamAsync"/>.
///
/// Public because it also escapes the *non*-streaming paths (a register write from
/// <c>SetOutputRangeAsync</c>, the liveness ping), where there is no stream to end cleanly and the
/// app has to turn it into a "device was unplugged" answer rather than a 500.
/// </summary>
public sealed class Qa40xDeviceLostException(string message) : Exception(message);

/// <summary>
/// Thrown by <see cref="Qa40xTransport.Open"/> when no QA40x analyzer is found. Public, unlike its two
/// siblings here, because "nothing plugged in" is an ordinary state the app has to handle rather
/// than a transport-internal failure: <c>DeviceLifecycleHostedService</c> catches this one to keep
/// startup going, and logs it without a stack trace since there is nothing to diagnose.
/// </summary>
public sealed class Qa40xDeviceNotFoundException(string message) : Exception(message);

/// <summary>
/// Thrown by <see cref="Qa40xTransport.Open"/> when a QA40x analyzer is present but its interface cannot be
/// claimed — i.e. another process still holds the handle. Distinct from
/// <see cref="Qa40xDeviceNotFoundException"/> and from any later protocol failure so the log says
/// which of the two "device unreachable after exit" causes is in play.
/// </summary>
internal sealed class Qa40xDeviceBusyException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// LibUsbDotNet 3.x plumbing for the QA40x family: device open/claim/close, the 5-byte big-endian
/// register read/write framing (docs/protocol.md §2.1/§2.2), and queued bulk data transfers on
/// the streaming endpoint pair. No protocol semantics beyond raw framing live here — register
/// meanings are in <see cref="Qa40xRegisters"/>, calibration in <see cref="Qa40xCalibration"/>.
///
/// Endpoint addresses/transfer type: EP1 (register channel) and EP2 (data channel), both bulk —
/// reference/PyQa40x-main/PyQa40x-main/src/PyQa40x/registers.py:15-16 and stream.py:22-23,58,64
/// (usb1.ENDPOINT_IN|OUT, setBulk), corroborated by
/// reference/qa40x-rs-main/qa40x-rs-main/src-tauri/src/qa40x/transport.rs:10,136. In LibUsbDotNet
/// 3.x terms these are <see cref="ReadEndpointID.Ep01"/>/<see cref="WriteEndpointID.Ep01"/>
/// (registers) and <see cref="ReadEndpointID.Ep02"/>/<see cref="WriteEndpointID.Ep02"/> (data) —
/// the same enum members reference/qa40x-baremetal/QA40x_BareMetal/Usb.cs:111-115 uses.
/// </summary>
internal sealed class Qa40xTransport : IDisposable
{
    /// <summary>Register channel read/write timeout (Usb.cs:74: <c>RegReadWriteTimeout = 20</c>).</summary>
    private const int RegisterTimeoutMs = 20;

    /// <summary>Data channel read/write timeout (Usb.cs:75: <c>MainI2SReadWriteTimeout = 1000</c>).</summary>
    private const int DataTimeoutMs = 1000;

    private readonly UsbContext _context = new();
    private readonly SemaphoreSlim _registerLock = new(1, 1);

    /// <summary>
    /// Teardown tracing. Every step below can block in native code with no timeout reachable from
    /// managed code, so the only way to know which one hung is a log line before and after each.
    /// Set by <see cref="Qa40xUsbDevice"/> right after construction.
    /// </summary>
    public Action<string>? TraceTeardown { get; set; }

    private IUsbDevice? _device;
    private UsbEndpointWriter? _regWriter;
    private UsbEndpointReader? _regReader;
    private UsbEndpointWriter? _dataWriter;
    private UsbEndpointReader? _dataReader;

    /// <summary>
    /// Which model answered, available only once <see cref="Open"/> has succeeded.
    ///
    /// <para>This is the <i>only</i> place the model can be established, which is why it is detected
    /// here rather than configured: the product id that opened is the one fact on the bus that names
    /// the hardware. <c>Qa40xUsbDevice</c> reads it for its capabilities, its generator ceiling and
    /// the name it reports.</para>
    /// </summary>
    public Qa40xModel? Model { get; private set; }

    /// <summary>
    /// Opens a QA40x analyzer, claims interface 0 (Usb.cs:104-109), and opens all four endpoints.
    /// Synchronous: every LibUsbDotNet call it makes (Find/Open/SetConfiguration/ClaimInterface/
    /// OpenEndpoint*) is itself synchronous.
    ///
    /// <para><b>Each known product id is tried in turn, and the VID is never matched on its own.</b>
    /// 0x16c0 is a shared "free" VID block rather than QuantAsylum's own, so it identifies neither
    /// the model nor the vendor: the QA451 programmable load is 0x16c0:0x4e35
    /// (<see cref="Qa451Protocol.Qa451ProductId"/>) and a great many unrelated V-USB devices share
    /// the block too. A VID-only finder would therefore claim interface 0 of whatever answered
    /// first — see <see cref="Qa40xModel.All"/> for the order and why the QA403 leads it.</para>
    /// </summary>
    public void Open()
    {
        IUsbDevice? device = null;
        Qa40xModel? model = null;
        foreach (var candidate in Qa40xModel.All)
        {
            device = _context.Find(new UsbDeviceFinder { Vid = Qa40xRegisters.Qa40xVendorId, Pid = candidate.ProductId });
            if (device is not null)
            {
                model = candidate;
                break;
            }
        }

        if (device is null || model is null)
        {
            var tried = string.Join(", ", Qa40xModel.All.Select(m => $"{m.ProductName} 0x{m.ProductId:x4}"));
            throw new Qa40xDeviceNotFoundException(
                $"No QA40x analyzer found (VID 0x{Qa40xRegisters.Qa40xVendorId:x4}; tried {tried}).");
        }

        // Open/claim failures are reported distinctly from every later failure because they separate
        // the two very different reasons a QA403 goes unreachable after this app exits, which
        // otherwise look identical from the outside ("can't connect, must power-cycle"):
        //
        //   * claim fails  -> the handle is still held. Some process still has the interface — most
        //                     likely a previous instance of this app that never exited (libusb_close
        //                     can block forever in native code; see Qa40xUsbDevice.StreamAsync's
        //                     remarks). Power-cycling "fixes" it only because re-enumeration
        //                     invalidates the zombie's handle. Killing that process is the real fix.
        //   * claim succeeds, register exchange then fails -> the handle is free but the *device* is
        //                     still streaming from a previous session. That is what ConnectAsync's
        //                     StreamingStop + EP2 drain recovery exists for.
        //
        // Guessing between these is what made the first two attempts at this bug miss.
        try
        {
            device.Open();
            device.SetConfiguration(1);
            device.ClaimInterface(0);
        }
        catch (Exception ex)
        {
            try { device.Dispose(); } catch { /* best-effort */ }

            throw new Qa40xDeviceBusyException(
                $"Found a {model.ProductName} but could not open and claim interface 0. Another process still holds it — "
                    + "check for a leftover Tonebench.App process and end it. "
                    + $"Underlying error: {ex.Message}",
                ex);
        }

        _device = device;
        Model = model;
        _regWriter = device.OpenEndpointWriter(WriteEndpointID.Ep01);
        _regReader = device.OpenEndpointReader(ReadEndpointID.Ep01);
        _dataWriter = device.OpenEndpointWriter(WriteEndpointID.Ep02);
        _dataReader = device.OpenEndpointReader(ReadEndpointID.Ep02);
    }

    /// <summary>
    /// Writes a register (Usb.cs:247-261,276-284): 5-byte frame <c>[reg][value:4 big-endian]</c>.
    /// Serialized against <see cref="ReadRegisterAsync"/> via <see cref="_registerLock"/> so a
    /// two-step read can't be interleaved with a write from another caller (Usb.cs:69,
    /// "so a two-step register read can't be interleaved with a write").
    /// </summary>
    public async Task WriteRegisterAsync(byte reg, uint value, CancellationToken ct)
    {
        if (reg >= Qa40xRegisters.ReadFlag)
            throw new ArgumentOutOfRangeException(nameof(reg), reg, "Register values >= 0x80 are reserved for reads.");

        var writer = _regWriter ?? throw new InvalidOperationException("Transport not open.");
        byte[] frame = BuildRegisterFrame(reg, value);

        await _registerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (error, _) = await TransferAsync(
                () => writer.WriteAsync(frame, 0, frame.Length, RegisterTimeoutMs), "register write").ConfigureAwait(false);
            ThrowIfDeviceLost(error, "register write");
        }
        finally
        {
            _registerLock.Release();
        }
    }

    /// <summary>
    /// Reads a register (Usb.cs:211-240, protocol.md §2.2): write <c>0x80+reg</c> with value 0,
    /// then read 4 bytes back and reassemble big-endian.
    /// </summary>
    public async Task<uint> ReadRegisterAsync(byte reg, CancellationToken ct)
    {
        var writer = _regWriter ?? throw new InvalidOperationException("Transport not open.");
        var reader = _regReader ?? throw new InvalidOperationException("Transport not open.");
        byte[] frame = BuildRegisterFrame((byte)(Qa40xRegisters.ReadFlag + reg), 0);
        byte[] readBuffer = new byte[4];

        await _registerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (writeError, _) = await TransferAsync(
                () => writer.WriteAsync(frame, 0, frame.Length, RegisterTimeoutMs), "register read (write phase)").ConfigureAwait(false);
            ThrowIfDeviceLost(writeError, "register read (write phase)");

            var (readError, readLength) = await TransferAsync(
                () => reader.ReadAsync(readBuffer, 0, readBuffer.Length, RegisterTimeoutMs), "register read (read phase)").ConfigureAwait(false);
            ThrowIfDeviceLost(readError, "register read (read phase)");
            if (readLength == 0)
                throw new InvalidOperationException($"Register {reg} read returned no data.");

            return BinaryPrimitives.ReadUInt32BigEndian(readBuffer);
        }
        finally
        {
            _registerLock.Release();
        }
    }

    /// <summary>
    /// Submits one bulk write on the data endpoint (Usb.cs:304-320 shape, ported to Task-based async)
    /// and returns the number of bytes actually transferred. Callers that care about the DAC actually
    /// receiving what they sent must check it against <paramref name="length"/>: a short or zero-length
    /// write reports no error here (only <see cref="Error.NoDevice"/>/Io/Pipe are treated as fatal,
    /// and a timeout is not), so silently discarding this value hides a completely dead TX path.
    /// </summary>
    public async Task<int> WriteDataAsync(byte[] buffer, int length, CancellationToken ct)
    {
        var writer = _dataWriter ?? throw new InvalidOperationException("Transport not open.");
        var (error, transferLength) = await TransferAsync(
            () => writer.WriteAsync(buffer, 0, length, DataTimeoutMs), "data write").ConfigureAwait(false);
        ThrowIfDeviceLost(error, "data write");
        return transferLength;
    }

    /// <summary>
    /// Submits one bulk read on the data endpoint (Usb.cs:341-346,352-364 shape, ported to Task-based async).
    /// Takes an explicit <paramref name="count"/> rather than always reading <c>buffer.Length</c> so callers can
    /// pass an <see cref="System.Buffers.ArrayPool{T}"/>-rented buffer, which is commonly larger than requested.
    /// </summary>
    public async Task<int> ReadDataAsync(byte[] buffer, int count, CancellationToken ct)
    {
        var reader = _dataReader ?? throw new InvalidOperationException("Transport not open.");
        var (error, transferLength) = await TransferAsync(
            () => reader.ReadAsync(buffer, 0, count, DataTimeoutMs), "data read").ConfigureAwait(false);
        ThrowIfDeviceLost(error, "data read");
        return transferLength;
    }

    /// <summary>
    /// Reads and discards whatever the device has already queued on the data endpoint, until a read
    /// comes back empty or times out. Used once at connect, right after StreamingStop, to clear a
    /// device left mid-stream by a host that died — the stale bytes would otherwise be interpreted
    /// as the first samples of the next acquisition, and can keep the register channel out of step.
    /// Bounded by <paramref name="maxChunks"/> as well as by the empty-read exit so a device that
    /// genuinely will not stop can never spin here forever. Returns the byte count discarded.
    /// </summary>
    public async Task<int> DrainDataEndpointAsync(CancellationToken ct, int maxChunks = 64)
    {
        var reader = _dataReader ?? throw new InvalidOperationException("Transport not open.");
        byte[] scratch = new byte[4096];
        int total = 0;

        for (int i = 0; i < maxChunks; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Short timeout on purpose: on an idle device every one of these costs the full wait, so
            // the drain must not use the 1000 ms DataTimeoutMs the streaming path needs.
            var (error, length) = await TransferAsync(
                () => reader.ReadAsync(scratch, 0, scratch.Length, 50), "data-endpoint drain").ConfigureAwait(false);
            if (error is Error.NoDevice)
                throw new Qa40xDeviceLostException("QA403 data-endpoint drain failed with NoDevice — device appears disconnected.");
            if (length <= 0)
                break;

            total += length;
        }

        return total;
    }

    /// <summary>
    /// Runs one LibUsbDotNet transfer, normalising the *second* way it reports a vanished device.
    ///
    /// Every transfer here has two failure channels, and only one of them was being watched.
    /// <c>WriteAsync</c>/<c>ReadAsync</c> hand back an <see cref="Error"/> for a transfer that
    /// completed unsuccessfully — that is what <see cref="ThrowIfDeviceLost"/> covers — but when the
    /// submit itself fails, <c>AsyncTransfer.TransferAsync</c> calls <c>ErrorExtensions.ThrowOnError</c>
    /// and a <see cref="UsbException"/> comes out instead. Unplugging a connected QA403 and then
    /// writing a register takes exactly that path: <c>UsbException: Input/Output Error</c> escaped the
    /// transport untranslated, so <see cref="Qa40xUsbDevice"/> never saw a device-lost signal, never
    /// raised <see cref="IAnalyzerDevice.DeviceLost"/>, and the app answered the user's Run press with
    /// a 500 and a stack trace.
    /// </summary>
    private static async Task<(Error Error, int Length)> TransferAsync(
        Func<Task<(Error, int)>> transfer, string phase)
    {
        try
        {
            var (error, length) = await transfer().ConfigureAwait(false);
            return (error, length);
        }
        catch (UsbException ex) when (ex.ErrorCode is Error.NoDevice or Error.Io or Error.Pipe or Error.NotFound)
        {
            // Same three codes as ThrowIfDeviceLost, plus NotFound: a handle whose device has been
            // removed. Anything else (Timeout, Busy, Overflow...) is a transfer problem on a device
            // that is still there, and must keep its own exception rather than being reported as an
            // unplug.
            throw new Qa40xDeviceLostException($"QA403 {phase} failed with {ex.ErrorCode} — device appears disconnected.");
        }
    }

    /// <summary><see cref="Error.NoDevice"/> is documented by LibUsbDotNet itself as "it may have been disconnected" — the one unambiguous disconnect signal. Io/Pipe are also treated as disconnect since a freshly-unplugged bulk endpoint on Windows/Linux commonly surfaces as one of those instead of NoDevice.</summary>
    private static void ThrowIfDeviceLost(Error error, string phase)
    {
        if (error is Error.NoDevice or Error.Io or Error.Pipe)
            throw new Qa40xDeviceLostException($"QA403 {phase} failed with {error} — device appears disconnected.");
    }

    /// <summary>5-byte register frame: [reg][value:4 big-endian] (Extensions.cs:11-16, Usb.cs:276-284).</summary>
    private static byte[] BuildRegisterFrame(byte reg, uint value)
    {
        byte[] frame = new byte[5];
        frame[0] = reg;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), value);
        return frame;
    }

    public void Dispose()
    {
        var stopwatch = Stopwatch.StartNew();
        void Trace(string step) => TraceTeardown?.Invoke($"{step} at {stopwatch.ElapsedMilliseconds} ms");

        Trace("transport dispose: begin");

        try
        {
            if (_device is { IsOpen: true } openDevice)
                openDevice.ReleaseInterface(0);

            Trace("transport dispose: interface released");
        }
        catch (Exception ex)
        {
            // Best-effort on teardown — the device may already be gone.
            Trace($"transport dispose: interface release failed ({ex.GetType().Name})");
        }

        try
        {
            // Dispose, not just Close(). These are different operations in LibUsbDotNet 3.0.224 and
            // only Close() was being called, which is the whole cause of the 0xC0000005 crash in
            // NativeMethods.UnrefDevice seen on the finalizer thread. Two distinct native resources
            // are in play: Close() releases the *device handle* (libusb_close), while the underlying
            // *device reference* (libusb_unref_device) is released by the Device SafeHandle, which
            // only UsbDevice.Dispose() disposes (UsbDevice.cs:231-239). UsbContext.Find() hands back
            // its match via Clone() — `new UsbDevice(NativeMethods.RefDevice(device), ...)`
            // (UsbDevice.cs:219-222) — so that reference is ours to release. Left undisposed it stays
            // reachable through _device, survives any GC we force here, and is finalized only once
            // this transport itself becomes garbage — by which point _context.Dispose() below has
            // already freed the libusb context out from under it, and the unref faults.
            _device?.Dispose();
            Trace("transport dispose: device disposed (libusb_close returned)");
        }
        catch (Exception ex)
        {
            // Best-effort on teardown — the device may already be gone.
            Trace($"transport dispose: device dispose failed ({ex.GetType().Name})");
        }
        finally
        {
            _device = null;
        }

        _registerLock.Dispose();

        // Backstop only, now that the device above is released deterministically: Find() scans the
        // whole bus and builds a Device SafeHandle per device, though it does dispose that collection
        // itself (UsbContext.cs, Find -> `using (List())`). Anything the library still leaks must be
        // finalized while the context is alive, since finalizing it afterwards is the crash above.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Trace("transport dispose: finalizers drained");

        try
        {
            _context.Dispose();
        }
        catch
        {
            // Observed with LibUsbDotNet 3.0.224: UsbContext.Dispose() can throw
            // NullReferenceException when the context never successfully found/opened a device
            // (e.g. Open() failed before any device was claimed). Swallow it — we're already in
            // best-effort teardown — but still suppress the finalizer below regardless: without
            // that, the same bug fires again later from the finalizer thread and crashes the
            // whole process, which is strictly worse than a leaked native handle here.
        }
        finally
        {
            GC.SuppressFinalize(_context);
            Trace("transport dispose: done");
        }
    }
}
