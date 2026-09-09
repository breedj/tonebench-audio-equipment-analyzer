namespace Qa403.Data;

/// <summary>
/// Where <c>qa403-config.db</c> lives — the application's own configuration, kept in a
/// <b>separate file</b> from the measurement history in <c>qa403.db</c>.
///
/// <para><b>Why a second database rather than more tables in the first one.</b> The two hold
/// different kinds of thing and want different lifetimes. Measurement history is portable: it is
/// what you copy to another machine, hand to someone else, or restore from a backup. Configuration
/// is <i>machine-specific</i> — and in this app that is not a matter of taste, because the impedance
/// fixture calibration stored here describes one particular QA403's inter-channel gain and phase
/// mismatch and one particular set of test leads. Carried along inside a copied measurement
/// database it would not merely be stale, it would be actively wrong: every impedance reading on the
/// other machine would be silently corrected by the wrong numbers, with nothing on screen to say
/// so.</para>
///
/// <para>Separating them makes that impossible by construction. It also means a corrupt or deleted
/// configuration costs you preferences rather than years of measurements, and that a
/// <c>qa403.db</c> restored from backup does not quietly revert the calibration.</para>
///
/// <para>Same directory and the same override mechanism as <see cref="Qa403DatabaseOptions"/> —
/// the <c>Config:Path</c> configuration key.</para>
/// </summary>
public sealed record Qa403ConfigDatabaseOptions
{
    public required string Path { get; init; }

    public static Qa403ConfigDatabaseOptions Default
    {
        get
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qa403");
            Directory.CreateDirectory(directory);
            return new Qa403ConfigDatabaseOptions { Path = System.IO.Path.Combine(directory, "qa403-config.db") };
        }
    }
}
