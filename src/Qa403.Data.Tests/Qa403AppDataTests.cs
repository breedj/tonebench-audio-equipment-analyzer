namespace Qa403.Data.Tests;

/// <summary>
/// Pins the promise the README makes: the configuration database, the logs and the diagnostic
/// captures all sit directly under one per-user folder, and none of them next to the binary — an
/// installed executable's folder is the installer's (and read-only under /usr/lib).
/// </summary>
public class Qa403AppDataTests
{
    [Fact]
    public void RootDirectory_IsThePerUserLocalApplicationDataFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(Path.IsPathRooted(Qa403AppData.RootDirectory));
        Assert.Equal(Path.Combine(localAppData, "Qa403"), Qa403AppData.RootDirectory);
        Assert.False(Qa403AppData.RootDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigDatabase_Logs_AndDiagnostics_AreDirectChildrenOfTheRoot()
    {
        Assert.Equal(Qa403AppData.RootDirectory, Path.GetDirectoryName(Qa403AppData.ConfigDatabasePath));
        Assert.Equal(Qa403AppData.RootDirectory, Path.GetDirectoryName(Qa403AppData.LogsDirectory));
        Assert.Equal(Qa403AppData.RootDirectory, Path.GetDirectoryName(Qa403AppData.DiagnosticsDirectory));

        Assert.Equal("qa403-config.db", Path.GetFileName(Qa403AppData.ConfigDatabasePath));
        Assert.Equal("logs", Path.GetFileName(Qa403AppData.LogsDirectory));
        Assert.Equal("diagnostics", Path.GetFileName(Qa403AppData.DiagnosticsDirectory));
    }

    [Fact]
    public void DefaultConfigOptions_UseTheSameFolder()
    {
        Assert.Equal(Qa403AppData.ConfigDatabasePath, Qa403ConfigDatabaseOptions.Default.Path);
    }
}
