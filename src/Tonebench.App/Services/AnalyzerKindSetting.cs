namespace Tonebench.App.Services;

/// <summary>
/// Which analyzer the app should come up on: the live copy of <c>AnalyzerKindConfig</c>, loaded at
/// startup and rewritten whenever the status bar's Analyzer dropdown is used.
///
/// <para>A mutable live copy rather than a value baked into <see cref="AnalyzerDeviceFactory"/>'s
/// registration, for the same reason <c>SoundCardSetting</c> is one: the factory is resolved
/// lazily, after the startup block has read the configuration database, so it can read the
/// remembered kind from here without the registration having to do database work.</para>
///
/// <para><b><c>--simulate</c> still wins.</b> An explicit flag on the command line is a decision
/// about this run; the document is a preference from the last one, and a flag that a stale
/// preference could override would make <c>run-simulator.bat</c> unreliable.</para>
/// </summary>
public sealed class AnalyzerKindSetting
{
    public DeviceKind Kind { get; set; } = DeviceKind.Hardware;
}
