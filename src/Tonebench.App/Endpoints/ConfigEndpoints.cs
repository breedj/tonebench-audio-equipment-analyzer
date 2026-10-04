using Tonebench.App.Diagnostics;
using System.Diagnostics;
using System.Text.Json;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Endpoints;

/// <summary>
/// Application configuration, which lives in its own database — see
/// <see cref="TonebenchConfigDatabaseOptions"/> for why it is not in the measurement history.
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

    /// <summary>
    /// The stored experimental 384 kHz opt-in, defaulting to off for an absent or unreadable
    /// document — the same contract as the capture delay. Shared with
    /// <see cref="StartupSettingsLoader"/>, which loads it into <see cref="Experimental384kSetting"/>.
    /// </summary>
    public static async Task<Experimental384kConfig> ReadExperimental384kAsync(
        ConfigRepository repo, CancellationToken ct)
    {
        string? stored = await repo.GetAsync(ConfigRepository.Experimental384kKey, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return Experimental384kConfig.Default;

        try
        {
            return JsonSerializer.Deserialize(stored, AppJsonContext.Default.Experimental384kConfig)
                   ?? Experimental384kConfig.Default;
        }
        catch (JsonException)
        {
            return Experimental384kConfig.Default;
        }
    }

    /// <summary>
    /// Opens a folder in the system file manager, so "send me the log" or "clear out the recordings"
    /// needs no path typed. The same shell launch <c>Program.LaunchBrowser</c> uses, pointed at a
    /// directory, and the directory is created first -- a folder nothing has written to yet is the
    /// commonest case for the recordings.
    ///
    /// <para>A failure is a 500 carrying its own message; the path is on screen beside the button
    /// either way, which is the fallback.</para>
    /// </summary>
    private static IResult OpenInFileManager(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", directory);
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", directory);
            else
                return Results.Problem("No file manager known for this platform.");

            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return Results.Problem($"Could not open {directory}: {ex.Message}");
        }
    }

    /// <summary>
    /// The stored stream-recording switch and cap, or the default -- the same contract as the
    /// capture delay, including falling back rather than failing on a document this build cannot
    /// read. An out-of-range cap is clamped rather than rejected: a document written by hand is not
    /// a reason to refuse to start.
    /// </summary>
    public static async Task<StreamRecordingConfig> ReadStreamRecordingAsync(
        ConfigRepository repo, CancellationToken ct)
    {
        string? stored = await repo.GetAsync(ConfigRepository.StreamRecordingKey, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return StreamRecordingConfig.Default;

        try
        {
            var config = JsonSerializer.Deserialize(stored, AppJsonContext.Default.StreamRecordingConfig);
            return config is null
                ? StreamRecordingConfig.Default
                : config with { MaxFileMegabytes = ClampRecordingCap(config.MaxFileMegabytes) };
        }
        catch (JsonException)
        {
            return StreamRecordingConfig.Default;
        }
    }

    /// <summary>
    /// The cap's bounds. The floor is a file worth having at all (16 MB is ~10 s of 192 kHz stereo
    /// float32); the ceiling is what ten recordings of it come to on a disk -- 4 GB each, which is
    /// also where RIFF's own 32-bit size field runs out.
    /// </summary>
    public const int MinRecordingCapMegabytes = 16;

    /// <summary>See <see cref="MinRecordingCapMegabytes"/>.</summary>
    public const int MaxRecordingCapMegabytes = 4096;

    private static int ClampRecordingCap(int megabytes) =>
        Math.Clamp(megabytes, MinRecordingCapMegabytes, MaxRecordingCapMegabytes);

    /// <summary>The stored external input gain, or the default -- the same contract as the capture delay.</summary>
    /// <summary>
    /// Reads which analyzer was last selected, falling back to the QA403.
    ///
    /// <para>An unrecognised name falls back rather than failing: the document outlives the build
    /// that wrote it, and a kind that has been renamed or removed must not stop the app starting.
    /// </para>
    /// </summary>
    public static async Task<DeviceKind> ReadAnalyzerKindAsync(ConfigRepository repo, CancellationToken ct)
    {
        string? json = await repo.GetAsync(ConfigRepository.AnalyzerKindKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return DeviceKind.Hardware;

        try
        {
            AnalyzerKindConfig? config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AnalyzerKindConfig);
            if (!Enum.TryParse(config?.Kind, ignoreCase: true, out DeviceKind kind) || !Enum.IsDefined(kind))
                return DeviceKind.Hardware;

            // The parsed value has to spell itself back, because Enum.TryParse also accepts a
            // *number*: "1" parses happily as Simulated. Storing a name rather than a number is
            // the whole point here, so a document holding a number is not a document this build
            // wrote and its number means nothing -- exactly the reinterpretation being avoided.
            return string.Equals(kind.ToString(), config!.Kind, StringComparison.OrdinalIgnoreCase)
                ? kind
                : DeviceKind.Hardware;
        }
        catch (JsonException)
        {
            return DeviceKind.Hardware;
        }
    }

    /// <summary>
    /// Reads the sound-card document, falling back to the default on anything unreadable — the same
    /// rule every setting here follows, because a configuration that will not parse must not stop
    /// the app starting.
    /// </summary>
    public static async Task<SoundCardConfig> ReadSoundCardAsync(
        ConfigRepository repo, CancellationToken ct)
    {
        string? json = await repo.GetAsync(ConfigRepository.SoundCardKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return SoundCardConfig.Default;

        try
        {
            SoundCardConfig? config = JsonSerializer.Deserialize(json, AppJsonContext.Default.SoundCardConfig);
            return config is { IsValid: true } ? config : SoundCardConfig.Default;
        }
        catch (JsonException)
        {
            return SoundCardConfig.Default;
        }
    }

    public static async Task<ExternalGainConfig> ReadExternalGainAsync(
        ConfigRepository repo, CancellationToken ct)
    {
        string? stored = await repo.GetAsync(ConfigRepository.ExternalGainKey, ct);
        if (string.IsNullOrWhiteSpace(stored))
            return ExternalGainConfig.Default;

        try
        {
            var config = JsonSerializer.Deserialize(stored, AppJsonContext.Default.ExternalGainConfig);
            return config is { IsValid: true } ? config : ExternalGainConfig.Default;
        }
        catch (JsonException)
        {
            return ExternalGainConfig.Default;
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

        // The experimental 384 kHz opt-in. No validation branch — a bool is always valid — and,
        // unlike /soundcard two blocks down, *no device reconnect*: the device reads this through a
        // Func (Qa40xUsbDevice.Allow384kHz), so the new rate list is reported by the already-open
        // device on the very next status poll. Rebuilding the device here would drop the USB
        // connection to change a number the open one is perfectly able to report.
        group.MapGet("/experimental-384k", async (
            ConfigRepository repo, CancellationToken ct) =>
            Results.Ok(await ReadExperimental384kAsync(repo, ct)));

        group.MapPut("/experimental-384k", async (
            Experimental384kConfig config, ConfigRepository repo, Experimental384kSetting setting,
            CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.Experimental384kKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.Experimental384kConfig),
                ct);
            setting.Enabled = config.Enabled;

            return Results.Ok(config);
        });

        // The external input gain: what sits between the DUT and each input. The document is the
        // durable copy; ExternalGainSetting is what the pipeline divides by. The PUT flushes the
        // pipeline as well, because a scale step landing mid-record is a broadband smear across
        // one whole FFT -- the same cost as any other configuration change, and a re-arm for a
        // held single shot, like any bare flush.
        group.MapGet("/external-gain", async (
            ConfigRepository repo, CancellationToken ct) =>
            Results.Ok(await ReadExternalGainAsync(repo, ct)));

        group.MapPut("/external-gain", async (
            ExternalGainConfig config, ConfigRepository repo, DeviceSessionService session,
            SpectrumPipeline pipeline, CancellationToken ct) =>
        {
            if (!config.IsValid)
                return Results.BadRequest(
                    ExternalGainConfig.IsKnownUnit(config.Unit)
                        ? $"An external gain must be within ±{ExternalGainConfig.MaxAbsDb:0} dB per input."
                        : $"'{config.Unit}' is not a gain unit — use '{ExternalGainConfig.DefaultUnit}' or '{ExternalGainConfig.TimesUnit}'.");

            await repo.SetAsync(
                ConfigRepository.ExternalGainKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.ExternalGainConfig),
                ct);

            // Through the session rather than straight into ExternalGainSetting: this document
            // describes the QA403's bench, and the sound card keeps its own. The session remembers
            // this as the analyzer-independent value and re-applies whichever is effective, so
            // editing it while the sound card is selected does not silently swap the card's
            // divider for this one.
            session.SetSharedExternalGain(config.LeftDb, config.RightDb, config.Unit);
            pipeline.Flush(0);

            return Results.Ok(config);
        });

        // The sound-card analyzer: which card, how it is wired, and where its full scale sits. One
        // document, because a calibration only means anything against the device and channel pair
        // it was taken on. The live copy is what AnalyzerDeviceFactory builds a device from, so a
        // write here is followed by a reconnect -- the two ranges are derived from the calibration,
        // and DeviceCapabilities is only read at connect.
        group.MapGet("/soundcard", async (
            ConfigRepository repo, CancellationToken ct) =>
            Results.Ok(await ReadSoundCardAsync(repo, ct)));

        group.MapPut("/soundcard", async (
            SoundCardConfig config, ConfigRepository repo, SoundCardSetting setting,
            DeviceSessionService session, IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            if (!config.IsValid)
                return Results.BadRequest("Those sound-card settings are out of range.");

            // Refused while a measurement owns the session, like every other device change: the
            // card is about to be reopened underneath it.
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(MeasurementRunGuard.Message);

            await repo.SetAsync(
                ConfigRepository.SoundCardKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.SoundCardConfig),
                ct);
            setting.Config = config;
            session.RefreshSoundCardGain();

            if (session.Kind == DeviceKind.SoundCard)
            {
                // Rebuild the device so the new card, rate, channel pair and calibration are the
                // ones in use -- Capabilities is documented as valid only after ConnectAsync, and
                // the two range rungs come from the calibration.
                try
                {
                    await session.ReopenAsync(ct);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(ex.Message);
                }
            }

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

        // Detailed logging for a fault report -- see DiagnosticLoggingSetting. The GET carries the
        // log folder's path because the checkbox is only half of "enable logging and send me the
        // file": the other half is knowing where the file is.
        group.MapGet("/diagnostic-logging", (DiagnosticLoggingSetting setting) =>
            Results.Ok(new DiagnosticLoggingResponse(setting.Enabled, TonebenchAppData.LogsDirectory)));

        group.MapPut("/diagnostic-logging", async (
            DiagnosticLoggingConfig config, DiagnosticLoggingSetting setting, ConfigRepository repo,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            await repo.SetAsync(
                ConfigRepository.DiagnosticLoggingKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.DiagnosticLoggingConfig),
                ct);

            var logger = loggers.CreateLogger("Tonebench.App.Logging");
            if (config.Enabled && !setting.Enabled)
            {
                // Flip first, then announce, so the announcement is the first detailed line -- and it
                // names the build, since a log file sent in on its own has no other way of saying
                // which version wrote it.
                setting.Enabled = true;
                logger.LogInformation(
                    "Detailed logging enabled (app {Version}, {Os}, .NET {Runtime})",
                    UpdateCheckService.CurrentVersion, Environment.OSVersion, Environment.Version);
            }
            else if (!config.Enabled && setting.Enabled)
            {
                logger.LogInformation("Detailed logging disabled");
                setting.Enabled = false;
            }

            return Results.Ok(new DiagnosticLoggingResponse(setting.Enabled, TonebenchAppData.LogsDirectory));
        });

        // Opens the log folder in the system file manager, so "send me the log file" needs no path
        // typed. The same shell launch LaunchBrowser uses, pointed at a directory; a failure is a
        // 500 with its message, and the path is still on screen beside the button.
        group.MapPost("/diagnostic-logging/open-folder", () => OpenInFileManager(TonebenchAppData.LogsDirectory));

        // The stream recorder: one switch and one cap, applied live and persisted. The GET answers
        // with the recorder's own state as well as the setting -- what it is doing now, and what the
        // last recording came to -- because the panel's only other way of knowing is the log.
        group.MapGet("/stream-recording", (StreamRecorder recorder) => Results.Ok(recorder.Describe()));

        group.MapPut("/stream-recording", async (
            StreamRecordingConfig config, StreamRecorder recorder, ConfigRepository repo,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (config.MaxFileMegabytes != ClampRecordingCap(config.MaxFileMegabytes))
            {
                return Results.BadRequest(
                    $"The per-file cap must be between {MinRecordingCapMegabytes} and {MaxRecordingCapMegabytes} MB.");
            }

            await repo.SetAsync(
                ConfigRepository.StreamRecordingKey,
                JsonSerializer.Serialize(config, AppJsonContext.Default.StreamRecordingConfig),
                ct);

            bool wasEnabled = recorder.Enabled;
            recorder.Enabled = config.Enabled;
            recorder.MaxFileMegabytes = config.MaxFileMegabytes;

            // Logged, and at Information: a recording that was armed explains both the files in that
            // folder and any extra I/O a later report blames on the app. Disarming mid-acquisition
            // leaves the open recording to finish -- the tap and the file belong to the run that
            // started them, and truncating one on a setting change would produce a file whose end
            // means nothing.
            if (config.Enabled != wasEnabled)
            {
                loggers.CreateLogger("Tonebench.App.Diagnostics").LogInformation(
                    "Stream recording {State} (cap {CapMb} MB per file, keeping {Kept} recordings in {Directory})",
                    config.Enabled ? "armed" : "disarmed", config.MaxFileMegabytes,
                    StreamRecorder.KeptRecordings, recorder.Directory);
            }

            return Results.Ok(recorder.Describe());
        });

        // The recordings and the single-record captures share a folder button each, for the reason
        // the log folder has one: a path on screen is not the same as a folder a user can get to.
        group.MapPost("/stream-recording/open-folder", (StreamRecorder recorder) =>
            OpenInFileManager(recorder.Directory));

        group.MapPost("/diagnostics/open-folder", () =>
            OpenInFileManager(TonebenchAppData.DiagnosticsDirectory));

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
