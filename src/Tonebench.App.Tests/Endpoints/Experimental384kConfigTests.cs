using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Json;
using Tonebench.Data;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The configuration document behind Configuration ▸ Acquisition ▸ "Allow 384 kHz sample rate
/// (experimental)". What is pinned is the contract the client relies on: the round trip through the
/// app's own JSON options, and that anything unreadable reads back as <b>off</b> — which for this
/// setting is not merely a tidy default. On, the analyzer offers a rate whose generator is refused
/// and whose usefulness is narrow; a damaged document must not be able to turn that on by itself.
/// </summary>
public class Experimental384kConfigTests
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
        var config = new Experimental384kConfig(Enabled: true);

        string json = JsonSerializer.Serialize(config, SerializerOptions);
        Assert.Contains("\"enabled\":true", json);

        var back = JsonSerializer.Deserialize<Experimental384kConfig>(json, SerializerOptions);
        Assert.NotNull(back);
        Assert.Equal(config, back);
    }

    [Fact]
    public void TheDefaultIsOff()
    {
        Assert.False(Experimental384kConfig.Default.Enabled);
        Assert.False(new Experimental384kConfig().Enabled);
    }

    /// <summary>
    /// A document written by a build that did not have this field, i.e. the upgrade case: the record
    /// has one init-only property with a default, so an empty object is "off" rather than a throw.
    /// </summary>
    [Fact]
    public void AnEmptyDocumentIsOff()
    {
        var back = JsonSerializer.Deserialize<Experimental384kConfig>("{}", SerializerOptions);

        Assert.NotNull(back);
        Assert.False(back.Enabled);
    }
}
