using Dapper;
using Qa403.Dsp;

namespace Qa403.Data;

/// <summary>
/// The impedance fixture as the application remembers it: the reference resistor, the level
/// calibration runs drive at, and the two captured standards.
///
/// <para><b>Stored as one document, and that is deliberate.</b> The four fields are only meaningful
/// together — a calibration belongs to the resistor it was taken through (see
/// <paramref name="ReferenceOhms"/>), so writing them separately would allow a half-updated fixture
/// to be applied to a measurement, which is worse than having none.</para>
/// </summary>
/// <param name="ReferenceOhms">
/// The series resistor the standards were captured through, and the default a new impedance
/// measurement starts from.
///
/// <para><b>The calibration is only valid at this resistance.</b> Both standards are stored as the
/// measured channel ratio, and both depend on R: an open reads <c>a·Z_in/(R + Z_in)</c> and a short
/// reads <c>a·Z_lead/(R + Z_lead)</c>. Applying them at a different R silently mis-corrects every
/// reading, so a measurement that uses this fixture takes this resistance with it rather than its
/// own.</para>
/// </param>
/// <param name="CapturedLowHz">The band the standards actually cover. Outside it a calibration curve is held flat, which is an extrapolation and has to be reported rather than assumed.</param>
/// <param name="CapturedUtc">When the standards were taken, so a stale calibration can be recognised as one.</param>
public sealed record ImpedanceFixtureConfig(
    double ReferenceOhms,
    double CalibrationLevelValue,
    string LevelUnit,
    ImpedanceCalibration? OpenCal = null,
    ImpedanceCalibration? ShortCal = null,
    double CapturedLowHz = 0.0,
    double CapturedHighHz = 0.0,
    string? CapturedUtc = null)
{
    /// <summary>What a machine with no stored fixture starts from. 100 ohms is what a typical bench fixture is built with.</summary>
    public static ImpedanceFixtureConfig Default { get; } = new(
        ReferenceOhms: 100.0,
        CalibrationLevelValue: 0.0,
        LevelUnit: "Dbv");

    /// <summary>Whether there is anything here worth applying. An open alone is useful; a short alone is not, since the open is what the short is corrected against.</summary>
    public bool HasCalibration => OpenCal is { IsUsable: true };
}

/// <summary>
/// How the window is laid out: the preferences that describe this screen rather than any measurement.
///
/// <para>Stored server-side rather than in the browser's <c>localStorage</c>, so the layout reads the
/// same in every tab and survives a browser cache clear. It sits in the configuration database
/// and not the measurement history for the usual reason — a project file copied to another machine
/// must not carry this screen's layout with it.</para>
/// </summary>
/// <param name="SidebarPinned">
/// Whether the measurement sidebar stays open at its full width. Unpinned, it collapses to a rail of
/// icons and floats open over the content while hovered, which is the extra chart width a small
/// screen needs. Pinned is the default because it is what the app always did.
/// </param>
/// <param name="SettingsRailCollapsed">
/// Whether the settings rail on the left of every measurement screen is collapsed to its edge
/// strip, leaving the width to the chart. The strip carries the button that opens it again.
/// </param>
/// <param name="DisplayRailCollapsed">
/// The same for the display rail on the right. Only meaningful on a window wide enough for that
/// rail to be a column (1280 px); narrower, the display sections sit under the chart and the flag
/// is kept but not applied.
/// </param>
public sealed record UiLayoutConfig(
    bool SidebarPinned = true,
    bool SettingsRailCollapsed = false,
    bool DisplayRailCollapsed = false)
{
    public static UiLayoutConfig Default { get; } = new();
}

/// <summary>
/// One project file the user opened, for the welcome screen's recent list. <paramref name="Name"/>
/// is a copy of the project's name at the time, so the list can be drawn without opening ten
/// databases — and so a file that has since gone missing can still be listed by name.
/// </summary>
public sealed record RecentProjectEntry(string Path, string Name, DateTimeOffset LastOpenedUtc);

