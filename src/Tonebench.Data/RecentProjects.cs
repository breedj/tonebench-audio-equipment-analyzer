namespace Tonebench.Data;

/// <summary>
/// The arithmetic of the recent-projects list, kept pure so it can be tested without a database:
/// most recent first, one entry per file, at most <see cref="Cap"/> entries.
///
/// <para>Paths compare the way the host file system does — case-insensitively on Windows and macOS,
/// exactly on Linux — because <c>C:\Bench\amp.tbench</c> and <c>c:\bench\AMP.tbench</c> are one file
/// there and two entries for it would be a list that lies about what was opened last. On Linux they
/// are two files, and folding them would hide one.</para>
/// </summary>
public static class RecentProjects
{
    public const int Cap = 10;

    /// <summary>
    /// Case-insensitive everywhere but Linux. APFS and HFS+ are case-<i>in</i>sensitive by default,
    /// so macOS belongs with Windows rather than with the other Unix — and the test is spelled as
    /// "Linux is the exception" rather than naming the two that fold, so a platform nobody has
    /// thought about yet lands on the safe side of the asymmetry below.
    ///
    /// <para><b>The two mistakes are not equally bad, and that is what decides this.</b> This
    /// comparer is not only the recent list's: <c>ProjectSessionService</c> reads it for "is this
    /// file already open" and for "is Save As writing onto its own source". Comparing exactly on a
    /// case-insensitive volume makes two spellings of one path look like two files, which defeats
    /// those guards — and a <c>.tbench</c> is SQLite, so copying one onto itself is <b>data
    /// loss</b>. Comparing case-insensitively on a case-<i>sensitive</i> volume (possible on APFS,
    /// rare, chosen at format time) merges two genuinely different files into one recent entry and
    /// refuses a legal Save As: annoying, visible, and nothing is lost. So the fold is the default
    /// and exactness is the opt-in.</para>
    ///
    /// <para>The properly correct answer is to ask the file system — probe a case-flipped path, or
    /// compare file identity — and it is deliberately not taken: this type is pure so it can be
    /// tested without a disk, which is the whole reason it exists apart from the repository.</para>
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

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
