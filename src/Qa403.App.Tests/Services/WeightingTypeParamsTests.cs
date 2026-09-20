using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

/// <summary>
/// How the weighting, the DUT gain and the chart unit ride through a measurement type: recorded
/// from the live pipeline options and the staged client values, restored (or cleared) when a type
/// is applied, and written out with a saved capture -- the weighting from the snapshot that was
/// captured, the noise figures as metric rows.
/// </summary>
public class WeightingTypeParamsTests
{
    [Fact]
    public async Task BuildCurrentParametersJson_CarriesWeightingDutGainAndChartUnit()
    {
        await WithHarnessAsync(async h =>
        {
            h.UserWeighting.Set(1, new UserWeightingTable("itu468", null, [new WeightingPoint(100.0, 19.8), new WeightingPoint(6300.0, -12.2)], DateTimeOffset.UtcNow));
            h.Pipeline.Options = h.Pipeline.Options with { Weighting = new WeightingSpec(WeightingStandard.A, User1: true) };

            var parameters = Deserialize(h.SaveService.BuildCurrentParametersJson(Staged(dutGainDb: 20.0, chartUnit: "dBVPerRootHz")));
            Assert.Equal("A", parameters.Weighting);
            Assert.True(parameters.WeightingUser1);
            Assert.False(parameters.WeightingUser2);
            Assert.Equal("itu468", parameters.WeightingUser1Name);
            Assert.Null(parameters.WeightingUser2Name);
            Assert.Equal(20.0, parameters.DutGainDb);
            Assert.Equal("dBVPerRootHz", parameters.ChartUnit);

            // Nothing staged: the client-side values are absent, the weighting is still the pipeline's.
            var bare = Deserialize(h.SaveService.BuildCurrentParametersJson());
            Assert.Equal("A", bare.Weighting);
            Assert.Null(bare.DutGainDb);
            Assert.Null(bare.ChartUnit);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Apply_RestoresTheWeightingOntoThePipeline()
    {
        await WithHarnessAsync(async h =>
        {
            h.Pipeline.Options = h.Pipeline.Options with { Weighting = new WeightingSpec(WeightingStandard.CcirArm, User2: true) };
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson(Staged(dutGainDb: 6.0, chartUnit: "dBV")));
            h.Pipeline.Options = h.Pipeline.Options with { Weighting = WeightingSpec.None };

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            Assert.Equal(new WeightingSpec(WeightingStandard.CcirArm, User2: true), h.Pipeline.Options.Weighting);
            Assert.Equal("CcirArm", applied.Weighting);
            Assert.False(applied.WeightingUser1);
            Assert.True(applied.WeightingUser2);
            Assert.Equal(6.0, applied.DutGainDb);
            Assert.Equal("dBV", applied.ChartUnit);
        });
    }

    [Fact]
    public async Task Apply_OfATypeWithoutWeighting_ClearsIt()
    {
        await WithHarnessAsync(async h =>
        {
            // A type written by a build that had no weighting: its params_json has no Weighting field.
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson());
            h.Pipeline.Options = h.Pipeline.Options with { Weighting = new WeightingSpec(WeightingStandard.A, User1: true) };

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            Assert.Equal(WeightingSpec.None, h.Pipeline.Options.Weighting);
            Assert.Equal("None", applied.Weighting);
            Assert.Null(applied.DutGainDb);
        });
    }

    [Fact]
    public async Task Save_WritesTheNoiseRowsAndTheWeightingThatProducedTheSnapshot()
    {
        await WithHarnessAsync(async h =>
        {
            const double sampleRateHz = 48_000.0;
            h.Pipeline.Options = h.Pipeline.Options with
            {
                FftSize = 8192,
                Averaging = AveragingMode.Off,
                SingleShot = true,
                Weighting = new WeightingSpec(WeightingStandard.A),
            };
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson(Staged(dutGainDb: 12.0, chartUnit: "dBVPerRootHzInput")));

            double toneHz = CoherentFrequency.Nearest(1000.0, sampleRateHz, 8192).FrequencyHz;
            await h.Session.ConfigureGeneratorAsync(
                new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(toneHz, -10.0)]), ChannelGeneratorConfig.Off) { SnapToBin = false },
                CancellationToken.None);
            await h.Session.StartAcquisitionAsync(
                new AcquisitionConfig(sampleRateHz, ChannelCount: 1, BlockFrameCount: DeviceSessionService.MinBlockFrameCount),
                CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!h.Pipeline.SingleShotComplete)
                    await Task.Delay(20, timeout.Token);
            }
            finally
            {
                await h.Session.StopAcquisitionAsync();
            }

            var snapshot = h.Pipeline.LatestSnapshots().Single();
            Assert.NotNull(snapshot.Noise);
            Assert.Equal(new WeightingSpec(WeightingStandard.A), snapshot.Weighting);

            // The weighting is changed *after* the capture, while stopped: the result must still say
            // what the trace was captured under.
            h.Pipeline.Options = h.Pipeline.Options with { Weighting = new WeightingSpec(WeightingStandard.RiaaRecord) };

            long resultId = await h.SaveService.SaveAsync(typeId, "weighted capture", null, CancellationToken.None);
            var detail = await h.Measurements.GetResultAsync(resultId, CancellationToken.None);

            Assert.NotNull(detail);
            var metrics = detail.MetricsByChannel[0];
            Assert.Equal(snapshot.Noise.BandRmsDbv, metrics["band_rms_dbv"], 5);
            Assert.Equal(snapshot.Noise.NoiseDensityDbvPerRootHz, metrics["noise_density_dbv_per_root_hz"], 5);

            var parameters = Deserialize(detail.ParamsJson);
            Assert.Equal("A", parameters.Weighting);
            Assert.False(parameters.WeightingUser1);
            // The staged client values are not part of a save (a result is saved from the stopped
            // pipeline, with no staged config), so the result takes the type's: the gain the "Noise in"
            // figure was shown against.
            Assert.Equal(12.0, parameters.DutGainDb);
            Assert.Equal("dBVPerRootHzInput", parameters.ChartUnit);
        });
    }

    private static StagedMeasurementConfig Staged(double dutGainDb, string chartUnit) => new(
        48_000.0, 1, 0.0, true,
        new GeneratorChannelRequest(true, [new GeneratorToneRequest(1000.0, -10.0)]),
        new GeneratorChannelRequest(false, []),
        true)
    {
        DutGainDb = dutGainDb,
        ChartUnit = chartUnit,
    };

    private static FftSpectrumParameters Deserialize(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<FftSpectrumParameters>(json, options)!;
    }

    private sealed class Harness(
        DeviceSessionService session, SpectrumPipeline pipeline, MeasurementSaveService saveService,
        ProjectRepository projects, MeasurementRepository measurements, UserWeightingSetting userWeighting)
    {
        public DeviceSessionService Session { get; } = session;
        public SpectrumPipeline Pipeline { get; } = pipeline;
        public MeasurementSaveService SaveService { get; } = saveService;
        public MeasurementRepository Measurements { get; } = measurements;
        public UserWeightingSetting UserWeighting { get; } = userWeighting;

        private long? _projectId;

        public async Task<long> CreateFftTypeAsync(string paramsJson)
        {
            _projectId ??= await projects.CreateProjectAsync("weighting", null, CancellationToken.None);
            return await projects.CreateTypeAsync(_projectId.Value, "FFTSpectrum", "fft", null, paramsJson, CancellationToken.None);
        }
    }

    private static async Task WithHarnessAsync(Func<Harness, Task> body)
    {
        var userWeighting = new UserWeightingSetting();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance, userWeighting: userWeighting);
        var session = new DeviceSessionService(new SimulatedDevice(randomSeed: 1), pipeline, NullLogger<DeviceSessionService>.Instance);
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
            var saveService = new MeasurementSaveService(
                session, pipeline,
                new ThdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance),
                new ImdSweepRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance),
                new FrequencyResponseRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance),
                new SteppedResponseRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance),
                new ImpulseResponseRunner(session, pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Options.Create(jsonOptions), userWeighting);

            await body(new Harness(session, pipeline, saveService, projects, measurements, userWeighting));
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
}
