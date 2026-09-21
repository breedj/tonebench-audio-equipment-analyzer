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

namespace Qa403.App.Tests.Services;

/// <summary>
/// An FFT type whose params_json is not a whole configuration -- found in the wild on 2026-09-21
/// as a document with the chart fields, an FFT size and a window but no generator, no harmonics
/// and an empty band. Deserializing it does not fail; applying it used to NRE on the generator
/// *after* pushing zero harmonics and a 0-0 Hz band onto the live pipeline, and every THD sweep
/// run afterwards then measured with no harmonics and drew nothing. Selecting the type must leave
/// the pipeline alone and answer with the live state instead.
/// </summary>
public class IncompleteFftTypeParamsTests
{
    private const string IncompleteJson = """
        {"fftSize":131072,"window":"Hann","averaging":"Off","averageCount":1,
         "chartYTopDbv":10,"chartYBottomDbv":-150,"chartXFromHz":10,"chartXToHz":100000}
        """;

    [Fact]
    public void AnFftDocumentWithoutAGeneratorOrHarmonics_IsNotAComplete_Configuration()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var parameters = JsonSerializer.Deserialize<FftSpectrumParameters>(IncompleteJson, options)!;

        Assert.Null(parameters.Generator);
        Assert.Equal(0, parameters.HarmonicCount);
        Assert.False(MeasurementSaveService.IsCompleteFftConfiguration(parameters));
    }

    [Fact]
    public async Task Apply_OfAnIncompleteFftType_LeavesThePipelineAloneAndReturnsTheLiveState()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync(IncompleteJson);
            var before = h.Pipeline.Options;
            Assert.True(before.HarmonicCount > 0);
            Assert.True(before.ProcessLeft);

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            // Nothing from the document reached the pipeline: not its FFT size, and not the zeros
            // it never had values for.
            Assert.Equal(before.FftSize, h.Pipeline.Options.FftSize);
            Assert.Equal(before.HarmonicCount, h.Pipeline.Options.HarmonicCount);
            Assert.Equal(before.MeasurementLoFreqHz, h.Pipeline.Options.MeasurementLoFreqHz);
            Assert.Equal(before.MeasurementHiFreqHz, h.Pipeline.Options.MeasurementHiFreqHz);
            Assert.True(h.Pipeline.Options.ProcessLeft);

            // And the answer is the live state, generator included, so the screen can seed itself.
            Assert.NotNull(applied.GeneratorLeft);
            Assert.NotNull(applied.GeneratorRight);
            Assert.Equal(h.Session.EffectiveGeneratorConfig.Left.Enabled, applied.GeneratorLeft.Enabled);

            // The stored document is whole again -- rewritten from the live state, with the chart
            // preferences it did carry kept -- so the next selection applies rather than falls back.
            var healed = Deserialize((await h.Projects.GetTypeAsync(typeId, CancellationToken.None))!.ParamsJson);
            Assert.True(MeasurementSaveService.IsCompleteFftConfiguration(healed));
            Assert.Equal(before.HarmonicCount, healed.HarmonicCount);
            Assert.True(healed.ProcessLeft);
            Assert.Equal(-150.0, healed.ChartYBottomDbv);
            Assert.Equal(100000.0, healed.ChartXToHz);
        });
    }

    [Fact]
    public async Task Apply_OfACompleteFftType_StillAppliesIt()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateFftTypeAsync(h.SaveService.BuildCurrentParametersJson());
            h.Pipeline.Options = h.Pipeline.Options with { HarmonicCount = 3 };

            await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            // The complete document carried the default harmonic count, and applying it put it back.
            Assert.NotEqual(3, h.Pipeline.Options.HarmonicCount);
        });
    }

    private static FftSpectrumParameters Deserialize(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<FftSpectrumParameters>(json, options)!;
    }

    private sealed class Harness(DeviceSessionService session, SpectrumPipeline pipeline, MeasurementSaveService saveService, ProjectRepository projects)
    {
        public DeviceSessionService Session { get; } = session;
        public SpectrumPipeline Pipeline { get; } = pipeline;
        public MeasurementSaveService SaveService { get; } = saveService;
        public ProjectRepository Projects { get; } = projects;

        private long? _projectId;

        public async Task<long> CreateFftTypeAsync(string paramsJson)
        {
            _projectId ??= await projects.CreateProjectAsync("incomplete", null, CancellationToken.None);
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
                new CrosstalkRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                new OutputImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(session, pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(session, pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Options.Create(jsonOptions), userWeighting);

            await body(new Harness(session, pipeline, saveService, projects));
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
