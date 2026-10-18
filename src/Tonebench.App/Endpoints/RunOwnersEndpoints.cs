using Tonebench.App.Services;
using Tonebench.App.Streaming;

namespace Tonebench.App.Endpoints;

/// <summary>
/// "These measurements no longer exist." The counterpart of the eight <c>POST .../owner</c> calls:
/// those say which measurement a runner's results belong to now, and this says that a measurement
/// they may still be held under has been destroyed.
///
/// <para><b>One call for all nine stores</b>, unlike the owner switches. A switch is an arrival at
/// one screen and touches the one runner that screen draws; this is not about a screen at all — a
/// destroyed measurement may have a parked run in any of them and a parked snapshot besides — so
/// splitting it would be nine round trips to say one thing.</para>
///
/// <para><b>The client names the owners, and this end still never parses one.</b> The key is the
/// browser's "projectId:typeId" string (see <c>measurements/liveOwner.ts</c>); building it here
/// would make the server know a format that is deliberately the client's, so the trash view sends
/// the keys of what it just destroyed and this hands them straight to the stores. See
/// <see cref="RunOwnerRequest"/>, whose opaqueness this preserves.</para>
///
/// <para><b>Not behind <see cref="MeasurementRunGuard"/></b>, for <c>TrashEndpoints</c>' reason and
/// one of its own: the destructive trash actions it follows are not guarded either, and each store
/// refuses to touch a run that is still going (see <c>ThdSweepRunner.ForgetOwner</c>).</para>
/// </summary>
public static class RunOwnersEndpoints
{
    public static void MapRunOwnerEndpoints(this WebApplication app)
    {
        // Idempotent, and quiet about owners it has never heard of: the trash sends every
        // measurement it destroyed, and most of them will never have been run in this process.
        app.MapPost("/api/runs/forget", (
            RunOwnersRequest request,
            ThdSweepRunner thd,
            ImdSweepRunner imd,
            FrequencyResponseRunner response,
            SteppedResponseRunner stepped,
            CrosstalkRunner crosstalk,
            ImpedanceRunner impedance,
            ImpulseResponseRunner impulse,
            OutputImpedanceRunner outputImpedance,
            SpectrumPipeline pipeline) =>
        {
            foreach (string owner in request.Normalized)
            {
                thd.ForgetOwner(owner);
                imd.ForgetOwner(owner);
                response.ForgetOwner(owner);
                stepped.ForgetOwner(owner);
                crosstalk.ForgetOwner(owner);
                impedance.ForgetOwner(owner);
                impulse.ForgetOwner(owner);
                outputImpedance.ForgetOwner(owner);
                pipeline.ForgetRetentionOwner(owner);
            }

            return Results.NoContent();
        });
    }
}

/// <summary>
/// The measurements that have stopped existing, by the same opaque owner key
/// <see cref="RunOwnerRequest"/> carries. Absent or empty is a no-op rather than an error — the
/// trash reports what it destroyed, and destroying only results destroys no measurement at all.
/// </summary>
public sealed record RunOwnersRequest(IReadOnlyList<string>? Owners)
{
    /// <summary>The owners worth acting on: blank entries are nobody, and nobody owns nothing.</summary>
    public IEnumerable<string> Normalized =>
        (Owners ?? []).Where(o => !string.IsNullOrWhiteSpace(o));
}
