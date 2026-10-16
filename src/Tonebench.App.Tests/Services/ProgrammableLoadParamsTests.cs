using System.Reflection;
using System.Text.Json;
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
/// The engaged programmable load, recorded with each saved result.
///
/// <para>A stored sweep that cannot say what it was measured into is a sweep whose power figures
/// cannot be checked later, which is the whole reason this is recorded — and the reason the first
/// test here is a reflection test rather than a list of ten names: CLAUDE.md's rule is that a field
/// missing from one of these mappings is not an error but a silent default, so an eleventh
/// measurement kind must not be able to forget.</para>
/// </summary>
public class ProgrammableLoadParamsTests
{
    /// <summary>
    /// Every parameters record that records the external gain must record the load too. Both are
    /// bench provenance set at save time from the session rather than from the request, so a kind
    /// that carries one and not the other is a kind that half-answers.
    /// </summary>
    [Fact]
    public void EveryKindThatRecordsTheExternalGain_AlsoRecordsTheEngagedLoad()
    {
        var carrying = typeof(FftSpectrumParameters).Assembly
            .GetTypes()
            .Where(t => t.GetProperty("ExternalGainLeftDb") is not null)
            .ToList();

        Assert.NotEmpty(carrying);

        foreach (Type type in carrying)
        {
            PropertyInfo? kind = type.GetProperty("ProgrammableLoadKind");
            PropertyInfo? ohms = type.GetProperty("ProgrammableLoadOhms");

            Assert.True(kind is not null, $"{type.Name} records the external gain but not the load's kind.");
            Assert.True(ohms is not null, $"{type.Name} records the external gain but not the load's ohms.");
            Assert.Equal(typeof(string), kind!.PropertyType);
            Assert.Equal(typeof(int?), ohms!.PropertyType);
        }
    }

    [Fact]
    public async Task Save_WithALoadEngaged_RecordsTheKindAndTheOhms()
    {
        await WithHarnessAsync(async h =>
        {
            await h.Load.ConnectAsync();
            await h.Load.SetImpedanceAsync(8);

            long resultId = await h.CaptureAndSaveAsync();
            var parameters = await h.ReadParamsAsync(resultId);

            Assert.Equal("Simulated", parameters.ProgrammableLoadKind);
            Assert.Equal(8, parameters.ProgrammableLoadOhms);
        });
    }

    [Fact]
    public async Task Save_WithTheLoadOpen_RecordsZeroRatherThanNothing()
    {
        // 0 is "open", a state worth recording: it says the DUT was unloaded on purpose, where null
        // would say nobody knows.
        await WithHarnessAsync(async h =>
        {
            await h.Load.ConnectAsync();
            await h.Load.SetImpedanceAsync(0);

            var parameters = await h.ReadParamsAsync(await h.CaptureAndSaveAsync());

            Assert.Equal("Simulated", parameters.ProgrammableLoadKind);
            Assert.Equal(0, parameters.ProgrammableLoadOhms);
        });
    }

    [Fact]
    public async Task Save_WithNoLoadSelected_LeavesBothNull()
    {
        await WithHarnessAsync(
            async h =>
            {
                var parameters = await h.ReadParamsAsync(await h.CaptureAndSaveAsync());

                Assert.Null(parameters.ProgrammableLoadKind);
                Assert.Null(parameters.ProgrammableLoadOhms);
            },
            kind: ProgrammableLoadKind.None);
    }

    /// <summary>
    /// The dependency is optional and last for a reason — dozens of tests construct this service
    /// directly. This is that guarantee as a test rather than as a comment.
    /// </summary>
    [Fact]
    public async Task Save_FromAServiceBuiltWithoutALoad_LeavesBothNull()
    {
        await WithHarnessAsync(
            async h =>
            {
                var parameters = await h.ReadParamsAsync(await h.CaptureAndSaveAsync());

                Assert.Null(parameters.ProgrammableLoadKind);
                Assert.Null(parameters.ProgrammableLoadOhms);
            },
            attachLoad: false);
    }

