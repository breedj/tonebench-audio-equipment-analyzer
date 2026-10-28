using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

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
        double sampleRateHz = 48_000.0,
        ImpedanceStimulus stimulus = ImpedanceStimulus.SteppedSine,
        double frequencySpanHz = 24_000.0,
        int resolutionMultiplier = 1,
        double chirpStartHz = 10.0,
        int smoothingDenominator = 12) =>
        new(startFreqHz, endFreqHz, stepsPerOctave, levelMode, levelValue, levelUnit, loadOhms, secondLoadOhms, nominalOhms,
            rightChannelIsReference, fftSize, WindowType.Hann, averages, sampleRateHz, adaptiveResolution,
            stimulus, frequencySpanHz, resolutionMultiplier, chirpStartHz,
            OutputImpedanceRequest.DefaultNoiseCutoffHz, smoothingDenominator);

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

    /// <summary>
    /// The chirp and the pink sequence measure the same output impedance the stepped sine does.
    /// Asserted against <see cref="SimulatedOutputStage.ZoutAt"/> - the model's own divider at the
    /// run's own sample rate - rather than against the stepped path, so a fault shared by both
    /// cannot pass.
    ///
    /// <para>Referenced, because that is the method whose answer is |Z| itself. The magnitude-only
    /// case has its own test below, and reads the smaller figure its own formula predicts.</para>
    /// </summary>
    [Theory]
    [InlineData(ImpedanceStimulus.Chirp)]
    [InlineData(ImpedanceStimulus.PinkNoise)]
    public async Task ABroadbandRun_ReadsTheSameOutputImpedanceTheModelHas(ImpedanceStimulus stimulus)
    {
        const double rate = 48_000.0;
        var trace = await RunBroadbandAgainstSimulatorAsync(
            Request(stimulus: stimulus, rightChannelIsReference: true, averages: 4, smoothingDenominator: 12),
            SimulatedOutputStageModel.SolidState);

        int checked_ = 0;
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            double f = trace.FreqHz[i];

            // The band the figures are worth reading over: below a few hundred hertz the solid-state
            // model is 28 milliohms of pure resistance, which is a 30 millidecibel gain ratio, and
            // the top of the analysed band is where the chirp's own energy runs out.
            if (f < 300.0 || f > 15_000.0)
                continue;

            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.SolidState, 8.0, f, rate);
            Assert.True(
                Math.Abs(OhmsAt(trace, i) - z.Magnitude) <= (0.05 * z.Magnitude) + 0.0015,
                $"{stimulus} at {f:0} Hz read {OhmsAt(trace, i):0.0000} ohm, the model says {z.Magnitude:0.0000}");
            checked_++;
        }

        Assert.True(checked_ > 20, $"only {checked_} columns fell inside the band worth checking");
    }

    /// <summary>
    /// Without a measured reference a broadband run reads magnitudes only, exactly as the stepped
    /// path does - each pass starts at its own arbitrary point in the periodic stimulus, so the two
    /// passes' phases are not comparable. It therefore under-reads a reactive Zout by the same
    /// formula, which is what this pins: the same answer, not a better or a worse one.
    /// </summary>
    [Fact]
    public async Task ABroadbandRun_WithNoReference_UnderReadsAReactiveZoutByItsOwnFormula()
    {
        const double rate = 48_000.0;
        var trace = await RunBroadbandAgainstSimulatorAsync(
            Request(stimulus: ImpedanceStimulus.Chirp, averages: 4, smoothingDenominator: 12),
            SimulatedOutputStageModel.SolidState);

        int checked_ = 0;
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            double f = trace.FreqHz[i];
            if (f < 300.0 || f > 15_000.0)
                continue;

            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.SolidState, 8.0, f, rate);
            double expected = 8.0 * ((Complex.One + (z / 8.0)).Magnitude - 1.0);
            Assert.True(
                Math.Abs(OhmsAt(trace, i) - expected) <= (0.05 * expected) + 0.0015,
                $"magnitude-only at {f:0} Hz read {OhmsAt(trace, i):0.0000} ohm, its own formula says {expected:0.0000}");
            checked_++;
        }

        Assert.True(checked_ > 20, $"only {checked_} columns fell inside the band worth checking");
    }

    /// <summary>
    /// The tube model's two ohms, measured broadband into 8 ohm then 4 ohm - the pairing the panel
    /// defaults to, where every other test here takes the second pass open. The denominator
    /// R2 - R1*q is the thing that changes with it, and it is the half of the solve a single pairing
    /// cannot exercise.
    /// </summary>
    [Fact]
    public async Task ABroadbandRun_IntoTwoLoads_SolvesTheSameImpedanceAnOpenSecondPassDoes()
    {
        const double rate = 48_000.0;
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with
        {
            OutputStage = SimulatedOutputStageModel.TubeAmp,
            Load = SimulatedLoad.EightOhms,
        };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness,
            Request(stimulus: ImpedanceStimulus.PinkNoise, rightChannelIsReference: true, averages: 4,
                secondLoadOhms: 4.0, smoothingDenominator: 12),
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.FourOhms });

        AssertFinished(status);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);

        int checked_ = 0;
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            double f = trace.FreqHz[i];
            if (f < 300.0 || f > 15_000.0)
                continue;

            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, f, rate);
            Assert.True(
                Math.Abs(OhmsAt(trace, i) - z.Magnitude) <= (0.05 * z.Magnitude) + 0.004,
                $"two-load at {f:0} Hz read {OhmsAt(trace, i):0.0000} ohm, the model says {z.Magnitude:0.0000}");
            checked_++;
        }

        Assert.True(checked_ > 20, $"only {checked_} columns fell inside the band worth checking");

        // Against the model rather than a hand-written range, and note it is NOT the figure
        // AgainstTheSimulatorsTubeModel_ReadsItsTwoOhms asserts: that run is magnitude-only, which
        // under-reads a 0.3 mH coil to 2.18 ohm and so reports a damping factor near 3.7. This one
        // is referenced, so it recovers the true |Z| of 2.75 ohm and reads 2.91.
        double zAt1k = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, 1000.0, rate).Magnitude;
        Assert.InRange(status.DampingFactorAt1k!.Value, (8.0 / zAt1k) * 0.97, (8.0 / zAt1k) * 1.03);
    }

    /// <summary>
    /// A broadband run counts records, not points - the status says so by leaving the point counts
    /// null, which is what the progress line branches on. Zero would read as a stepped run that has
    /// measured nothing yet.
    /// </summary>
    /// <summary>
    /// The widest smoothing band on the steepest impedance the simulator has — which is the one
    /// case where solving per bin and averaging the <i>ohms</i> is wrong even with no noise at all,
    /// and so the only call-site test that can tell the two orders apart here.
    ///
    /// <para>A 1/3-octave band at 10 kHz spans 2.4 kHz, over which the tube stage's 0.3 mH coil
    /// takes |Z| from 15 to 20 Ω — a factor of 14 across the measured band — while the gain ratio
    /// it is solved from moves by a factor of under 2. Averaging the <i>ratio</i> therefore costs
    /// almost nothing where averaging |Z| cost a great deal: measured end to end against
    /// <see cref="SimulatedOutputStage.ZoutAt"/>, the same run read <b>0.60 % mean and 3.93 % worst
    /// before the order was fixed and 0.33 % / 0.85 % after</b>. The bound below sits between those
    /// two, so this fails against the old order rather than merely documenting the new one.</para>
    ///
    /// <para><see cref="OutputImpedanceOrderingTests"/> pins the arithmetic; this pins that the
    /// runner asks for it. Both are needed — the smoothing-band clamp of 2026-10-21 was correct in
    /// the smoother and missing at a call site for a day, with every test still green.</para>
    /// </summary>
    [Fact]
    public async Task ABroadbandRun_AtTheWidestSmoothing_AveragesTheRatioRatherThanTheOhms()
    {
        const double rate = 48_000.0;
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with
        {
            OutputStage = SimulatedOutputStageModel.TubeAmp,
            Load = SimulatedLoad.EightOhms,
        };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness,
            Request(stimulus: ImpedanceStimulus.Chirp, rightChannelIsReference: true, averages: 4,
                secondLoadOhms: 4.0, smoothingDenominator: 3),
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.FourOhms });

        AssertFinished(status);
        var trace = Assert.IsType<ImpedanceTrace>(status.Trace);

        double worst = 0.0;
        double total = 0.0;
        int checked_ = 0;
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            double f = trace.FreqHz[i];
            if (f < 300.0 || f > 15_000.0)
                continue;

            var z = SimulatedOutputStage.ZoutAt(SimulatedOutputStageModel.TubeAmp, 8.0, f, rate);
            double error = Math.Abs(OhmsAt(trace, i) - z.Magnitude) / z.Magnitude;
            worst = Math.Max(worst, error);
            total += error;
            checked_++;
        }

        Assert.True(checked_ > 20, $"only {checked_} columns fell inside the band worth checking");
        // The worst column is the discriminating bound - 0.85 % now against 3.93 % before, so
        // this is set between them with room on both sides. The mean is a looser sanity check;
        // it is higher than the 0.33 % quoted in the manual only because that figure runs from
        // 30 Hz, where the coil has not started to climb and every order agrees.
        Assert.True(worst < 0.015, $"worst column was {worst * 100.0:0.00} % off the model");
        Assert.True(total / checked_ < 0.006, $"mean error was {total / checked_ * 100.0:0.00} %");
    }

    [Fact]
    public async Task ABroadbandRun_ReportsRecordsRatherThanPoints()
    {
        var (trace, status) = await RunBroadbandWithStatusAsync(
            Request(stimulus: ImpedanceStimulus.Chirp, rightChannelIsReference: true, averages: 2),
            SimulatedOutputStageModel.SolidState);

        Assert.NotNull(trace);
        Assert.Null(status.PointsMeasured);
        Assert.Null(status.PointCount);
        Assert.Equal(ImpedanceStimulus.Chirp, status.Stimulus);
        Assert.Equal(2, status.AverageCount);
        Assert.Equal(2, status.AveragesTaken);
    }

    /// <summary>A stepped run still counts points, and still says which stimulus it was.</summary>
    [Fact]
    public async Task ASteppedRun_StillCountsPoints()
    {
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness, Request(startFreqHz: 1000.0, endFreqHz: 2000.0, stepsPerOctave: 1),
            () => device.LoadOhms = null);

        AssertFinished(status);
        Assert.NotNull(status.PointCount);
        Assert.Equal(status.PointCount, status.PointsMeasured);
        Assert.Equal(ImpedanceStimulus.SteppedSine, status.Stimulus);
    }

    private static async Task<ImpedanceTrace> RunBroadbandAgainstSimulatorAsync(
        OutputImpedanceRequest request, SimulatedOutputStageModel model)
    {
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { OutputStage = model, Load = SimulatedLoad.EightOhms };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness, request,
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.Open });

        AssertFinished(status);
        return Assert.IsType<ImpedanceTrace>(status.Trace);
    }

    private static async Task<(ImpedanceTrace Trace, OutputImpedanceStatus Status)> RunBroadbandWithStatusAsync(
        OutputImpedanceRequest request, SimulatedOutputStageModel model)
    {
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { OutputStage = model, Load = SimulatedLoad.EightOhms };
        var harness = await CreateAsync(device);

        var status = await RunBothPassesAsync(
            harness, request,
            () => device.SignalProfile = device.SignalProfile with { Load = SimulatedLoad.Open });

        AssertFinished(status);
        return (Assert.IsType<ImpedanceTrace>(status.Trace), status);
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

        int perPass = harness.Runner.Status.PointCount!.Value / 2;
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
                new CrosstalkRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<CrosstalkRunner>.Instance),
                harness.Runner,
                new ImpulseResponseRunner(harness.Session, harness.Pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
                new ImpedanceRunner(harness.Session, harness.Pipeline, new SpectrumBroadcastHub(), NullLogger<ImpedanceRunner>.Instance),
                projects, measurements, Microsoft.Extensions.Options.Options.Create(jsonOptions));

            long projectId = await projects.CreateProjectAsync("zout", null, CancellationToken.None);
            long typeId = await projects.CreateTypeAsync(
                projectId, MeasurementSaveService.OutputImpedanceKind, "damping factor", null,
                saveService.DefaultOutputImpedanceParametersJson(), CancellationToken.None);

            // Every kind's save carries the notes on its charts now, so the result keeps its own
            // frozen copy and goes on printing them however the measurement's own list changes
            // afterwards -- see ChartAnnotationParamsTests for the rule and the FFT's own cases.
            var note = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                """{"id":1,"style":"point","text":"this point","view":"Bode","x":1000,"y":0.01}""");
            long resultId = await saveService.SaveOutputImpedanceAsync(typeId, "0.5 ohm", null, [note], CancellationToken.None);
            var traces = await measurements.GetResponseTracesAsync(resultId, CancellationToken.None);
            var detail = await measurements.GetResultAsync(resultId, CancellationToken.None);

            Assert.Equal(
                [Tonebench.Data.ResponseTraceKind.OutputImpedance, Tonebench.Data.ResponseTraceKind.GainLoad1, Tonebench.Data.ResponseTraceKind.GainLoad2],
                traces.Select(t => t.Kind));
            var zout = traces.Single(t => t.Kind == Tonebench.Data.ResponseTraceKind.OutputImpedance);
            Assert.All(zout.MagnitudeMaxDb, db => Assert.InRange(ImpedanceMath.FromDbOhms(db), 0.49, 0.51));

            Assert.NotNull(detail);
            var parameters = System.Text.Json.JsonSerializer.Deserialize<Tonebench.Data.OutputImpedanceParameters>(detail.ParamsJson, jsonOptions.SerializerOptions);
            Assert.NotNull(parameters);
            Assert.Equal(
                "this point",
                parameters.ChartAnnotations!.Single().GetProperty("text").GetString());
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

    [Fact]
    public async Task ARunSwitchedAwayFromAndBackTo_IsStillThere()
    {
        // The sweep report of 2026-10-17 in this runner: one output-impedance runner serves every
        // output-impedance measurement, so selecting a second one and coming back used to find an
        // empty chart. See ThdSweepRunner.SwitchOwner.
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        harness.Runner.SwitchOwner("1:1");

        var measured = await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null);
        AssertFinished(measured);
        Assert.NotNull(measured.Trace);

        // A different output-impedance measurement: it has never run, so it gets an empty chart.
        var other = harness.Runner.SwitchOwner("1:2");
        Assert.Null(other.Trace);
        Assert.Equal(FrequencyResponsePhase.Idle, other.Phase);
        Assert.Null(other.MedianOhms);
        Assert.Equal((null, null), harness.Runner.GainTraces);

        var back = harness.Runner.SwitchOwner("1:1");

        Assert.NotNull(back.Trace);
        Assert.Equal(measured.Phase, back.Phase);
        Assert.Equal(measured.PointCount, back.PointCount);
        Assert.Equal(measured.MedianOhms, back.MedianOhms);
        Assert.NotNull(harness.Runner.Status.Trace);
        // The as-run request and the two gain passes both: SaveOutputImpedanceAsync stores them
        // beside the Zout curve so a result keeps what it was derived from.
        Assert.NotNull(harness.Runner.LastRequest);
        var (first, second) = harness.Runner.GainTraces;
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task SwitchingOwnerWhileARunWaitsForTheLoadChange_IsIgnored()
    {
        // WaitingForLoadChange is a busy phase -- the run still owns the session, the pipeline
        // options and the input range -- so a run paused for the user to move the load must not be
        // parked out from under its own CONTINUE.
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        try
        {
            await AwaitPhaseAsync(harness.Runner, FrequencyResponsePhase.WaitingForLoadChange);

            harness.Runner.SwitchOwner("1:2");
            Assert.Equal("1:1", harness.Runner.Owner);
            Assert.True(harness.Runner.Running);
            Assert.Equal(FrequencyResponsePhase.WaitingForLoadChange, harness.Runner.Status.Phase);
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
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);
        harness.Runner.SwitchOwner("1:1");

        AssertFinished(await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null));
        harness.Runner.Clear();

        harness.Runner.SwitchOwner("1:2");

        Assert.Null(harness.Runner.SwitchOwner("1:1").Trace);
    }

    [Fact]
    public async Task ARunMadeBeforeAnyMeasurementClaimedTheRunner_IsAdoptedRatherThanThrownAway()
    {
        // A page that reloaded mid-run: nobody owns it, and the first measurement to arrive keeps
        // it -- the same rule the FFT display's owner follows.
        var device = new OutputStageDevice(CleanDevice(), outputOhms: 0.5) { LoadOhms = 8.0 };
        var harness = await CreateAsync(device);

        AssertFinished(await RunBothPassesAsync(harness, Request(), () => device.LoadOhms = null));

        Assert.NotNull(harness.Runner.SwitchOwner("1:1").Trace);
    }
}
