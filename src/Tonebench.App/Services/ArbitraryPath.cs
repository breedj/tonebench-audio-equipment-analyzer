namespace Tonebench.App.Services;

/// <summary>
/// Which form an arbitrary stimulus's <c>.wav</c> path is written in, and how it is read back.
///
/// <para><b>A waveform that lives with the project is stored relative to it, so the folder is
/// portable.</b> Copy the project folder to another machine, or to another drive, and the
/// measurements still find their files; an absolute path would break on both, correctly and with a
/// message, but break. A waveform that lives <i>elsewhere</i> keeps its absolute path, because
/// there is nothing portable about <c>..\..\..\Music\sweep.wav</c> — it is longer, harder to read
/// and no more likely to survive a move than the absolute form it replaced.</para>
///
/// <para><b>So the test is "is the file at or below the project's folder", not "can a relative path
/// be computed".</b> One can almost always be computed; it is only worth having when it has no
/// <c>..</c> in it.</para>
///
/// <para>Case and separators are <see cref="Path.GetRelativePath"/>'s business, which is the point
/// of using it: it compares the way the host file system does, so this does not need a second copy
/// of the rule <c>RecentProjects.PathComparer</c> already states (ordinal on Linux, case-insensitive
/// everywhere else).</para>
/// </summary>
public static class ArbitraryPath
{
    /// <summary>
    /// The form to <b>store</b>: relative to the project's folder where the file sits at or below
    /// it, and the path unchanged otherwise — including when no project is open, when the path is
    /// already relative, and when the two are on different drives.
    /// </summary>
    public static string ForStorage(string path, string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return path;

        string? directory = ProjectDirectory(projectFilePath);
        if (directory is null)
            return path;

        string relative;
        try
        {
            relative = Path.GetRelativePath(directory, path);
        }
        catch (ArgumentException)
        {
            return path;
        }

        // GetRelativePath hands back the absolute path when the two share no root (a different
        // drive, a UNC share), and a `..` chain when the file is outside the project's folder.
        // Neither is worth storing in place of the absolute path.
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            return path;

        return relative;
    }

    /// <summary>
    /// The form to <b>load</b>: a stored relative path resolved against the project's folder, and
    /// anything already rooted unchanged.
    ///
    /// <para>A relative path with no project open is returned as it stands rather than guessed at
    /// against the working directory — the loader then reports that file by name, which is a
    /// refusal a user can act on, where a silent read of some unrelated file in the app's own
    /// folder would not be.</para>
    /// </summary>
    public static string ForLoading(string path, string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            return path;

        string? directory = ProjectDirectory(projectFilePath);
        if (directory is null)
            return path;

        try
        {
            return Path.GetFullPath(Path.Combine(directory, path));
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>The folder a project file sits in, or null when there is no project to be relative to.</summary>
    private static string? ProjectDirectory(string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
            return null;

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
            return string.IsNullOrEmpty(directory) ? null : directory;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