    /// <summary>
    /// A <i>type's</i> own params_json is a template, not a record of a run: the load belongs to the
    /// result that was measured, the same way the capture delay does.
    /// </summary>
    [Fact]
    public async Task ATypesOwnParameters_NeverCarryTheLoad()
    {
        await WithHarnessAsync(async h =>
        {
            await h.Load.ConnectAsync();
            await h.Load.SetImpedanceAsync(4);

            var parameters = Deserialize(h.SaveService.BuildCurrentParametersJson(Staged()));

            Assert.Null(parameters.ProgrammableLoadKind);
            Assert.Null(parameters.ProgrammableLoadOhms);
        });
    }

    private static FftSpectrumParameters Deserialize(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<FftSpectrumParameters>(json, options)!;
    }

    private static StagedMeasurementConfig Staged() =>
        new(
            48_000.0, 1, 0.0, true,
            new GeneratorChannelRequest(true, [new GeneratorToneRequest(1000.0, -10.0)]),
            new GeneratorChannelRequest(false, []),
            true);

    /// <summary>Hands out a simulated load, so no test can open the real QA451 attached to this bench.</summary>
    private sealed class SimulatedOnlyLoadFactory() : ProgrammableLoadFactory(NullLoggerFactory.Instance)
    {
        public override IProgrammableLoad? Create(ProgrammableLoadKind kind) =>
            kind == ProgrammableLoadKind.None ? null : new SimulatedProgrammableLoad(softStartSettleMs: 0);
    }

    private sealed class Harness(
        DeviceSessionService session,
        SpectrumPipeline pipeline,
        MeasurementSaveService saveService,
        ProjectRepository projects,
        MeasurementRepository measurements,
        LoadSessionService load)
    {
        public MeasurementSaveService SaveService { get; } = saveService;

        public LoadSessionService Load { get; } = load;

        private long? _projectId;

        /// <summary>One single-shot FFT capture, then a save — the shortest path to a stored result.</summary>
        public async Task<long> CaptureAndSaveAsync()
        {
            _projectId ??= await projects.CreateProjectAsync("load", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                _projectId.Value, $"FFT {Guid.NewGuid():N}", "fft", null,
                saveService.BuildCurrentParametersJson(Staged()), CancellationToken.None);

            pipeline.Options = pipeline.Options with { FftSize = 8192, Averaging = AveragingMode.Off, SingleShot = true };
            await session.StartAcquisitionAsync(
                new AcquisitionConfig(48_000.0, ChannelCount: 1, BlockFrameCount: DeviceSessionService.MinBlockFrameCount),
                CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!pipeline.SingleShotComplete)
                    await Task.Delay(20, timeout.Token);
            }
            finally
            {
                await session.StopAcquisitionAsync();
            }

            return await saveService.SaveAsync(typeId, $"capture {Guid.NewGuid():N}", null, null, CancellationToken.None);
        }

        public async Task<FftSpectrumParameters> ReadParamsAsync(long resultId)
        {
            var detail = await measurements.GetResultAsync(resultId, CancellationToken.None);
            Assert.NotNull(detail);
            return Deserialize(detail!.ParamsJson);
        }
    }

    private static async Task WithHarnessAsync(
        Func<Harness, Task> body,
        ProgrammableLoadKind kind = ProgrammableLoadKind.Simulated,
        bool attachLoad = true)
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(randomSeed: 1), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        using var load = new LoadSessionService(
            new SimulatedOnlyLoadFactory(), new LoadKindSetting { Kind = kind }, NullLogger<LoadSessionService>.Instance);

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
                projects, measurements, Options.Create(jsonOptions), null, attachLoad ? load : null);

            await body(new Harness(session, pipeline, saveService, projects, measurements, load));
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
