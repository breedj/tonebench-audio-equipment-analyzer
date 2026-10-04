namespace Tonebench.Data;

/// <summary>
/// The arithmetic of the recent-projects list, kept pure so it can be tested without a database:
/// most recent first, one entry per file, at most <see cref="Cap"/> entries.
///
/// <para>Paths compare the way the host file system does — case-insensitively on Windows, exactly
/// elsewhere — because <c>C:\Bench\amp.tbench</c> and <c>c:\bench\AMP.tbench</c> are one file there and
/// two entries for it would be a list that lies about what was opened last. On Linux they are two
/// files, and folding them would hide one.</para>
/// </summary>
public static class RecentProjects
{
    public const int Cap = 10;

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Moves (or inserts) <paramref name="entry"/> to the front and drops anything past the cap.</summary>
    public static IReadOnlyList<RecentProjectEntry> Touch(
        IReadOnlyList<RecentProjectEntry> entries, RecentProjectEntry entry, int cap = Cap)
    {
        var result = new List<RecentProjectEntry>(entries.Count + 1) { entry };
        result.AddRange(entries.Where(e => !PathComparer.Equals(e.Path, entry.Path)));
        if (result.Count > cap)
            result.RemoveRange(cap, result.Count - cap);
        return result;
    }

    public static IReadOnlyList<RecentProjectEntry> Remove(IReadOnlyList<RecentProjectEntry> entries, string path) =>
        [.. entries.Where(e => !PathComparer.Equals(e.Path, path))];

    /// <summary>Updates the display name of the entry at <paramref name="path"/> without changing its position.</summary>
    public static IReadOnlyList<RecentProjectEntry> Rename(IReadOnlyList<RecentProjectEntry> entries, string path, string name) =>
        [.. entries.Select(e => PathComparer.Equals(e.Path, path) ? e with { Name = name } : e)];
}
