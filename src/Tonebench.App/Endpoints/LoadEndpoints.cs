using System.Text.Json;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Device.Hardware;

namespace Tonebench.App.Endpoints;

/// <summary>
/// The programmable load: which one is selected, what it reports, and the three things it can be
/// told to do. Its protocol and the hardware behaviour behind these routes are documented in
/// <c>docs/qa451-protocol.md</c>.
///
/// <para>Deliberately <b>not</b> under <c>/api/device</c>: a load is a second instrument on the
/// bench rather than part of the analyzer, and the analyzer's status poll has no business growing a
/// load's temperatures. Its selection does live with every other setting
/// (<c>PUT /api/load/kind</c> writes the configuration document), for the same reason the analyzer's
/// does.</para>
/// </summary>
public static class LoadEndpoints
{
    /// <summary>
    /// Why a load change is refused mid-measurement. A sweep's every point assumes the load it
    /// started with: switching 8 Ω to 4 Ω under a running THD-vs-frequency sweep leaves a curve
    /// whose halves were measured against different DUT loading, with nothing in the result saying
    /// so. Same reasoning as the refusal of a manual input-range change while a runner owns the
    /// session.
    /// </summary>
    public const string RunningMessage =
        "A measurement is running — stop it before changing the load.";

    // A command the device declined is a 409 carrying the driver's own message, not a 500: nothing
    // is wrong with the request, and the message names the likeliest cause (DC on a load input makes
    // the QA451 refuse relay control). It reached the browser as a 500 with a stack trace once, on a
    // bench that was genuinely refusing — the diagnosis was right and the status code was not.

    public static void MapLoadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/load");

        // One document for the whole screen, and the only route the UI polls. The reading inside it
        // is throttled by the session, so several open tabs do not multiply USB traffic.
        group.MapGet("/", async (LoadSessionService load, CancellationToken ct) =>
            Results.Ok(await load.GetStatusAsync(ct)));

        // The retry behind the Connect button, and what a bench uses after replugging a load that
        // had latched an over-current fault. A failure is a 503 with the device's own message
        // rather than a 500: nothing is wrong with the request.
        group.MapPost("/connect", async (LoadSessionService load, CancellationToken ct) =>
        {
            try
            {
                await load.ConnectAsync(ct);
                return Results.Ok(await load.GetStatusAsync(ct));
            }
            catch (Exception ex) when (ex is Qa451DeviceNotFoundException or Qa451DeviceLostException)
            {
                return Results.Json(await load.GetStatusAsync(ct), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Which load the bench has. Persisted, because a bench that owns a QA451 owns it tomorrow
        // too — and because the default is None, so a bench without one is never shown a load
        // reporting a device that is not there.
        group.MapPut("/kind", async (
            ProgrammableLoadConfig config,
            LoadSessionService load,
            ConfigRepository repo,
            DeviceSessionService session,
            IEnumerable<IMeasurementRun> runs,
            CancellationToken ct) =>
        {
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(RunningMessage);

            if (!Enum.TryParse(config.Kind, ignoreCase: true, out ProgrammableLoadKind kind)
                || !Enum.IsDefined(kind)
                || !string.Equals(kind.ToString(), config.Kind, StringComparison.OrdinalIgnoreCase))
            {
                // The name has to spell itself back, because Enum.TryParse also accepts a number:
                // "1" would parse as Qa451. Storing a name is the point, so a number is not a value
                // this build wrote — the same check ReadAnalyzerKindAsync makes.
                return Results.BadRequest($"Unknown load kind '{config.Kind}'.");
            }

            await repo.SetAsync(
                ConfigRepository.ProgrammableLoadKey,
                JsonSerializer.Serialize(
                    new ProgrammableLoadConfig(kind.ToString()), AppJsonContext.Default.ProgrammableLoadConfig),
                ct);

            try
            {
                await load.SwitchKindAsync(kind, ct);
            }
            catch (Exception ex) when (ex is Qa451DeviceNotFoundException or Qa451DeviceLostException)
            {
                // The selection is stored and the error is on the status: a user who has said "I
                // have a QA451" should not have that undone because it happens to be unplugged.
                return Results.Json(
                    await load.GetStatusAsync(ct), statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(await load.GetStatusAsync(ct));
        });

        // 0 = open, 4 or 8. Ohms rather than an enum name, matching QuantAsylum's own REST API and
        // the front panel's labels.
        group.MapPut("/impedance/{ohms:int}", async (
            int ohms,
            LoadSessionService load,
            DeviceSessionService session,
            IEnumerable<IMeasurementRun> runs,
            CancellationToken ct) =>
        {
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(RunningMessage);

            try
            {
                await load.SetImpedanceAsync(ohms, ct);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Not ex.Message: ArgumentOutOfRangeException appends its own "(Parameter 'ohms')" and
                // "Actual value was 5." lines, and whatever comes back is printed verbatim on the
                // dialog's error line. One sentence, like the dut-power refusal below.
                return Results.BadRequest("A load is 0 (open), 4 or 8 ohms.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or Qa451CommandRefusedException)
            {
                return Results.Conflict(ex.Message);
            }

            return Results.Ok(await load.GetStatusAsync(ct));
        });

        // The DUT's own supply, through the load's high-side switch. Takes about a second and a half
        // to close — the device runs a soft start and does not answer during it — so this request is
        // slow by nature and returns once the supply is actually up.
        group.MapPut("/dut-power/{state:int}", async (
            int state,
            LoadSessionService load,
            DeviceSessionService session,
            IEnumerable<IMeasurementRun> runs,
            CancellationToken ct) =>
        {
            if (state is not (0 or 1))
                return Results.BadRequest("DUT power is 0 or 1.");

            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(RunningMessage);

            try
            {
                await load.SetDutPowerAsync(state == 1, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Qa451CommandRefusedException)
            {
                return Results.Conflict(ex.Message);
            }

            return Results.Ok(await load.GetStatusAsync(ct));
        });

        // No load, DUT power off — one press for "put the bench down", and what the host does for
        // itself at shutdown.
        group.MapPost("/safe-state", async (
            LoadSessionService load,
            DeviceSessionService session,
            IEnumerable<IMeasurementRun> runs,
            CancellationToken ct) =>
        {
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(RunningMessage);

            try
            {
                await load.SetSafeStateAsync(ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Qa451CommandRefusedException)
            {
                return Results.Conflict(ex.Message);
            }

            return Results.Ok(await load.GetStatusAsync(ct));
        });
    }

    /// <summary>
    /// The stored load selection, defaulting to <see cref="ProgrammableLoadKind.None"/> for an
    /// absent or unreadable document — the same contract every other setting here has. Shared with
    /// <see cref="StartupSettingsLoader"/>.
    /// </summary>
    public static async Task<ProgrammableLoadKind> ReadLoadKindAsync(ConfigRepository repo, CancellationToken ct)
    {
        string? json = await repo.GetAsync(ConfigRepository.ProgrammableLoadKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return ProgrammableLoadKind.None;

        try
        {
            ProgrammableLoadConfig? config =
                JsonSerializer.Deserialize(json, AppJsonContext.Default.ProgrammableLoadConfig);
            if (!Enum.TryParse(config?.Kind, ignoreCase: true, out ProgrammableLoadKind kind) || !Enum.IsDefined(kind))
                return ProgrammableLoadKind.None;

            return string.Equals(kind.ToString(), config!.Kind, StringComparison.OrdinalIgnoreCase)
                ? kind
                : ProgrammableLoadKind.None;
        }
        catch (JsonException)
        {
            return ProgrammableLoadKind.None;
        }
    }
}
