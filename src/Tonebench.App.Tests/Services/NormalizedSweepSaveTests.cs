using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// A frequency-normalized sweep, run for real against the simulator, saved, and read back: the
/// stored result has to say which method it ran (a normalized point carries two sets of figures
/// and only <c>params_json</c> says which the run was about), and the normalized figures have to
/// come back as metric rows where they existed and be absent — not 0 — where they did not.
/// </summary>
public class NormalizedSweepSaveTests
{
    [Fact]
    public async Task SaveSweep_FrequencyNormalized_StoresTheMethodAndTheNormalizedFigures()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };
        var session = new DeviceSessionService(new SimulatedDevice(randomSeed: 1), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        string path = Path.Combine(Path.GetTempPath(), $"tonebench-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var jsonOptions = new JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            var projects = new ProjectRepository(factory);
            var measurements = new MeasurementRepository(factory);
            var sweepRunner = new ThdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline, sweepRunner,
                new ImdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance),
                new FrequencyResponseRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance),
                new SteppedResponseRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance),
                new CrosstalkRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                new OutputImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(session, pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Options.Create(jsonOptions));

            var request = new ThdSweepRequest(
                1000.0, 4000.0, 2, ThdSweepLevelMode.GeneratorVoltage, -20.0, ThdSweepLevelUnit.Dbv, 8.0,
                ProcessLeft: true, ProcessRight: false,
                4096, WindowType.Hann, AverageCount: 1, 48_000.0, MeasureNoiseFloor: false,
                ThdMethod: ThdMethod.FrequencyNormalized);

            await sweepRunner.StartAsync(request, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (sweepRunner.Running && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.Equal(ThdSweepPhase.Finished, sweepRunner.Status.Phase);

            long projectId = await projects.CreateProjectAsync("p", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                projectId, MeasurementSaveService.ThdVsFrequencyKind, "t", null, saveService.DefaultSweepParametersJson(), CancellationToken.None);
            long resultId = await saveService.SaveSweepAsync(typeId, "normalized", null, CancellationToken.None);

            var detail = await measurements.GetResultAsync(resultId, CancellationToken.None);
            Assert.NotNull(detail);
            var parameters = JsonSerializer.Deserialize<ThdVsFrequencyParameters>(detail!.ParamsJson, jsonOptions.SerializerOptions);
            Assert.NotNull(parameters);
            Assert.Equal("FrequencyNormalized", parameters!.ThdMethod);
            Assert.False(parameters.AdaptiveResolution);

            var stored = await measurements.GetSweepPointsAsync(resultId, CancellationToken.None);
            Assert.Equal(sweepRunner.Status.Points.Count, stored.Count);

            // The requested points have their normalized figures; the top of the extended grid does not.
            var bottom = stored.First().MetricsByChannel[0];
            Assert.True(bottom.ContainsKey("nthd_percent"));
            Assert.True(bottom.ContainsKey("nh2_percent"));
            Assert.InRange(bottom["nh2_percent"], 0.1, 2.0);

            var top = stored.Last().MetricsByChannel[0];
            Assert.True(top.ContainsKey("thd_percent"));
            Assert.False(top.ContainsKey("nthd_percent"));
            Assert.False(top.ContainsKey("nh2_percent"));
            Assert.False(top.ContainsKey("nd6plus_percent"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
