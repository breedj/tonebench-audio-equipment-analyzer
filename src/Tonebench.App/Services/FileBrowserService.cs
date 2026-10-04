using Tonebench.App.Endpoints;

namespace Tonebench.App.Services;

/// <summary>
/// The server side of the in-app file dialog: lists one directory at a time.
///
/// <para>The app is a Kestrel server driving the system browser, and a web page cannot open the
/// operating system's file dialog on the server's behalf. Native dialogs spawned from the server
/// (PowerShell on Windows, zenity on Linux) open behind the browser window, flash a console, and
/// need packages that may not be installed; a listing rendered by the web UI looks the same on both
/// platforms and needs nothing. It lists and never writes — the project session creates files, and
/// only where it was told to.</para>
///
/// <para>It exposes the file system to whoever can reach the API, which is anyone on this machine:
/// Kestrel binds <c>127.0.0.1</c> only, the API already lets that caller delete measurement data, no
/// CORS headers are ever sent so a page on another origin cannot read a listing, and every mutating
/// project endpoint takes a JSON body, which the browser preflights. Hidden and system entries are
/// skipped (dot-files on Linux, the attributes on Windows) because they are noise in a dialog for
/// picking a project, not because hiding them protects anything.</para>
/// </summary>
public sealed class FileBrowserService
{
    /// <summary>
    /// Lists <paramref name="path"/> (or, for none, the default projects folder), showing files with
    /// <paramref name="extension"/> only. A directory that does not exist lists its nearest existing
    /// ancestor instead — the default projects folder is not created until the first project is
    /// written into it, and a dialog that opened on an error for that would be no dialog at all.
    /// </summary>
    /// <exception cref="ProjectFileException">The path is relative.</exception>
    /// <exception cref="DirectoryNotFoundException">Nothing on the path exists, not even its root.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory cannot be read.</exception>
    public FileBrowseResponse Browse(string? path, string extension) => Browse(path, [extension]);

    /// <inheritdoc cref="Browse(string?, string)"/>
    /// <remarks>
    /// Several extensions because the project file's own changed with the app's name: a listing that
    /// showed only <c>.tbench</c> would hide every project written before the rename.
    /// </remarks>
    public FileBrowseResponse Browse(string? path, IReadOnlyList<string> extensions)
    {
        string directory = string.IsNullOrWhiteSpace(path) ? ProjectPaths.DefaultFolder : ProjectPaths.Normalize(path);
        directory = Path.TrimEndingDirectorySeparator(directory);
        if (directory.Length == 2 && directory[1] == ':')
            directory += Path.DirectorySeparatorChar; // "C:" alone means the drive's current directory, not its root

        while (!Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory)
                ?? throw new DirectoryNotFoundException($"'{path}' does not exist.");
        }

        var info = new DirectoryInfo(directory);
        var directories = new List<FileBrowseEntry>();
        var files = new List<FileBrowseEntry>();

        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (IsHidden(entry))
                continue;

            if (entry is DirectoryInfo)
                directories.Add(new FileBrowseEntry(entry.Name, entry.FullName, ModifiedOf(entry), null));
            else if (entry is FileInfo file && extensions.Any(e => file.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                files.Add(new FileBrowseEntry(entry.Name, entry.FullName, ModifiedOf(entry), file.Length));
        }

        var comparer = NameComparer;
        directories.Sort((a, b) => comparer.Compare(a.Name, b.Name));
        files.Sort((a, b) => comparer.Compare(a.Name, b.Name));

        return new FileBrowseResponse(
            info.FullName,
            info.Parent?.FullName,
            directories,
            files,
            Roots(),
            ProjectPaths.DefaultFolder,
            Path.DirectorySeparatorChar.ToString());
    }

    private static IReadOnlyList<FileBrowseRoot> Roots()
    {
        var roots = new List<FileBrowseRoot>();

        if (Directory.Exists(ProjectPaths.DefaultFolder))
            roots.Add(new FileBrowseRoot("Tonebench Projects", ProjectPaths.DefaultFolder));

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(documents))
            roots.Add(new FileBrowseRoot(documents == home ? "Home" : "Documents", documents));
        if (documents != home && Directory.Exists(home))
            roots.Add(new FileBrowseRoot("Home", home));

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                string label;
                try
                {
                    label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.Name} ({drive.VolumeLabel})";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    label = drive.Name;
                }
                roots.Add(new FileBrowseRoot(label, drive.Name));
            }
        }
        else
        {
            roots.Add(new FileBrowseRoot("/", "/"));
        }

        return roots;
    }

    private static bool IsHidden(FileSystemInfo entry)
    {
        if (OperatingSystem.IsWindows())
            return (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        return entry.Name.StartsWith('.');
    }

    private static DateTimeOffset? ModifiedOf(FileSystemInfo entry)
    {
        try
        {
            return new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // Case-insensitive on every platform. This is a display order, not the file system's collation:
    // a dialog that lists "Zed" above "amp" because Linux sorts by byte value reads as unsorted,
    // and the Windows/Linux split here used to make the same folder list differently — which is
    // also how the test for it only failed in CI.
    private static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;
}
