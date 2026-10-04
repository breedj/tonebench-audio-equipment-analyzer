using Microsoft.Data.Sqlite;
using Tonebench.App.Endpoints;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Remembering which analyzer was last selected. The reading is what is pinned here rather than
/// the writing: a document that will not parse, or names a kind this build no longer has, must
/// leave the app starting on the QA403 rather than refusing to start at all.
/// </summary>
public sealed class AnalyzerKindMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tonebench-kind-{Guid.NewGuid():N}");
    private readonly ConfigRepository _config;

    public AnalyzerKindMemoryTests()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config.db");
        _config = new ConfigRepository(new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = path }));
        using var connection = SqliteConnectionFactory.OpenAtAsync(path, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private Task StoreAsync(string json) =>
        _config.SetAsync(ConfigRepository.AnalyzerKindKey, json, CancellationToken.None);

    private Task<DeviceKind> ReadAsync() =>
        ConfigEndpoints.ReadAnalyzerKindAsync(_config, CancellationToken.None);

    [Fact]
    public async Task WithNothingStored_TheAppComesUpOnTheQa403()
    {
        Assert.Equal(DeviceKind.Hardware, await ReadAsync());
    }

    [Theory]
    [InlineData(DeviceKind.Hardware)]
    [InlineData(DeviceKind.Simulated)]
    [InlineData(DeviceKind.SoundCard)]
    public async Task EveryKind_SurvivesTheRoundTripTheEndpointWrites(DeviceKind kind)
    {
        // Exactly what POST /api/device/kind stores, through the app's own source-generated
        // context -- a kind that serialized here and read back as something else would put the
        // app on an analyzer nobody chose, silently.
        await StoreAsync(System.Text.Json.JsonSerializer.Serialize(
            new AnalyzerKindConfig(kind.ToString()), AppJsonContext.Default.AnalyzerKindConfig));

        Assert.Equal(kind, await ReadAsync());
    }

    /// <summary>
    /// The reason the document holds a name and not the enum's number: a build that reorders
    /// DeviceKind would silently reinterpret a stored number, where a stored name either matches
    /// or falls back.
    /// </summary>
    [Theory]
    [InlineData("""{"kind":"Nonsense"}""")]
    [InlineData("""{"kind":""}""")]
    [InlineData("""{"kind":null}""")]
    [InlineData("""{"kind":"7"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task AnUnreadableDocument_FallsBackToTheQa403_RatherThanStoppingTheApp(string json)
    {
        await StoreAsync(json);
        Assert.Equal(DeviceKind.Hardware, await ReadAsync());
    }

    /// <summary>
    /// A number that happens to name a kind is still refused. `Enum.TryParse` accepts "1" as the
    /// member with value 1, which is exactly the reinterpretation storing a name exists to avoid.
    /// </summary>
    [Fact]
    public async Task ANumericKind_IsNotAcceptedAsAName()
    {
        await StoreAsync("""{"kind":"1"}""");
        Assert.Equal(DeviceKind.Hardware, await ReadAsync());
    }

    [Fact]
    public async Task TheNameIsReadWithoutRegardToCase()
    {
        await StoreAsync("""{"kind":"soundcard"}""");
        Assert.Equal(DeviceKind.SoundCard, await ReadAsync());
    }
}
