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
///
/// <para>It is also what keeps two measurements from running at once. Every runner owns the
/// pipeline options, the generator and the input range for the length of its run, and a second one
/// started over it takes them away between its points: measured on 2026-09-21, an IMD sweep left
/// running while a crosstalk run was started failed both — the crosstalk with "the pipeline
/// returned no aggressor and no victim", the sweep with a point that never completed. So
/// <see cref="Endpoints.MeasurementRunGuard"/> refuses every start while any runner is
/// <see cref="Running"/>, and the client's switch cancels them all first through
/// <see cref="CancelAsync"/> (<c>POST /api/acquisition/cancel-runs</c>).</para>
/// </summary>
public interface IMeasurementRun
{
    bool Running { get; }

    /// <summary>
    /// Asks a running measurement to stop and waits for it to unwind; a no-op when nothing runs.
    /// The session's own acquisition is not a measurement in this sense and is stopped separately.
    /// </summary>
    Task CancelAsync();
}
