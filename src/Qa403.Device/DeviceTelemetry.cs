namespace Qa403.Device;

/// <summary>
/// The analyzer's own housekeeping sensors — the four figures QuantAsylum's software shows in its
/// status bar beside the firmware version. Nothing here describes a measurement: these are the
/// instrument's supply rails and its die temperature, useful for spotting a marginal USB port or a
/// unit that has been left running in a warm rack.
///
/// <para>Decoded from read-only registers 0x11/0x12/0x16 (see
/// <see cref="Hardware.Qa40xRegisters.DecodeTelemetry"/>). The raw units are millivolts,
/// milliamps and deci-degrees; this record carries them converted, so the wire and the UI never
/// repeat the scaling.</para>
///
/// <para>Three figures, not the vendor status bar's four: there is deliberately no isolated-rail
/// current, because a QA403 does not report one — see
/// <see cref="Hardware.Qa40xRegisters.TelemetryIsoCurrent"/>, which records what its register
/// actually answers and why nothing reads it.</para>
///
/// <para>A device with no such sensors — the simulator — reports no telemetry at all rather than
/// plausible constants, so nothing on screen can be mistaken for a reading taken from hardware.</para>
/// </summary>
/// <param name="UsbVoltageV">The USB bus rail, in volts. Nominally 5 V; a long or thin cable shows up here.</param>
/// <param name="UsbCurrentMa">What the unit is drawing from that rail, in milliamps.</param>
/// <param name="TemperatureC">Internal temperature, in degrees Celsius.</param>
public sealed record DeviceTelemetry(
    double UsbVoltageV,
    double UsbCurrentMa,
    double TemperatureC);