/// <summary>The recent-projects list, most recent first — see <see cref="RecentProjects"/> for its rules.</summary>
public sealed record RecentProjectsConfig(IReadOnlyList<RecentProjectEntry> Entries)
{
    public static RecentProjectsConfig Default { get; } = new([]);
}

/// <summary>
/// Whether the app starts on the welcome screen. Unchecked, the last opened project is opened at
/// startup instead — if its file still exists; a missing file falls back to the welcome screen
/// rather than an error, since there is nothing to show an error on yet.
/// </summary>
public sealed record StartupConfig(bool ShowWelcomeOnStartup = true)
{
    public static StartupConfig Default { get; } = new();
}

/// <summary>
/// Whether a measurement's settings are written the moment they change, instead of waiting for its
/// Save button. On by default (since 2026-09-19): a setting left unsaved is the commoner mistake,
/// and the explicit Save is still there for a bench that would rather turn this off.
/// </summary>
public sealed record AutosaveConfig(bool Enabled = true)
{
    public static AutosaveConfig Default { get; } = new();
}

/// <summary>
/// Whether the log file carries the detailed record a fault report needs: every API request with
/// its status and timing, and the Debug-level lines the ordinary Information floor drops. Off by
/// default -- the ordinary log is what the app writes for itself; this is what a tester is asked
/// to switch on (Configuration ▸ General ▸ Detailed logging) before reproducing a problem and
/// sending the file in. Persisted, so it survives the restart the reproduction usually involves.
/// </summary>
public sealed record DiagnosticLoggingConfig(bool Enabled = false)
{
    public static DiagnosticLoggingConfig Default { get; } = new();
}

/// <summary>
/// How a date and time is written wherever the app puts one on paper -- the Screenshot's date
/// line, a report's "Taken" row and its "Generated" line. The vocabulary is the client's
/// (<c>web/src/dateTime.ts</c>): the server keeps the id and checks only that it is a non-empty
/// string, so a build that adds a format needs no server change, and an id this build's client
/// does not know falls back to the system locale on the way out.
/// </summary>
public sealed record DateTimeFormatConfig(string Format = "system")
{
    public static DateTimeFormatConfig Default { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Format) && Format.Length <= 64;
}

