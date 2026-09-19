namespace Qa403.App.Services;

/// <summary>
/// Something that must be stopped before the project can change: an acquisition, or a sweep.
///
/// <para>Both, and not just the acquisition, because a sweep runner is <i>not</i> acquiring between
/// points — it stops the stream, reconfigures the generator and starts again — so a project switch
/// that only asked <see cref="DeviceSessionService.Acquiring"/> would find a gap and pull the
/// database out from under a run that then fails on its next save with nothing to explain why.
/// Every runner and the session itself implement this; <see cref="ProjectSessionService"/> refuses
/// while any of them is running, the way <see cref="DeviceSessionService.SwitchDeviceAsync"/> does.</para>
/// </summary>
public interface IMeasurementRun
{
    bool Running { get; }
}
