using Microsoft.Data.Sqlite;
using Tonebench.Dsp;

namespace Tonebench.Data.Tests;

public class MeasurementRepositoryTests
{
    [Fact]
    public async Task Save_ThenReloadOnFreshConnection_TraceMatchesWithinQuantisationError()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var setupFactory = await TestDb.CreateMigratedFactoryAsync(path);
            var projectRepo = new ProjectRepository(setupFactory);
            long projectId = await projectRepo.CreateProjectAsync("GM70", null, CancellationToken.None);
            long typeId = await projectRepo.CreateTypeAsync(projectId, "FFTSpectrum", "type", null, "{}", CancellationToken.None);

            const int binCount = 32769; // one-sided length of a 65536-point real FFT
            var original = BuildSaveRequest(typeId, channels: 2, binCount, seed: 42);

            long resultId = await new MeasurementRepository(setupFactory).SaveResultAsync(original, CancellationToken.None);

            // A fresh factory and repository instance, standing in for a full app restart.
            SqliteConnection.ClearAllPools();
            var reloadedRepo = new MeasurementRepository(new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path }));

            var detail = await reloadedRepo.GetResultAsync(resultId, CancellationToken.None);
            Assert.NotNull(detail);

            for (int channel = 0; channel < 2; channel++)
            {
                var stored = await reloadedRepo.GetSpectrumAsync(detail!.CaptureId, channel, CancellationToken.None);
                Assert.NotNull(stored);

                double[] expected = original.Spectra[channel].Dbv;
                Assert.Equal(expected.Length, stored!.Dbv.Length);
                for (int i = 0; i < expected.Length; i++)
                {
                    double error = Math.Abs(stored.Dbv[i] - expected[i]);
                    Assert.True(error <= 0.005, $"channel {channel}, bin {i}: expected={expected[i]}, actual={stored.Dbv[i]}, error={error}");
                }
            }
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_StoresS1S2EnbwAndNorm()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var request = BuildSaveRequest(typeId, channels: 1, binCount: 100, seed: 1);

            long resultId = await measurementRepo.SaveResultAsync(request, CancellationToken.None);
            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);
            var stored = await measurementRepo.GetSpectrumAsync(detail!.CaptureId, 0, CancellationToken.None);

            var expected = request.Spectra[0];
            Assert.Equal(expected.Sums.S1, stored!.S1, 6);
            Assert.Equal(expected.Sums.S2, stored.S2, 6);
            Assert.Equal(expected.Sums.EnbwHz(expected.SampleRateHz), stored.EnbwHz, 6);
            Assert.Equal(SpectrumNorm.Amplitude, stored.Norm);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_TwoChannels_WritesOneRowPerChannel()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var request = BuildSaveRequest(typeId, channels: 2, binCount: 50, seed: 2);

            long resultId = await measurementRepo.SaveResultAsync(request, CancellationToken.None);
            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);

            Assert.Equal([0, 1], detail!.AvailableChannels);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_TwoChannels_WritesMetricsPerChannel()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var request = BuildSaveRequest(typeId, channels: 2, binCount: 50, seed: 3);

            long resultId = await measurementRepo.SaveResultAsync(request, CancellationToken.None);
            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);

            Assert.True(detail!.MetricsByChannel.ContainsKey(0));
            Assert.True(detail.MetricsByChannel.ContainsKey(1));
            Assert.NotEqual(detail.MetricsByChannel[0]["thd_percent"], detail.MetricsByChannel[1]["thd_percent"]);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_StoresHarmonicsAsMetricRows()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var request = BuildSaveRequest(typeId, channels: 1, binCount: 50, seed: 4);
            request = request with
            {
                Metrics =
                [
                    .. request.Metrics,
                    new MeasurementMetric(0, "h2_hz", 2000.0, "Hz"),
                    new MeasurementMetric(0, "h2_dbc", -60.0, "dBc"),
                ],
            };

            long resultId = await measurementRepo.SaveResultAsync(request, CancellationToken.None);
            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);

            Assert.Equal(2000.0, detail!.MetricsByChannel[0]["h2_hz"]);
            Assert.Equal(-60.0, detail.MetricsByChannel[0]["h2_dbc"]);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_StoresParamsSnapshotIndependentOfType()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, projectRepo) = await SetUpAsync(path, typeParamsJson: """{"fftSize":65536}""");
            var request = BuildSaveRequest(typeId, channels: 1, binCount: 10, seed: 5) with { ParamsJson = """{"fftSize":131072}""" };

            long resultId = await measurementRepo.SaveResultAsync(request, CancellationToken.None);

            // Changing the type's configured params after the fact must not affect the snapshot
            // already stored on the result.
            await projectRepo.UpdateTypeAsync(typeId, "type", null, """{"fftSize":262144}""", CancellationToken.None);

            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);
            Assert.Equal("""{"fftSize":131072}""", detail!.ParamsJson);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_WritesLexicallySortableUtcTimestamp()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            long resultId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 1, 10, seed: 6), CancellationToken.None);

            await using var connection = await factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ts FROM measurement_result WHERE id = @id;";
            command.Parameters.AddWithValue("@id", resultId);
            string ts = (string)(await command.ExecuteScalarAsync())!;

            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", ts);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Save_LeavesPcmNull()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            long resultId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 1, 10, seed: 7), CancellationToken.None);
            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);

            await using var connection = await factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pcm FROM capture WHERE id = @id;";
            command.Parameters.AddWithValue("@id", detail!.CaptureId);
            object? result = await command.ExecuteScalarAsync();

            Assert.True(result is null or DBNull);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task List_FiltersByProjectAndType()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var projectRepo = new ProjectRepository(factory);
            var measurementRepo = new MeasurementRepository(factory);

            long projectA = await projectRepo.CreateProjectAsync("A", null, CancellationToken.None);
            long projectB = await projectRepo.CreateProjectAsync("B", null, CancellationToken.None);
            long typeA1 = await projectRepo.CreateTypeAsync(projectA, "FFTSpectrum", "A1", null, "{}", CancellationToken.None);
            long typeA2 = await projectRepo.CreateTypeAsync(projectA, "FFTSpectrum", "A2", null, "{}", CancellationToken.None);
            long typeB1 = await projectRepo.CreateTypeAsync(projectB, "FFTSpectrum", "B1", null, "{}", CancellationToken.None);

            await measurementRepo.SaveResultAsync(BuildSaveRequest(typeA1, 1, 10, seed: 1, name: "a1-result"), CancellationToken.None);
            await measurementRepo.SaveResultAsync(BuildSaveRequest(typeA2, 1, 10, seed: 2, name: "a2-result"), CancellationToken.None);
            await measurementRepo.SaveResultAsync(BuildSaveRequest(typeB1, 1, 10, seed: 3, name: "b1-result"), CancellationToken.None);

            var byProjectA = await measurementRepo.ListResultsAsync(new MeasurementFilter(ProjectId: projectA), CancellationToken.None);
            Assert.Equal(2, byProjectA.Count);
            Assert.DoesNotContain(byProjectA, r => r.Name == "b1-result");

            var byTypeA1 = await measurementRepo.ListResultsAsync(new MeasurementFilter(MeasurementTypeId: typeA1), CancellationToken.None);
            var onlyResult = Assert.Single(byTypeA1);
            Assert.Equal("a1-result", onlyResult.Name);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task List_FiltersByDateRange()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });

            long oldId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 1, 10, seed: 1, name: "old"), CancellationToken.None);
            long newId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 1, 10, seed: 2, name: "new"), CancellationToken.None);
            await SetResultTimestampAsync(factory, oldId, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await SetResultTimestampAsync(factory, newId, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var filtered = await measurementRepo.ListResultsAsync(
                new MeasurementFilter(From: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                CancellationToken.None);

            Assert.Contains(filtered, r => r.Name == "new");
            Assert.DoesNotContain(filtered, r => r.Name == "old");
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task List_OrdersByTimestampDescending()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });

            long firstId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 1, 10, seed: 1, name: "first"), CancellationToken.None);
            long secondId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 1, 10, seed: 2, name: "second"), CancellationToken.None);
            await SetResultTimestampAsync(factory, firstId, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await SetResultTimestampAsync(factory, secondId, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var results = await measurementRepo.ListResultsAsync(new MeasurementFilter(), CancellationToken.None);

            Assert.Equal(["second", "first"], results.Select(r => r.Name));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Delete_RemovesResultCapturesSpectraAndMetrics()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (measurementRepo, typeId, _) = await SetUpAsync(path);
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            long resultId = await measurementRepo.SaveResultAsync(BuildSaveRequest(typeId, 2, 10, seed: 8), CancellationToken.None);
            var detail = await measurementRepo.GetResultAsync(resultId, CancellationToken.None);

            bool deleted = await measurementRepo.DeleteResultAsync(resultId, CancellationToken.None);

            Assert.True(deleted);
            Assert.Null(await measurementRepo.GetResultAsync(resultId, CancellationToken.None));

            await using var connection = await factory.OpenAsync(CancellationToken.None);
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, $"SELECT COUNT(*) FROM capture WHERE id = {detail!.CaptureId};"));
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, $"SELECT COUNT(*) FROM spectrum WHERE capture_id = {detail.CaptureId};"));
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, $"SELECT COUNT(*) FROM metric WHERE capture_id = {detail.CaptureId};"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static async Task<(MeasurementRepository MeasurementRepo, long TypeId, ProjectRepository ProjectRepo)> SetUpAsync(
        string path, string typeParamsJson = "{}")
    {
        var factory = await TestDb.CreateMigratedFactoryAsync(path);
        var projectRepo = new ProjectRepository(factory);
        var measurementRepo = new MeasurementRepository(factory);

        long projectId = await projectRepo.CreateProjectAsync("GM70", null, CancellationToken.None);
        long typeId = await projectRepo.CreateTypeAsync(projectId, "FFTSpectrum", "type", null, typeParamsJson, CancellationToken.None);

        return (measurementRepo, typeId, projectRepo);
    }

    private static MeasurementSaveRequest BuildSaveRequest(long typeId, int channels, int binCount, int seed, string name = "result")
    {
        var random = new Random(seed);
        var spectra = new List<StoredSpectrumInput>();
        var metrics = new List<MeasurementMetric>();

        for (int channel = 0; channel < channels; channel++)
        {
            var dbv = new double[binCount];
            for (int i = 0; i < binCount; i++)
                dbv[i] = random.NextDouble() * 220.0 - 200.0; // realistic dBV range

            spectra.Add(new StoredSpectrumInput(
                channel, WindowType.Hann, new WindowSums(S1: 32768.0, S2: 24576.0, Length: 65536),
                SampleRateHz: 48000.0, BinSizeHz: 48000.0 / 65536.0, SpectrumNorm.Amplitude, dbv));

            metrics.Add(new MeasurementMetric(channel, "thd_percent", 1.0 + channel, "%"));
            metrics.Add(new MeasurementMetric(channel, "thd_n_percent", 1.1 + channel, "%"));
            metrics.Add(new MeasurementMetric(channel, "snr_db", 90.0 + channel, "dB"));
            metrics.Add(new MeasurementMetric(channel, "fundamental_hz", 1000.0, "Hz"));
        }

        return new MeasurementSaveRequest(
            typeId, name, "notes", """{"fftSize":65536}""",
            "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
            SampleRateHz: 48000.0, FftSize: 65536, Channels: channels,
            InRangeDbv: 12.0, OutRangeDbv: 8.0, GenJson: """{"left":{"enabled":true}}""",
            spectra, metrics);
    }

    private static async Task SetResultTimestampAsync(SqliteConnectionFactory factory, long resultId, DateTimeOffset ts)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE measurement_result SET ts = @ts WHERE id = @id;";
        command.Parameters.AddWithValue("@ts", TonebenchTimestamp.FormatUtc(ts));
        command.Parameters.AddWithValue("@id", resultId);
        await command.ExecuteNonQueryAsync();
    }
}
