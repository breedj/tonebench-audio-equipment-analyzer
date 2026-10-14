using System.Runtime.InteropServices;
using System.Text;

namespace Tonebench.Device.Hardware;

/// <summary>
/// HID transport over Windows' own <c>hid.dll</c> and SetupAPI. A QA451 binds to the in-box HidUsb
/// driver, so there is nothing to install and nothing to vendor.
///
/// <para>Every transfer is <b>overlapped with a timeout</b>, which is the whole reason this is not
/// three lines over a <see cref="FileStream"/>: a synchronous HID read blocks until a report
/// arrives, and this device only ever answers when asked. One firmware hiccup would then hang the
/// calling thread for ever, with no <see cref="CancellationToken"/> able to reach it — the same
/// class of fault as the QA403's native-code stalls.</para>
///
/// <para>The report buffers are unmanaged, allocated once. An overlapped transfer hands the kernel a
/// pointer that must stay put until it completes, and a timeout path explicitly does not wait for
/// completion, so a managed array would have to stay pinned past the point where this code stops
/// looking at it.</para>
/// </summary>
internal sealed class WindowsHidTransport(int vendorId, int productId) : IHidTransport
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int DigcfPresent = 0x02;
    private const int DigcfDeviceInterface = 0x10;
    private const int ErrorIoPending = 997;
    private const int WaitObject0 = 0;
    private const int WaitTimeout = 0x102;

    /// <summary>How long to wait for a cancelled transfer to unwind before giving up on it.</summary>
    private const int CancelDrainMs = 200;

    private static readonly IntPtr InvalidHandle = new(-1);

    private IntPtr _handle = InvalidHandle;
    private IntPtr _event = IntPtr.Zero;
    private IntPtr _overlapped = IntPtr.Zero;
    private IntPtr _buffer = IntPtr.Zero;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsOpen => _handle != InvalidHandle;

    /// <inheritdoc />
    public string? UsbSerial { get; private set; }

    /// <inheritdoc />
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? path = FindInterfacePath();
        if (path is null)
        {
            throw new Qa451DeviceNotFoundException(
                $"No HID device found with VID 0x{vendorId:x4}, PID 0x{productId:x4}.");
        }

        IntPtr handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle == InvalidHandle)
        {
            throw new Qa451DeviceLostException(
                $"Found the device at {path} but could not open it (error {Marshal.GetLastWin32Error()}). "
                    + "Another process may hold it — QuantAsylum's own QA451 application, most likely.");
        }

        _handle = handle;
        _event = CreateEvent(IntPtr.Zero, manualReset: true, initialState: false, name: null);
        _overlapped = Marshal.AllocHGlobal(OverlappedSize);
        _buffer = Marshal.AllocHGlobal(Qa451Protocol.ReportLength);
        UsbSerial = ReadSerial(handle);
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> report)
    {
        RequireOpen();
        Marshal.Copy(report.ToArray(), 0, _buffer, report.Length);
        Transfer(write: true, report.Length, Qa451Protocol.ExchangeTimeoutMs);
    }

    /// <inheritdoc />
    public int Read(Span<byte> report, int timeoutMs)
    {
        RequireOpen();
        int transferred = Transfer(write: false, report.Length, timeoutMs);

        var managed = new byte[transferred];
        Marshal.Copy(_buffer, managed, 0, transferred);
        managed.AsSpan().CopyTo(report[..transferred]);
        return transferred;
    }

    /// <summary>
    /// One overlapped transfer in either direction. The timeout path cancels the request and waits
    /// briefly for the kernel to let go of the buffer before returning — without that, the next
    /// transfer would hand the same memory to a request that is still outstanding.
    /// </summary>
    private int Transfer(bool write, int length, int timeoutMs)
    {
        ResetEvent(_event);
        for (int offset = 0; offset < OverlappedSize; offset += IntPtr.Size)
        {
            Marshal.WriteIntPtr(_overlapped, offset, IntPtr.Zero);
        }

        Marshal.WriteIntPtr(_overlapped, 2 * IntPtr.Size + 8, _event);

        bool completed = write
            ? WriteFile(_handle, _buffer, length, IntPtr.Zero, _overlapped)
            : ReadFile(_handle, _buffer, length, IntPtr.Zero, _overlapped);

        int error = Marshal.GetLastWin32Error();
        if (!completed && error != ErrorIoPending)
        {
            throw new Qa451DeviceLostException(
                $"HID {(write ? "write" : "read")} failed (error {error}).");
        }

        int wait = WaitForSingleObject(_event, timeoutMs);
        if (wait == WaitTimeout)
        {
            CancelIoEx(_handle, _overlapped);
            WaitForSingleObject(_event, CancelDrainMs);
            throw new TimeoutException(
                $"QA451 did not complete a HID {(write ? "write" : "read")} within {timeoutMs} ms.");
        }

        if (wait != WaitObject0)
        {
            throw new Qa451DeviceLostException(
                $"Waiting on a HID {(write ? "write" : "read")} failed (error {Marshal.GetLastWin32Error()}).");
        }

        if (!GetOverlappedResult(_handle, _overlapped, out int transferred, wait: false))
        {
            throw new Qa451DeviceLostException(
                $"HID {(write ? "write" : "read")} did not complete (error {Marshal.GetLastWin32Error()}).");
        }

        return transferred;
    }

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen)
        {
            throw new InvalidOperationException("The HID transport is not open.");
        }
    }

    /// <summary>
    /// OVERLAPPED is two pointer-sized fields, then two DWORDs, then the event handle — so both its
    /// size and the handle's offset follow <see cref="IntPtr.Size"/> rather than being 32 and 24
    /// everywhere.
    /// </summary>
    private static int OverlappedSize => (3 * IntPtr.Size) + 8;

    private string? FindInterfacePath()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle)
        {
            return null;
        }

        try
        {
            for (int index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>(),
                };

                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    return null;
                }

                string? path = InterfacePath(set, ref interfaceData);
                if (path is null)
                {
                    continue;
                }

                // Opened with no access rights at all: enough to ask hid.dll what the device is,
                // and it cannot disturb whoever currently holds it for I/O.
                IntPtr probe = CreateFile(
                    path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (probe == InvalidHandle)
                {
                    continue;
                }

                try
                {
                    var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                    if (HidD_GetAttributes(probe, ref attributes)
                        && attributes.VendorId == vendorId
                        && attributes.ProductId == productId)
                    {
                        return path;
                    }
                }
                finally
                {
                    CloseHandle(probe);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? InterfacePath(IntPtr set, ref SpDeviceInterfaceData interfaceData)
    {
        int required = 0;
        SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required <= 0)
        {
            return null;
        }

        IntPtr detail = Marshal.AllocHGlobal(required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA: the struct's own header plus one char of
            // the path, which is 8 on 64-bit and 6 on 32-bit.
            Marshal.WriteInt32(detail, 0, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, detail, required, ref required, IntPtr.Zero)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static string? ReadSerial(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        return HidD_GetSerialNumberString(handle, builder, builder.Capacity * 2) ? builder.ToString() : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != InvalidHandle)
        {
            CloseHandle(_handle);
            _handle = InvalidHandle;
        }

        if (_event != IntPtr.Zero)
        {
            CloseHandle(_event);
            _event = IntPtr.Zero;
        }

        if (_overlapped != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_overlapped);
            _overlapped = IntPtr.Zero;
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr handle, ref HiddAttributes attributes);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetSerialNumberString(IntPtr handle, StringBuilder buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr set, IntPtr deviceInfo, ref Guid guid, int index, ref SpDeviceInterfaceData interfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr set,
        ref SpDeviceInterfaceData interfaceData,
        IntPtr detail,
        int detailSize,
        ref int required,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr handle, IntPtr buffer, int toRead, IntPtr read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr handle, IntPtr buffer, int toWrite, IntPtr written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
}
