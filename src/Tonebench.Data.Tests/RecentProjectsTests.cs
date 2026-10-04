namespace Tonebench.Data.Tests;

public class RecentProjectsTests
{
    private static RecentProjectEntry Entry(string path, string name = "p") => new(path, name, DateTimeOffset.UtcNow);

    [Fact]
    public void Touch_InsertsAtTheFront_AndMovesAnExistingEntryThere()
    {
        var list = RecentProjects.Touch([], Entry("/a"));
        list = RecentProjects.Touch(list, Entry("/b"));
        list = RecentProjects.Touch(list, Entry("/c"));
        Assert.Equal(["/c", "/b", "/a"], list.Select(e => e.Path));

        list = RecentProjects.Touch(list, Entry("/a", "renamed"));
        Assert.Equal(["/a", "/c", "/b"], list.Select(e => e.Path));
        Assert.Equal("renamed", list[0].Name);
    }

    [Fact]
    public void Touch_CapsAtTen_DroppingTheOldest()
    {
        IReadOnlyList<RecentProjectEntry> list = [];
        for (int i = 0; i < 12; i++)
            list = RecentProjects.Touch(list, Entry($"/p{i}"));

        Assert.Equal(10, list.Count);
        Assert.Equal("/p11", list[0].Path);
        Assert.Equal("/p2", list[^1].Path);
    }

    /// <summary>Paths compare the way the host's file system does: one file, one entry, on Windows in any case.</summary>
    [Fact]
    public void Touch_DedupesByPath_WithTheHostsCaseRule()
    {
        var list = RecentProjects.Touch([Entry("/Bench/amp.tbench")], Entry("/bench/AMP.tbench"));
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, list.Count);
    }

    [Fact]
    public void Remove_And_Rename_LeaveTheOrderAlone()
    {
        IReadOnlyList<RecentProjectEntry> list = [Entry("/a", "a"), Entry("/b", "b"), Entry("/c", "c")];

        Assert.Equal(["/a", "/c"], RecentProjects.Remove(list, "/b").Select(e => e.Path));
        Assert.Equal(["a", "B!", "c"], RecentProjects.Rename(list, "/b", "B!").Select(e => e.Name));
        Assert.Equal(3, RecentProjects.Remove(list, "/nope").Count);
    }
}
