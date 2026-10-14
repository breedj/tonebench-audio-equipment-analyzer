namespace Tonebench.Device.Hardware;

/// <summary>
/// One HID device, opened by VID/PID, exchanging whole reports. Deliberately the narrowest surface
/// that <see cref="Qa451Load"/> needs — open, write a report, read a report with a timeout — so that
/// the driver's logic is testable against a scripted fake and the platform code holds no protocol
/// knowledge at all.
///
/// <para>Why this app owns its HID transport instead of taking a dependency: the two platform
/// implementations are a few dozen lines each over APIs that are already present everywhere
/// (<c>hid.dll</c> on Windows, <c>/dev/hidraw</c> on Linux), there is no native library to vendor
/// or to credit in the About dialog, and nothing has to be installed on either OS — a QA451 binds
/// to Windows' own HidUsb driver and to Linux's hidraw. The QA403's libusb path is the opposite
/// trade and for good reason: it moves megabytes a second and needs queued bulk transfers. This
/// device answers one 64-byte question a second.</para>
/// </summary>
internal interface IHidTransport : IDisposable
{
    /// <summary>True between a successful <see cref="Open"/> and a loss or dispose.</summary>
    bool IsOpen { get; }

    /// <summary>The USB serial-number string, available once open. Null when the platform cannot read it.</summary>
    string? UsbSerial { get; }

    /// <summary>
    /// Finds and opens the first matching device.
    /// </summary>
    /// <exception cref="Qa451DeviceNotFoundException">No device with that VID/PID is attached.</exception>
    /// <exception cref="Qa451DeviceLostException">One was found but could not be opened.</exception>
    void Open();

    /// <summary>
    /// Writes one whole output report, including its leading report-id byte.
    /// </summary>
    /// <exception cref="Qa451DeviceLostException">The write failed at the OS level.</exception>
    /// <exception cref="TimeoutException">The write did not complete in time.</exception>
    void Write(ReadOnlySpan<byte> report);

    /// <summary>
    /// Reads one whole input report, including its leading report-id byte, and returns how many
    /// bytes arrived.
    /// </summary>
    /// <exception cref="Qa451DeviceLostException">The read failed at the OS level.</exception>
    /// <exception cref="TimeoutException">No report arrived within <paramref name="timeoutMs"/>.</exception>
    int Read(Span<byte> report, int timeoutMs);
}
