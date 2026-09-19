using Qa403.App.Services;

namespace Qa403.App.Tests.Services;

/// <summary>The listing behind the in-app file dialog, against a throwaway directory tree.</summary>
public class FileBrowserServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-browse-{Guid.NewGuid():N}");
    private readonly FileBrowserService _browser = new();

    public FileBrowserServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Directory.CreateDirectory(Path.Combine(_dir, "Another"));
        File.WriteAllText(Path.Combine(_dir, "amp.qa403"), "x");
        File.WriteAllText(Path.Combine(_dir, "Zed.qa403"), "x");
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, ".hidden.qa403"), "x");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(Path.Combine(_dir, ".hidden.qa403"), FileAttributes.Hidden);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Browse_ListsFoldersThenMatchingFiles_Sorted_WithoutHiddenOnes()
    {
        var listing = _browser.Browse(_dir, ".qa403");

        Assert.Equal(_dir, listing.Path);
        Assert.Equal(Path.GetDirectoryName(_dir), listing.Parent);
        Assert.Equal(["Another", "sub"], listing.Directories.Select(d => d.Name));
        Assert.Equal(["amp.qa403", "Zed.qa403"], listing.Files.Select(f => f.Name));
        Assert.All(listing.Files, f => Assert.Equal(1L, f.SizeBytes));
        Assert.Equal(Path.DirectorySeparatorChar.ToString(), listing.Separator);
        Assert.Equal(ProjectPaths.DefaultFolder, listing.DefaultFolder);
        Assert.NotEmpty(listing.Roots);
    }

    [Fact]
    public void Browse_AcceptsATrailingSeparator()
    {
        var listing = _browser.Browse(_dir + Path.DirectorySeparatorChar, ".qa403");
        Assert.Equal(_dir, listing.Path);
    }

    [Fact]
    public void Browse_WithNoPath_StartsSomewhereThatExists()
    {
        var listing = _browser.Browse(null, ".qa403");
        Assert.True(Directory.Exists(listing.Path));
    }

    [Fact]
    public void Browse_RefusesARelativePath()
    {
        Assert.Throws<ProjectFileException>(() => _browser.Browse("sub", ".qa403"));
    }

    /// <summary>The default projects folder does not exist until the first project is written into it.</summary>
    [Fact]
    public void Browse_OfAMissingDirectory_ListsTheNearestExistingAncestor()
    {
        var listing = _browser.Browse(Path.Combine(_dir, "nope", "deeper"), ".qa403");
        Assert.Equal(_dir, listing.Path);
    }

    [Fact]
    public void Browse_WithNothingOnThePath_ReportsIt()
    {
        string bogus = OperatingSystem.IsWindows() ? @"Q:
othing\here" : "/nothing/here";
        if (Directory.Exists(Path.GetPathRoot(bogus)!))
            return; // the root exists on this machine, so the walk-up would land there instead
        Assert.Throws<DirectoryNotFoundException>(() => _browser.Browse(bogus, ".qa403"));
    }

    [Fact]
    public void Browse_AtARoot_HasNoParent()
    {
        string root = Path.GetPathRoot(_dir)!;
        var listing = _browser.Browse(root, ".qa403");
        Assert.Null(listing.Parent);
    }

    [Fact]
    public void Browse_ExpandsTilde()
    {
        var listing = _browser.Browse("~", ".qa403");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Path.TrimEndingDirectorySeparator(listing.Path));
    }
}
