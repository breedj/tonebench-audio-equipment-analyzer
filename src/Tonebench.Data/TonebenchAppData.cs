namespace Tonebench.Data;

/// <summary>
/// The one place that names the per-user data folder: <c>%LOCALAPPDATA%\Tonebench</c> on Windows,
/// <c>~/.local/share/Tonebench</c> on Linux. Everything the app writes for itself — the configuration
/// database, the logs, the diagnostic captures — lives here and <b>never next to the binary</b>.
///
/// <para>Beside the exe was fine while the app was only ever run from its build output. An installed
/// binary is different: the Windows installer owns its folder and the .deb puts the executable under
/// <c>/usr/lib</c>, which is not writable at all. And a rebuild or an upgrade must not take the logs
/// with it. Static rather than injected because the logger is configured before the host builder
/// exists (<c>Program.cs</c>), and nothing about these paths depends on configuration.</para>
///
/// <para>Note for anyone reading logs from a process spawned by the Claude desktop app: that app runs
/// under AppData virtualization, so its children see an overlay copy of this folder, not the user's
/// real one — see the memory note <c>appdata-virtualization-trap</c>.</para>
/// </summary>
public static class TonebenchAppData
{
    private const string FolderName = "Tonebench";
    private const string ConfigDatabaseName = "tonebench-config.db";

    /// <summary>
    /// What the folder and the configuration database were called while the app was named after the
    /// QA403. Kept only so <see cref="MigrateLegacyConfigDatabase"/> can find them once.
    /// </summary>
    private const string LegacyFolderName = "Qa403";
    private const string LegacyConfigDatabaseName = "qa403-config.db";

    public static string RootDirectory { get; } = Path.Combine(LocalAppData, FolderName);

    public static string ConfigDatabasePath => Path.Combine(RootDirectory, ConfigDatabaseName);

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");

    /// <summary>
    /// Where the stream recorder's WAV pairs go. Separate from <see cref="DiagnosticsDirectory"/>
    /// because these are the one thing here measured in hundreds of megabytes: a user asked to
    /// clear them out, or to send one, should be looking at a folder that holds nothing else.
    /// </summary>
    public static string RecordingsDirectory => Path.Combine(RootDirectory, "recordings");

    /// <summary>
    /// Where <c>Report an issue</c> leaves the zip it builds.
    ///
    /// <para>Its own folder, beside the three above rather than inside any of them, because a
    /// report is a <i>copy</i> of what they hold: a zip written into <see cref="LogsDirectory"/>
    /// would be caught by the log retention, and one written into
    /// <see cref="RecordingsDirectory"/> by the recorder's purge of all but the newest ten sets —
    /// in both cases deleting the thing the user was about to send. It is also the one folder here
    /// whose contents are meant to leave the machine, which is a good enough reason on its own for
    /// it to be the only thing in it.</para>
    /// </summary>
    public static string IssueReportsDirectory => Path.Combine(RootDirectory, "issue-reports");

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The pre-rename folder, left in place rather than deleted — see
    /// <see cref="MigrateLegacyConfigDatabase"/>.
    /// </summary>
    public static string LegacyRootDirectory => Path.Combine(LocalAppData, LegacyFolderName);

    /// <summary>
    /// Carries the configuration database over from the QA403-era folder, once, on the first run
    /// after the rename. Returns a line for the startup log, or null when there was nothing to do.
    ///
    /// <para>Only the configuration database moves, and that is deliberate: it holds the settings,
    /// the recent-projects list, the last opened file and — the one that cannot be recreated — the
    /// loopback and impedance-fixture calibrations, which describe this bench's own leads. The logs,
    /// the diagnostic captures and the WAV recordings stay behind, being disposable and, in the
    /// recordings' case, measured in hundreds of megabytes. Project files are untouched: they live
    /// in Documents and are opened by path.</para>
    ///
    /// <para>A <b>copy</b>, not a move, and the old folder is never deleted: a failed rename on the
    /// one file holding the calibrations is not worth the tidiness. The destination's own existence
    /// is the "already done" marker, so this is idempotent and a second run does nothing. The
    /// <c>-wal</c> is copied <i>before</i> the database and the database last, so a copy interrupted
    /// half way leaves no destination and is simply retried next time; the <c>-shm</c> is
    /// deliberately not copied, being an in-memory index mirror that SQLite rebuilds and that can
    /// only cause harm if carried over stale. It never throws — a host that cannot start because of
    /// a settings file is worse than one that starts with default settings, which is the same
    /// reasoning that makes a missing device non-fatal.</para>
    /// </summary>
    public static string? MigrateLegacyConfigDatabase() =>
        MigrateConfigDatabase(Path.Combine(LegacyRootDirectory, LegacyConfigDatabaseName), ConfigDatabasePath);

    /// <summary>
    /// <see cref="MigrateLegacyConfigDatabase"/> with both paths given, which is the only way to
    /// test it: the no-argument version names the real per-user folder, and a test that exercised
    /// that would copy over the developer's own configuration.
    /// </summary>
    public static string? MigrateConfigDatabase(string source, string destination)
    {
        try
        {
            if (File.Exists(destination) || !File.Exists(source))
                return null;

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (File.Exists(source + "-wal"))
                File.Copy(source + "-wal", destination + "-wal", overwrite: true);
            File.Copy(source, destination, overwrite: true);

            return $"carried its configuration over from {Path.GetDirectoryName(source)}, the folder it "
                + "used while the app was named after the QA403; the original is left in place";
        }
        catch (Exception ex)
        {
            return $"could not carry its configuration over from {source}: {ex.Message}. "
                + "Settings, the recent-projects list and any stored calibrations start empty";
        }
    }
}
