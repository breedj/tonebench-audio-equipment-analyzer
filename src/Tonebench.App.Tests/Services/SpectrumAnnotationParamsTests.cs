using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// How the spectrum chart's annotations ride through a measurement: staged from the client onto the
/// type, handed back when the type is applied, frozen onto a saved result, and re-edited on a stored
/// result afterwards.
///
/// <para>The one rule worth stating twice: <b>a result keeps its own copy</b>. Everything else the
/// chart stores is a preference about the screen, read back from the measurement type at display
/// time; an annotation is a note about one capture, so it travels with the save and survives a later
/// change to the measurement's own list.</para>
/// </summary>
public class SpectrumAnnotationParamsTests
{
    private static readonly SpectrumAnnotation Hum = new(1, "point", "Mains hum", 50.0, -98.2, 0, 0.45, 14.0);
    private static readonly SpectrumAnnotation Corner = new(2, "frequency", "Bandwidth corner", 21_500.0);

    /// <summary>A region from 1 to 4 kHz between -40 and -80 dBV: top-left corner first, bottom-right second.</summary>
    private static readonly SpectrumAnnotation Band =
        new(3, "box", "Hum band", 1_000.0, -40.0, null, 0.3, 12.0, 4_000.0, -80.0);

    [Fact]
    public async Task BuildCurrentParametersJson_CarriesTheStagedAnnotations()
    {
        await WithHarnessAsync(async h =>
        {
            var parameters = Deserialize(h.SaveService.BuildCurrentParametersJson(Staged(Hum, Corner)));

            Assert.NotNull(parameters.ChartAnnotations);
            Assert.Equal(2, parameters.ChartAnnotations!.Count);
            Assert.Equal(Hum, parameters.ChartAnnotations[0]);
            Assert.Equal(Corner, parameters.ChartAnnotations[1]);

            // Nothing staged: no annotations, which is also what every row written before they
            // existed says.
            Assert.Null(Deserialize(h.SaveService.BuildCurrentParametersJson()).ChartAnnotations);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Apply_HandsTheTypesAnnotationsBackToThePanel()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateTypeAsync("FFTSpectrum", h.SaveService.BuildCurrentParametersJson(Staged(Hum)));

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            Assert.NotNull(applied.ChartAnnotations);
            Assert.Equal(Hum, Assert.Single(applied.ChartAnnotations!));
        });
    }

    [Fact]
    public async Task Apply_OfATypeWhoseAnnotationsNameAStyleNothingKnows_StillSelectsTheType()
    {
        await WithHarnessAsync(async h =>
        {
            // Nothing here validates a style -- the client's parseAnnotations drops what it cannot
            // draw -- so a params_json another build wrote must not fail a measurement the user has
            // merely selected.
            var document = JsonNode.Parse(h.SaveService.BuildCurrentParametersJson())!.AsObject();
            document["chartAnnotations"] = JsonNode.Parse("""[{"id":1,"style":"banner","text":"?","freqHz":1000}]""");
            long typeId = await h.CreateTypeAsync("FFTSpectrum", document.ToJsonString());

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            Assert.Equal("banner", Assert.Single(applied.ChartAnnotations!).Style);
        });
    }

    [Fact]
    public async Task Save_WithAnnotationsOnTheRequest_RecordsThoseRatherThanTheTypes()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateTypeAsync("FFTSpectrum", h.SaveService.BuildCurrentParametersJson(Staged(Corner)));
            await h.CaptureAsync();

            // What the chart carried when Save was pressed, which need not be what the measurement
            // was last saved with: a user annotates a trace in order to save it.
            long resultId = await h.SaveService.SaveAsync(typeId, "annotated", null, [Hum], CancellationToken.None);

