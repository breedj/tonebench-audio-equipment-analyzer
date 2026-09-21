using Qa403.App.Services;

namespace Qa403.App.Endpoints;

/// <summary>
/// Refuses a measurement's <c>/start</c> while any runner is still running — see
/// <see cref="IMeasurementRun"/> for why two cannot share the pipeline. The session's own
/// acquisition is not a runner: every runner stops and restarts it as it likes, and the FFT
/// screen's RUN is exactly what a runner-free session is for.
///
/// <para>An endpoint filter rather than a check inside each runner's <c>StartAsync</c>, so the
/// eight start routes get one rule and one message, and a runner that is itself running answers
/// the same 409 it always did.</para>
/// </summary>
public sealed class MeasurementRunGuard(DeviceSessionService session, IEnumerable<IMeasurementRun> runs) : IEndpointFilter
{
    public const string Message = "Another measurement is still running — stop or cancel it first.";

    public static bool AnotherRunIsActive(DeviceSessionService session, IEnumerable<IMeasurementRun> runs) =>
        runs.Any(run => !ReferenceEquals(run, session) && run.Running);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (AnotherRunIsActive(session, runs))
            return Results.Conflict(Message);

        return await next(context);
    }
}
