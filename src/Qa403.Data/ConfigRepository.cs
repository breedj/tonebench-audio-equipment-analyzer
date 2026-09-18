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
public sealed record ReportSettingsConfig(Dictionary<string, ReportKindSettings> Kinds)
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

    /// <summary>The report settings' key. One document, see <see cref="ReportSettingsConfig"/>.</summary>
    public const string ReportSettingsKey = "report.settings";

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
