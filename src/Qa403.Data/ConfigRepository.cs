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
public sealed record UiLayoutConfig(bool SidebarPinned = true)
{
    public static UiLayoutConfig Default { get; } = new();
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
