using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

public class CrosstalkRunnerTests
{
    /// <summary>A short grid — every point is a capture plus a flush, and nothing here depends on the grid being wide.</summary>
    private static CrosstalkRequest Request(
        double startFreqHz = 1000.0,
        double endFreqHz = 4000.0,
        int stepsPerOctave = 2,
        CrosstalkDirection direction = CrosstalkDirection.LeftToRight,
        ThdSweepLevelMode levelMode = ThdSweepLevelMode.GeneratorVoltage,
        double levelValue = -20.0,
        ThdSweepLevelUnit levelUnit = ThdSweepLevelUnit.Dbv,
        double loadOhms = 8.0,
        int averages = 1,
        bool adaptiveResolution = true,
        int fftSize = 16384,
        double sampleRateHz = 48_000.0) =>
        new(startFreqHz, endFreqHz, stepsPerOctave, direction, levelMode, levelValue, levelUnit, loadOhms,
            fftSize, WindowType.Hann, averages, sampleRateHz, adaptiveResolution);

    private sealed record Harness(CrosstalkRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

    /// <summary>A straight wire with nothing added — see <c>SteppedResponseRunnerTests.CleanDevice</c>.</summary>
    private static SimulatedDevice CleanDevice()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with
        {
            HarmonicsEnabled = false,
            NoiseEnabled = false,
            HumEnabled = false,
        };

        return device;
    }

    /// <summary>The clean wire with a known coupling: −80 dB left-to-right, −60 dB right-to-left.</summary>
    private static CrosstalkDevice CoupledDevice(double leftToRightDb = -80.0, double rightToLeftDb = -60.0) =>
        new(CleanDevice(), leftToRightDb, rightToLeftDb);

