using Microsoft.Data.Sqlite;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Endpoints;
using Tonebench.App.Json;
using Tonebench.Data;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The two configuration documents behind Configuration ▸ Diagnostics, and the one decision in them
/// that is a judgement rather than a mechanism: detailed logging is <b>on</b> out of the box.
/// </summary>
public sealed class DiagnosticsConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-diagcfg-{Guid.NewGuid():N}");
    private readonly string _configPath;

    public DiagnosticsConfigTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.db");
        using var connection = SqliteConnectionFactory
            .OpenAtAsync(_configPath, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix)
            .RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private ConfigRepository Repository() =>
        new(new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = _configPath }));

    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            return options.SerializerOptions;
        }
    }

    /// <summary>
    /// A fresh install logs in detail. Reversed on 2026-10-03: the switch was written for a tester
    /// who is <i>told</i> to turn it on before reproducing a fault, and the faults here are
    /// intermittent — a dropped chunk or a stalled pump is reported after the run that showed it,
    /// when an Information-floor log can no longer answer for it. Pinned because it is a decision,
    /// not an implementation detail: nothing else fails if the default flips back.
    /// </summary>
    [Fact]
    public void DetailedLogging_IsOnByDefault()
    {
        Assert.True(DiagnosticLoggingConfig.Default.Enabled);
        Assert.True(new DiagnosticLoggingConfig().Enabled);
    }

    /// <summary>And the recording is off by default, for the opposite reason: it writes hundreds of megabytes.</summary>
    [Fact]
    public void StreamRecording_IsOffByDefault()
    {
        Assert.False(StreamRecordingConfig.Default.Enabled);
        Assert.Equal(256, StreamRecordingConfig.Default.MaxFileMegabytes);
    }

    [Fact]
    public async Task StreamRecording_RoundTripsThroughTheDatabase()
    {
        var repo = Repository();
        var config = new StreamRecordingConfig(Enabled: true, MaxFileMegabytes: 512);

        await repo.SetAsync(
            ConfigRepository.StreamRecordingKey,
            JsonSerializer.Serialize(config, SerializerOptions),
            CancellationToken.None);

        var back = await ConfigEndpoints.ReadStreamRecordingAsync(repo, CancellationToken.None);
        Assert.Equal(config, back);
    }

    /// <summary>
    /// A document this build cannot read falls back rather than failing, and an out-of-range cap is
    /// clamped rather than refused — the same contract the capture delay has, and for the same
    /// reason: a hand-edited document is not a reason for the app not to start.
    /// </summary>
    [Theory]
    [InlineData("{\"enabled\":true,\"maxFileMegabytes\":1}", true, ConfigEndpoints.MinRecordingCapMegabytes)]
    [InlineData("{\"enabled\":true,\"maxFileMegabytes\":99999}", true, ConfigEndpoints.MaxRecordingCapMegabytes)]
    [InlineData("not json at all", false, 256)]
    [InlineData("", false, 256)]
    public async Task StreamRecording_ReadsADamagedOrOutOfRangeDocumentSafely(
        string stored, bool expectedEnabled, int expectedCap)
    {
        var repo = Repository();
        await repo.SetAsync(ConfigRepository.StreamRecordingKey, stored, CancellationToken.None);

        var config = await ConfigEndpoints.ReadStreamRecordingAsync(repo, CancellationToken.None);

        Assert.Equal(expectedEnabled, config.Enabled);
        Assert.Equal(expectedCap, config.MaxFileMegabytes);
    }
}
