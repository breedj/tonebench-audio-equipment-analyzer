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
    /// <summary>
    /// The stored capture delay, or the default when nothing has been stored or the document cannot
    /// be read -- the same contract as the fixture and the layout. Shared with <c>Program</c>, which
    /// loads it into <see cref="CaptureDelaySetting"/> at startup.
    /// </summary>
    public static async Task<CaptureDelayConfig> ReadCaptureDelayAsync(
        ConfigRepository repo, JsonSerializerOptions serializerOptions, CancellationToken ct)
    {
        string? stored = await repo.GetAsync(ConfigRepository.CaptureDelayKey, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return CaptureDelayConfig.Default;

        try
        {
            var config = JsonSerializer.Deserialize<CaptureDelayConfig>(stored, serializerOptions);
            return config is { IsValid: true } ? config : CaptureDelayConfig.Default;
        }
        catch (JsonException)
        {
            return CaptureDelayConfig.Default;
        }
    }

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

        // The window layout — currently just whether the measurement sidebar is pinned. Same
        // contract as the fixture: defaults rather than 404, and an unreadable document is replaced
        // by them rather than surfaced, since nothing about a layout is worth an error.
        group.MapGet("/ui-layout", async (
            ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.UiLayoutKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(UiLayoutConfig.Default);

            try
            {
                var layout = JsonSerializer.Deserialize<UiLayoutConfig>(stored, jsonOptions.Value.SerializerOptions);
                return Results.Ok(layout ?? UiLayoutConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(UiLayoutConfig.Default);
            }
        });

        // The capture delay: how long the DUT is given to settle after every stimulus change. The
        // document is the durable copy; CaptureDelaySetting is the one the session reads, and a PUT
        // writes both so a sweep started a moment later already runs with the new value.
        group.MapGet("/capture-delay", async (
            ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
            Results.Ok(await ReadCaptureDelayAsync(repo, jsonOptions.Value.SerializerOptions, ct)));

        group.MapPut("/capture-delay", async (
            CaptureDelayConfig config, ConfigRepository repo, CaptureDelaySetting setting,
            IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            if (!config.IsValid)
            {
                return Results.BadRequest(
                    $"The capture delay must be between 0 and {CaptureDelayConfig.MaxCaptureDelayMs:0} ms.");
            }

            await repo.SetAsync(
                ConfigRepository.CaptureDelayKey,
                JsonSerializer.Serialize(config, jsonOptions.Value.SerializerOptions),
                ct);
            setting.Ms = config.CaptureDelayMs;

            return Results.Ok(config);
        });

        // Whether to start on the welcome screen. Client-owned; the server-owned counterpart, which
        // project was open last, is written by ProjectSessionService and never by a PUT.
        group.MapGet("/startup", async (ProjectSessionService session, CancellationToken ct) =>
            Results.Ok(await session.ReadStartupAsync(ct)));

        group.MapPut("/startup", async (
            StartupConfig config, ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.StartupKey,
                JsonSerializer.Serialize(config, jsonOptions.Value.SerializerOptions),
                ct);

            return Results.Ok(config);
        });

        // Whether measurement settings save themselves as they change. Client-owned like the
        // startup preference: the browser is what decides when to call Save, so it reads the flag
        // and the server only keeps it. Defaults rather than 404, and an unreadable document is the
        // default -- nothing about a preference is worth an error.
        group.MapGet("/autosave", async (
            ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.AutosaveKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(AutosaveConfig.Default);

            try
            {
                var config = JsonSerializer.Deserialize<AutosaveConfig>(stored, jsonOptions.Value.SerializerOptions);
                return Results.Ok(config ?? AutosaveConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(AutosaveConfig.Default);
            }
        });

        group.MapPut("/autosave", async (
            AutosaveConfig config, ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.AutosaveKey,
                JsonSerializer.Serialize(config, jsonOptions.Value.SerializerOptions),
                ct);

            return Results.Ok(config);
        });

        group.MapPut("/ui-layout", async (
            UiLayoutConfig layout, ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.UiLayoutKey,
                JsonSerializer.Serialize(layout, jsonOptions.Value.SerializerOptions),
                ct);

            return Results.Ok(layout);
        });

        // Which elements a report includes, per measurement kind. Same contract as the layout: the
        // client owns the vocabulary, so a document this build cannot read is replaced by the
        // defaults (every element) rather than surfaced.
        group.MapGet("/report-settings", async (
            ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.ReportSettingsKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(ReportSettingsConfig.Default);

            try
            {
                var settings = JsonSerializer.Deserialize<ReportSettingsConfig>(stored, jsonOptions.Value.SerializerOptions);
                return Results.Ok(settings is { IsValid: true } ? settings : ReportSettingsConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(ReportSettingsConfig.Default);
            }
        });

        group.MapPut("/report-settings", async (
            ReportSettingsConfig settings, ConfigRepository repo, IOptions<JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            if (!settings.IsValid)
                return Results.BadRequest("Every report element must be a non-empty id under a non-empty kind.");

            await repo.SetAsync(
                ConfigRepository.ReportSettingsKey,
                JsonSerializer.Serialize(settings, jsonOptions.Value.SerializerOptions),
                ct);

            return Results.Ok(settings);
        });
    }
}
