using System.Text.Json;

namespace Tonebench.Data.Tests;

/// <summary>
/// The panel-order document: which order a measurement screen's two rails are stacked in, per
/// measurement kind.
///
/// <para>The ids are the client's own vocabulary — a slug of a panel's heading, or a string it
/// declares — so the server stores the strings and checks nothing but that they are strings. These
/// pin the one thing it <i>does</i> check, and that the shape survives a round trip: a document
/// that reads back as something else would reorder a bench's rails at random rather than failing
/// in a way anyone could see.</para>
/// </summary>
public class PanelOrderConfigTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Default_IsNoOrderAtAll_WhichReadsAsTheOrderTheScreenGivesThem()
    {
        Assert.Empty(PanelOrderConfig.Default.Kinds);
        Assert.True(PanelOrderConfig.Default.IsValid);
    }

    [Fact]
    public void ARailOrder_SurvivesARoundTrip()
    {
        var order = new PanelOrderConfig(new Dictionary<string, PanelRailOrder>
        {
            ["Oscilloscope"] = new(
                Settings: ["trigger", "generator", "acquisition", "record"],
                Display: ["mask", "view"]),
        });

        var back = JsonSerializer.Deserialize<PanelOrderConfig>(JsonSerializer.Serialize(order, Web), Web);

        Assert.NotNull(back);
        Assert.True(back.IsValid);
        Assert.Equal(["trigger", "generator", "acquisition", "record"], back.Kinds["Oscilloscope"].Settings!);
        Assert.Equal(["mask", "view"], back.Kinds["Oscilloscope"].Display!);
    }

    /// <summary>A rail that was never dragged has no entry, which is not the same as an empty one.</summary>
    [Fact]
    public void ARailThatWasNeverDragged_IsNull_RatherThanAnEmptyList()
    {
        var order = new PanelOrderConfig(new Dictionary<string, PanelRailOrder>
        {
            ["FftSpectrum"] = new(Settings: ["generator"]),
        });

        var back = JsonSerializer.Deserialize<PanelOrderConfig>(JsonSerializer.Serialize(order, Web), Web);

        Assert.Null(back!.Kinds["FftSpectrum"].Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyKind_IsRefused(string kind)
    {
        var order = new PanelOrderConfig(new Dictionary<string, PanelRailOrder>
        {
            [kind] = new(Settings: ["trigger"]),
        });

        Assert.False(order.IsValid);
    }

    [Fact]
    public void AnEmptyPanelId_IsRefused()
    {
        Assert.False(new PanelOrderConfig(new Dictionary<string, PanelRailOrder>
        {
            ["Oscilloscope"] = new(Settings: ["trigger", " "]),
        }).IsValid);

        Assert.False(new PanelOrderConfig(new Dictionary<string, PanelRailOrder>
        {
            ["Oscilloscope"] = new(Display: [""]),
        }).IsValid);
    }

    /// <summary>
    /// An id this build does not render is kept rather than refused: the client simply never
    /// matches it, and a document rejected for naming a panel that has since been renamed would
    /// throw away the order of every other panel with it.
    /// </summary>
    [Fact]
    public void AnIdThisBuildDoesNotKnow_IsValid()
    {
        Assert.True(new PanelOrderConfig(new Dictionary<string, PanelRailOrder>
        {
            ["Oscilloscope"] = new(Settings: ["something-removed-in-0.8", "trigger"]),
        }).IsValid);
    }
}
