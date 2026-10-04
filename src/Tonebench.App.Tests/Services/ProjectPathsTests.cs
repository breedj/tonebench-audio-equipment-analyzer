using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

public class ProjectPathsTests
{
    [Theory]
    [InlineData("GM70 amp", "GM70 amp")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
    [InlineData("...dots...", "dots")]
    [InlineData("tab\tand\nnewline", "tab and newline")]
    [InlineData("", "Project")]
    [InlineData("???", "Project")]
    [InlineData("Ω 6L6 (push-pull)", "Ω 6L6 (push-pull)")]
    public void SanitizeFileName_ProducesANameValidOnEveryPlatform(string name, string expected)
    {
        Assert.Equal(expected, ProjectPaths.SanitizeFileName(name));
    }

    [Fact]
    public void EnsureExtension_AppendsOnceAndIgnoresCase()
    {
        Assert.Equal("amp.tbench", ProjectPaths.EnsureExtension("amp"));
        Assert.Equal("amp.tbench", ProjectPaths.EnsureExtension("amp.tbench"));
        Assert.Equal("amp.TBench", ProjectPaths.EnsureExtension("amp.TBench"));
    }

    /// <summary>
    /// A project written before the rename keeps its own extension when it is saved: appending the
    /// new one would turn <c>amp.qa403</c> into <c>amp.qa403.tbench</c> and leave the original
    /// sitting beside it, so the user's next Open finds two files and an unchanged one.
    /// </summary>
    [Fact]
    public void EnsureExtension_LeavesTheLegacyExtensionAlone()
    {
        Assert.Equal("amp.qa403", ProjectPaths.EnsureExtension("amp.qa403"));
        Assert.Equal("amp.QA403", ProjectPaths.EnsureExtension("amp.QA403"));
    }

    /// <summary>
    /// Both extensions are listed by the file browser, new one first. A set that held only
    /// <see cref="ProjectPaths.Extension"/> would show a folder of pre-rename projects as empty.
    /// </summary>
    [Fact]
    public void OpenableExtensions_AreTheCurrentOneThenTheLegacyOne()
    {
        Assert.Equal([".tbench", ".qa403"], ProjectPaths.OpenableExtensions);
    }

    [Fact]
    public void Normalize_RefusesRelativeAndEmpty_ExpandsHome()
    {
        Assert.Throws<ProjectFileException>(() => ProjectPaths.Normalize(""));
        Assert.Throws<ProjectFileException>(() => ProjectPaths.Normalize("amp.tbench"));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "x.tbench")), ProjectPaths.Normalize("~/x.tbench"));
        Assert.Equal(Path.GetFullPath(home), ProjectPaths.Normalize("~"));
    }

    [Fact]
    public void DefaultFolder_IsUnderDocuments()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProjectPaths.DefaultFolder);
        Assert.EndsWith("Tonebench Projects", ProjectPaths.DefaultFolder);
    }
}
