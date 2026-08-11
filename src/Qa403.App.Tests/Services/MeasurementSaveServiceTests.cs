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

            var saveService = new MeasurementSaveService(
                session, pipeline, new ProjectRepository(factory), new MeasurementRepository(factory),
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
