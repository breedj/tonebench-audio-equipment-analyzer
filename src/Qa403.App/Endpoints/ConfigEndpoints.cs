using System.Text.Json;
using Qa403.App.Json;
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
    private static UserWeightingSummary? Summarize(UserWeightingTable? table) =>
        table is null
            ? null
            : new UserWeightingSummary(
                table.Name, table.SourcePath, table.Points.Count,
                table.Points.Min(p => p.FreqHz), table.Points.Max(p => p.FreqHz), table.ImportedUtc);

    /// <summary>
    /// The stored capture delay, or the default when nothing has been stored or the document cannot
    /// be read -- the same contract as the fixture and the layout. Shared with <c>Program</c>, which
    /// loads it into <see cref="CaptureDelaySetting"/> at startup.
    /// </summary>
    public static async Task<CaptureDelayConfig> ReadCaptureDelayAsync(
        ConfigRepository repo, CancellationToken ct)
    {
        string? stored = await repo.GetAsync(ConfigRepository.CaptureDelayKey, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return CaptureDelayConfig.Default;

        try
        {
            var config = JsonSerializer.Deserialize(stored, AppJsonContext.Default.CaptureDelayConfig);
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
            ConfigRepository repo, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.ImpedanceFixtureKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(ImpedanceFixtureConfig.Default);

            try
            {
                var fixture = JsonSerializer.Deserialize(stored, AppJsonContext.Default.ImpedanceFixtureConfig);
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
            CancellationToken ct) =>
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
                JsonSerializer.Serialize(fixture, AppJsonContext.Default.ImpedanceFixtureConfig),
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
            ConfigRepository repo, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.UiLayoutKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(UiLayoutConfig.Default);

            try
            {
                var layout = JsonSerializer.Deserialize(stored, AppJsonContext.Default.UiLayoutConfig);
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
            ConfigRepository repo, CancellationToken ct) =>
            Results.Ok(await ReadCaptureDelayAsync(repo, ct)));

        group.MapPut("/capture-delay", async (
            CaptureDelayConfig config, ConfigRepository repo, CaptureDelaySetting setting,
            CancellationToken ct) =>
        {
            if (!config.IsValid)
            {
                return Results.BadRequest(
                    $"The capture delay must be between 0 and {CaptureDelayConfig.MaxCaptureDelayMs:0} ms.");
            }

            await repo.SetAsync(
                ConfigRepository.CaptureDelayKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.CaptureDelayConfig),
                ct);
            setting.Ms = config.CaptureDelayMs;

            return Results.Ok(config);
        });

        // The two user weighting tables. The document is the durable copy and UserWeightingSetting
        // the one the pipeline reads; an import is a path because the browser cannot read a local
        // file, and the response is a summary rather than the points -- the panel only needs to say
        // what is loaded, the pipeline already has the table.
        group.MapGet("/weighting", (UserWeightingSetting setting) =>
            Results.Ok(new UserWeightingResponse(Summarize(setting.Get(1)), Summarize(setting.Get(2)))));

        group.MapPost("/weighting/{slot:int}/import", async (
            int slot, ImportWeightingRequest request, UserWeightingService service, UserWeightingSetting setting, CancellationToken ct) =>
        {
            if (slot is not (1 or 2))
                return Results.BadRequest("User weighting slots are 1 and 2.");
            try
            {
                await service.ImportAsync(slot, request.Path, ct);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound($"No file at {request.Path}.");
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(ex.Message);
            }

            return Results.Ok(new UserWeightingResponse(Summarize(setting.Get(1)), Summarize(setting.Get(2))));
        });

        group.MapDelete("/weighting/{slot:int}", async (
            int slot, UserWeightingService service, UserWeightingSetting setting, CancellationToken ct) =>
        {
            if (slot is not (1 or 2))
                return Results.BadRequest("User weighting slots are 1 and 2.");
            await service.ClearAsync(slot, ct);
            return Results.Ok(new UserWeightingResponse(Summarize(setting.Get(1)), Summarize(setting.Get(2))));
        });

        // Whether to start on the welcome screen. Client-owned; the server-owned counterpart, which
        // project was open last, is written by ProjectSessionService and never by a PUT.
        group.MapGet("/startup", async (ProjectSessionService session, CancellationToken ct) =>
            Results.Ok(await session.ReadStartupAsync(ct)));

        group.MapPut("/startup", async (
            StartupConfig config, ConfigRepository repo, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.StartupKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.StartupConfig),
                ct);

            return Results.Ok(config);
        });

        // Whether measurement settings save themselves as they change. Client-owned like the
        // startup preference: the browser is what decides when to call Save, so it reads the flag
        // and the server only keeps it. Defaults rather than 404, and an unreadable document is the
        // default -- nothing about a preference is worth an error.
        group.MapGet("/autosave", async (
            ConfigRepository repo, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.AutosaveKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(AutosaveConfig.Default);

            try
            {
                var config = JsonSerializer.Deserialize(stored, AppJsonContext.Default.AutosaveConfig);
                return Results.Ok(config ?? AutosaveConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(AutosaveConfig.Default);
            }
        });

        group.MapPut("/autosave", async (
            AutosaveConfig config, ConfigRepository repo, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.AutosaveKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.AutosaveConfig),
                ct);

            return Results.Ok(config);
        });

        // How dates are written on screenshots and reports. Client-owned vocabulary, like the report
        // settings: the server keeps an id and the client decides what it means.
        group.MapGet("/date-time-format", async (
            ConfigRepository repo, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.DateTimeFormatKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(DateTimeFormatConfig.Default);

            try
            {
                var config = JsonSerializer.Deserialize(stored, AppJsonContext.Default.DateTimeFormatConfig);
                return Results.Ok(config is { IsValid: true } ? config : DateTimeFormatConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(DateTimeFormatConfig.Default);
            }
        });

        group.MapPut("/date-time-format", async (
            DateTimeFormatConfig config, ConfigRepository repo, CancellationToken ct) =>
        {
            if (!config.IsValid)
                return Results.BadRequest("A date/time format id must be a non-empty string.");

            await repo.SetAsync(
                ConfigRepository.DateTimeFormatKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.DateTimeFormatConfig),
                ct);

            return Results.Ok(config);
        });

        // The Screenshot dialog's remembered answers -- size, aspect ratio, destination, and the
        // title when the dialog was told to keep it. Same contract as the rest: defaults rather than
        // 404, and an unreadable document is the defaults.
        group.MapGet("/screenshot-settings", async (
            ConfigRepository repo, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.ScreenshotSettingsKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(ScreenshotSettingsConfig.Default);

            try
            {
                var settings = JsonSerializer.Deserialize(stored, AppJsonContext.Default.ScreenshotSettingsConfig);
                return Results.Ok(settings is { IsValid: true } ? settings : ScreenshotSettingsConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(ScreenshotSettingsConfig.Default);
            }
        });

        group.MapPut("/screenshot-settings", async (
            ScreenshotSettingsConfig settings, ConfigRepository repo, CancellationToken ct) =>
        {
            if (!settings.IsValid)
                return Results.BadRequest(
                    $"A screenshot width must be {ScreenshotSettingsConfig.MinWidthPx}-{ScreenshotSettingsConfig.MaxWidthPx} px, and the aspect ratio and destination must be named.");

            await repo.SetAsync(
                ConfigRepository.ScreenshotSettingsKey,
                JsonSerializer.Serialize(settings, AppJsonContext.Default.ScreenshotSettingsConfig),
                ct);

            return Results.Ok(settings);
        });

        group.MapPut("/ui-layout", async (
            UiLayoutConfig layout, ConfigRepository repo, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.UiLayoutKey,
                JsonSerializer.Serialize(layout, AppJsonContext.Default.UiLayoutConfig),
                ct);

            return Results.Ok(layout);
        });

        // Which elements a report includes, per measurement kind. Same contract as the layout: the
        // client owns the vocabulary, so a document this build cannot read is replaced by the
        // defaults (every element) rather than surfaced.
        group.MapGet("/report-settings", async (
            ConfigRepository repo, CancellationToken ct) =>
        {
            string? stored = await repo.GetAsync(ConfigRepository.ReportSettingsKey, ct);
            if (string.IsNullOrWhiteSpace(stored))
                return Results.Ok(ReportSettingsConfig.Default);

            try
            {
                var settings = JsonSerializer.Deserialize(stored, AppJsonContext.Default.ReportSettingsConfig);
                return Results.Ok(settings is { IsValid: true } ? settings : ReportSettingsConfig.Default);
            }
            catch (JsonException)
            {
                return Results.Ok(ReportSettingsConfig.Default);
            }
        });

        group.MapPut("/report-settings", async (
            ReportSettingsConfig settings, ConfigRepository repo, CancellationToken ct) =>
        {
            if (!settings.IsValid)
                return Results.BadRequest("Every report element must be a non-empty id under a non-empty kind.");

            await repo.SetAsync(
                ConfigRepository.ReportSettingsKey,
                JsonSerializer.Serialize(settings, AppJsonContext.Default.ReportSettingsConfig),
                ct);

            return Results.Ok(settings);
        });
    }
}

/// <summary>What the FFT settings panel shows for a user weighting slot: the table's name and extent, not its rows.</summary>
public sealed record UserWeightingSummary(string Name, string? SourcePath, int PointCount, double MinFreqHz, double MaxFreqHz, DateTimeOffset ImportedUtc);

/// <summary>Both user weighting slots; a null slot is empty.</summary>
public sealed record UserWeightingResponse(UserWeightingSummary? User1, UserWeightingSummary? User2);

/// <summary>The file to import into a user weighting slot -- a path on the server's machine, from the in-app file listing.</summary>
public sealed record ImportWeightingRequest(string Path);
