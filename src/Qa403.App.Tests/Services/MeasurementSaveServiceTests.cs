using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

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

        string path = Path.Combine(Path.GetTempPath(), $"qa403-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var sweepRunner = new ThdSweepRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance);
            var responseRunner = new FrequencyResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance);
            var steppedRunner = new SteppedResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline, sweepRunner, responseRunner, steppedRunner,
                new ImpulseResponseRunner(
                    session, pipeline, new SqliteLoopbackReferenceStore(new MeasurementRepository(factory)),
                    NullLogger<ImpulseResponseRunner>.Instance),
                new ProjectRepository(factory), new MeasurementRepository(factory),
                Options.Create(new JsonOptions()));

            string json = saveService.BuildDefaultParametersJsonForNewType();
            using var doc = JsonDocument.Parse(json);

            Assert.True(doc.RootElement.GetProperty("generator").GetProperty("snapToBin").GetBoolean());
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

        string path = Path.Combine(Path.GetTempPath(), $"qa403-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            var sweepRunner = new ThdSweepRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance);
            var responseRunner = new FrequencyResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance);
            var steppedRunner = new SteppedResponseRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline, sweepRunner, responseRunner, steppedRunner,
                new ImpulseResponseRunner(
                    session, pipeline, new SqliteLoopbackReferenceStore(new MeasurementRepository(factory)),
                    NullLogger<ImpulseResponseRunner>.Instance),
                new ProjectRepository(factory), new MeasurementRepository(factory),
                Options.Create(new JsonOptions()));

            // The service must reject the save before ever touching the repository -- an
            // acquiring-but-otherwise-nonexistent measurementTypeId (1) would surface as a
            // foreign-key-shaped failure instead of this one if the guard clause ran too late.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                saveService.SaveAsync(measurementTypeId: 1, name: "test", notes: null, CancellationToken.None));
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
