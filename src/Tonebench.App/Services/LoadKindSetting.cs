namespace Tonebench.App.Services;

/// <summary>
/// Which programmable load the app should come up on: the live copy of
/// <c>ProgrammableLoadConfig</c>, loaded at startup and rewritten whenever the load is switched.
///
/// <para>Unlike <see cref="AnalyzerKindSetting"/>, <b>this one is not order-sensitive at startup</b>,
/// and that is a deliberate design choice rather than luck: <see cref="LoadSessionService"/> builds
/// nothing in its constructor, so resolving it cannot pin a kind the way resolving
/// <see cref="DeviceSessionService"/> pins <see cref="AnalyzerDeviceFactory.InitialKind"/>. That trap
/// cost a correctly-written preference being read too late (see
/// <see cref="StartupSettingsLoader"/>); a service that connects on request instead of on
/// construction cannot fall into it.</para>
/// </summary>
public sealed class LoadKindSetting
{
    /// <summary>
    /// The remembered load. <see cref="ProgrammableLoadKind.None"/> by default, because most benches
    /// have no load and the ones that do will say so once.
    /// </summary>
    public ProgrammableLoadKind Kind { get; set; } = ProgrammableLoadKind.None;
}
