using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Qa403.App.Services;
using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Endpoints;

/// <summary>
/// Application configuration, which lives in its own database — see
/// <see cref="Qa403ConfigDatabaseOptions"/> for why it is not in the measurement history.
/// </summary>
public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/config");

        // The impedance fixture: reference resistor, calibration drive level, and the two captured
        // standards. Returns the defaults rather than 404 when nothing has been stored, so a client
        // never has to special-case a machine that has not calibrated yet.
        group.MapGet("/impedance-fixture", async (
            ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.ImpedanceFixtureKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(ImpedanceFixtureConfig.Default);

            try
            {
                var fixture = JsonSerializer.Deserialize<ImpedanceFixtureConfig>(
                    stored, jsonOptions.Value.SerializerOptions);
                return Results.Ok(fixture ?? ImpedanceFixtureConfig.Default);
            }
            catch (JsonException)
            {
                // A document this app can no longer read is not worth taking the screen down for —
                // the fixture is a convenience, and a measurement can always calibrate its own.
                return Results.Ok(ImpedanceFixtureConfig.Default);
            }
        });

        group.MapPut("/impedance-fixture", async (
            ImpedanceFixtureConfig fixture, ConfigRepository repo,
            IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            if (!(fixture.ReferenceOhms >= ImpedanceRequest.MinReferenceOhms
                  && fixture.ReferenceOhms <= ImpedanceRequest.MaxReferenceOhms))
            {
                return Results.BadRequest(
                    $"The reference resistor must be between {ImpedanceRequest.MinReferenceOhms} and "
                        + $"{ImpedanceRequest.MaxReferenceOhms} ohms.");
            }

            if (!double.IsFinite(fixture.CalibrationLevelValue))
                return Results.BadRequest("The calibration level must be a finite number.");

            // Refused rather than stored: a standard that is present but unusable is worse than an
            // absent one, because the UI would offer it and the solver would quietly skip it.
            if (fixture.OpenCal is { } open && !open.IsUsable)
                return Results.BadRequest("The open calibration curve is empty or malformed.");
            if (fixture.ShortCal is { } shortCal && !shortCal.IsUsable)
                return Results.BadRequest("The short calibration curve is empty or malformed.");

            await repo.SetAsync(
                ConfigRepository.ImpedanceFixtureKey,
                JsonSerializer.Serialize(fixture, jsonOptions.Value.SerializerOptions),
                ct);

            return Results.Ok(fixture);
        });

        group.MapDelete("/impedance-fixture", async (ConfigRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteAsync(ConfigRepository.ImpedanceFixtureKey, ct);
            return Results.Ok(ImpedanceFixtureConfig.Default);
        });
    }
}
