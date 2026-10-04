using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>
/// The live copy of <see cref="Experimental384kConfig"/>: whether the analyzer reports 384 kHz as a
/// capture rate.
///
/// <para>A singleton in memory rather than a document read on demand, because its reader is
/// <c>Qa40xUsbDevice.Capabilities</c> — a property, evaluated on every status poll and inside
/// per-point runner loops, which has no business doing database I/O.
/// <see cref="StartupSettingsLoader"/> loads it once and <c>/api/config/experimental-384k</c> writes
/// both the document and this.</para>
///
/// <para><b>The device reads it through a <c>Func</c>, not a captured value</b>
/// (<c>AnalyzerDeviceFactory</c> passes <c>() =&gt; setting.Enabled</c>). That is what makes the
/// checkbox take effect on the already-constructed device without a reconnect, and what makes the
/// startup ordering safe: the first device is built the moment <see cref="DeviceSessionService"/> is
/// resolved, which can precede the document being read. See
/// <c>Qa40xUsbDevice.Allow384kHz</c>.</para>
/// </summary>
public sealed class Experimental384kSetting
{
    private volatile bool _enabled;

    /// <summary>
    /// Whether 384 kHz is offered. Written by the endpoint and at startup; read live by whichever
    /// device the session currently holds.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
