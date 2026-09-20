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
/// How the FFT screen's two-tone IMD spec rides through a measurement type: recorded from the live
/// pipeline options plus the staged template name, restored onto the pipeline (or cleared) when a
/// type is applied, and written out as metric rows when a capture is saved.
/// </summary>
public class IntermodulationTypeParamsTests
{
    private static readonly IntermodulationSpec SmpteSpec = new(58.59, 7001.95, IntermodulationMethod.Smpte, 5);

    [Fact]
    public async Task BuildCurrentParametersJson_CarriesThePipelinesSpecAndTheStagedPreset()
    {
        await WithHarnessAsync(async h =>
        {
            h.Pipeline.Options = h.Pipeline.Options with { Intermodulation = SmpteSpec };

            var parameters = Deserialize(h.SaveService.BuildCurrentParametersJson(StagedWith("Smpte")));
            Assert.Equal("Smpte", parameters.ImdPreset);
            Assert.Equal("Smpte", parameters.ImdMethod);
            Assert.Equal(5, parameters.ImdMaxOrder);
            Assert.Equal(58.59, parameters.ImdToneLowHz);
            Assert.Equal(7001.95, parameters.ImdToneHighHz);
            Assert.Null(parameters.ImdMaxProductFreqHz);

            // A spec with nothing staged is a typed pair; no spec is no IMD, whatever was staged.
            Assert.Equal("Custom", Deserialize(h.SaveService.BuildCurrentParametersJson()).ImdPreset);
            h.Pipeline.Options = h.Pipeline.Options with { Intermodulation = null };
            var without = Deserialize(h.SaveService.BuildCurrentParametersJson(StagedWith("Smpte")));
            Assert.Null(without.ImdPreset);
            Assert.Null(without.ImdToneLowHz);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Apply_RestoresTheIntermodulationSpecOntoThePipeline()
    {
        await WithHarnessAsync(async h =>
        {
            h.Pipeline.Options = h.Pipeline.Options with { Intermodulation = SmpteSpec with { MaxProductFreqHz = 30_000.0 } };
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson(StagedWith("Smpte")));
            h.Pipeline.Options = h.Pipeline.Options with { Intermodulation = null };

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            Assert.Equal(SmpteSpec with { MaxProductFreqHz = 30_000.0 }, h.Pipeline.Options.Intermodulation);
            Assert.Equal("Smpte", applied.ImdPreset);
            Assert.Equal("Smpte", applied.ImdMethod);
            Assert.Equal(5, applied.ImdMaxOrder);
        });
    }

    [Fact]
    public async Task Apply_OfATypeWithoutImd_ClearsTheSpec()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson());
            h.Pipeline.Options = h.Pipeline.Options with { Intermodulation = SmpteSpec };

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            Assert.Null(h.Pipeline.Options.Intermodulation);
            Assert.Null(applied.ImdPreset);
        });
    }

    [Fact]
    public async Task Save_WithAnIntermodulationSpec_WritesTheImdMetricRows()
    {
        await WithHarnessAsync(async h =>
        {
            const double sampleRateHz = 48_000.0;
            const int fftSize = 8192;
            var preset = IntermodulationPresets.Resolve(IntermodulationPreset.Smpte);
            double lowHz = CoherentFrequency.Nearest(preset.LowHz, sampleRateHz, fftSize).FrequencyHz;
            double highHz = CoherentFrequency.Nearest(preset.HighHz, sampleRateHz, fftSize).FrequencyHz;
            var (lowDbv, highDbv) = IntermodulationPresets.ToneLevelsDbv(-6.0, preset.LowToHighRatio);

            h.Pipeline.Options = h.Pipeline.Options with
            {
                FftSize = fftSize,
                Averaging = AveragingMode.Off,
                SingleShot = true,
                FundamentalMinSearchHz = 30.0,
                Intermodulation = new IntermodulationSpec(lowHz, highHz, preset.Method, preset.DefaultMaxOrder),
            };
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson(StagedWith("Smpte")));

            await h.Session.ConfigureGeneratorAsync(
                new GeneratorConfig(
                    new ChannelGeneratorConfig(true, [new GeneratorTone(lowHz, lowDbv), new GeneratorTone(highHz, highDbv)]),
                    ChannelGeneratorConfig.Off)
                { SnapToBin = false },
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
            Assert.NotNull(snapshot.Meters?.Intermodulation);

            long resultId = await h.SaveService.SaveAsync(typeId, "imd capture", null, CancellationToken.None);
            var detail = await h.Measurements.GetResultAsync(resultId, CancellationToken.None);

            Assert.NotNull(detail);
            var metrics = detail.MetricsByChannel[0];
            Assert.Equal(snapshot.Meters.Intermodulation.ImdPercent, metrics["imd_percent"], 9);
            Assert.Equal(snapshot.Meters.Intermodulation.ToneLowFreqHz, metrics["imd_tone_low_hz"], 6);
            Assert.Equal(snapshot.Meters.Intermodulation.SineEquivalentDbv, metrics["imd_sine_equivalent_dbv"], 6);
            Assert.True(metrics.ContainsKey("im2_percent"));
            Assert.True(metrics.ContainsKey("imd_p0_hz"));
            Assert.Equal(snapshot.Meters.Intermodulation.Products.Count, metrics.Keys.Count(k => k.StartsWith("imd_p") && k.EndsWith("_hz")));

            var parameters = Deserialize(detail.ParamsJson);
            Assert.Equal("Smpte", parameters.ImdMethod);
            Assert.Equal(lowHz, parameters.ImdToneLowHz);
            // The type stored the same pair under its template name, so the result records it too.
            Assert.Equal("Smpte", parameters.ImdPreset);
        });
    }

    private static StagedMeasurementConfig StagedWith(string preset) => new(
        48_000.0, 1, 0.0, true,
        new GeneratorChannelRequest(true, [new GeneratorToneRequest(60.0, -7.94), new GeneratorToneRequest(7000.0, -19.98)]),
        new GeneratorChannelRequest(false, []),
        true)
    {
        ImdPreset = preset,
        ImdMethod = "Smpte",
        ImdMaxOrder = 5,
    };

    private static FftSpectrumParameters Deserialize(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<FftSpectrumParameters>(json, options)!;
    }

    private sealed class Harness(
        DeviceSessionService session, SpectrumPipeline pipeline, MeasurementSaveService saveService,
        ProjectRepository projects, MeasurementRepository measurements)
    {
        public DeviceSessionService Session { get; } = session;
        public SpectrumPipeline Pipeline { get; } = pipeline;
        public MeasurementSaveService SaveService { get; } = saveService;
        public MeasurementRepository Measurements { get; } = measurements;

        private long? _projectId;

        public async Task<long> CreateFftTypeAsync(string paramsJson)
        {
            _projectId ??= await projects.CreateProjectAsync("imd", null, CancellationToken.None);
            return await projects.CreateTypeAsync(_projectId.Value, "FFTSpectrum", "fft", null, paramsJson, CancellationToken.None);
        }
    }

    private static async Task WithHarnessAsync(Func<Harness, Task> body)
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
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
                projects, measurements, Options.Create(jsonOptions));

            await body(new Harness(session, pipeline, saveService, projects, measurements));
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
