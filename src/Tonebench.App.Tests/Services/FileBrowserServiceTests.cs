using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>The listing behind the in-app file dialog, against a throwaway directory tree.</summary>
public class FileBrowserServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tonebench-browse-{Guid.NewGuid():N}");
    private readonly FileBrowserService _browser = new();

    public FileBrowserServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Directory.CreateDirectory(Path.Combine(_dir, "Another"));
        File.WriteAllText(Path.Combine(_dir, "amp.tbench"), "x");
        File.WriteAllText(Path.Combine(_dir, "Zed.tbench"), "x");
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "old.qa403"), "x");
        File.WriteAllText(Path.Combine(_dir, ".hidden.tbench"), "x");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(Path.Combine(_dir, ".hidden.tbench"), FileAttributes.Hidden);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// The open dialog lists every extension a project file may carry, so a bench full of
    /// pre-rename .qa403 files does not read as an empty folder. One extension still means one:
    /// the endpoint's `ext` query is a filter, and widening it would break the callers that pass
    /// something else entirely (a .db, a .csv).
    /// </summary>
    [Fact]
    public void Browse_WithSeveralExtensions_ListsThemAll_WhileOneStillMeansOne()
    {
        Assert.Equal(
            ["amp.tbench", "old.qa403", "Zed.tbench"],
            _browser.Browse(_dir, ProjectPaths.OpenableExtensions).Files.Select(f => f.Name));

        Assert.Equal(["old.qa403"], _browser.Browse(_dir, ".qa403").Files.Select(f => f.Name));
    }

    [Fact]
    public void Browse_ListsFoldersThenMatchingFiles_Sorted_WithoutHiddenOnes()
    {
        var listing = _browser.Browse(_dir, ".tbench");

        Assert.Equal(_dir, listing.Path);
        Assert.Equal(Path.GetDirectoryName(_dir), listing.Parent);
        Assert.Equal(["Another", "sub"], listing.Directories.Select(d => d.Name));
        Assert.Equal(["amp.tbench", "Zed.tbench"], listing.Files.Select(f => f.Name));
        Assert.All(listing.Files, f => Assert.Equal(1L, f.SizeBytes));
        Assert.Equal(Path.DirectorySeparatorChar.ToString(), listing.Separator);
        Assert.Equal(ProjectPaths.DefaultFolder, listing.DefaultFolder);
        Assert.NotEmpty(listing.Roots);
    }

    [Fact]
    public void Browse_AcceptsATrailingSeparator()
    {
        var listing = _browser.Browse(_dir + Path.DirectorySeparatorChar, ".tbench");
        Assert.Equal(_dir, listing.Path);
    }

    [Fact]
    public void Browse_WithNoPath_StartsSomewhereThatExists()
    {
        var listing = _browser.Browse(null, ".tbench");
        Assert.True(Directory.Exists(listing.Path));
    }

    [Fact]
    public void Browse_RefusesARelativePath()
    {
        Assert.Throws<ProjectFileException>(() => _browser.Browse("sub", ".tbench"));
    }

    /// <summary>The default projects folder does not exist until the first project is written into it.</summary>
    [Fact]
    public void Browse_OfAMissingDirectory_ListsTheNearestExistingAncestor()
    {
        var listing = _browser.Browse(Path.Combine(_dir, "nope", "deeper"), ".tbench");
        Assert.Equal(_dir, listing.Path);
    }

    [Fact]
    public void Browse_WithNothingOnThePath_ReportsIt()
    {
        string bogus = OperatingSystem.IsWindows() ? @"Q:
othing\here" : "/nothing/here";
        if (Directory.Exists(Path.GetPathRoot(bogus)!))
            return; // the root exists on this machine, so the walk-up would land there instead
        Assert.Throws<DirectoryNotFoundException>(() => _browser.Browse(bogus, ".tbench"));
    }

    [Fact]
    public void Browse_AtARoot_HasNoParent()
    {
        string root = Path.GetPathRoot(_dir)!;
        var listing = _browser.Browse(root, ".tbench");
        Assert.Null(listing.Parent);
    }

    [Fact]
    public void Browse_ExpandsTilde()
    {
        var listing = _browser.Browse("~", ".tbench");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Path.TrimEndingDirectorySeparator(listing.Path));
    }
}
