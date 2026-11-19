using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Which form a <c>.wav</c> path is stored in. The whole of the feature is one rule — a waveform
/// that lives with the project is stored relative to it — and the whole of the care is in when
/// that is <b>not</b> worth doing.
/// </summary>
public class ArbitraryPathTests
{
    private static string Project(params string[] parts) =>
        Path.Combine([Path.GetTempPath(), "tonebench-rel", .. parts]);

    private static string ProjectFile => Project("amp.tbench");

    [Fact]
    public void AFileBesideTheProject_IsStoredRelative()
    {
        string wav = Project("step.wav");

        Assert.Equal("step.wav", ArbitraryPath.ForStorage(wav, ProjectFile));
    }

    [Fact]
    public void AFileBelowTheProject_KeepsItsFolders()
    {
        string wav = Project("waves", "burst", "step.wav");

        string stored = ArbitraryPath.ForStorage(wav, ProjectFile);

        Assert.Equal(Path.Combine("waves", "burst", "step.wav"), stored);
        Assert.False(Path.IsPathRooted(stored));
    }

    /// <summary>
    /// <b>The test is "at or below the project's folder", not "can a relative path be computed".</b>
    /// One can almost always be computed; <c>..\..\..\Music\sweep.wav</c> is longer, harder to read
    /// and no more likely to survive a move than the absolute path it would replace.
    /// </summary>
    [Fact]
    public void AFileOutsideTheProjectsFolder_StaysAbsolute()
    {
        string wav = Path.Combine(Path.GetTempPath(), "tonebench-elsewhere", "sweep.wav");

        Assert.Equal(wav, ArbitraryPath.ForStorage(wav, ProjectFile));
    }

    [Fact]
    public void WithNoProjectOpen_NothingIsMadeRelative()
    {
        string wav = Project("step.wav");

        Assert.Equal(wav, ArbitraryPath.ForStorage(wav, null));
        Assert.Equal(wav, ArbitraryPath.ForStorage(wav, "   "));
    }

    [Fact]
    public void AnAlreadyRelativePathIsLeftAlone()
    {
        Assert.Equal("step.wav", ArbitraryPath.ForStorage("step.wav", ProjectFile));
        Assert.Equal(
            Path.Combine("waves", "step.wav"),
            ArbitraryPath.ForStorage(Path.Combine("waves", "step.wav"), ProjectFile));
    }

    /// <summary>
    /// On Windows a file on another drive has no relative form at all —
    /// <see cref="Path.GetRelativePath"/> hands the absolute path back, which this must notice
    /// rather than store as if it were relative.
    /// </summary>
    [Fact]
    public void AFileOnAnotherRoot_StaysAbsolute()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string wav = @"D:\samples\step.wav";

        Assert.Equal(wav, ArbitraryPath.ForStorage(wav, @"C:\bench\amp.tbench"));
    }

    [Fact]
    public void AStoredRelativePath_IsResolvedAgainstTheProject()
    {
        string resolved = ArbitraryPath.ForLoading(Path.Combine("waves", "step.wav"), ProjectFile);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(Path.GetFullPath(Project("waves", "step.wav")), resolved);
    }

    [Fact]
    public void AnAbsoluteStoredPathIsLoadedAsItStands()
    {
        string wav = Path.Combine(Path.GetTempPath(), "elsewhere", "step.wav");

        Assert.Equal(wav, ArbitraryPath.ForLoading(wav, ProjectFile));
    }

    /// <summary>
    /// A relative path with no project open is handed back as it stands rather than resolved
    /// against the working directory: the loader then names <i>that</i> file in its refusal, where
    /// a silent read of something in the app's own folder would be the worst possible answer.
    /// </summary>
    [Fact]
    public void ARelativePathWithNoProject_IsNotGuessedAt()
    {
        Assert.Equal("step.wav", ArbitraryPath.ForLoading("step.wav", null));
    }

    /// <summary>
    /// The two are inverses for every file the rule applies to, which is the property a stored
    /// measurement depends on: what is written must read back as the same file.
    /// </summary>
    [Theory]
    [InlineData("step.wav")]
    [InlineData("waves/step.wav")]
    [InlineData("waves/burst/one two.wav")]
    public void StoringThenLoading_IsTheSameFile(string relative)
    {
        string wav = Path.GetFullPath(Project(relative.Replace('/', Path.DirectorySeparatorChar)));

        string stored = ArbitraryPath.ForStorage(wav, ProjectFile);
        Assert.False(Path.IsPathRooted(stored));
        Assert.Equal(wav, ArbitraryPath.ForLoading(stored, ProjectFile));
    }

    /// <summary>
    /// And the point of the whole thing: the same stored path under a project that has <b>moved</b>
    /// finds the file in its new home. That is what an absolute path could not do.
    /// </summary>
    [Fact]
    public void AMovedProjectFolder_StillFindsItsWaveform()
    {
        string wav = Project("waves", "step.wav");
        string stored = ArbitraryPath.ForStorage(wav, ProjectFile);

        string moved = Path.Combine(Path.GetTempPath(), "tonebench-moved", "amp.tbench");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tonebench-moved", "waves", "step.wav")),
            ArbitraryPath.ForLoading(stored, moved));
    }
}
