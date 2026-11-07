using System.Globalization;
using System.Runtime.InteropServices;

namespace Tonebench.Device.Hardware;

/// <summary>
/// HID transport over Linux's <c>hidraw</c>. A QA451 binds to the in-kernel <c>hid-generic</c>
/// driver, so the device node is all that is needed — no libusb, no detaching a kernel driver, and
/// nothing to install.
///
/// <para>Discovery reads <c>/sys/class/hidraw/*/device/uevent</c>, whose <c>HID_ID</c> line carries
/// the bus, vendor and product as <c>0003:000016C0:00004E35</c>. That is a plain file read per
/// candidate, which is why there is no udev or libudev dependency here.</para>
///
/// <para>Reads go through <c>poll(2)</c> first, for the same reason the Windows side uses overlapped
/// I/O: this device answers only when asked, so a bare blocking read on a firmware hiccup would
/// park the calling thread with nothing able to interrupt it.</para>
///
/// <para>Opening the node needs write access to it, which on a stock distribution means a udev rule
/// — the same arrangement as <c>packaging/udev/99-qa403.rules</c>, for VID 16c0 PID 4e35.</para>
/// </summary>
internal sealed class LinuxHidRawTransport(int vendorId, int productId) : IHidTransport
{
    private const short PollIn = 0x001;
    private const string HidRawClass = "/sys/class/hidraw";

    private FileStream? _stream;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsOpen => _stream is not null;

    /// <inheritdoc />
    public string? UsbSerial { get; private set; }

    /// <inheritdoc />
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? node = FindDeviceNode(out string? sysfsDirectory);
        if (node is null)
        {
            throw new Qa451DeviceNotFoundException(
                $"No hidraw device found with VID 0x{vendorId:x4}, PID 0x{productId:x4}.");
        }

        try
        {
            // bufferSize 1 keeps FileStream out of the way: hidraw is report-oriented, and a read
            // has to return exactly the report the kernel queued rather than a slice of a buffer.
            _stream = new FileStream(node, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new Qa451DeviceLostException(
                $"Found a QA451 at {node} but cannot open it for writing. Install the udev rule for "
                    + $"VID 0x{vendorId:x4} PID 0x{productId:x4} and make sure this user is in the group it "
                    + "grants (the QA40x ships the same arrangement in packaging/udev).",
                ex);
        }
        catch (IOException ex)
        {
            throw new Qa451DeviceLostException($"Could not open {node}: {ex.Message}", ex);
        }

        UsbSerial = ReadSerial(sysfsDirectory);
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> report)
    {
        FileStream stream = RequireOpen();
        try
        {
            stream.Write(report);
            stream.Flush();
        }
        catch (IOException ex)
        {
            throw new Qa451DeviceLostException($"hidraw write failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public int Read(Span<byte> report, int timeoutMs)
    {
        FileStream stream = RequireOpen();

        var fds = new PollFd
        {
            Fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
            Events = PollIn,
        };

        int ready = poll(ref fds, 1, timeoutMs);
        if (ready < 0)
        {
            throw new Qa451DeviceLostException($"poll() on the hidraw node failed (errno {Marshal.GetLastWin32Error()}).");
        }

        if (ready == 0)
        {
            throw new TimeoutException($"QA451 sent no HID report within {timeoutMs} ms.");
        }

        try
        {
            return stream.Read(report);
        }
        catch (IOException ex)
        {
            throw new Qa451DeviceLostException($"hidraw read failed: {ex.Message}", ex);
        }
    }

    private FileStream RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stream ?? throw new InvalidOperationException("The HID transport is not open.");
    }

    /// <summary>
    /// Returns the first <c>/dev/hidraw*</c> node whose sysfs entry reports this VID/PID, and that
    /// entry's directory so the serial number can be looked up from it.
    /// </summary>
    private string? FindDeviceNode(out string? sysfsDirectory)
    {
        sysfsDirectory = null;
        if (!Directory.Exists(HidRawClass))
        {
            return null;
        }

        string expected = string.Create(
            CultureInfo.InvariantCulture, $"HID_ID=0003:{vendorId:X8}:{productId:X8}");

        foreach (string entry in Directory.EnumerateDirectories(HidRawClass))
        {
            string uevent = Path.Combine(entry, "device", "uevent");
            if (!File.Exists(uevent))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(uevent);
            }
            catch (IOException)
            {
                continue;
            }

            if (!lines.Any(line => line.Equals(expected, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string node = "/dev/" + Path.GetFileName(entry);
            if (!File.Exists(node))
            {
                continue;
            }

            sysfsDirectory = entry;
            return node;
        }

        return null;
    }

    /// <summary>
    /// Best-effort USB serial: walk up from the hidraw entry until a <c>serial</c> file appears,
    /// which is on the USB device node a couple of levels above the HID one. Null rather than an
    /// exception if it is not there — the serial is for telling two units apart, not for talking to
    /// one.
    /// </summary>
    private static string? ReadSerial(string? sysfsDirectory)
    {
        if (sysfsDirectory is null)
        {
            return null;
        }

        string? directory = Path.Combine(sysfsDirectory, "device");
        for (int level = 0; level < 6 && directory is not null; level++)
        {
            string candidate = Path.Combine(directory, "serial");
            if (File.Exists(candidate))
            {
                try
                {
                    return File.ReadAllText(candidate).Trim();
                }
                catch (IOException)
                {
                    return null;
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream?.Dispose();
        _stream = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, uint count, int timeoutMs);
}
