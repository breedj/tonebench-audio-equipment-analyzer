namespace Tonebench.Data.Tests;

/// <summary>
/// Pins the promise the README makes: the configuration database, the logs and the diagnostic
/// captures all sit directly under one per-user folder, and none of them next to the binary — an
/// installed executable's folder is the installer's (and read-only under /usr/lib).
/// </summary>
public class TonebenchAppDataTests
{
    [Fact]
    public void RootDirectory_IsThePerUserLocalApplicationDataFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(Path.IsPathRooted(TonebenchAppData.RootDirectory));
        Assert.Equal(Path.Combine(localAppData, "Tonebench"), TonebenchAppData.RootDirectory);
        Assert.False(TonebenchAppData.RootDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigDatabase_Logs_AndDiagnostics_AreDirectChildrenOfTheRoot()
    {
        Assert.Equal(TonebenchAppData.RootDirectory, Path.GetDirectoryName(TonebenchAppData.ConfigDatabasePath));
        Assert.Equal(TonebenchAppData.RootDirectory, Path.GetDirectoryName(TonebenchAppData.LogsDirectory));
        Assert.Equal(TonebenchAppData.RootDirectory, Path.GetDirectoryName(TonebenchAppData.DiagnosticsDirectory));

        Assert.Equal("tonebench-config.db", Path.GetFileName(TonebenchAppData.ConfigDatabasePath));
        Assert.Equal("logs", Path.GetFileName(TonebenchAppData.LogsDirectory));
        Assert.Equal("diagnostics", Path.GetFileName(TonebenchAppData.DiagnosticsDirectory));
    }

    [Fact]
    public void DefaultConfigOptions_UseTheSameFolder()
    {
        Assert.Equal(TonebenchAppData.ConfigDatabasePath, TonebenchConfigDatabaseOptions.Default.Path);
    }

    /// <summary>
    /// The legacy folder is still named, because <see cref="TonebenchAppData.MigrateLegacyConfigDatabase"/>
    /// has to find it. It must stay the QA403-era name — "fixing" it to Tonebench would make the
    /// migration look for a folder that has never existed, so nobody's settings would come across
    /// and nothing would say so.
    /// </summary>
    [Fact]
    public void LegacyRootDirectory_StillNamesTheFolderTheAppUsedBeforeTheRename()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, "Qa403"), TonebenchAppData.LegacyRootDirectory);
        Assert.NotEqual(TonebenchAppData.LegacyRootDirectory, TonebenchAppData.RootDirectory);
    }
}

/// <summary>
/// The one-time carry-over of the configuration database from the QA403-era folder. It holds the
/// settings, the recent-projects list and — the part that cannot be recreated — this bench's
/// loopback and impedance-fixture calibrations, so each of these is a way for a user to lose them
/// silently on the first run after the rename.
/// </summary>
public class ConfigDatabaseMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tonebench-migrate-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly string _source;
    private readonly string _destination;

    public ConfigDatabaseMigrationTests()
    {
        _source = Path.Combine(_root, "Qa403", "qa403-config.db");
        _destination = Path.Combine(_root, "Tonebench", "tonebench-config.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    [Fact]
    public void ALegacyDatabase_IsCopiedIntoTheNewFolder_AndLeftWhereItWas()
    {
        File.WriteAllText(_source, "settings and calibrations");

        string? note = TonebenchAppData.MigrateConfigDatabase(_source, _destination);

        Assert.Equal("settings and calibrations", File.ReadAllText(_destination));
        Assert.True(File.Exists(_source), "a copy, not a move: the original is the fallback if anything went wrong");
        Assert.Contains("QA403", note);
    }

    /// <summary>
    /// The write-ahead log goes with it. A database copied without its -wal loses every change since
    /// the last checkpoint — which, on a config database written seconds before a shutdown, is
    /// exactly the settings the user last touched.
    /// </summary>
    [Fact]
    public void TheWriteAheadLog_IsCopiedWithTheDatabase()
    {
        File.WriteAllText(_source, "db");
        File.WriteAllText(_source + "-wal", "uncheckpointed frames");

        TonebenchAppData.MigrateConfigDatabase(_source, _destination);

        Assert.Equal("uncheckpointed frames", File.ReadAllText(_destination + "-wal"));
    }

    /// <summary>
    /// The -shm is deliberately not carried over: it mirrors in-memory index state that SQLite
    /// rebuilds, and a stale one is the only way copying it could do harm.
    /// </summary>
    [Fact]
    public void TheSharedMemoryFile_IsNotCopied()
    {
        File.WriteAllText(_source, "db");
        File.WriteAllText(_source + "-shm", "stale index");

        TonebenchAppData.MigrateConfigDatabase(_source, _destination);

        Assert.False(File.Exists(_destination + "-shm"));
    }

    /// <summary>
    /// The destination's existence is the "already done" marker, so this runs on every startup and
    /// must never overwrite the configuration the user has been building since.
    /// </summary>
    [Fact]
    public void AnExistingDatabase_IsNeverOverwritten_AndTheMigrationIsSilent()
    {
        File.WriteAllText(_source, "the old one");
        Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
        File.WriteAllText(_destination, "the one in use");

        string? note = TonebenchAppData.MigrateConfigDatabase(_source, _destination);

        Assert.Null(note);
        Assert.Equal("the one in use", File.ReadAllText(_destination));
    }

    [Fact]
    public void WithNoLegacyDatabase_NothingHappensAndNothingIsSaid()
    {
        string? note = TonebenchAppData.MigrateConfigDatabase(_source, _destination);

        Assert.Null(note);
        Assert.False(File.Exists(_destination));
    }

    /// <summary>
    /// It never throws. This runs before the host is built, and a settings file is not worth
    /// refusing to start over — the same reasoning that makes a missing device non-fatal.
    /// </summary>
    [Fact]
    public void AnUnusableDestination_ReturnsAComplaintRatherThanThrowing()
    {
        File.WriteAllText(_source, "db");
        // A path whose parent is an existing *file* cannot be created as a directory.
        string blocked = Path.Combine(_source, "nested", "tonebench-config.db");

        string? note = TonebenchAppData.MigrateConfigDatabase(_source, blocked);

        Assert.NotNull(note);
        Assert.Contains("could not carry", note);
    }
}
