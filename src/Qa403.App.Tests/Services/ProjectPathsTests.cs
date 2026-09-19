using Qa403.App.Services;

namespace Qa403.App.Tests.Services;

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
        Assert.Equal("amp.qa403", ProjectPaths.EnsureExtension("amp"));
        Assert.Equal("amp.qa403", ProjectPaths.EnsureExtension("amp.qa403"));
        Assert.Equal("amp.QA403", ProjectPaths.EnsureExtension("amp.QA403"));
    }

    [Fact]
    public void Normalize_RefusesRelativeAndEmpty_ExpandsHome()
    {
        Assert.Throws<ProjectFileException>(() => ProjectPaths.Normalize(""));
        Assert.Throws<ProjectFileException>(() => ProjectPaths.Normalize("amp.qa403"));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "x.qa403")), ProjectPaths.Normalize("~/x.qa403"));
        Assert.Equal(Path.GetFullPath(home), ProjectPaths.Normalize("~"));
    }

    [Fact]
    public void DefaultFolder_IsUnderDocuments()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProjectPaths.DefaultFolder);
        Assert.EndsWith("QA403 Projects", ProjectPaths.DefaultFolder);
    }
}
