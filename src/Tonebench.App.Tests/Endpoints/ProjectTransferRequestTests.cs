using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The transfer endpoints' own two jobs: turning a request's clash policies into the repository's,
/// and writing responses the client can actually parse.
///
/// <para>The serialisation half has its own test for the reason <c>ImpedanceStatusJsonTests</c>
/// gives: <c>System.Text.Json</c> discovers a value it cannot write part way through a response
/// that has already gone out as a 200, so the body simply stops mid-document and the browser sees
/// unparseable JSON with no error anywhere. The contents response carries a size per result, which
/// is exactly the shape of value that gets quietly turned into a <c>double</c> one day.</para>
/// </summary>
public class ProjectTransferRequestTests
{
    /// <summary>The app's own HTTP JSON options, including the string enum converter Program.cs adds.</summary>
    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            return options.SerializerOptions;
        }
    }

    [Theory]
    [InlineData("Rename", TypeNameClash.Rename)]
    [InlineData("merge", TypeNameClash.Merge)]
    [InlineData("SKIP", TypeNameClash.Skip)]
    public void OnNameClash_ParsesEveryPolicy_CaseInsensitively(string raw, TypeNameClash expected)
    {
        var (selection, onClash) = ProjectSessionEndpoints.ReadSelection([new ProjectTransferItemRequest(1, [2])], raw);

        Assert.Equal(expected, onClash);
        Assert.Null(Assert.Single(selection).OnClash);
    }

    [Fact]
    public void OnNameClash_WhenItIsNotGiven_DefaultsToRename()
    {
        var (_, onClash) = ProjectSessionEndpoints.ReadSelection([new ProjectTransferItemRequest(1, [2])], onNameClash: null);
        Assert.Equal(TypeNameClash.Rename, onClash);
    }

    [Fact]
    public void OnNameClash_WithAnUnknownName_IsRefusedAndNamesTheValidOnes()
    {
        // A 400 that says what the valid names are, rather than a 500 or — far worse — a silent
        // fall back to the default: "rename" and "merge" do different things to the user's project.
        var request = Assert.Throws<ProjectFileException>(
            () => ProjectSessionEndpoints.ReadSelection([new ProjectTransferItemRequest(1, [2])], "clobber"));
        Assert.Contains("Rename", request.Message);
        Assert.Contains("Merge", request.Message);
        Assert.Contains("Skip", request.Message);

        var perItem = Assert.Throws<ProjectFileException>(
            () => ProjectSessionEndpoints.ReadSelection([new ProjectTransferItemRequest(1, [2], "clobber")], null));
        Assert.Contains("clobber", perItem.Message);
    }

    [Fact]
    public void APerItemPolicy_IsCarriedAndAnAbsentOneStaysNull()
    {
        var (selection, onClash) = ProjectSessionEndpoints.ReadSelection(
            [
                new ProjectTransferItemRequest(1, [10, 11], "Merge"),
                new ProjectTransferItemRequest(2, [12]),
            ],
            "Skip");

        Assert.Equal(TypeNameClash.Skip, onClash);
        Assert.Equal(TypeNameClash.Merge, selection[0].OnClash);
        Assert.Equal([10L, 11L], selection[0].SourceResultIds);

        // Null rather than the resolved fallback: the repository takes the request's default for an
        // item that said nothing, so baking it in here would make a later change to that default
        // quietly not apply.
        Assert.Null(selection[1].OnClash);
    }

    [Fact]
    public void AnItemWithNoResults_IsASettingsOnlyPick_AndNoItemsAtAllIsAnEmptySelection()
    {
        var (settingsOnly, _) = ProjectSessionEndpoints.ReadSelection([new ProjectTransferItemRequest(1, null!)], null);
        Assert.Empty(Assert.Single(settingsOnly).SourceResultIds);

        // The session refuses an empty selection with a message; the parser must get that far
        // rather than throwing on the way.
        var (nothing, _) = ProjectSessionEndpoints.ReadSelection(null, null);
        Assert.Empty(nothing);
    }

    [Fact]
    public void TheContentsResponse_SerialisesCompletely_WithTheWireNamesTheClientReads()
    {
        var response = new ProjectContentsResponse(
            @"C:\bench\amp.tbench", 1, "GM70 amp", "the big one", 15,
            [
                new ProjectContentsTypeResponse(7, "FFTSpectrum", "Amp 1 kHz", "notes", 0,
                    [
                        new ProjectContentsResultResponse(31, "run 1", DateTimeOffset.Parse("2026-10-16T09:30:00Z"), null, 1, 65_536),
                        new ProjectContentsResultResponse(32, "sim", DateTimeOffset.Parse("2026-10-16T09:40:00Z"), "simulation", 1, 536_870_912),
                    ]),
                new ProjectContentsTypeResponse(8, "ThdVsFrequency", "THD vs f", null, 1, []),
            ]);

        string json = JsonSerializer.Serialize(response, SerializerOptions);
        using var parsed = JsonDocument.Parse(json);

        var root = parsed.RootElement;
        Assert.Equal(15, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("types").GetArrayLength());

        var first = root.GetProperty("types")[0];
        Assert.Equal("Amp 1 kHz", first.GetProperty("name").GetString());
        var results = first.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());

        // A whole number of bytes, and large enough to matter: half a gigabyte is the case this
        // figure exists for, and it must arrive as an integer the client can format itself.
        Assert.Equal(536_870_912L, results[1].GetProperty("approximateBytes").GetInt64());
        Assert.Equal("simulation", results[1].GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, results[0].GetProperty("source").ValueKind);
        Assert.Empty(root.GetProperty("types")[1].GetProperty("results").EnumerateArray());
    }

    [Fact]
    public void TheTransferResponse_SerialisesEveryOutcome_AsAName()
    {
        var response = new ProjectTransferResponse(
            [
                new ProjectTransferTypeResponse(1, "Amp", 11, "Amp (2)", TransferOutcome.Created.ToString()),
                new ProjectTransferTypeResponse(2, "THD vs f", 12, "THD vs f", TransferOutcome.Merged.ToString()),
                new ProjectTransferTypeResponse(3, "Noise", null, null, TransferOutcome.Skipped.ToString()),
            ],
            ResultsCopied: 4,
            CapturesCopied: 9);

        string json = JsonSerializer.Serialize(response, SerializerOptions);
        using var parsed = JsonDocument.Parse(json);

        var types = parsed.RootElement.GetProperty("types");
        Assert.Equal(["Created", "Merged", "Skipped"], types.EnumerateArray().Select(t => t.GetProperty("outcome").GetString()));
        Assert.Equal("Amp (2)", types[0].GetProperty("destinationName").GetString());
        Assert.Equal(JsonValueKind.Null, types[2].GetProperty("destinationTypeId").ValueKind);
        Assert.Equal(4, parsed.RootElement.GetProperty("resultsCopied").GetInt32());
        Assert.Equal(9, parsed.RootElement.GetProperty("capturesCopied").GetInt32());
    }
}