            Assert.Equal(Hum, Assert.Single((await h.ReadResultParamsAsync(resultId)).ChartAnnotations!));
        });
    }

    [Fact]
    public async Task Save_WithNoAnnotationsOnTheRequest_CopiesTheTypes()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateTypeAsync("FFTSpectrum", h.SaveService.BuildCurrentParametersJson(Staged(Corner)));
            await h.CaptureAsync();

            // Null means "none were sent" -- an older client, or a caller that has no list to send.
            long resultId = await h.SaveService.SaveAsync(typeId, "unannotated request", null, null, CancellationToken.None);

            Assert.Equal(Corner, Assert.Single((await h.ReadResultParamsAsync(resultId)).ChartAnnotations!));
        });
    }

    [Fact]
    public async Task Save_WithAnEmptyListOnTheRequest_RecordsNoneRatherThanFallingBackToTheTypes()
    {
        await WithHarnessAsync(async h =>
        {
            long typeId = await h.CreateTypeAsync("FFTSpectrum", h.SaveService.BuildCurrentParametersJson(Staged(Corner)));
            await h.CaptureAsync();

            long resultId = await h.SaveService.SaveAsync(typeId, "deliberately none", null, [], CancellationToken.None);

            Assert.Empty((await h.ReadResultParamsAsync(resultId)).ChartAnnotations!);
        });
    }

    [Fact]
    public async Task BuildCurrentParametersJson_CarriesABoxAnnotationsSecondCorner()
    {
        await WithHarnessAsync(async h =>
        {
            var parameters = Deserialize(h.SaveService.BuildCurrentParametersJson(Staged(Band)));

            // Record equality covers the two new fields, so this also pins that nothing on the way
            // through drops a corner.
            Assert.Equal(Band, Assert.Single(parameters.ChartAnnotations!));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Apply_OfATypeWhoseAnnotationsPredateTheBoxStyle_ReadsItsSecondCornerAsNull()
    {
        await WithHarnessAsync(async h =>
        {
            // A params_json written before a box existed: the two fields are simply absent.
            var document = JsonNode.Parse(h.SaveService.BuildCurrentParametersJson())!.AsObject();
            document["chartAnnotations"] =
                JsonNode.Parse("""[{"id":1,"style":"point","text":"Mains hum","freqHz":50,"levelDbv":-98.2}]""");
            long typeId = await h.CreateTypeAsync("FFTSpectrum", document.ToJsonString());

            var applied = await h.SaveService.ApplyAsync(typeId, CancellationToken.None);

            var back = Assert.Single(applied.ChartAnnotations!);
            Assert.Null(back.FreqHz2);
            Assert.Null(back.LevelDbv2);
            Assert.Equal(-98.2, back.LevelDbv);
        });
    }

    [Fact]
    public async Task SetResultAnnotations_OfABox_WritesBothCorners()
    {
        await WithHarnessAsync(async h =>
        {
            long resultId = await h.WriteResultAsync("FFTSpectrum", h.SaveService.BuildCurrentParametersJson());

            await h.SaveService.SetResultAnnotationsAsync(resultId, [Band], CancellationToken.None);

            Assert.Equal(Band, Assert.Single((await h.ReadResultParamsAsync(resultId)).ChartAnnotations!));
        });
    }

    [Fact]
    public async Task SetResultAnnotations_RewritesThatFieldAndLeavesEveryOtherOneAlone()
    {
        await WithHarnessAsync(async h =>
        {
            string stored = h.SaveService.BuildCurrentParametersJson(Staged(Corner));
            long resultId = await h.WriteResultAsync("FFTSpectrum", stored);

            await h.SaveService.SetResultAnnotationsAsync(resultId, [Hum], CancellationToken.None);

            var before = JsonNode.Parse(stored)!.AsObject();
            var after = JsonNode.Parse((await h.ReadResultDetailAsync(resultId)).ParamsJson)!.AsObject();
            foreach (var (key, value) in before)
            {
                if (key == "chartAnnotations") continue;
                Assert.Equal(value?.ToJsonString(), after[key]?.ToJsonString());
            }
            Assert.Equal(Hum, Assert.Single((await h.ReadResultParamsAsync(resultId)).ChartAnnotations!));
        });
    }

    [Fact]
    public async Task SetResultAnnotations_OnAParamsDocumentCarryingAFieldThisBuildDoesNotKnow_KeepsIt()
    {
        await WithHarnessAsync(async h =>
        {
            // The reason this splices a JsonNode: a round trip through today's record would drop
            // whatever a later build had written, i.e. change what the result says it measured in
            // order to add a note about it.
            var document = JsonNode.Parse(h.SaveService.BuildCurrentParametersJson())!.AsObject();
            document["chartSomethingNewer"] = "kept";
            long resultId = await h.WriteResultAsync("FFTSpectrum", document.ToJsonString());

            await h.SaveService.SetResultAnnotationsAsync(resultId, [Corner], CancellationToken.None);

            var after = JsonNode.Parse((await h.ReadResultDetailAsync(resultId)).ParamsJson)!.AsObject();
            Assert.Equal("kept", (string?)after["chartSomethingNewer"]);
        });
    }

    [Fact]
    public async Task SetResultAnnotations_OnAResultOfAnotherKind_IsRefused()
    {
        await WithHarnessAsync(async h =>
        {
            long resultId = await h.WriteResultAsync("ThdVsFrequency", "{}");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.SaveService.SetResultAnnotationsAsync(resultId, [Hum], CancellationToken.None));
        });
    }

    [Fact]
    public async Task SetResultAnnotations_OnAResultThatIsNotThere_IsNotFound()
    {
        await WithHarnessAsync(async h =>
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => h.SaveService.SetResultAnnotationsAsync(4242, [Hum], CancellationToken.None)));
    }

    private static StagedMeasurementConfig Staged(params SpectrumAnnotation[] annotations) => new(
        48_000.0, 1, 0.0, true,
        new GeneratorChannelRequest(true, [new GeneratorToneRequest(1000.0, -10.0)]),
        new GeneratorChannelRequest(false, []),
        true)
    {
        ChartAnnotations = annotations,
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
        public SpectrumPipeline Pipeline { get; } = pipeline;
        public MeasurementSaveService SaveService { get; } = saveService;

        private long? _projectId;

        public async Task<long> CreateTypeAsync(string kind, string paramsJson)
        {
            _projectId ??= await projects.CreateProjectAsync("annotations", null, CancellationToken.None);
            return await projects.CreateTypeAsync(_projectId.Value, kind, $"m {Guid.NewGuid():N}", null, paramsJson, CancellationToken.None);
        }

        /// <summary>A result row written straight to the database, for the cases that are about the stored document rather than about a capture.</summary>
        public async Task<long> WriteResultAsync(string kind, string paramsJson)
        {
            long typeId = await CreateTypeAsync(kind, paramsJson);
            return await measurements.SaveResultAsync(
                new MeasurementSaveRequest(
                    typeId, "stored", null, paramsJson, "Simulated", null, null, true,
                    48_000.0, 8192, 1, 0.0, 0.0, null, [], []),
                CancellationToken.None);
        }

        public async Task<MeasurementDetail> ReadResultDetailAsync(long resultId) =>
            (await measurements.GetResultAsync(resultId, CancellationToken.None))!;

        public async Task<FftSpectrumParameters> ReadResultParamsAsync(long resultId) =>
            Deserialize((await ReadResultDetailAsync(resultId)).ParamsJson);

        /// <summary>One single-shot record, so there is a retained snapshot for SaveAsync to write.</summary>
        public async Task CaptureAsync()
        {
            const double sampleRateHz = 48_000.0;
            Pipeline.Options = Pipeline.Options with { FftSize = 8192, Averaging = AveragingMode.Off, SingleShot = true };

            double toneHz = CoherentFrequency.Nearest(1000.0, sampleRateHz, 8192).FrequencyHz;
            await session.ConfigureGeneratorAsync(
                new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(toneHz, -10.0)]), ChannelGeneratorConfig.Off) { SnapToBin = false },
                CancellationToken.None);
            await session.StartAcquisitionAsync(
                new AcquisitionConfig(sampleRateHz, ChannelCount: 1, BlockFrameCount: DeviceSessionService.MinBlockFrameCount),
                CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!Pipeline.SingleShotComplete)
                    await Task.Delay(20, timeout.Token);
            }
            finally
            {
                await session.StopAcquisitionAsync();
            }
        }
    }

    private static async Task WithHarnessAsync(Func<Harness, Task> body)
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
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
            var hub = new SpectrumBroadcastHub();
            var saveService = new MeasurementSaveService(
                session, pipeline,
                new ThdSweepRunner(session, pipeline, hub, NullLogger<ThdSweepRunner>.Instance),
                new ImdSweepRunner(session, pipeline, hub, NullLogger<ImdSweepRunner>.Instance),
                new FrequencyResponseRunner(session, pipeline, hub, NullLogger<FrequencyResponseRunner>.Instance),
                new SteppedResponseRunner(session, pipeline, hub, NullLogger<SteppedResponseRunner>.Instance),
                new CrosstalkRunner(session, pipeline, hub, NullLogger<CrosstalkRunner>.Instance),
                new OutputImpedanceRunner(session, pipeline, hub, NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(session, pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(session, pipeline, hub, NullLogger<ImpedanceRunner>.Instance),
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
