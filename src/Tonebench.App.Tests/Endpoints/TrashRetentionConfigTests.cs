using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Json;
using Tonebench.Data;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The configuration document behind the Trash view's "Keep deleted items for … days".
/// </summary>
public class TrashRetentionConfigTests
{
    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            return options.SerializerOptions;
        }
    }

    [Fact]
    public void RoundTrip()
    {
        var config = new TrashRetentionConfig(30);

        string json = JsonSerializer.Serialize(config, SerializerOptions);
        Assert.Contains("\"days\":30", json);

        var back = JsonSerializer.Deserialize<TrashRetentionConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.Equal(config, back);
    }

    /// <summary>
    /// The upgrade case every existing bench takes: nothing stored, or a document from a build
    /// without the field, is a fortnight rather than a throw or a zero — and zero would mean
    /// purging the trash the moment anything landed in it.
    /// </summary>
    [Fact]
    public void AnEmptyDocumentIsTheDefaultFortnight()
    {
        var back = JsonSerializer.Deserialize<TrashRetentionConfig>("{}", SerializerOptions);

        Assert.NotNull(back);
        Assert.Equal(14, back.Days);
        Assert.True(back.IsValid);
    }

    /// <summary>
    /// The primary constructor's default cannot name <c>DefaultDays</c> (a const declared in its own
    /// body), so the two are written out separately. This is what keeps them the same number.
    /// </summary>
    [Fact]
    public void TheDefaultMatchesTheConstant()
    {
        Assert.Equal(TrashRetentionConfig.DefaultDays, TrashRetentionConfig.Default.Days);
        Assert.Equal(TrashRetentionConfig.DefaultDays, new TrashRetentionConfig().Days);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(3650)]
    public void AUsableRetentionIsValid(int days) => Assert.True(new TrashRetentionConfig(days).IsValid);

    /// <summary>
    /// Zero is deliberately <i>out</i> of range: the trash is a safety net for a misclick, and a
    /// retention of nothing is not a shorter net but no net at all — the delete would be as
    /// irreversible as it was before the trash existed, with the UI still promising a restore.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public void AnUnusableRetentionIsRejected(int days) => Assert.False(new TrashRetentionConfig(days).IsValid);
}
