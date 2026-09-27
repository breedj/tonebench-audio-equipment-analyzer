namespace Qa403.Device.Hardware;

/// <summary>
/// Register addresses and value-encoding tables for the QA403 control channel. Every entry here
/// is cited to a reference source per docs/protocol.md — nothing is inferred or guessed (see
/// CLAUDE.md "Never invent USB protocol details").
/// </summary>
internal static class Qa40xRegisters
{
    /// <summary>USB VID for QA403 (reference/qa40x-baremetal/QA40x_BareMetal/Form1.cs:43).</summary>
    public const int Qa403VendorId = 0x16c0;

    /// <summary>USB PID for QA403 (reference/qa40x-baremetal/QA40x_BareMetal/Form1.cs:43).</summary>
    public const int Qa403ProductId = 0x4e39;

    /// <summary>Connection-verify scratch register: write a random uint32, read it back, must match (Usb.cs:189-204).</summary>
    public const byte ConnectionVerify = 0;

    /// <summary>Input range select, values 0..7 (Form1.cs:74-81). See <see cref="InputRangeDbvToRegister"/>.</summary>
    public const byte InputRange = 5;

    /// <summary>Output range select, values 0..3 (Form1.cs:89-92). See <see cref="OutputRangeDbvToRegister"/>.</summary>
    public const byte OutputRange = 6;

    /// <summary>Streaming enable: write 0x5 to start, 0x0 to stop, before sending any data (Acquisition.cs:127-129,172,190).</summary>
    public const byte StreamingEnable = 8;

    /// <summary>Streaming-start value for <see cref="StreamingEnable"/> (Acquisition.cs:129). Individual bit meanings are undocumented (protocol.md §7) — treat as opaque.</summary>
    public const uint StreamingStart = 0x5;

    /// <summary>Streaming-stop value for <see cref="StreamingEnable"/> (Acquisition.cs:172,190).</summary>
    public const uint StreamingStop = 0x0;

    /// <summary>
    /// Sample rate index register. Not touched by qa40x-baremetal at all; sourced from
    /// reference/PyQa40x-main/PyQa40x-main/src/PyQa40x/control.py:40-49, corroborated by
    /// reference/qa40x-rs-main (docs/protocol.md §2.4). Write the index (0/1/2/3), never the Hz
    /// value, then wait <see cref="SampleRateSettleMs"/>.
    /// </summary>
    public const byte SampleRate = 9;

    /// <summary>Settle delay after writing <see cref="SampleRate"/> (PyQa40x control.py:49: <c>time.sleep(0.1)</c>).</summary>
    public const int SampleRateSettleMs = 100;

    /// <summary>
    /// Calibration flash page-select register. Write <c>0x10</c> to select the factory
    /// calibration page (page 0), then stream it out via <see cref="CalibrationPageStream"/>
    /// (PyQa40x control.py:58-59; qa40x-rs-main register.rs:85-94 names it PAGE_SELECT).
    /// </summary>
    public const byte CalibrationPageSelect = 0x0d;

    /// <summary>Value written to <see cref="CalibrationPageSelect"/> to select the factory calibration page (PyQa40x control.py:58).</summary>
    public const uint CalibrationPageSelectFactoryPage = 0x10;

    /// <summary>
    /// Calibration page stream register: each read returns the next 4 bytes (little-endian) of
    /// the 512-byte selected page. Read <see cref="CalibrationPageBytes"/>/4 = 128 times
    /// (PyQa40x control.py:59-65; qa40x-rs-main names it CALIBRATION).
    /// </summary>
    public const byte CalibrationPageStream = 0x19;

    /// <summary>Size in bytes of one calibration flash page (PyQa40x control.py:59).</summary>
    public const int CalibrationPageBytes = 512;

    /// <summary>
    /// Firmware build number, read-only. Tier-2 source: qa40x-rs-main only, "confirmed on
    /// hardware by a read-only register scan" per that project's own comment (docs/protocol.md
    /// §2.4) — not cross-checked against PyQa40x or qa40x-baremetal. Worst case on real QA403
    /// hardware this reads a value that doesn't decode into a sensible version string.
    /// </summary>
    public const byte FirmwareVersion = 0x10;

    /// <summary>Unit serial number, packed u32, read-only. Same Tier-2 caveat as <see cref="FirmwareVersion"/> (docs/protocol.md §2.4).</summary>
    public const byte SerialNumber = 0x1d;

    /// <summary>
    /// USB rail voltage in millivolts, read-only. Tier-2 source: qa40x-rs-main only
    /// (register.rs:39-42, doc/device-notes.md:66-75 — "telemetry decodings (mV / mA / deci-°C)
    /// were validated on hardware"), not cross-checked against PyQa40x or qa40x-baremetal, neither
    /// of which touches this register. Same caveat as <see cref="FirmwareVersion"/>
    /// (docs/protocol.md §2.4). Read-only, so the worst case is a nonsense number on screen.
    /// </summary>
    public const byte TelemetryUsbVoltage = 0x11;

