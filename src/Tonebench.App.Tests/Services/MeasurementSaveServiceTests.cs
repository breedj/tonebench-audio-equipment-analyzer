using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

public class MeasurementSaveServiceTests
{
    /// <summary>
    /// A brand-new measurement type (via "+ New measurement", which sends no staged config -- see
    /// NewMeasurementDialog.vue) must always get SnapToBin on, regardless of what the live session
    /// happens to be sitting on. Before this test, <see cref="MeasurementSaveService.BuildDefaultParametersJsonForNewType"/>
    /// only overrode the enabled channel when the generator was fully silent; SnapToBin was cloned
    /// verbatim from whatever the session's live <see cref="GeneratorConfig"/> had, so a device left
    /// with it off from an earlier experiment made every subsequent "+ New measurement" seed off as
    /// well -- off creeping in as the session's new normal instead of staying the one-off exception
    /// it's meant to be.
    /// </summary>
    [Fact]
    public async Task BuildDefaultParametersJsonForNewType_AlwaysForcesSnapToBinOn()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        // Mimics a device left with SnapToBin off from an earlier measurement -- the exact live
        // state a fresh "+ New measurement" must not inherit.
        var offTones = new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(offTones, ChannelGeneratorConfig.Off) { SnapToBin = false }, CancellationToken.None);

        string path = Path.Combine(Path.GetTempPath(), $"tonebench-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var sweepRunner = new ThdSweepRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance);
            var responseRunner = new FrequencyResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance);
            var steppedRunner = new SteppedResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline, sweepRunner,
                new ImdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance),
                responseRunner, steppedRunner,
                new CrosstalkRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                new OutputImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(
                    session, pipeline, new InMemoryLoopbackReferenceStore(),
                    NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(
                    session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                new ProjectRepository(factory), new MeasurementRepository(factory),
                Options.Create(new JsonOptions()));

            string json = saveService.BuildDefaultParametersJsonForNewType();
            using var doc = JsonDocument.Parse(json);

            Assert.True(doc.RootElement.GetProperty("generator").GetProperty("snapToBin").GetBoolean());

            // "Autofit when measurement done" is *off* for a new type, on every kind that has the
            // checkbox (Joost, 2026-10-17): a chart that refits its own axes the moment a run ends
            // moves the window out from under whoever set it. The FFT kind says so by leaving the
            // field null, which useChartDisplay.ts reads as off; the three sweep kinds write false,
            // because their panels restore the field they find and a configured type should say
            // what it does.
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("chartAutofitOnDone").ValueKind);
            using var sweepDoc = JsonDocument.Parse(saveService.DefaultSweepParametersJson());
            Assert.False(sweepDoc.RootElement.GetProperty("chartAutofitOnDone").GetBoolean());
            using var amplitudeDoc = JsonDocument.Parse(saveService.DefaultAmplitudeSweepParametersJson());
            Assert.False(amplitudeDoc.RootElement.GetProperty("chartAutofitOnDone").GetBoolean());
            // BuildCurrentParametersJson, which a save of an existing type goes through, still
            // reports whatever was staged (null here) rather than forcing either answer.
            using var currentDoc = JsonDocument.Parse(saveService.BuildCurrentParametersJson());
            Assert.Equal(JsonValueKind.Null, currentDoc.RootElement.GetProperty("chartAutofitOnDone").ValueKind);
        }
        finally
        {
            await pipeline.StopAsync();

            SqliteConnection.ClearAllPools();
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Fact]
    public async Task Save_WhileAcquiring_IsRejected()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48000, ChannelCount: 1, BlockFrameCount: DeviceSessionService.MinBlockFrameCount);
        await session.StartAcquisitionAsync(config, CancellationToken.None);

        string path = Path.Combine(Path.GetTempPath(), $"tonebench-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var sweepRunner = new ThdSweepRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance);
            var responseRunner = new FrequencyResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance);
            var steppedRunner = new SteppedResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline, sweepRunner,
                new ImdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance),
                responseRunner, steppedRunner,
                new CrosstalkRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                new OutputImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(
                    session, pipeline, new InMemoryLoopbackReferenceStore(),
                    NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(
                    session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                new ProjectRepository(factory), new MeasurementRepository(factory),
                Options.Create(new JsonOptions()));

            // The service must reject the save before ever touching the repository -- an
            // acquiring-but-otherwise-nonexistent measurementTypeId (1) would surface as a
            // foreign-key-shaped failure instead of this one if the guard clause ran too late.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                saveService.SaveAsync(measurementTypeId: 1, name: "test", notes: null, annotations: null, CancellationToken.None));
        }
        finally
        {
            await session.StopAcquisitionAsync();
            await pipeline.StopAsync();

            SqliteConnection.ClearAllPools();
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }
}