    private static async Task<Harness> CreateAsync(IAnalyzerDevice? device = null)
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };
        var session = new DeviceSessionService(device ?? CleanDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        return new Harness(
            new CrosstalkRunner(session, pipeline, hub, NullLogger<CrosstalkRunner>.Instance),
            session,
            pipeline);
    }

    private static async Task<FrequencyResponseStatus> AwaitCompletionAsync(CrosstalkRunner runner, int timeoutSeconds = 180)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (runner.Running)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The run did not finish within {timeoutSeconds} s (phase {runner.Status.Phase}).");

            await Task.Delay(25);
        }

        return runner.Status;
    }

    private static void AssertFinished(FrequencyResponseStatus status) =>
        Assert.True(
            status.Phase == FrequencyResponsePhase.Finished,
            $"the run ended in {status.Phase}: {status.Error ?? "(no message)"}");

    private static FrequencyResponseTrace Trace(FrequencyResponseStatus status, FrequencyResponseTraceKind kind) =>
        status.Traces.Single(t => t.Kind == kind);

    [Fact]
    public async Task LeftToRight_AgainstAKnownCoupling_ReadsTheCouplingAtEveryPoint()
    {
        var harness = await CreateAsync(CoupledDevice());

        var started = await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(1, started.PassCount);
        // One trace, its own kind, nothing else — no Left/Right/Gain triple.
        var trace = Assert.Single(status.Traces);
        Assert.Equal(FrequencyResponseTraceKind.CrosstalkLeftToRight, trace.Kind);
        Assert.Equal(started.PointCount, trace.FreqHz.Length);
        Assert.All(trace.MagnitudeMaxDb, db => Assert.InRange(db, -80.2, -79.8));
        Assert.Equal(trace.MagnitudeMinDb, trace.MagnitudeMaxDb);
        Assert.All(trace.Coherence, c => Assert.Equal(1.0f, c));
        Assert.All(trace.PhaseDeg, p => Assert.Equal(0.0f, p));
        // A crosstalk trace has no corners; the reference is the peak, which is the worst case.
        Assert.Equal(0.0, trace.OneDb.BandwidthHz);
        Assert.Equal(0.0, trace.ThreeDb.BandwidthHz);
        Assert.Equal(trace.PeakDb, trace.ReferenceDb);
        Assert.Equal(ResponsePhaseKind.None, status.PhaseKind);
    }

    /// <summary>
    /// The clean simulator with Noise off hands the victim channel exact digital silence, which is
    /// thousands of dB down — a number the chart's autofit, the CSV and the readings would all carry
    /// as if it meant something. The point is floored at <see cref="CrosstalkRunner.FloorDb"/> instead.
    /// </summary>
    [Fact]
    public async Task ASilentVictim_ReadsTheFloor_NotMinusThousandsOfDb()
    {
        var harness = await CreateAsync(CleanDevice());

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);
        Assert.All(trace.MagnitudeMaxDb, db => Assert.Equal((float)CrosstalkRunner.FloorDb, db));
    }

    [Fact]
    public async Task RightToLeft_ReadsTheOtherCoupling()
    {
        var harness = await CreateAsync(CoupledDevice());

        await harness.Runner.StartAsync(Request(direction: CrosstalkDirection.RightToLeft), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);
        Assert.Equal(FrequencyResponseTraceKind.CrosstalkRightToLeft, trace.Kind);
        Assert.All(trace.MagnitudeMaxDb, db => Assert.InRange(db, -60.2, -59.8));
    }

    [Fact]
    public async Task Both_RunsTwoPassesAndYieldsTwoTraces_InOrder()
    {
        var harness = await CreateAsync(CoupledDevice());

        var started = await harness.Runner.StartAsync(Request(direction: CrosstalkDirection.Both), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(2, started.PassCount);
        Assert.Equal(2, status.PassesCompleted);
        Assert.Equal(2 * started.PointCount, status.PointCount);
        Assert.Equal(status.PointCount, status.PointsMeasured);
        Assert.Equal(
            [FrequencyResponseTraceKind.CrosstalkLeftToRight, FrequencyResponseTraceKind.CrosstalkRightToLeft],
            status.Traces.Select(t => t.Kind));
        // The asymmetric device is what proves the passes were not swapped.
        Assert.All(Trace(status, FrequencyResponseTraceKind.CrosstalkLeftToRight).MagnitudeMaxDb, db => Assert.InRange(db, -80.2, -79.8));
        Assert.All(Trace(status, FrequencyResponseTraceKind.CrosstalkRightToLeft).MagnitudeMaxDb, db => Assert.InRange(db, -60.2, -59.8));
        Assert.Equal(2, harness.Runner.GeneratorAmpDbvByKind.Count);
    }

    [Fact]
    public async Task AgainstTheSimulatorsModel_EveryPointReadsCouplingDb()
    {
        // The simulator's own knob, end to end through the runner: both directions read the exact
        // sampled coupling, including its rise above the corner.
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { CrosstalkEnabled = true };
        var harness = await CreateAsync(device);

        await harness.Runner.StartAsync(
            Request(startFreqHz: 200.0, endFreqHz: 20_000.0, stepsPerOctave: 3, direction: CrosstalkDirection.Both, adaptiveResolution: false),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(2, status.Traces.Count);
        foreach (var trace in status.Traces)
        {
            for (int i = 0; i < trace.FreqHz.Length; i++)
            {
                double expected = SimulatedCrosstalk.CouplingDb(trace.FreqHz[i], 48_000.0);
                Assert.True(
                    Math.Abs(trace.MagnitudeMaxDb[i] - expected) <= 0.3,
                    $"{trace.Kind} at {trace.FreqHz[i]:0} Hz read {trace.MagnitudeMaxDb[i]:0.00} dB, model says {expected:0.00} dB");
            }
        }

        // The rise is there: the top of the band reads well above the floor.
        var l2r = Trace(status, FrequencyResponseTraceKind.CrosstalkLeftToRight);
        Assert.True(l2r.MagnitudeMaxDb[^1] - l2r.MagnitudeMaxDb[0] > 6.0);
    }

    [Fact]
    public async Task OutputVoltageMode_ProbesTheDrivenChannel()
    {
        // The inverse of SteppedResponseRunnerTests.TheLevelProbe_ReadsTheLeftChannelRatherThanTheLouderOne:
        // driving the right channel only in an output-referred mode must *succeed*, because the probe
        // reads the aggressor, whichever channel that is. A probe fixed on the left input would find
        // silence and stop.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(direction: CrosstalkDirection.RightToLeft, levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: 6.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        // A unity DUT: the generator lands on the target.
        Assert.InRange(status.GeneratorAmpDbv, 5.5, 6.5);
        Assert.InRange(harness.Runner.GeneratorAmpDbvByKind[FrequencyResponseTraceKind.CrosstalkRightToLeft], 5.5, 6.5);
    }

    [Fact]
    public async Task Both_InOutputVoltageMode_SolvesALevelPerPass()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(direction: CrosstalkDirection.Both, levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: 0.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var levels = harness.Runner.GeneratorAmpDbvByKind;
        Assert.Equal(2, levels.Count);
        Assert.InRange(levels[FrequencyResponseTraceKind.CrosstalkLeftToRight], -0.5, 0.5);
        Assert.InRange(levels[FrequencyResponseTraceKind.CrosstalkRightToLeft], -0.5, 0.5);
    }

    [Fact]
    public async Task TheAggressorDrivesTheInputRange_NotTheVictim()
    {
        // One input range serves both channels; the aggressor at 0 dBV is what it must accommodate,
        // and a range picked for the -80 dBV victim would clip the aggressor on every point.
        var harness = await CreateAsync(CoupledDevice());
        var ranges = harness.Session.Capabilities.InputRangesDbv;
        await harness.Session.SetInputRangeAsync(ranges.Max(), CancellationToken.None);

        await harness.Runner.StartAsync(Request(levelValue: 0.0), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(InputRangeSelector.For(ranges, 0.0), harness.Session.InputRangeDbv, 3);
        Assert.NotEqual(InputRangeSelector.For(ranges, -80.0), harness.Session.InputRangeDbv);
    }

    [Fact]
    public async Task Running_IsTrueWhileTheLevelProbeIsStillGoing()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: 0.0),
            CancellationToken.None);

        Assert.True(harness.Runner.Running);
        Assert.Equal(FrequencyResponsePhase.DeterminingLevel, harness.Runner.Status.Phase);

        await AwaitCompletionAsync(harness.Runner);
    }

    [Fact]
    public async Task ASecondStart_IsRejectedWhileTheFirstIsRunning()
    {
        var harness = await CreateAsync();
        await harness.Runner.StartAsync(Request(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Runner.StartAsync(Request(), CancellationToken.None));

        await AwaitCompletionAsync(harness.Runner);
    }

    [Fact]
    public async Task Cancelling_KeepsThePointsAlreadyMeasured()
    {
        var harness = await CreateAsync(CoupledDevice());
        await harness.Runner.StartAsync(
            Request(startFreqHz: 500.0, endFreqHz: 16000.0, stepsPerOctave: 4, direction: CrosstalkDirection.Both),
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.PointsMeasured < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "no point was measured in time");
            await Task.Delay(20);
        }

        await harness.Runner.CancelAsync();

        var status = harness.Runner.Status;
        Assert.Equal(FrequencyResponsePhase.Cancelled, status.Phase);
        Assert.False(status.Running);
        Assert.NotEmpty(status.Traces);
        Assert.Equal(0, status.PassesCompleted);
        Assert.InRange(status.Traces[0].FreqHz.Length, 2, status.PointCount - 1);
    }

    [Fact]
    public async Task Clear_EmptiesTheCurvesAndTheProgressWithThem()
    {
        var harness = await CreateAsync(CoupledDevice());
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        var cleared = harness.Runner.Clear();

        Assert.Empty(cleared.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, cleared.Phase);
        Assert.Equal(0, cleared.PointCount);
        Assert.Empty(harness.Runner.GeneratorAmpDbvByKind);
    }

    [Fact]
    public async Task ARun_RestoresThePipelineOptionsItChanged()
    {
        var harness = await CreateAsync();
        var before = harness.Pipeline.Options with { Weighting = new WeightingSpec(WeightingStandard.A) };
        harness.Pipeline.Options = before;

        await harness.Runner.StartAsync(Request(averages: 2), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(before, harness.Pipeline.Options);
    }

    [Fact]
    public async Task ARun_SilencesTheGeneratorWhenItEnds()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(direction: CrosstalkDirection.Both), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        var generator = harness.Session.EffectiveGeneratorConfig;
        Assert.False(generator.Left.Enabled);
        Assert.False(generator.Right.Enabled);
    }

    [Fact]
    public async Task NoDevice_IsAConflictRatherThanAFailedRun()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(CleanDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        var runner = new CrosstalkRunner(session, pipeline, hub, NullLogger<CrosstalkRunner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData(1000.0, 1000.0, 5, 1)]
    [InlineData(0.0, 1000.0, 5, 1)]
    [InlineData(1000.0, 4000.0, 5, 0)]
    [InlineData(1000.0, 4000.0, 5, 99)]
    public void AnUnmeasurableRequest_IsRejectedBeforeAnythingTouchesTheDevice(double startHz, double endHz, int stepsPerOctave, int averages)
    {
        var request = Request(startFreqHz: startHz, endFreqHz: endHz, stepsPerOctave: stepsPerOctave, averages: averages);
        Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
    }

    [Fact]
    public void Validate_RejectsAnUndefinedDirection()
    {
        var request = Request(direction: (CrosstalkDirection)42);
        Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
    }

    [Fact]
    public async Task SaveCrosstalk_WritesOneTraceRowPerDirectionUnderItsOwnKind_AndBothGeneratorLevels()
    {
        var harness = await CreateAsync(CoupledDevice());
        await harness.Runner.StartAsync(Request(direction: CrosstalkDirection.Both), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        string path = Path.Combine(Path.GetTempPath(), $"tonebench-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new Tonebench.Data.SqliteConnectionFactory(new Tonebench.Data.TonebenchDatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new Tonebench.Data.MigrationRunner().RunAsync(connection, CancellationToken.None);

            var projects = new Tonebench.Data.ProjectRepository(factory);
            var measurements = new Tonebench.Data.MeasurementRepository(factory);
            var jsonOptions = new Microsoft.AspNetCore.Http.Json.JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var saveService = new MeasurementSaveService(
                harness.Session, harness.Pipeline,
                new ThdSweepRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance),
                new ImdSweepRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance),
                new FrequencyResponseRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance),
                new SteppedResponseRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance),
                harness.Runner,
                new OutputImpedanceRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<OutputImpedanceRunner>.Instance),
                new ImpulseResponseRunner(harness.Session, harness.Pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Microsoft.Extensions.Options.Options.Create(jsonOptions));

            long projectId = await projects.CreateProjectAsync("xtalk", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                projectId, MeasurementSaveService.CrosstalkKind, "crosstalk", null,
                saveService.DefaultCrosstalkParametersJson(), CancellationToken.None);

            // Every kind's save carries the notes on its charts now, so the result keeps its own
            // frozen copy and goes on printing them however the measurement's own list changes
            // afterwards -- see ChartAnnotationParamsTests for the rule and the FFT's own cases.
            var note = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                """{"id":1,"style":"point","text":"this point","view":"Crosstalk","x":1000,"y":0.01}""");
            long resultId = await saveService.SaveCrosstalkAsync(typeId, "both ways", null, [note], CancellationToken.None);
            var traces = await measurements.GetResponseTracesAsync(resultId, CancellationToken.None);
            var detail = await measurements.GetResultAsync(resultId, CancellationToken.None);

            Assert.Equal(
                [Tonebench.Data.ResponseTraceKind.CrosstalkLeftToRight, Tonebench.Data.ResponseTraceKind.CrosstalkRightToLeft],
                traces.Select(t => t.Kind));
            Assert.All(traces.Single(t => t.Kind == Tonebench.Data.ResponseTraceKind.CrosstalkLeftToRight).MagnitudeMaxDb, db => Assert.InRange(db, -80.2, -79.8));

            Assert.NotNull(detail);
            var parameters = System.Text.Json.JsonSerializer.Deserialize<Tonebench.Data.CrosstalkParameters>(detail.ParamsJson, jsonOptions.SerializerOptions);
            Assert.NotNull(parameters);
            Assert.Equal(
                "this point",
                parameters.ChartAnnotations!.Single().GetProperty("text").GetString());
            Assert.Equal("Both", parameters.Direction);
            Assert.Equal(-20.0, parameters.GeneratorAmpDbvLeftToRight);
            Assert.Equal(-20.0, parameters.GeneratorAmpDbvRightToLeft);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    [Fact]
    public void Passes_AreInTheOrderTheDirectionNames()
    {
        Assert.Equal([StreamChannel.Left], CrosstalkDirection.LeftToRight.Passes().Select(p => p.Aggressor));
        Assert.Equal([StreamChannel.Right], CrosstalkDirection.RightToLeft.Passes().Select(p => p.Aggressor));
        Assert.Equal([StreamChannel.Left, StreamChannel.Right], CrosstalkDirection.Both.Passes().Select(p => p.Aggressor));
        var pass = CrosstalkDirection.RightToLeft.Passes()[0];
        Assert.Equal(StreamChannel.Left, pass.Victim);
        Assert.True(pass.GenerateRight);
        Assert.False(pass.GenerateLeft);
        Assert.Equal(FrequencyResponseTraceKind.CrosstalkRightToLeft, pass.Kind);
    }

    [Fact]
    public async Task ARunSwitchedAwayFromAndBackTo_IsStillThere()
    {
        // The sweep report of 2026-10-17 in this runner: one crosstalk runner serves every
        // crosstalk measurement, so selecting a second one and coming back used to find an empty
        // chart. See ThdSweepRunner.SwitchOwner.
        var harness = await CreateAsync(CoupledDevice());
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        var measured = harness.Runner.Status;
        Assert.NotEmpty(measured.Traces);
        Assert.NotEmpty(harness.Runner.GeneratorAmpDbvByKind);

        // A different crosstalk measurement: this one has never run, so it gets an empty chart.
        var other = harness.Runner.SwitchOwner("1:2");
        Assert.Empty(other.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, other.Phase);
        Assert.Empty(harness.Runner.GeneratorAmpDbvByKind);

        var back = harness.Runner.SwitchOwner("1:1");

        Assert.Equal(measured.Traces.Count, back.Traces.Count);
        Assert.Equal(measured.Phase, back.Phase);
        Assert.Equal(measured.PointCount, back.PointCount);
        Assert.Equal(back.Traces.Count, harness.Runner.Status.Traces.Count);
        // The as-run request and the per-pass levels both, or the restored run could be looked at
        // and not saved: SaveCrosstalkAsync reads them beside the status.
        Assert.NotNull(harness.Runner.LastRequest);
        Assert.NotEmpty(harness.Runner.GeneratorAmpDbvByKind);
    }

    [Fact]
    public async Task SwitchingOwnerWhileARunIsGoing_IsIgnoredRatherThanRacingTheWriter()
    {
        // Unreachable from the app -- a measurement switch is refused while anything runs -- but a
        // park that lifted the list the sweep is appending to would be far worse than a no-op.
        var harness = await CreateAsync(CoupledDevice());
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(
            Request(startFreqHz: 200.0, endFreqHz: 12000.0, stepsPerOctave: 6), CancellationToken.None);
        try
        {
            harness.Runner.SwitchOwner("1:2");
            Assert.Equal("1:1", harness.Runner.Owner);
            Assert.True(harness.Runner.Running);
        }
        finally
        {
            await harness.Runner.CancelAsync();
        }
    }

    [Fact]
    public async Task ARunThatWasCleared_StaysCleared_AcrossASwitchAwayAndBack()
    {
        // Clear is a deliberate "throw this away"; parking must not quietly undo it.
        var harness = await CreateAsync(CoupledDevice());
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        harness.Runner.Clear();

        harness.Runner.SwitchOwner("1:2");

        Assert.Empty(harness.Runner.SwitchOwner("1:1").Traces);
    }

    [Fact]
    public async Task ARunMadeBeforeAnyMeasurementClaimedTheRunner_IsAdoptedRatherThanThrownAway()
    {
        // A page that reloaded mid-run: nobody owns it, and the first measurement to arrive keeps
        // it -- the same rule the FFT display's owner follows.
        var harness = await CreateAsync(CoupledDevice());

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        int measured = harness.Runner.Status.Traces.Count;

        Assert.Equal(measured, harness.Runner.SwitchOwner("1:1").Traces.Count);
    }
}
