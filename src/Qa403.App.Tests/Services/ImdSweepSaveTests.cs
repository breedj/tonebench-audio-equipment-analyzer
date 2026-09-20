using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

/// <summary>
/// An IMD sweep run for real against the simulator, saved, and read back through the sweep-point
/// path every other sweep uses: one capture per level carrying the IMD metric rows, and a
/// params_json that says which pair and which convention produced them.
/// </summary>
public class ImdSweepSaveTests
{
    [Fact]
    public async Task SaveImdSweep_WritesOneCapturePerLevelWithTheImdMetrics()
    {
        const int fftSize = 4096;
        const double sampleRateHz = 48_000.0;
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = fftSize, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with { Dut = SimulatedDutModel.ClassAbCrossover };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        string path = Path.Combine(Path.GetTempPath(), $"qa403-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var jsonOptions = new JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            var projects = new ProjectRepository(factory);
            var measurements = new MeasurementRepository(factory);
            var imdRunner = new ImdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline,
                new ThdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance),
                imdRunner,
                new FrequencyResponseRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance),
                new SteppedResponseRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance),
                new CrosstalkRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                new OutputImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(session, pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Options.Create(jsonOptions));

            // Bins 11 and 601 at 4096/48k -- odd, so the coherent snap leaves them where they are.
            var request = new ImdSweepRequest(
                IntermodulationPreset.Custom, 11 * sampleRateHz / fftSize, 601 * sampleRateHz / fftSize, 4.0,
                IntermodulationMethod.Smpte, 5, 0.05, 0.4, 1, 8.0, true, false, 1, true,
                FftSize: fftSize, SampleRateHz: sampleRateHz);

            await imdRunner.StartAsync(request, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (imdRunner.Running && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.Equal(ThdSweepPhase.Finished, imdRunner.Status.Phase);
            Assert.Equal(4, imdRunner.Status.Points.Count);

            long projectId = await projects.CreateProjectAsync("p", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                projectId, MeasurementSaveService.ImdVsAmplitudeKind, "t", null, saveService.DefaultImdSweepParametersJson(), CancellationToken.None);
            long resultId = await saveService.SaveImdSweepAsync(typeId, "imd", null, CancellationToken.None);

            var detail = await measurements.GetResultAsync(resultId, CancellationToken.None);
            Assert.NotNull(detail);
            var parameters = JsonSerializer.Deserialize<ImdVsAmplitudeParameters>(detail!.ParamsJson, jsonOptions.SerializerOptions);
            Assert.NotNull(parameters);
            Assert.Equal("Smpte", parameters!.Method);
            Assert.Equal("Custom", parameters.Preset);
            Assert.Equal(request.ToneLowHz, parameters.ToneLowHz);
            Assert.Equal(4.0, parameters.LowToHighRatio);
            Assert.Equal(5, parameters.MaxOrder);
            Assert.Equal(0.05, parameters.StartLevelVolt);
            Assert.NotNull(parameters.CaptureDelayMs);

            var stored = await measurements.GetSweepPointsAsync(resultId, CancellationToken.None);
            Assert.Equal(4, stored.Count);

            var runPoints = imdRunner.Status.Points;
            for (int i = 0; i < stored.Count; i++)
            {
                var m = stored[i].MetricsByChannel[0];
                Assert.Equal(runPoints[i].ImdPercent, m["imd_percent"], 9);
                Assert.Equal(runPoints[i].GeneratorSineEquivalentDbv, m["generator_dbv"], 9);
                Assert.Equal(runPoints[i].SineEquivalentDbv, m["fundamental_dbv"], 9);
                Assert.Equal(runPoints[i].SineEquivalentDbv, m["imd_sine_equivalent_dbv"], 9);
                Assert.Equal(runPoints[i].OrderPercent[0], m["im2_percent"], 9);
                Assert.True(m.ContainsKey("power_watt"));
                Assert.True(m.ContainsKey("noise_percent"));
                Assert.False(m.ContainsKey("thd_percent"));
                Assert.Equal(request.ToneLowHz, stored[i].FreqHz, 6);
                Assert.True(stored[i].PreviewsByChannel?.ContainsKey(0) == true);
            }

            // Level-stepped: the generator level differs from point to point.
            Assert.True(stored[0].MetricsByChannel[0]["generator_volt"] < stored[^1].MetricsByChannel[0]["generator_volt"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
