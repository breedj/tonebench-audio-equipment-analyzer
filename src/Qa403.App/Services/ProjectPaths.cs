namespace Qa403.App.Services;

/// <summary>
/// Path rules for project files, shared by the session, the file browser and the endpoints.
/// </summary>
public static class ProjectPaths
{
    /// <summary>One project per file; the extension is what the file browser filters on.</summary>
    public const string Extension = ".qa403";

    /// <summary>
    /// Where new projects go unless the user says otherwise: <c>Documents\QA403 Projects</c>. On
    /// Linux .NET maps <c>MyDocuments</c> to <c>$HOME</c>, so it is <c>~/QA403 Projects</c> there —
    /// visible user data either way, rather than something under an application-data folder that
    /// nobody browses or backs up. Never created here; the session creates it when the first file
    /// is written into it. <c>MyDocuments</c> can come back <b>empty</b> — on Linux it is derived from
    /// <c>$HOME</c>, and a service account or a CI runner may have none — which would make this a
    /// relative path that the file browser cannot walk up from; the fallbacks are the user profile,
    /// then the current directory, so it is always absolute.
    /// </summary>
    public static string DefaultFolder { get; } = Path.Combine(DocumentsFolder(), "QA403 Projects");

    private static string DocumentsFolder()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                return path;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// A typed path made absolute: a leading <c>~</c> becomes the home directory (the convention on
    /// Linux, harmless on Windows), and a relative path is refused rather than resolved against the
    /// server's working directory, which the user cannot see and which is the binary's folder.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ProjectFileException("No file path was given.");

        string expanded = ExpandHome(path.Trim());
        if (!Path.IsPathRooted(expanded))
            throw new ProjectFileException($"'{path}' is not an absolute path.");

        return Path.GetFullPath(expanded);
    }

    public static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);

        return path;
    }

    /// <summary>Appends <see cref="Extension"/> unless the path already ends in it.</summary>
    public static string EnsureExtension(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? path : path + Extension;

    /// <summary>
    /// A project name as a file name, valid on every platform at once — the fixed Windows set of
    /// forbidden characters rather than the host's, so a file made on Linux can be copied to a
    /// Windows bench. Control characters go too; runs of whitespace collapse; the leading and
    /// trailing dots and spaces Windows silently strips are stripped here instead; and an empty
    /// result becomes "Project" rather than a file with no name.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        const string forbidden = "<>:\"/\\|?*";
        var chars = new List<char>(name.Length);
        bool pendingSpace = false;
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = chars.Count > 0;
                continue;
            }

            if (char.IsControl(c) || forbidden.Contains(c))
                continue;

            if (pendingSpace)
            {
                chars.Add(' ');
                pendingSpace = false;
            }
            chars.Add(c);
        }

        string result = new string([.. chars]).Trim(' ', '.');
        return result.Length == 0 ? "Project" : result;
    }
}