    /// <summary>USB rail current in milliamps, read-only. Same Tier-2 source and caveat as <see cref="TelemetryUsbVoltage"/>.</summary>
    public const byte TelemetryUsbCurrent = 0x12;

    /// <summary>
    /// Isolated-rail current, read-only — documented by qa40x-rs-main as milliamps, and
    /// <b>deliberately not read by this app</b>, which is why the constant exists only to record
    /// that decision.
    ///
    /// <para>QuantAsylum's own software shows <c>ISO Current: ---</c> on a QA403 rather than a
    /// figure. Measured here on a real QA403 (2026-09-27) while the other two registers tracked a
    /// live load — 4.844/4.845 V and 877-882 mA over five polls — this one answered a constant
    /// <c>4626</c>, which is <c>0x1212</c>. Not 4.6 A of anything, and not a sensor: the QA402 that
    /// the reference's capture came from has an isolated rail and this model evidently does not.
    /// Displaying it would put a fabricated ampere reading in the status bar, so the readout has
    /// three figures and not four.</para>
    /// </summary>
    public const byte TelemetryIsoCurrent = 0x13;

    /// <summary>
    /// Internal temperature in deci-degrees Celsius, read-only. Same Tier-2 source and caveat as
    /// <see cref="TelemetryUsbVoltage"/>.
    ///
    /// <para>Note the gap: 0x14 and 0x15 sit between the currents and this register but are
    /// <i>not</i> telemetry — they are the firmware trace buffer's readout and its length
    /// (constant 0x418 on a healthy unit). qa40x-rs-main polled 0x15 as a fifth telemetry word for
    /// a long time before a USB capture identified it, so do not "complete the range".</para>
    /// </summary>
    public const byte TelemetryTemperature = 0x16;

    /// <summary>
    /// Raw telemetry registers -> <see cref="DeviceTelemetry"/>. Split out from the device so the
    /// scaling can be tested without hardware; the decodings are qa40x-rs-main's
    /// (device.rs:2077-2088), which that project states were validated against a real unit.
    /// </summary>
    public static DeviceTelemetry DecodeTelemetry(uint usbVoltageRaw, uint usbCurrentRaw, uint temperatureRaw)
        => new(
            UsbVoltageV: usbVoltageRaw / 1000.0,
            UsbCurrentMa: usbCurrentRaw,
            TemperatureC: temperatureRaw / 10.0);

    /// <summary>Read-request flag: OR this into a register address to turn a write frame into a two-step read (Usb.cs:223, protocol.md §2.2).</summary>
    public const byte ReadFlag = 0x80;

    /// <summary>Input full-scale ranges the QA403 supports, in ascending dBV order (Form1.cs:70-81, DeviceCapabilities.Qa403).</summary>
    public static readonly IReadOnlyList<double> InputRangesDbv = [0, 6, 12, 18, 24, 30, 36, 42];

    /// <summary>Output full-scale ranges the QA403 supports, in ascending dBV order (Form1.cs:86-94, DeviceCapabilities.Qa403).</summary>
    public static readonly IReadOnlyList<double> OutputRangesDbv = [-12, -2, 8, 18];

    /// <summary>
    /// dBV -> register-5 value. Ported verbatim from PyQa40x control.py:17
    /// (<c>input2reg = {0: 0, 6: 1, 12: 2, 18: 3, 24: 4, 30: 5, 36: 6, 42: 7}</c>), which agrees
    /// exactly with baremetal's switch (Form1.cs:74-81).
    /// </summary>
    public static byte InputRangeDbvToRegister(double dbv) => dbv switch
    {
        0 => 0,
        6 => 1,
        12 => 2,
        18 => 3,
        24 => 4,
        30 => 5,
        36 => 6,
        42 => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(dbv), dbv, $"Unsupported input range. Supported values: {string.Join(", ", InputRangesDbv)} dBV."),
    };

    /// <summary>
    /// dBV -> register-6 value. Ported verbatim from PyQa40x control.py:16
    /// (<c>output2reg = {18: 3, 8: 2, -2: 1, -12: 0}</c>), which agrees exactly with baremetal's
    /// switch (Form1.cs:89-92).
    /// </summary>
    public static byte OutputRangeDbvToRegister(double dbv) => dbv switch
    {
        -12 => 0,
        -2 => 1,
        8 => 2,
        18 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(dbv), dbv, $"Unsupported output range. Supported values: {string.Join(", ", OutputRangesDbv)} dBV."),
    };

    /// <summary>
    /// Sample rate Hz -> register-9 index. Ported verbatim from PyQa40x control.py:18
    /// (<c>samplerate2reg = {48000: 0, 96000: 1, 192000: 2}</c>), extended with QA403's 384 kHz
    /// per qa40x-rs-main (docs/protocol.md §2.4: index 3, QA403-only).
    /// </summary>
    public static uint SampleRateHzToRegister(double sampleRateHz) => sampleRateHz switch
    {
        48_000 => 0,
        96_000 => 1,
        192_000 => 2,
        384_000 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "Unknown sample rate."),
    };
}
