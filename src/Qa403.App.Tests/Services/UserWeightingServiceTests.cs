using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The user weighting tables' import/clear/load path: a QA40x-format file becomes a config
/// document and a live curve, a bad file changes nothing, and startup reloads what was stored.
/// </summary>
public sealed class UserWeightingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-weighting-{Guid.NewGuid():N}");
    private readonly ConfigRepository _config;
    private readonly UserWeightingSetting _setting = new();
    private readonly UserWeightingService _service;

    public UserWeightingServiceTests()
    {
        Directory.CreateDirectory(_dir);
        string configPath = Path.Combine(_dir, "config.db");
        _config = new ConfigRepository(new ConfigConnectionFactory(new Qa403ConfigDatabaseOptions { Path = configPath }));
        using var connection = SqliteConnectionFactory.OpenAtAsync(configPath, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        _service = new UserWeightingService(_config, _setting, NullLogger<UserWeightingService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string WriteFile(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // The tutorial's ITU-R 468 table, in the file's sign convention (positive = attenuation).
    private const string Itu468File = "31.5, 29.9\r\n63, 23.9\r\n100, 19.8\r\n200, 13.8\r\n400, 7.8\r\n800, 1.9\r\n1000, 0\r\n2000, -5.6\r\n3150, -9\r\n4000, -10.5\r\n5000, -11.7\r\n6300, -12.2\r\n7100, -12\r\n8000, -11.4\r\n9000, -10.1\r\n10000, -8.1\r\n12500, 0\r\n14000, 5.3\r\n16000, 11.7\r\n20000, 22.2\r\n31500, 42.7\r\n";

    [Fact]
    public async Task Import_ParsesAQa40xFileAndUpdatesTheLiveSetting()
    {
        string path = WriteFile("ITU-R 468.txt", Itu468File);

        var table = await _service.ImportAsync(1, path, CancellationToken.None);

        Assert.Equal("ITU-R 468", table.Name);
        Assert.Equal(21, table.Points.Count);
        Assert.Equal(Path.GetFullPath(path), table.SourcePath);
        Assert.Same(table, _setting.Get(1));
        Assert.Null(_setting.Get(2));

        // The live curve reads the file's sign the right way round: 6.3 kHz is +12.2 dB of gain.
        var curve = _setting.CurveFor(1);
        Assert.NotNull(curve);
        Assert.Equal(12.2, curve.GainDb(6300.0), 9);
        Assert.Equal(-29.9, curve.GainDb(31.5), 9);

        // And it is durable: a fresh setting loaded from the document has the same table.
        var reloaded = new UserWeightingSetting();
        await new UserWeightingService(_config, reloaded, NullLogger<UserWeightingService>.Instance).LoadAllAsync(CancellationToken.None);
        Assert.Equal(table.Points, reloaded.Get(1)!.Points);
        Assert.Equal("ITU-R 468", reloaded.Get(1)!.Name);
    }

    [Fact]
    public async Task Import_OfAMalformedFile_ThrowsFormatExceptionAndLeavesTheSlotUntouched()
    {
        await _service.ImportAsync(1, WriteFile("good.txt", "100, 1\n1000, 2\n"), CancellationToken.None);
        int versionBefore = _setting.Version;

        var ex = await Assert.ThrowsAsync<FormatException>(() =>
            _service.ImportAsync(1, WriteFile("bad.txt", "100, 1\nnot a row at all\n1000, 2\n"), CancellationToken.None));

        Assert.Contains("Line 2", ex.Message);
        Assert.Equal("good", _setting.Get(1)!.Name);
        Assert.Equal(versionBefore, _setting.Version);
        Assert.NotNull(await _config.GetAsync(ConfigRepository.UserWeightingKey(1), CancellationToken.None));
    }

    [Fact]
    public async Task Import_OfAMissingFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.ImportAsync(2, Path.Combine(_dir, "nope.txt"), CancellationToken.None));
        Assert.Null(_setting.Get(2));
    }

    [Fact]
    public async Task Clear_RemovesTheDocumentAndTheLiveTable()
    {
        await _service.ImportAsync(2, WriteFile("flat.txt", "10, 3\n100000, 3\n"), CancellationToken.None);
        Assert.NotNull(_setting.Get(2));

        await _service.ClearAsync(2, CancellationToken.None);

        Assert.Null(_setting.Get(2));
        Assert.Null(_setting.CurveFor(2));
        Assert.Null(await _config.GetAsync(ConfigRepository.UserWeightingKey(2), CancellationToken.None));
    }

    [Fact]
    public async Task Import_BumpsTheVersionThePipelineKeysItsCacheOn()
    {
        int before = _setting.Version;
        await _service.ImportAsync(1, WriteFile("a.txt", "10, 3\n100000, 3\n"), CancellationToken.None);
        int afterFirst = _setting.Version;
        await _service.ImportAsync(1, WriteFile("b.txt", "10, 6\n100000, 6\n"), CancellationToken.None);

        Assert.True(afterFirst > before);
        Assert.True(_setting.Version > afterFirst);
        Assert.Equal(-6.0, _setting.CurveFor(1)!.GainDb(1000.0), 9);
    }

    [Fact]
    public async Task LoadAll_WithADamagedDocument_LeavesTheSlotEmptyAndCarriesOn()
    {
        await _config.SetAsync(ConfigRepository.UserWeightingKey(1), "{ this is not json", CancellationToken.None);
        await _service.ImportAsync(2, WriteFile("ok.txt", "10, 1\n100000, 1\n"), CancellationToken.None);

        var fresh = new UserWeightingSetting();
        await new UserWeightingService(_config, fresh, NullLogger<UserWeightingService>.Instance).LoadAllAsync(CancellationToken.None);

        Assert.Null(fresh.Get(1));
        Assert.Equal("ok", fresh.Get(2)!.Name);
    }

    [Fact]
    public void WeightingCurve_SamplesTheCombinedCurveOnALogGrid_AndSaysWhenNothingIsWeighted()
    {
        var setting = new UserWeightingSetting();
        setting.Set(1, new UserWeightingTable("flat6", null, [new WeightingPoint(1.0, 6.0), new WeightingPoint(1_000_000.0, 6.0)], DateTimeOffset.UtcNow));

        var none = WeightingCurveResponse.Sample(WeightingSpec.None, setting, 5.0, 100_000.0, 400);
        Assert.False(none.Weighted);
        Assert.Empty(none.FreqHz);

        // An empty slot, checked, weighs nothing and is reported as unweighted.
        Assert.False(WeightingCurveResponse.Sample(new WeightingSpec(WeightingStandard.None, User2: true), setting, 5.0, 100_000.0, 10).Weighted);

        var curve = WeightingCurveResponse.Sample(new WeightingSpec(WeightingStandard.A, User1: true), setting, 10.0, 100_000.0, 401);
        Assert.True(curve.Weighted);
        Assert.Equal(401, curve.FreqHz.Length);
        Assert.Equal(10.0, curve.FreqHz[0], 9);
        Assert.Equal(100_000.0, curve.FreqHz[400], 6);
        Assert.Equal(1000.0, curve.FreqHz[200], 6);   // log-spaced: the middle sample is the geometric mean
        Assert.Equal(-6.0, curve.GainDb[200], 6);     // A is 0 dB at 1 kHz, the table takes 6 dB off
        Assert.All(curve.GainDb, g => Assert.True(double.IsFinite(g)));

        // A curve that vanishes at DC is clamped, not NaN or -Infinity, so the document stays JSON.
        var fromDc = WeightingCurveResponse.Sample(new WeightingSpec(WeightingStandard.A), null, 1e-9, 10.0, 3);
        Assert.Equal(WeightingCurveResponse.FloorDb, fromDc.GainDb[0]);
    }

    [Fact]
    public void TryMergeWeighting_PatchesOnlyWhatIsGiven()
    {
        var current = new WeightingSpec(WeightingStandard.A, User1: true);

        Assert.True(AcquisitionEndpoints.TryMergeWeighting(current, null, out var same, out _));
        Assert.Equal(current, same);

        Assert.True(AcquisitionEndpoints.TryMergeWeighting(current, new WeightingConfigRequest(User2: true), out var user2, out _));
        Assert.Equal(new WeightingSpec(WeightingStandard.A, true, true), user2);

        Assert.True(AcquisitionEndpoints.TryMergeWeighting(current, new WeightingConfigRequest("itur468", User1: false), out var standard, out _));
        Assert.Equal(new WeightingSpec(WeightingStandard.ItuR468, false, false), standard);

        Assert.False(AcquisitionEndpoints.TryMergeWeighting(current, new WeightingConfigRequest("B"), out _, out string error));
        Assert.Contains("WeightingStandard", error);
        Assert.Contains("RiaaRecord", error);
    }
}
