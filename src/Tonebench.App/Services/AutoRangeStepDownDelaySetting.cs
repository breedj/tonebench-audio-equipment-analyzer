using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>
/// The live copy of <see cref="AutoRangeStepDownDelayConfig"/>: how long a peak must stay
/// under-range before the input range steps down, in milliseconds.
///
/// <para>A singleton in memory for a different reason from <see cref="Experimental384kSetting"/>'s.
/// That one exists because its reader is a property on a hot path; this one exists because its
/// reader is <see cref="AnalyzerDeviceFactory.Create"/>, which runs whenever the analyzer is
/// switched and must not do database I/O to answer. The document is the durable copy;
/// <c>POST /api/device/auto-range-step-down-delay</c> writes the device, this, and the document
/// together, so the three cannot drift.</para>
///
/// <para><b>Written into the device rather than read from it.</b> The delay is a field on the
/// device's own ranger (<c>Qa40xAutoRanger.StepDownDelayMs</c>), so unlike the 384 kHz opt-in there
/// is no <c>Func</c> to read it live: a device built after the setting was loaded has to be told.
/// That is what carries the value across a restart and across an analyzer switch, and why the
/// <see cref="StartupSettingsLoader"/> ordering rule applies to it — the first device is built the
/// moment anything resolves <see cref="DeviceSessionService"/>.</para>
/// </summary>
public sealed class AutoRangeStepDownDelaySetting
{
    private double _ms = AutoRangeStepDownDelayConfig.Default.DelayMs;

    /// <summary>
    /// The stored delay in milliseconds. Written by the endpoint and at startup; read by the factory
    /// for each device it builds.
    /// </summary>
    public double Ms
    {
        get => Volatile.Read(ref _ms);
        set => Volatile.Write(ref _ms, value);
    }
}
