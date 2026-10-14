using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Services;

/// <summary>Which programmable load the app is talking to, if any.</summary>
public enum ProgrammableLoadKind
{
    /// <summary>
    /// No load. The default, and not merely "off": most benches do not have one, and a bench that
    /// does not must never be shown a load screen reporting a device that is not there.
    /// </summary>
    None = 0,

    /// <summary>A real QA451 or QA451B over USB HID — see <see cref="Qa451Load"/>.</summary>
    Qa451 = 1,

    /// <summary>The synthetic load — see <see cref="SimulatedProgrammableLoad"/>.</summary>
    Simulated = 2,
}

/// <summary>
/// Makes programmable loads on demand, so which one the app is using is a runtime choice.
///
/// <para>Shaped like <see cref="AnalyzerDeviceFactory"/> and for the same reasons: the container
/// does not own the load's lifetime, because a singleton it cannot replace is one it must not
/// dispose out from under a switch. <see cref="LoadSessionService"/> creates and disposes every
/// load itself.</para>
/// </summary>
/// <remarks>
/// Not sealed, and <see cref="Create"/> is virtual, for the reason the analyzer's factory gives: a
/// unit test must never construct a <see cref="Qa451Load"/>, because on a machine with a QA451
/// attached that would open the real device and turn an ordinary test into a hardware-dependent
/// one. Overriding this is how the session tests get a fake load.
/// </remarks>
public class ProgrammableLoadFactory(ILoggerFactory loggerFactory)
{
    /// <summary>The load for <paramref name="kind"/>, or null for <see cref="ProgrammableLoadKind.None"/>.</summary>
    public virtual IProgrammableLoad? Create(ProgrammableLoadKind kind) => kind switch
    {
        ProgrammableLoadKind.Qa451 => new Qa451Load(loggerFactory.CreateLogger<Qa451Load>()),
        ProgrammableLoadKind.Simulated => new SimulatedProgrammableLoad(),
        _ => null,
    };
}