/// <summary>
/// The Screenshot dialog's answers, kept so the next screenshot needs none of them asked again.
/// The title rides along only while <paramref name="SaveText"/> is on -- unticked, the client
/// sends it as null and a reopened dialog starts blank. The size is a pixel width, the aspect
/// ratio and destination are the client's own ids (<c>web/src/screenshot.ts</c>), validated no
/// further than "present", for the same reason as <see cref="DateTimeFormatConfig"/>.
/// </summary>
public sealed record ScreenshotSettingsConfig(
    string? Title = null,
    bool SaveText = false,
    bool AddDate = false,
    int WidthPx = 1500,
    string AspectRatio = "3:2",
    string Destination = "file",
    bool Monochrome = false)
{
    public const int MinWidthPx = 500;
    public const int MaxWidthPx = 2000;

    public static ScreenshotSettingsConfig Default { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        WidthPx >= MinWidthPx && WidthPx <= MaxWidthPx
        && !string.IsNullOrWhiteSpace(AspectRatio) && AspectRatio.Length <= 16
        && !string.IsNullOrWhiteSpace(Destination) && Destination.Length <= 32
        && (Title is null || Title.Length <= 500);
}

/// <summary>
/// The project file that was open most recently — what <see cref="StartupConfig"/> reopens. Server-owned:
/// written by the project session on every open, never by the client, and distinct from the recent
/// list's head because Close leaves it alone (the user closed the project, not their preference for
/// it) while Delete clears it.
/// </summary>
public sealed record LastOpenedProjectConfig(string? Path)
{
    public static LastOpenedProjectConfig Default { get; } = new((string?)null);
}

/// <summary>
/// How long the DUT is given to settle after every stimulus change, before the analysis window
/// starts filling. Applied server-side, at the one place every generator change passes through
/// (<c>DeviceSessionService.ConfigureGeneratorAsync</c>), so it covers the live FFT screen and every
/// sweep point alike — not the browser's Run button, which is where it used to live.
///
/// <para>Stored here rather than in <c>localStorage</c> for the same reason as the fixture: the
/// runners execute on the server and cannot read a browser's storage, and a settle time describes
/// this bench (its amplifier's coupling caps, its external gear), which is what the configuration
/// database is for.</para>
/// </summary>
/// <param name="CaptureDelayMs">
/// Milliseconds of audio discarded after the stimulus latency, on every generator change and at
/// every acquisition start with the generator on. 0 disables it. Bounded by
/// <see cref="MaxCaptureDelayMs"/> — a settle time that is a sizeable fraction of a sweep point is a
/// mistake, not a preference.
/// </param>
public sealed record CaptureDelayConfig(double CaptureDelayMs = 0.0)
{
    public const double MaxCaptureDelayMs = 10_000.0;

    public static CaptureDelayConfig Default { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => double.IsFinite(CaptureDelayMs) && CaptureDelayMs >= 0.0 && CaptureDelayMs <= MaxCaptureDelayMs;
}

/// <summary>
/// The gain of whatever sits between the DUT and each analyzer input, in dB: a 10:1 divider in
/// front of the left input is <c>LeftDb = -20</c>. The pipeline divides every sample by it as it
/// arrives, so each level the app reports is the DUT's output rather than what reached the
/// analyzer. Per input, because a bench fixture rarely puts the divider on both -- an impedance or
/// response measurement that references the right channel has the DUT on one side only. 0 means
/// nothing in the path. Bounded at ±<see cref="MaxAbsDb"/>: a 60 dB divider brings +92 dBV down
/// to the analyzer's ceiling, and anything past that is a typo.
/// </summary>
public sealed record ExternalGainConfig(double LeftDb = 0.0, double RightDb = 0.0)
{
    public const double MaxAbsDb = 60.0;

    public static ExternalGainConfig Default { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        double.IsFinite(LeftDb) && Math.Abs(LeftDb) <= MaxAbsDb
        && double.IsFinite(RightDb) && Math.Abs(RightDb) <= MaxAbsDb;
}

/// <summary>
/// Which sound card the analyzer is, how it is wired, and where its full scale sits in volts.
///
/// <para><b>One document, not several.</b> A calibration only means anything against the device,
/// the channel pair and the rate it was taken on; splitting them lets a card be swapped while its
/// predecessor's millivolts survive, which is a wrong answer that looks like a right one. Same
/// reasoning as <see cref="ImpedanceFixtureConfig"/>.</para>
///
/// <para><b>Devices are named, never indexed.</b> PortAudio's indices renumber whenever anything is
/// plugged in or a driver updates, and an index that silently resolves to a different card is a
/// calibration quietly applied to the wrong hardware.</para>
///
/// <para>It lives in the configuration database rather than a project file: the card is a property
/// of this bench, and a project copied to another machine must not carry it.</para>
/// </summary>
/// <param name="OutputFullScalePeakMv">
/// Peak millivolts at digital full scale on the output, or null before that step has been run. Null
/// is not zero and not a guess: an uncalibrated card reports its levels as dBFS and says so.
/// </param>
/// <param name="GainDisplayUnit">
/// Whether the status bar shows the two front-end gains in dB or as a plain ratio. Display only --
/// the stored value is always dB, which is what every measurement's parameters record.
/// </param>
public sealed record SoundCardConfig(
    string InputHostApi = "",
    string InputDeviceName = "",
    string OutputHostApi = "",
    string OutputDeviceName = "",
    int InputChannelOffset = 0,
    int OutputChannelOffset = 0,
    double SampleRateHz = 48_000.0,
    double? OutputFullScalePeakMv = null,
    double? InputFullScalePeakMvLeft = null,
    double? InputFullScalePeakMvRight = null,
    DateTimeOffset? CalibratedUtc = null,
    double FrontEndGainLeftDb = 0.0,
    double FrontEndGainRightDb = 0.0,
    string GainDisplayUnit = "Db",
    double MinStimulusFrequencyHz = 20.0)
{
    /// <summary>The same bound the external gain uses, and for the same reason: past it, it is a typo rather than a fixture.</summary>
    public const double MaxAbsGainDb = ExternalGainConfig.MaxAbsDb;

    /// <summary>A card's full scale outside this is a mis-entered voltmeter reading, not a card.</summary>
    public const double MinFullScaleMv = 0.1;

    public const double MaxFullScaleMv = 1_000_000.0;

    public static SoundCardConfig Default { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(InputDeviceName) && !string.IsNullOrWhiteSpace(OutputDeviceName);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        InputChannelOffset >= 0 && InputChannelOffset <= 64
        && OutputChannelOffset >= 0 && OutputChannelOffset <= 64
        && double.IsFinite(SampleRateHz) && SampleRateHz > 0.0
        && double.IsFinite(FrontEndGainLeftDb) && Math.Abs(FrontEndGainLeftDb) <= MaxAbsGainDb
        && double.IsFinite(FrontEndGainRightDb) && Math.Abs(FrontEndGainRightDb) <= MaxAbsGainDb
        && double.IsFinite(MinStimulusFrequencyHz) && MinStimulusFrequencyHz >= 0.0 && MinStimulusFrequencyHz <= 1_000.0
        && (GainDisplayUnit == "Db" || GainDisplayUnit == "Times")
        && IsFullScaleValid(OutputFullScalePeakMv)
        && IsFullScaleValid(InputFullScalePeakMvLeft)
        && IsFullScaleValid(InputFullScalePeakMvRight);

    private static bool IsFullScaleValid(double? mv) =>
        mv is null || (double.IsFinite(mv.Value) && mv.Value >= MinFullScaleMv && mv.Value <= MaxFullScaleMv);
}

/// <summary>
/// One user weighting table, stored parsed: the QA40x-style "frequency, dB" file's rows, with the
/// file's sign convention (positive = attenuation, see <see cref="Qa403.Dsp.WeightingPoint"/>).
/// Stored as its points and not as a path, because the file is the user's and may move; the
/// name and source path are for the panel to say where the table came from.
/// </summary>
/// <param name="Name">What the panel shows beside the slot — the file name without its extension.</param>
/// <param name="SourcePath">Where it was imported from, informational only.</param>
/// <param name="Points">The table itself, at least two rows.</param>
/// <param name="ImportedUtc">When it was imported.</param>
public sealed record UserWeightingTable(string Name, string? SourcePath, IReadOnlyList<Qa403.Dsp.WeightingPoint> Points, DateTimeOffset ImportedUtc)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && Points is { Count: >= 2 }
        && Points.All(p => double.IsFinite(p.FreqHz) && p.FreqHz > 0.0 && double.IsFinite(p.AttenuationDb));
}

/// <summary>
/// Which elements a PDF report of a measurement includes, remembered per measurement kind — the
/// vocabulary (<c>chart</c>, <c>description</c>, <c>settings</c>, <c>measurements</c>, ...) is the
/// client's, and it only ever uses what it recognises, so the server stores the strings and checks
/// nothing but that they are strings. A kind with no entry gets every element it offers.
/// </summary>
/// <param name="Elements">The element ids to include, in the client's own order.</param>
/// <param name="Views">Which of the kind's chart views to include (the client's ids); null on a document written before views were chosen.</param>
/// <param name="Mode">How several measurements share a report: <c>combined</c> (one chart) or <c>individual</c> (a chart each); null before it existed.</param>
public sealed record ReportKindSettings(string[] Elements, string[]? Views = null, string? Mode = null);

/// <summary>
/// The report settings document, one entry per measurement kind. Lives here rather than in the
/// project database because it describes how this user likes a report, not what a measurement was.
/// </summary>
/// <param name="Kinds">The per-kind choices.</param>
/// <param name="ChartsMonochrome">Whether report charts are rendered in black and white. One flag for every kind — a project report spans kinds and prints as one document; null on a document written before it existed.</param>
public sealed record ReportSettingsConfig(Dictionary<string, ReportKindSettings> Kinds, bool? ChartsMonochrome = null)
{
    public static ReportSettingsConfig Default { get; } = new(new Dictionary<string, ReportKindSettings>());

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        Kinds is not null
        && Kinds.All(kind => !string.IsNullOrWhiteSpace(kind.Key)
                             && kind.Value?.Elements is { } elements
                             && elements.All(e => !string.IsNullOrWhiteSpace(e))
                             && (kind.Value.Views is null || kind.Value.Views.All(v => !string.IsNullOrWhiteSpace(v)))
                             && kind.Value.Mode is null or "combined" or "individual");
}

/// <summary>
/// Reads and writes the configuration database's key/value documents.
///
/// <para>Deliberately takes <see cref="ConfigConnectionFactory"/> and not
/// <see cref="SqliteConnectionFactory"/>: the two databases are different files with different
/// lifetimes, and the type system is what keeps this out of the measurement history.</para>
/// </summary>
public sealed class ConfigRepository(ConfigConnectionFactory factory)
{
    /// <summary>The impedance fixture's key. One document, see <see cref="ImpedanceFixtureConfig"/>.</summary>
    public const string ImpedanceFixtureKey = "impedance.fixture";

    /// <summary>The window layout's key. One document, see <see cref="UiLayoutConfig"/>.</summary>
    public const string UiLayoutKey = "ui.layout";

    /// <summary>The capture delay's key. One document, see <see cref="CaptureDelayConfig"/>.</summary>
    public const string CaptureDelayKey = "acquisition.captureDelay";

    /// <summary>The external input gain's key. One document, see <see cref="ExternalGainConfig"/>.</summary>
    public const string ExternalGainKey = "acquisition.externalGain";

    /// <summary>The sound-card analyzer's key. One document, see <see cref="SoundCardConfig"/>.</summary>
    public const string SoundCardKey = "soundcard.settings";

    /// <summary>A user weighting table's key, one document per slot (1 or 2) — see <see cref="UserWeightingTable"/>.</summary>
    public static string UserWeightingKey(int slot)
    {
        if (slot is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "User weighting slots are 1 and 2.");
        return $"measurement.weighting.user{slot}";
    }

    /// <summary>The report settings' key. One document, see <see cref="ReportSettingsConfig"/>.</summary>
    public const string ReportSettingsKey = "report.settings";

    /// <summary>The recent-projects list's key. One document, see <see cref="RecentProjectsConfig"/>.</summary>
    public const string RecentProjectsKey = "projects.recent";

    /// <summary>The startup behaviour's key. One document, see <see cref="StartupConfig"/>.</summary>
    public const string StartupKey = "projects.showWelcomeOnStartup";

    /// <summary>The autosave preference's key. One document, see <see cref="AutosaveConfig"/>.</summary>
    public const string AutosaveKey = "projects.autosave";

    /// <summary>The detailed-logging switch's key. One document, see <see cref="DiagnosticLoggingConfig"/>.</summary>
    public const string DiagnosticLoggingKey = "logging.diagnostic";

    /// <summary>The date/time format's key. One document, see <see cref="DateTimeFormatConfig"/>.</summary>
    public const string DateTimeFormatKey = "ui.dateTimeFormat";

    /// <summary>The Screenshot dialog's remembered answers. One document, see <see cref="ScreenshotSettingsConfig"/>.</summary>
    public const string ScreenshotSettingsKey = "screenshot.settings";

    /// <summary>The last opened project's key. One document, see <see cref="LastOpenedProjectConfig"/>.</summary>
    public const string LastOpenedProjectKey = "projects.lastOpened";

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT value FROM app_setting WHERE key = @Key;", new { Key = key });
    }

    /// <summary>Writes a document, replacing whatever was there. Upsert rather than update, so a first write needs no special case.</summary>
    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO app_setting (key, value, updated_utc) VALUES (@Key, @Value, @UpdatedUtc)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
            """,
            new { Key = key, Value = value, UpdatedUtc = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await connection.ExecuteAsync("DELETE FROM app_setting WHERE key = @Key;", new { Key = key });
    }
}
