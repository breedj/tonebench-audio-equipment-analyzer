using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

public class OutputImpedanceRunnerTests
{
    /// <summary>A short grid — every point is a capture plus a flush, twice over, and nothing here depends on the grid being wide.</summary>
    private static OutputImpedanceRequest Request(
        double startFreqHz = 1000.0,
        double endFreqHz = 4000.0,
        int stepsPerOctave = 2,
        ThdSweepLevelMode levelMode = ThdSweepLevelMode.GeneratorVoltage,
        double levelValue = -20.0,
        ThdSweepLevelUnit levelUnit = ThdSweepLevelUnit.Dbv,
        double loadOhms = 8.0,
        double? secondLoadOhms = null,
        double nominalOhms = 8.0,
        bool rightChannelIsReference = false,
        int averages = 1,
        bool adaptiveResolution = true,
        int fftSize = 16384,
        double sampleRateHz = 48_000.0) =>
        new(startFreqHz, endFreqHz, stepsPerOctave, levelMode, levelValue, levelUnit, loadOhms, secondLoadOhms, nominalOhms,
            rightChannelIsReference, fftSize, WindowType.Hann, averages, sampleRateHz, adaptiveResolution);

    private sealed record Harness(OutputImpedanceRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

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
            new OutputImpedanceRunner(session, pipeline, hub, NullLogger<OutputImpedanceRunner>.Instance),
            session,
            pipeline);
    }

    private static async Task AwaitPhaseAsync(OutputImpedanceRunner runner, FrequencyResponsePhase phase, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (runner.Status.Phase != phase)
        {
            if (!runner.Running && runner.Status.Phase != phase)
                throw new InvalidOperationException($"The run ended in {runner.Status.Phase} ({runner.Status.Error ?? "(no message)"}) before reaching {phase}.");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The run did not reach {phase} within {timeoutSeconds} s (phase {runner.Status.Phase}).");

            await Task.Delay(25);
        }
    }

    private static async Task<OutputImpedanceStatus> AwaitCompletionAsync(OutputImpedanceRunner runner, int timeoutSeconds = 180)
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

    /// <summary>Runs both passes, changing the load through <paramref name="changeLoad"/> at the pause.</summary>
    private static async Task<OutputImpedanceStatus> RunBothPassesAsync(Harness harness, OutputImpedanceRequest request, Action changeLoad)
    {
        await harness.Runner.StartAsync(request, CancellationToken.None);
        await AwaitPhaseAsync(harness.Runner, FrequencyResponsePhase.WaitingForLoadChange);
        changeLoad();
        harness.Runner.Continue();
        return await AwaitCompletionAsync(harness.Runner);
    }

    private static void AssertFinished(OutputImpedanceStatus status) =>
        Assert.True(
            status.Phase == FrequencyResponsePhase.Finished,
            $"the run ended in {status.Phase}: {status.Error ?? "(no message)"}");

    private static double OhmsAt(ImpedanceTrace trace, int i) => ImpedanceMath.FromDbOhms(trace.MagnitudeMaxDbOhms[i]);

    [Fact]
    public async Task AResistiveOutputImpedance_ReadsItsValueAtEveryPoint_MagnitudeOnly()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null);

        AssertFinished(status);
        Assert.Equal(2, status.PassesCompleted);
        Assert.Equal(status.PointCount, status.PointsMeasured);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);
        Assert.Equal(ImpedanceTraceKind.Impedance, trace.Kind);
        Assert.Equal(status.PointCount / 2, trace.FreqHz.Length);
        for (int i = 0; i < trace.FreqHz.Length; i++)
            Assert.InRange(OhmsAt(trace, i), 0.49, 0.51);
        Assert.All(trace.PhaseDeg, p => Assert.Equal(0.0f, p));
        Assert.NotNull(status.MedianOhms);
        Assert.InRange(status.MedianOhms!.Value, 0.49, 0.51);
        Assert.InRange(status.DampingFactorAt1k!.Value, 15.7, 16.3);
        Assert.Null(status.Warning);
    }

    /// <summary>
    /// The default arrangement: two loads, no open circuit — an output transformer with signal on it
    /// and nothing across its secondary is how tube amplifiers are killed. 8 Ω then 4 Ω against a
    /// 0.5 Ω source solves to the same 0.5 Ω, both methods.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoLoads_SolveTheSameOutputImpedance_AsALoadAndAnOpen(bool referenced)
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness, Request(secondLoadOhms: 4.0, rightChannelIsReference: referenced), () => device.LoadOhms = 4.0);

        AssertFinished(status);
        Assert.Equal(4.0, status.SecondLoadOhms);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);
        for (int i = 0; i < trace.FreqHz.Length; i++)
            Assert.InRange(OhmsAt(trace, i), 0.49, 0.51);
        Assert.InRange(status.DampingFactorAt1k!.Value, 15.7, 16.3);
        // The second pass is the quieter one here (a heavier load), and that is fine: the ratio is
        // what is measured, whichever way round it comes.
        Assert.True(status.SecondGainAt1kDb < status.FirstGainAt1kDb);
    }

    [Fact]
    public async Task TwoLoads_AgainstTheSimulatorsCoil_ReadTheSameZoutAsTheOpenMethod()
    {
        // The sampled divider's Zout does not depend on the load it was measured into, so 8 Ω then
        // 4 Ω must land on the same oracle the open method does — referenced, so the coil is real.
        const double rate = 48_000.0;
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { OutputStage = SimulatedOutputStageModel.SolidState, Load = SimulatedLoad.EightOhms };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness,
            Request(startFreqHz: 1000.0, endFreqHz: 20_000.0, stepsPerOctave: 1, secondLoadOhms: 4.0, rightChannelIsReference: true, adaptiveResolution: false, sampleRateHz: rate),
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.FourOhms });

        AssertFinished(status);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.SolidState, 8.0, trace.FreqHz[i], rate);
            Assert.True(
                Math.Abs(OhmsAt(trace, i) - z.Magnitude) <= 0.05 * z.Magnitude + 0.003,
                $"at {trace.FreqHz[i]:0} Hz read {OhmsAt(trace, i):0.0000} ohm, the model says {z.Magnitude:0.0000}");
        }
    }

    [Fact]
    public void Solve_ReducesToTheTutorialsFormula_WhenTheSecondPassIsOpen()
    {
        // 0.5 ohm behind 8 ohm: G1 = 8/8.5; open: G2 = 1 -> q = 8.5/8.
        var open = OutputImpedanceMath.Solve(new Complex(8.5 / 8.0, 0.0), 8.0, null);
        Assert.Equal(0.5, open.Real, 9);
        // ...and with 4 ohm as the second load: G2 = 4/4.5, q = (4/4.5)/(8/8.5).
        var twoLoads = OutputImpedanceMath.Solve(new Complex((4.0 / 4.5) / (8.0 / 8.5), 0.0), 8.0, 4.0);
        Assert.Equal(0.5, twoLoads.Real, 9);
        // A complex source comes back complex: 0.028 + j0.19 behind 8 and 4 ohm.
        var zs = new Complex(0.028, 0.19);
        var q = (4.0 / (4.0 + zs)) / (8.0 / (8.0 + zs));
        var solved = OutputImpedanceMath.Solve(q, 8.0, 4.0);
        Assert.Equal(zs.Real, solved.Real, 9);
        Assert.Equal(zs.Imaginary, solved.Imaginary, 9);
    }

    [Fact]
    public void Validate_RejectsTwoEqualLoads() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(loadOhms: 8.0, secondLoadOhms: 8.0).Validate());

    [Fact]
    public async Task AResistiveOutputImpedance_ReadsItsValue_Referenced()
    {
        // The complex ratio's magnitude is the same figure, and its phase is nought for a resistor.
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(harness, Request(rightChannelIsReference: true), () => device.LoadOhms = null);

        AssertFinished(status);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            Assert.InRange(OhmsAt(trace, i), 0.49, 0.51);
            Assert.InRange(trace.PhaseDeg[i], -2.0, 2.0);
        }
    }

    /// <summary>
    /// The reason the reference channel exists. A coil's reactance sits in quadrature with the load,
    /// so the level ratio the magnitude-only method reads barely moves — it reports
    /// <c>R·(|1 + Z/R| − 1)</c>, a fraction of |Z| — while the complex ratio recovers the coil.
    /// The tutorial's own BC-1 figures show it: 0.066 Ω measured against 0.19 Ω from its 1.5 µH coil.
    /// </summary>
    [Fact]
    public async Task AnInductiveOutputImpedance_IsUnderReadByMagnitudeOnly_AndRecoveredWithTheReference()
    {
        const double rate = 48_000.0;
        var request = Request(startFreqHz: 5000.0, endFreqHz: 20_000.0, stepsPerOctave: 2, adaptiveResolution: false, sampleRateHz: rate);

        var magnitudeOnly = await RunAgainstSimulatorAsync(request);
        var referenced = await RunAgainstSimulatorAsync(request with { RightChannelIsReference = true });

        for (int i = 0; i < magnitudeOnly.FreqHz.Length; i++)
        {
            double f = magnitudeOnly.FreqHz[i];
            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.SolidState, 8.0, f, rate);
            double expectedMagnitudeOnly = 8.0 * ((Complex.One + (z / 8.0)).Magnitude - 1.0);

            Assert.True(
                Math.Abs(OhmsAt(magnitudeOnly, i) - expectedMagnitudeOnly) <= 0.1 * expectedMagnitudeOnly + 0.002,
                $"magnitude-only at {f:0} Hz read {OhmsAt(magnitudeOnly, i):0.0000} ohm, its own formula says {expectedMagnitudeOnly:0.0000}");
            Assert.True(
                Math.Abs(OhmsAt(referenced, i) - z.Magnitude) <= 0.05 * z.Magnitude + 0.002,
                $"referenced at {f:0} Hz read {OhmsAt(referenced, i):0.0000} ohm, the model says {z.Magnitude:0.0000}");
            Assert.InRange(referenced.PhaseDeg[i], z.Phase * 180.0 / Math.PI - 5.0, z.Phase * 180.0 / Math.PI + 5.0);
        }

        // And the two disagree by more than either tolerance at the top, which is the point.
        int top = magnitudeOnly.FreqHz.Length - 1;
        Assert.True(OhmsAt(referenced, top) > 3.0 * OhmsAt(magnitudeOnly, top));
    }

    private static async Task<ImpedanceTrace> RunAgainstSimulatorAsync(OutputImpedanceRequest request)
    {
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { OutputStage = SimulatedOutputStageModel.SolidState, Load = SimulatedLoad.EightOhms };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness, request,
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.Open });

        AssertFinished(status);
        return Assert.IsType<ImpedanceTrace>(status.Trace);
    }

    [Fact]
    public async Task AgainstTheSimulatorsTubeModel_ReadsItsTwoOhms()
    {
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { OutputStage = SimulatedOutputStageModel.TubeAmp, Load = SimulatedLoad.EightOhms };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness, Request(startFreqHz: 100.0, endFreqHz: 1000.0, stepsPerOctave: 1),
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.Open });

        AssertFinished(status);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, trace.FreqHz[i], 48_000.0);
            double expected = 8.0 * ((Complex.One + (z / 8.0)).Magnitude - 1.0);
            Assert.InRange(OhmsAt(trace, i), expected * 0.97, expected * 1.03);
        }

        Assert.InRange(status.DampingFactorAt1k!.Value, 3.5, 4.5);
    }

    [Fact]
    public async Task TheWait_IsARunningPhase_AndContinueResumesIt()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        await AwaitPhaseAsync(harness.Runner, FrequencyResponsePhase.WaitingForLoadChange);

        var waiting = harness.Runner.Status;
        Assert.True(waiting.Running);
        Assert.Equal(1, waiting.PassesCompleted);
        Assert.Equal(waiting.PointCount / 2, waiting.PointsMeasured);
        Assert.Null(waiting.Trace);
        // The generator is silent while the user handles the load.
        Assert.False(harness.Session.EffectiveGeneratorConfig.Left.Enabled);
        // A second start is refused: the run still owns the bench.
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.StartAsync(Request(), CancellationToken.None));

        device.LoadOhms = null;
        harness.Runner.Continue();
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);
        Assert.NotNull(status.Trace);
    }

    [Fact]
    public async Task Continue_WhenNotWaiting_IsRefused()
    {
        var harness = await CreateAsync();
        Assert.Throws<InvalidOperationException>(() => harness.Runner.Continue());

        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        harness = await CreateAsync(device);
        await harness.Runner.StartAsync(Request(startFreqHz: 500.0, endFreqHz: 16000.0, stepsPerOctave: 4), CancellationToken.None);
        // Still sweeping the loaded pass.
        Assert.Throws<InvalidOperationException>(() => harness.Runner.Continue());
        await harness.Runner.CancelAsync();
    }

    [Fact]
    public async Task Cancelling_DuringTheWait_EndsCancelled_WithNoZout()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        await AwaitPhaseAsync(harness.Runner, FrequencyResponsePhase.WaitingForLoadChange);
        await harness.Runner.CancelAsync();

        var status = harness.Runner.Status;
        Assert.Equal(FrequencyResponsePhase.Cancelled, status.Phase);
        Assert.False(status.Running);
        Assert.Null(status.Trace);
        Assert.Equal(1, status.PassesCompleted);
        Assert.False(harness.Session.Acquiring);
    }

    [Fact]
    public async Task Cancelling_InTheOpenPass_KeepsThePairsAlreadyMeasured()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        await harness.Runner.StartAsync(Request(startFreqHz: 500.0, endFreqHz: 16000.0, stepsPerOctave: 4), CancellationToken.None);
        await AwaitPhaseAsync(harness.Runner, FrequencyResponsePhase.WaitingForLoadChange);
        device.LoadOhms = null;
        harness.Runner.Continue();

        int perPass = harness.Runner.Status.PointCount / 2;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.PointsMeasured < perPass + 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "no open-pass point was measured in time");
            await Task.Delay(20);
        }

        await harness.Runner.CancelAsync();

        var status = harness.Runner.Status;
        Assert.Equal(FrequencyResponsePhase.Cancelled, status.Phase);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);
        Assert.InRange(trace.FreqHz.Length, 2, perPass - 1);
    }

    [Fact]
    public async Task TheInputRange_IsChosenOnce_AndHeldThroughBothPasses()
    {
        // The measurement rests on the two passes sharing one range. Output-referred mode: the range
        // comes from the probed level plus the open headroom, and the open pass — louder — stays on it.
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 2.0) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        var rangesSeen = new HashSet<double>();

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: 0.0, levelUnit: ThdSweepLevelUnit.Dbv),
            CancellationToken.None);
        await AwaitPhaseAsync(harness.Runner, FrequencyResponsePhase.WaitingForLoadChange);
        double rangeAfterLoaded = harness.Session.InputRangeDbv;
        rangesSeen.Add(rangeAfterLoaded);
        device.LoadOhms = null;
        harness.Runner.Continue();

        while (harness.Runner.Running)
        {
            rangesSeen.Add(harness.Session.InputRangeDbv);
            await Task.Delay(10);
        }

        var status = harness.Runner.Status;
        AssertFinished(status);
        Assert.Single(rangesSeen);
        Assert.Equal(rangeAfterLoaded, status.InputRangeDbv);
        // 0 dBV loaded, +6 dB of headroom on top: the 6 dBV step is what 5 dB of headroom over 6 dBV rounds up to.
        Assert.Equal(InputRangeSelector.For(harness.Session.Capabilities.InputRangesDbv, 0.0 + OutputImpedanceRunner.OpenHeadroomDb), status.InputRangeDbv);
        Assert.Null(status.Warning);
        // Both passes ran at the probed generator level, and the loaded output landed on the target.
        Assert.InRange(status.FirstGainAt1kDb!.Value + status.GeneratorAmpDbv, -0.5, 0.5);
        Assert.InRange(status.MedianOhms!.Value, 1.9, 2.1);
    }

    [Fact]
    public async Task ARun_RestoresThePipelineOptionsItChanged_AndSilencesTheGenerator()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        var before = harness.Pipeline.Options with { Weighting = new WeightingSpec(WeightingStandard.A) };
        harness.Pipeline.Options = before;

        var status = await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null);

        AssertFinished(status);
        Assert.Equal(before, harness.Pipeline.Options);
        var generator = harness.Session.EffectiveGeneratorConfig;
        Assert.False(generator.Left.Enabled);
        Assert.False(generator.Right.Enabled);
        Assert.False(harness.Session.Acquiring);
    }

    [Fact]
    public async Task Clear_EmptiesTheCurveAndTheReadings()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        AssertFinished(await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null));

        var cleared = harness.Runner.Clear();

        Assert.Null(cleared.Trace);
        Assert.Null(cleared.MedianOhms);
        Assert.Null(cleared.DampingFactorAt1k);
        Assert.Equal(FrequencyResponsePhase.Idle, cleared.Phase);
        Assert.Equal(0, cleared.PointCount);
        var (first, second) = harness.Runner.GainTraces;
        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task AFinishedRun_KeepsBothGainPasses_ForTheSave()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        AssertFinished(await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null));

        var (first, second) = harness.Runner.GainTraces;
        Assert.Equal(FrequencyResponseTraceKind.GainLoad1, first!.Kind);
        Assert.Equal(FrequencyResponseTraceKind.GainLoad2, second!.Kind);
        // The divider: 8/(8.5) is −0.53 dB loaded against open.
        for (int i = 0; i < first.FreqHz.Length; i++)
            Assert.InRange(second.MagnitudeMaxDb[i] - first.MagnitudeMaxDb[i], 0.50, 0.56);
    }

    [Fact]
    public async Task SaveOutputImpedance_WritesTheZoutRowAndBothGainRows_AndTheRangeItWasReadAt()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        AssertFinished(await RunBothPassesAsync(harness, Request(secondLoadOhms: 4.0), () => device.LoadOhms = 4.0));

        string path = Path.Combine(Path.GetTempPath(), $"qa403-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new Qa403.Data.SqliteConnectionFactory(new Qa403.Data.Qa403DatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new Qa403.Data.MigrationRunner().RunAsync(connection, CancellationToken.None);

            var projects = new Qa403.Data.ProjectRepository(factory);
            var measurements = new Qa403.Data.MeasurementRepository(factory);
            var jsonOptions = new Microsoft.AspNetCore.Http.Json.JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var saveService = new MeasurementSaveService(
                harness.Session, harness.Pipeline,
                new ThdSweepRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance),
                new ImdSweepRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ImdSweepRunner>.Instance),
                new FrequencyResponseRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<FrequencyResponseRunner>.Instance),
                new SteppedResponseRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<SteppedResponseRunner>.Instance),
                new CrosstalkRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                harness.Runner,
                new ImpulseResponseRunner(harness.Session, harness.Pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Microsoft.Extensions.Options.Options.Create(jsonOptions));

            long projectId = await projects.CreateProjectAsync("zout", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                projectId, MeasurementSaveService.OutputImpedanceKind, "damping factor", null,
                saveService.DefaultOutputImpedanceParametersJson(), CancellationToken.None);

            long resultId = await saveService.SaveOutputImpedanceAsync(typeId, "0.5 ohm", null, CancellationToken.None);
            var traces = await measurements.GetResponseTracesAsync(resultId, CancellationToken.None);
            var detail = await measurements.GetResultAsync(resultId, CancellationToken.None);

            Assert.Equal(
                [Qa403.Data.ResponseTraceKind.OutputImpedance, Qa403.Data.ResponseTraceKind.GainLoad1, Qa403.Data.ResponseTraceKind.GainLoad2],
                traces.Select(t => t.Kind));
            var zout = traces.Single(t => t.Kind == Qa403.Data.ResponseTraceKind.OutputImpedance);
            Assert.All(zout.MagnitudeMaxDb, db => Assert.InRange(ImpedanceMath.FromDbOhms(db), 0.49, 0.51));

            Assert.NotNull(detail);
            var parameters = System.Text.Json.JsonSerializer.Deserialize<Qa403.Data.OutputImpedanceParameters>(detail.ParamsJson, jsonOptions.SerializerOptions);
            Assert.NotNull(parameters);
            Assert.Equal(8.0, parameters.LoadOhms);
            Assert.Equal(4.0, parameters.SecondLoadOhms);
            Assert.Equal(8.0, parameters.NominalOhms);
            Assert.Equal(-20.0, parameters.GeneratorAmpDbv);
            Assert.NotNull(parameters.InputRangeDbv);
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
    public async Task StartAsync_WithNoDevice_Throws()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(CleanDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        var runner = new OutputImpedanceRunner(session, pipeline, hub, NullLogger<OutputImpedanceRunner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData(0.0, 8.0)]
    [InlineData(8.0, 0.0)]
    [InlineData(-8.0, 8.0)]
    public void Validate_RejectsANonPositiveResistance(double loadOhms, double nominalOhms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(loadOhms: loadOhms, nominalOhms: nominalOhms).Validate());

    [Fact]
    public void Validate_AcceptsTheTutorialsRun() =>
        Request(startFreqHz: 20.0, endFreqHz: 20_000.0, stepsPerOctave: 3,
            levelMode: ThdSweepLevelMode.OutputPower, levelValue: 1.0, levelUnit: ThdSweepLevelUnit.Watt).Validate();

    [Fact]
    public void NearestInLogFrequency_PicksTheLogNeighbour()
    {
        // 800 and 1300 straddle 1 kHz; 800 is nearer in log frequency though 1300 is nearer in hertz.
        Assert.Equal(1, OutputImpedanceRunner.NearestInLogFrequency([20.0, 800.0, 1300.0, 20_000.0], 1000.0));
    }
}
