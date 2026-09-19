namespace Qa403.Data;

/// <summary>
/// The one place that names the per-user data folder: <c>%LOCALAPPDATA%\Qa403</c> on Windows,
/// <c>~/.local/share/Qa403</c> on Linux. Everything the app writes for itself — the configuration
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
public static class Qa403AppData
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qa403");

    public static string ConfigDatabasePath => Path.Combine(RootDirectory, "qa403-config.db");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");
}
