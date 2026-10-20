using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The impedance runner end to end against the simulator's fixture models, whose impedance is known
/// by construction.
///
/// <para>The reference channel is Left throughout — the screen's default, and the bench convention
/// the simulator's fixtures are wired to (generator side on the left input, load junction on the
/// right). See <see cref="SimulatedImpedanceFixture"/>.</para>
/// </summary>
public class ImpedanceRunnerTests
{
    /// <summary>The smallest span, so a record is 32768 frames at 48 kHz — 0.68 s. Nothing under test depends on the span.</summary>
    private const double SpanHz = 24_000.0;

    private static ImpedanceRequest Request(
        double referenceOhms = SimulatedImpedanceFixture.ReferenceOhms,
        int averages = 1,
        bool continuous = false,
        ImpedanceCalTarget calTarget = ImpedanceCalTarget.None,
        ImpedanceCableMode cableMode = ImpedanceCableMode.None,
        ImpedanceCalibration? openCal = null,
        ImpedanceCalibration? shortCal = null,
        int smoothing = 0,
        double chirpStartHz = 10.0,
        bool rightChannelIsReference = false) =>
        new(ImpedanceMethod.ReferenceResistor,
            ImpedanceStimulus.Chirp,
            referenceOhms,
            RightChannelIsReference: rightChannelIsReference,
            SpanHz,
            ResolutionMultiplier: 1,
            LevelValue: -20.0,
            LevelUnit: ThdSweepLevelUnit.Dbv,
            smoothing,
            averages,
            continuous,
            chirpStartHz,
            calTarget,
            cableMode,
            FixedCableOhms: 0.0,
            FixedCableMillihenry: 0.0,
            AnalyzerInputOhms: null,
            ApplySweepGate: false,
            openCal,
            shortCal);

    private sealed record Harness(ImpedanceRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

    /// <summary>
    /// The simulator with one fixture model patched in and nothing else dirtying the signal — the
    /// default profile deliberately adds harmonics, hum and noise to make the FFT screen look
    /// realistic, which would put a floor under every assertion here.
    /// </summary>
    private static SimulatedDevice FixtureDevice(SimulatedDutModel model)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with
        {
            Dut = model,
            HarmonicsEnabled = false,
            NoiseEnabled = false,
            HumEnabled = false,
        };

        return device;
    }

    private static async Task<Harness> CreateAsync(SimulatedDutModel model)
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };

        var session = new DeviceSessionService(FixtureDevice(model), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        return new Harness(
            new ImpedanceRunner(session, pipeline, hub, NullLogger<ImpedanceRunner>.Instance),
            session,
            pipeline);
    }

    private static async Task<ImpedanceStatus> AwaitCompletionAsync(ImpedanceRunner runner, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (runner.Running)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The measurement did not finish within {timeoutSeconds} s (phase {runner.Status.Phase}).");

            await Task.Delay(25);
        }

        return runner.Status;
    }

    private static void AssertFinished(ImpedanceStatus status) =>
        Assert.True(
            status.Phase == FrequencyResponsePhase.Finished,
            $"the measurement ended in {status.Phase}: {status.Error ?? "(no message)"}");

    /// <summary>The measured impedance at the column nearest a frequency, in ohms and degrees.</summary>
    private static (double Ohms, double PhaseDeg) At(ImpedanceTrace trace, double freqHz)
    {
        int best = 0;
        for (int i = 1; i < trace.FreqHz.Length; i++)
        {
            if (Math.Abs(trace.FreqHz[i] - freqHz) < Math.Abs(trace.FreqHz[best] - freqHz))
                best = i;
        }

        // The midpoint of the column's min/max, which is what the chart's own reader takes: max
        // alone carries the aggregation bias of a max over several bins.
        double midDbOhms = 0.5 * (trace.MagnitudeMinDbOhms[best] + trace.MagnitudeMaxDbOhms[best]);
        return (ImpedanceMath.FromDbOhms(midDbOhms), trace.PhaseDeg[best]);
    }

    /// <summary>
    /// The calibration target: an 8 ohm resistor has the same impedance at every frequency and no
    /// reactance at all, so a correct measurement is a flat line through a chain that involves a
    /// chirp, a two-channel ratio and a divider inversion.
    ///
    /// <para><b>Asserted in dB, and not to the last digit, because there is a known residual.</b> The
    /// H1 estimator divides by <c>Sxx + lambda</c>, which scales every bin by exactly
    /// <c>1/(1 + lambda/Sxx)</c> -- a one-sided bias, always low, and largest where the stimulus has
    /// least energy per bin. A log sweep's energy falls as 1/f, so the residual grows with frequency:
    /// measured here as 0.000 dB at 50 Hz, 0.0005 dB at 1 kHz and <b>0.0062 dB</b> at 10 kHz. That is
    /// the deliberate trade behind <see cref="FrequencyResponseEstimator.RegularisationFraction"/>
    /// being 1e-6, and it is predicted rather than tolerated in the DSP tests that own it; here it
    /// only has to stay negligible against anything the measurement claims.</para>
    /// </summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public async Task AgainstTheResistiveFixture_ReadsItsResistor(double freqHz)
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);
        Assert.Equal(ImpedanceTraceKind.Impedance, trace.Kind);

        var (ohms, phaseDeg) = At(trace, freqHz);

        double errorDb = ImpedanceMath.ToDbOhms(ohms) - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms);
        Assert.InRange(errorDb, -0.01, 0.0);
        Assert.Equal(0.0, phaseDeg, 2);
    }

    /// <summary>
    /// And the one-number summary of the same measurement, to the same tolerance and for the same
    /// reason.
    /// </summary>
    [Fact]
    public async Task AgainstTheResistiveFixture_ReportsTheMedianAndTheDriveCurrent()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        double median = Assert.IsType<double>(status.MedianOhms);
        double driveCurrentAmps = Assert.IsType<double>(status.DriveCurrentAmps);

        double errorDb = ImpedanceMath.ToDbOhms(median)
            - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms);
        Assert.InRange(errorDb, -0.01, 0.0);

        // -20 dBV is 0.1 V across 100 + 8 ohms, so about 0.926 mA.
        Assert.Equal(0.1 / (SimulatedImpedanceFixture.ReferenceOhms + median), driveCurrentAmps, 9);
        Assert.InRange(driveCurrentAmps * 1000.0, 0.92, 0.93);
    }

    /// <summary>
    /// The same calibration target through the noise stimulus, which shares nothing with the chirp
    /// path but the divider inversion: a different generator, a Hann window instead of a rectangular
    /// one, no gate, and an estimate that is a Welch cross-spectrum rather than a deconvolution.
    ///
    /// <para>The resistive fixture is a memoryless gain, so the two channels of any one record are
    /// exactly proportional and H is the divider ratio in every bin whatever the stimulus was. What
    /// is left to be wrong is everything around that, which is the point of running it.</para>
    ///
    /// <para><b>Asserted tighter than the chirp's own version of this test</b>, and that is not
    /// bravado: this path passes <c>BroadbandRegularisationFraction</c>, so the H1 bias that costs
    /// the chirp 0.006 dB at 10 kHz costs this 0.001 dB, and nothing else here is inexact. Measured
    /// within <b>0.002 dB</b> at all three frequencies. The default fraction, which this used before
    /// the noise path was allowed to choose, read 0.11 dB low at 10 kHz -- so a tolerance loose
    /// enough to be comfortable would be loose enough to pass against that.</para>
    /// </summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public async Task ByPinkNoise_AgainstTheResistiveFixture_ReadsItsResistor(double freqHz)
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(averages: 2) with { Stimulus = ImpedanceStimulus.PinkNoise }, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(ImpedanceStimulus.PinkNoise, status.Stimulus);

        var trace = Assert.Single(status.Traces);
        var (ohms, phaseDeg) = At(trace, freqHz);

        double errorDb = ImpedanceMath.ToDbOhms(ohms) - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms);
        Assert.InRange(errorDb, -0.01, 0.01);
        Assert.Equal(0.0, phaseDeg, 2);
    }

    /// <summary>
    /// The property the stimulus itself has to have: the reference channel must carry the same noise
    /// that drove the DUT.
    ///
    /// <para>This is the one thing a noise stimulus can get wrong that a chirp cannot. A chirp is
    /// deterministic, so two channels synthesising it separately produce the same waveform by
    /// construction; two channels drawing successive samples from one RNG do not, and the resulting
    /// H is the ratio of two unrelated noises — a plausible-looking curve with coherence near zero.
    /// See <c>PinkNoiseShaper.WhiteAt</c>. Averaging is what makes the number meaningful: coherence
    /// is identically 1 for a single record however unrelated the channels are.</para>
    /// </summary>
    [Fact]
    public async Task ByPinkNoise_ReportsFullCoherence()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(averages: 3) with { Stimulus = ImpedanceStimulus.PinkNoise }, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);

        // Across the middle of the band, away from the analysis edges.
        int from = trace.FreqHz.Length / 8;
        int to = trace.FreqHz.Length - (trace.FreqHz.Length / 8);
        for (int i = from; i < to; i++)
        {
            Assert.True(
                trace.Coherence[i] > 0.99,
                $"Coherence at {trace.FreqHz[i]:0} Hz was {trace.Coherence[i]:0.000}, which means the reference channel is not carrying the stimulus.");
        }
    }

    /// <summary>
    /// A noise pass publishes its curve after every record, not only when the pass ends.
    ///
    /// <para>Worth a test rather than a comment because it is the one place the two whole-band
    /// stimuli deliberately behave differently, and because the reason is a property of the
    /// stimulus: every noise record is an independent draw, so the estimate visibly improves as they
    /// accumulate, while a chirp's records repeat one deterministic sweep and a partial average of
    /// those is the same curve at more expense. Four averages of a 0.68 s record is nearly three
    /// seconds of a chart that would otherwise sit empty.</para>
    /// </summary>
    [Fact]
    public async Task ByPinkNoise_PublishesACurveBeforeTheAveragesAreDone()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(averages: 4) with { Stimulus = ImpedanceStimulus.PinkNoise }, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        ImpedanceStatus? midPass = null;

        while (harness.Runner.Running && DateTime.UtcNow < deadline)
        {
            var status = harness.Runner.Status;
            if (status.Phase == FrequencyResponsePhase.Measuring
                && status.PassesCompleted == 0
                && status.AveragesTaken is > 0 and < 4
                && status.Traces.Count > 0)
            {
                midPass = status;
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(midPass);
        Assert.Equal(ImpedanceTraceKind.Impedance, midPass.Traces[0].Kind);

        // And the partial curve is a real measurement rather than a placeholder.
        var (ohms, _) = At(midPass.Traces[0], 1000.0);
        Assert.InRange(
            ImpedanceMath.ToDbOhms(ohms) - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms),
            -0.01, 0.01);

        AssertFinished(await AwaitCompletionAsync(harness.Runner));
    }

    /// <summary>
    /// In RUN mode a noise measurement is one sliding average, not a sequence of restarts. The
    /// noise path publishes after every record, so an estimator recreated per pass put a
    /// single-record curve on screen once every N records -- the worst curve of the run, on a
    /// schedule. Once the first pass has filled the window, every later record replaces the
    /// oldest and the status never reads fewer than N records again.
    /// </summary>
    [Fact]
    public async Task ByPinkNoise_Continuous_SlidesTheAverageInsteadOfRestartingIt()
    {
        const int averages = 3;
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(averages: averages, continuous: true) with { Stimulus = ImpedanceStimulus.PinkNoise },
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        var afterFirstPass = new List<int>();

        while (harness.Runner.Status.PassesCompleted < 3)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("no third pass within 60 s");

            var status = harness.Runner.Status;
            if (status.PassesCompleted >= 1 && status.Phase == FrequencyResponsePhase.Measuring)
                afterFirstPass.Add(status.AveragesTaken);

            await Task.Delay(5);
        }

        var final = harness.Runner.Status;
        await harness.Runner.CancelAsync();

        Assert.NotEmpty(afterFirstPass);
        Assert.All(afterFirstPass, taken => Assert.Equal(averages, taken));
        Assert.Equal(averages, final.AveragesTaken);

        // And the sliding estimate is still the fixture's resistor.
        var (ohms, _) = At(final.Traces[0], 1000.0);
        Assert.InRange(
            ImpedanceMath.ToDbOhms(ohms) - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms),
            -0.01, 0.01);
    }

    /// <summary>
    /// A gate is a sweep's business, and asking for one on a noise run must be ignored rather than
    /// obeyed: those harmonic packets exist because sweep deconvolution folds each harmonic into its
    /// own impulse, and a noise estimate has no such structure to delete.
    /// </summary>
    [Fact]
    public async Task ByPinkNoise_IgnoresTheSweepGate()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(averages: 2) with { Stimulus = ImpedanceStimulus.PinkNoise, ApplySweepGate = true },
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);
        var (ohms, _) = At(trace, 1000.0);

        double errorDb = ImpedanceMath.ToDbOhms(ohms) - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms);
        Assert.InRange(errorDb, -0.01, 0.01);
    }

    /// <summary>
    /// A series R and L: the magnitude has to rise and the phase go positive with it. Reading a
    /// rising magnitude with a flat phase is the classic symptom of the two channels coming from
    /// different records, which this measurement cannot tolerate.
    /// </summary>
    [Fact]
    public async Task AgainstTheReactiveFixture_ReadsRisingImpedanceWithInductivePhase()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceReactive);
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);

        var low = At(trace, 100.0);
        var corner = At(trace, 955.0);

        // Z = 6 + j*2*pi*f*1mH.
        Assert.Equal(6.033, low.Ohms, 2);
        Assert.Equal(5.98, low.PhaseDeg, 1);

        // The 45-degree point, where the reactance equals the resistance.
        Assert.Equal(8.49, corner.Ohms, 1);
        Assert.Equal(45.0, corner.PhaseDeg, 0);
    }

    /// <summary>
    /// The shape an impedance sweep is normally run to see. At resonance everything but the motional
    /// term cancels, so the peak is exactly <c>Re + Rres</c> and purely resistive.
    /// </summary>
    [Fact]
    public async Task AgainstTheLoudspeakerFixture_PutsItsPeakAtTheResonance()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceLoudspeaker);
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);

        Assert.Equal(SimulatedImpedanceFixture.SpeakerResonanceHz, trace.PeakFreqHz, 0);
        Assert.Equal(SimulatedImpedanceFixture.SpeakerPeakOhms, ImpedanceMath.FromDbOhms(trace.PeakDbOhms), 0);

        // Well above resonance only the voice coil is left.
        Assert.Equal(SimulatedImpedanceFixture.SpeakerReOhms, At(trace, 10_000.0).Ohms, 2);
    }

    /// <summary>
    /// A calibration run captures a standard and hands it back on the status, because the server
    /// keeps none of its own — the browser stores it in the measurement type's params_json.
    /// </summary>
    [Fact]
    public async Task OpenCalibration_ReturnsAStandardTheBrowserCanStore()
    {
        var harness = await CreateAsync(SimulatedDutModel.StraightWire);
        await harness.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Open), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(ImpedanceCalTarget.Open, status.CalTarget);

        var calibration = Assert.IsType<ImpedanceCalibration>(status.CapturedCalibration);
        Assert.True(calibration.IsUsable);
        Assert.Equal(ImpedanceCalibration.DefaultPoints, calibration.FreqHz.Length);

        var trace = Assert.Single(status.Traces);
        Assert.Equal(ImpedanceTraceKind.OpenCal, trace.Kind);

        // A straight wire is a perfect open standard: both channels see the generator, so the ratio
        // is unity and the stored curve reads 0 dB with no phase.
        Assert.Equal(1.0, calibration.At(1000.0).Magnitude, 3);
        Assert.Equal(0.0, calibration.At(1000.0).Phase, 3);
    }

    /// <summary>
    /// And applying that standard to a real fixture must not change an answer that was already
    /// right. A calibration is a correction, so against a simulator that needs none it has to be a
    /// no-op rather than a small shift.
    /// </summary>
    [Fact]
    public async Task AnAppliedOpenCalibration_LeavesACorrectMeasurementAlone()
    {
        var calibrationHarness = await CreateAsync(SimulatedDutModel.StraightWire);
        await calibrationHarness.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Open), CancellationToken.None);
        var open = (await AwaitCompletionAsync(calibrationHarness.Runner)).CapturedCalibration;
        Assert.NotNull(open);

        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(Request(openCal: open), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(SimulatedImpedanceFixture.ResistiveOhms, At(Assert.Single(status.Traces), 1000.0).Ohms, 3);
    }

    /// <summary>
    /// The short standard, captured from the simulator's own shorted-leads fixture rather than a
    /// synthetic curve: 0.12 ohm over a 100 ohm reference is a divider ratio of -58 dB, and the run
    /// has to resolve that. Applied to the 8 ohm fixture it then subtracts leads that fixture does not
    /// have, so the corrected reading is 7.88 ohms — which is exactly the arithmetic a tester will see
    /// walking the open/short/measure flow against the simulator, and worth pinning as such.
    /// </summary>
    [Fact]
    public async Task AShortCalibration_SubtractsTheLeadsItMeasured()
    {
        var calibrationHarness = await CreateAsync(SimulatedDutModel.ImpedanceShort);
        await calibrationHarness.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Short), CancellationToken.None);
        var calibrationStatus = await AwaitCompletionAsync(calibrationHarness.Runner);

        AssertFinished(calibrationStatus);
        Assert.Equal(ImpedanceTraceKind.ShortCal, Assert.Single(calibrationStatus.Traces).Kind);
        var shortCal = Assert.IsType<ImpedanceCalibration>(calibrationStatus.CapturedCalibration);

        double leadRatio = SimulatedImpedanceFixture.ShortOhms
            / (SimulatedImpedanceFixture.ReferenceOhms + SimulatedImpedanceFixture.ShortOhms);
        Assert.Equal(leadRatio, shortCal.At(1000.0).Magnitude, leadRatio * 0.02);

        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(cableMode: ImpedanceCableMode.Measured, shortCal: shortCal), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(
            SimulatedImpedanceFixture.ResistiveOhms - SimulatedImpedanceFixture.ShortOhms,
            At(Assert.Single(status.Traces), 1000.0).Ohms, 2);
    }

    /// <summary>
    /// The wiring mistake the numbers can prove. The reference channel the wrong way round against
    /// the 8 ohm fixture reads <c>Z = 100·13.5/(1 − 13.5) = −108 ohms</c> — drawn as a flat 108 ohm
    /// line with the phase at ±180 degrees, which was reported as a wrong axis scale. A passive load
    /// cannot go negative, so the run has to say what it is instead of drawing it straight-faced;
    /// the correct wiring must stay silent.
    /// </summary>
    [Fact]
    public async Task TheWrongReferenceChannel_IsCalledOutAsAWarning_AndTheRightOneIsNot()
    {
        var wrong = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await wrong.Runner.StartAsync(Request(rightChannelIsReference: true), CancellationToken.None);
        var wrongStatus = await AwaitCompletionAsync(wrong.Runner);

        AssertFinished(wrongStatus);
        Assert.Equal(108.0, At(Assert.Single(wrongStatus.Traces), 1000.0).Ohms, 1);
        Assert.NotNull(wrongStatus.Warning);
        Assert.Contains("try Left", wrongStatus.Warning);

        var right = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await right.Runner.StartAsync(Request(), CancellationToken.None);
        var rightStatus = await AwaitCompletionAsync(right.Runner);

        AssertFinished(rightStatus);
        Assert.Null(rightStatus.Warning);
    }

    /// <summary>
    /// The other setup mistake the numbers can prove, and a sharper one: a standard captured with
    /// the fixture in the wrong state. Measured on a real fixture on 2026-09-24, an open taken with
    /// the leads still shorted stored |H| = 0.00084 where an open reads 0.9998 -- and nothing
    /// downstream could tell, because the resulting measurement is a clean, coherent, flat negative
    /// resistance rather than anything that looks broken.
    /// </summary>
    [Fact]
    public async Task AnOpenCapturedOverAShort_IsCalledOutWhenItIsCaptured()
    {
        var wrong = await CreateAsync(SimulatedDutModel.ImpedanceShort);
        await wrong.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Open), CancellationToken.None);
        var wrongStatus = await AwaitCompletionAsync(wrong.Runner);

        AssertFinished(wrongStatus);
        Assert.NotNull(wrongStatus.Warning);
        Assert.Contains("a short, not an open", wrongStatus.Warning);

        var right = await CreateAsync(SimulatedDutModel.ImpedanceOpen);
        await right.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Open), CancellationToken.None);
        var rightStatus = await AwaitCompletionAsync(right.Runner);

        AssertFinished(rightStatus);
        Assert.Null(rightStatus.Warning);
    }

    /// <summary>The same test the other way round, so a short taken with nothing connected is caught too.</summary>
    [Fact]
    public async Task AShortCapturedOverAnOpen_IsCalledOutWhenItIsCaptured()
    {
        var wrong = await CreateAsync(SimulatedDutModel.ImpedanceOpen);
        await wrong.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Short), CancellationToken.None);
        var status = await AwaitCompletionAsync(wrong.Runner);

        AssertFinished(status);
        Assert.NotNull(status.Warning);
        Assert.Contains("an open, not a short", status.Warning);
    }

    /// <summary>
    /// The case that actually bites: the bad standard was captured once, days ago, and now silently
    /// ruins every measurement corrected with it. Checked at run start so the warning is on screen
    /// before the first pass -- and it must survive the pass, because the bad correction also drives
    /// the impedance negative and so raises the swapped-channel warning, which blames the wiring and
    /// sends the search to the bench. That is exactly what happened on 2026-09-24.
    /// </summary>
    [Fact]
    public async Task AMeasurementCorrectedWithAnOpenStandardThatIsAShort_SaysSoBeforeItIsBelieved()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(openCal: FlatStandard(0.00084)), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.NotNull(status.Warning);
        Assert.Contains("stored open standard", status.Warning);
    }

    /// <summary>A stored short that is really an open, which makes the lead subtraction meaningless.</summary>
    [Fact]
    public async Task AMeasurementCorrectedWithAShortStandardThatIsAnOpen_SaysSo()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(cableMode: ImpedanceCableMode.Measured, shortCal: FlatStandard(0.98)),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.NotNull(status.Warning);
        Assert.Contains("stored short standard", status.Warning);
    }

    /// <summary>A standard of one flat magnitude across the band, which is what both of them are.</summary>
    private static ImpedanceCalibration FlatStandard(double magnitude)
    {
        var freqHz = new double[] { 10.0, 100.0, 1000.0, 10_000.0, 20_000.0 };
        var real = new double[freqHz.Length];
        Array.Fill(real, magnitude);
        return new ImpedanceCalibration(freqHz, real, new double[freqHz.Length]);
    }

    /// <summary>
    /// The reference resistor is not cosmetic: it scales the answer directly, so entering the wrong
    /// one is a silent proportional error. Halving it must halve the reported impedance.
    /// </summary>
    [Fact]
    public async Task TheReferenceResistorScalesTheAnswer()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            Request(referenceOhms: SimulatedImpedanceFixture.ReferenceOhms / 2.0), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(SimulatedImpedanceFixture.ResistiveOhms / 2.0, At(Assert.Single(status.Traces), 1000.0).Ohms, 3);
    }

    [Fact]
    public async Task Start_WhileAlreadyRunning_IsRejected()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(Request(averages: 3), CancellationToken.None);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Runner.StartAsync(Request(), CancellationToken.None));
        }
        finally
        {
            await harness.Runner.CancelAsync();
        }
    }

    [Fact]
    public async Task Clear_WhileRunning_IsRejected()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(Request(averages: 3), CancellationToken.None);

        try
        {
            Assert.Throws<InvalidOperationException>(harness.Runner.Clear);
        }
        finally
        {
            await harness.Runner.CancelAsync();
        }
    }

    [Fact]
    public async Task Clear_AfterARun_EmptiesTheTraces()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        var cleared = harness.Runner.Clear();

        Assert.Empty(cleared.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, cleared.Phase);
    }

    // ---------------------------------------------------------------- stepped sine

    /// <summary>
    /// A stepped grid small enough to run in a test and wide enough to catch a frequency-dependent
    /// error: 100 Hz to 10 kHz at one point per octave is seven tones.
    /// </summary>
    private static ImpedanceRequest SteppedRequest(
        int averages = 1,
        ImpedanceCalTarget calTarget = ImpedanceCalTarget.None,
        ImpedanceStimulus calibrationStimulus = ImpedanceStimulus.SteppedSine,
        ImpedanceCalibration? openCal = null) =>
        Request(averages: averages, calTarget: calTarget, openCal: openCal) with
        {
            Stimulus = ImpedanceStimulus.SteppedSine,
            CalibrationStimulus = calibrationStimulus,
            StartFreqHz = 100.0,
            EndFreqHz = 10_000.0,
            StepsPerOctave = 1,
            AdaptiveResolution = true,
        };

    /// <summary>
    /// The calibration target, measured one tone at a time. An 8 ohm resistor has the same impedance
    /// at every frequency and no reactance at all, so this is the same arithmetic check the chirp
    /// path gets — through a completely different acquisition loop.
    ///
    /// <para>Asserted at <b>every</b> point rather than at a few chosen ones, because a stepped run's
    /// characteristic failure is one bad point rather than a tilted curve: a record that still holds
    /// part of the previous tone reads that one bin low and leaves its neighbours untouched.</para>
    /// </summary>
    [Fact]
    public async Task SteppedSine_AgainstTheResistiveFixture_ReadsItsResistorAtEveryPoint()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(SteppedRequest(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);
        Assert.Equal(ImpedanceTraceKind.Impedance, trace.Kind);
        Assert.Equal(status.PointCount, trace.FreqHz.Length);
        Assert.InRange(trace.FreqHz.Length, 6, 8);

        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            double ohms = ImpedanceMath.FromDbOhms(trace.MagnitudeMaxDbOhms[i]);
            double errorDb = ImpedanceMath.ToDbOhms(ohms)
                - ImpedanceMath.ToDbOhms(SimulatedImpedanceFixture.ResistiveOhms);

            Assert.True(
                Math.Abs(errorDb) < 0.01,
                $"{trace.FreqHz[i]:0.##} Hz read {ohms:0.0000} ohms ({errorDb:+0.0000;-0.0000} dB)");
            Assert.Equal(0.0, trace.PhaseDeg[i], 2);
        }
    }

    /// <summary>
    /// Nothing is decimated and nothing is smoothed, so the min and max of a "column" are the same
    /// number — every sample here <i>is</i> a measurement. The chart draws the max as the line and
    /// the min as a band beneath it, and a min that had drifted away from the max would draw a band
    /// around a curve that has no spread.
    /// </summary>
    [Fact]
    public async Task SteppedSine_ProducesOnePointPerTone_WithNoBandBetweenMinAndMax()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        var started = await harness.Runner.StartAsync(SteppedRequest(), CancellationToken.None);

        Assert.Equal(ImpedanceStimulus.SteppedSine, started.Stimulus);
        Assert.NotNull(started.PointCount);
        Assert.NotNull(started.FrequenciesHz);
        Assert.Equal(started.PointCount, started.FrequenciesHz!.Count);
        Assert.True(started.EstimatedSeconds > 0.0);

        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);
        AssertFinished(status);

        var trace = Assert.Single(status.Traces);
        Assert.Equal(trace.MagnitudeMinDbOhms, trace.MagnitudeMaxDbOhms);
        Assert.Equal(started.FrequenciesHz.Count, trace.FreqHz.Length);
        Assert.Equal(started.PointCount, status.PointsMeasured);
    }

    /// <summary>
    /// Every point against the closed form of a series R and L. Magnitude alone would pass with the
    /// two channels taken from different records; the phase is what does not.
    ///
    /// <para><b>The closed form is the digital one, and that is the whole reason this asserts to
    /// 0.02 dB rather than to a decimal place.</b> The fixture is a bilinear-transformed biquad, so
    /// its reactance follows <c>(2/T)tan(pi f / fs)</c> rather than <c>2 pi f</c> — the two agree at
    /// the bottom of the band and part company as the tone climbs toward Nyquist. Measured here, the
    /// gap is 0.001 dB at 100 Hz and <b>0.047 dB at 2.13 kHz</b>, which is larger than anything the
    /// measurement itself contributes and would read as a rising magnitude error. It is not one: the
    /// analog formula is simply not what this DUT is. The chirp's own test never met it because it
    /// asserts at 100 Hz and 955 Hz only.</para>
    /// </summary>
    [Fact]
    public async Task SteppedSine_AgainstTheReactiveFixture_FollowsTheAnalyticImpedance()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceReactive);
        await harness.Runner.StartAsync(SteppedRequest(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);

        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            // The bilinear transform maps analog frequency onto the tangent of the digital one, so
            // this is the analog network's reactance evaluated where the digital filter actually
            // sits.
            double warpedRadians = 2.0 * status.SampleRateHz
                * Math.Tan(Math.PI * trace.FreqHz[i] / status.SampleRateHz);
            double reactance = warpedRadians * SimulatedImpedanceFixture.ReactiveHenry;

            double expectedOhms = Math.Sqrt((SimulatedImpedanceFixture.ReactiveOhms * SimulatedImpedanceFixture.ReactiveOhms)
                + (reactance * reactance));
            double expectedDeg = Math.Atan2(reactance, SimulatedImpedanceFixture.ReactiveOhms) * 180.0 / Math.PI;

            double ohms = ImpedanceMath.FromDbOhms(trace.MagnitudeMaxDbOhms[i]);

            Assert.True(
                Math.Abs(ImpedanceMath.ToDbOhms(ohms) - ImpedanceMath.ToDbOhms(expectedOhms)) < 0.02,
                $"{trace.FreqHz[i]:0.##} Hz read {ohms:0.000} ohms, expected {expectedOhms:0.000}");
            Assert.True(
                Math.Abs(trace.PhaseDeg[i] - expectedDeg) < 0.5,
                $"{trace.FreqHz[i]:0.##} Hz read {trace.PhaseDeg[i]:0.00} degrees, expected {expectedDeg:0.00}");
        }
    }

    /// <summary>
    /// Coherence is measured here rather than written as 1.0, which is the one place the stepped path
    /// is better informed than the chirp's. On a noiseless simulator it has to come back at 1 —
    /// anything less would mean the runner is comparing records that are not the same measurement.
    /// </summary>
    [Fact]
    public async Task SteppedSine_WithAveraging_ReportsMeasuredCoherence()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(SteppedRequest(averages: 3), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 180);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);

        foreach (float coherence in trace.Coherence)
            Assert.InRange(coherence, 0.999f, 1.0f);
    }

    /// <summary>
    /// A stepped calibration stores the points it measured, and stores them uncorrected: an open
    /// standard normalised against a previous open would report the difference between two
    /// calibrations as an impedance.
    /// </summary>
    [Fact]
    public async Task SteppedSine_CapturingAnOpenStandard_StoresItsOwnPoints()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        await harness.Runner.StartAsync(
            SteppedRequest(calTarget: ImpedanceCalTarget.Open), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);

        AssertFinished(status);
        Assert.Equal(ImpedanceTraceKind.OpenCal, Assert.Single(status.Traces).Kind);

        var calibration = Assert.IsType<ImpedanceCalibration>(status.CapturedCalibration);
        Assert.True(calibration.IsUsable);
        Assert.Equal(status.PointCount, calibration.FreqHz.Length);

        // A calibration run measures a standard rather than an impedance, so it has no median to
        // report -- and it must say so with null, never NaN, which truncates the whole HTTP response.
        Assert.Null(status.MedianOhms);
        Assert.Null(status.DriveCurrentAmps);
    }

    /// <summary>
    /// The pairing the separate calibration selector exists for: a standard captured by the fast
    /// chirp corrects a measurement taken one tone at a time.
    ///
    /// <para>It works because a standard is a curve in <i>frequency</i> —
    /// <see cref="ImpedanceCalibration.At"/> interpolates in log f — and because a fixture standard
    /// is a property of a linear, passive network, which does not depend on how it was excited.</para>
    ///
    /// <para><b>The short standard rather than the open, and the reactive fixture rather than the
    /// resistive one</b>, because those two choices are what make the test discriminating. The
    /// simulator cannot present an open circuit — its fixture models are always some load — so a
    /// captured "open" would be a divider reading, and dividing a later measurement by it drives
    /// <c>H</c> to 1, where <c>Z = R·H/(1−H)</c> runs to megohms and any error is amplified beyond
    /// meaning (measured: 15.5 Mohm). A short standard is subtracted rather than divided by, so the
    /// arithmetic stays well conditioned and the right answer is <b>zero</b>. And it has to be a
    /// <i>frequency-dependent</i> standard: subtracting a flat 8 ohms would cancel even if the curve
    /// had been resampled at entirely the wrong frequencies, which is exactly the failure this is
    /// here to catch.</para>
    /// </summary>
    [Fact]
    public async Task AChirpCapturedShortStandard_CorrectsASteppedMeasurementAtItsOwnFrequencies()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceReactive);

        // The chirp calibration, run through the same runner with only the calibration selector
        // pointing at it -- exactly what the panel's Measure short button sends.
        await harness.Runner.StartAsync(
            SteppedRequest(calTarget: ImpedanceCalTarget.Short, calibrationStimulus: ImpedanceStimulus.Chirp),
            CancellationToken.None);
        var calStatus = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);

        AssertFinished(calStatus);
        Assert.Equal(ImpedanceStimulus.Chirp, calStatus.Stimulus);
        Assert.Null(calStatus.PointCount);
        Assert.Equal(ImpedanceTraceKind.ShortCal, Assert.Single(calStatus.Traces).Kind);
        var shortCal = Assert.IsType<ImpedanceCalibration>(calStatus.CapturedCalibration);

        var request = SteppedRequest(calibrationStimulus: ImpedanceStimulus.Chirp) with
        {
            CableMode = ImpedanceCableMode.Measured,
            ShortCal = shortCal,
            // Pinned to the rate the chirp standard was captured at, and that is about the
            // *simulator* rather than about this measurement. Its fixtures are Biquad.FromAnalog
            // without prewarping, so a simulated impedance is genuinely a slightly different
            // impedance at 48 kHz than at 192 kHz -- a few per cent by the top of this grid. A real
            // fixture has one impedance whatever the analyzer is clocked at, which is the whole
            // premise of reusing a standard across stimuli; leaving the rates to differ here would
            // measure the simulator's warping instead of the resample this test is about.
            AdaptiveResolution = false,
            SteppedSampleRateHz = ImpedanceRequest.MaxSpanHz * 2.0,
        };

        await harness.Runner.StartAsync(request, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);

        AssertFinished(status);
        Assert.Equal(ImpedanceStimulus.SteppedSine, status.Stimulus);

        var trace = Assert.Single(status.Traces);
        for (int i = 0; i < trace.FreqHz.Length; i++)
        {
            double ohms = ImpedanceMath.FromDbOhms(trace.MagnitudeMaxDbOhms[i]);

            // The fixture measured through itself is nothing at all. The residual is the estimator's
            // own regularisation bias plus the interpolation of a 256-point stored curve onto these
            // seven frequencies -- both small, and neither growing with frequency, which is what a
            // misaligned resample would do.
            Assert.True(
                ohms < 0.1,
                $"{trace.FreqHz[i]:0.##} Hz read {ohms:0.0000} ohms after subtracting its own standard");
        }
    }

    /// <summary>
    /// And the same run without the standard, so the assertion above is known to bite. A test whose
    /// "corrected to nothing" would also pass uncorrected proves nothing.
    /// </summary>
    [Fact]
    public async Task WithoutTheShortStandard_TheSameSteppedRunReadsTheFixture()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceReactive);
        await harness.Runner.StartAsync(SteppedRequest(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 120);

        AssertFinished(status);
        var trace = Assert.Single(status.Traces);

        foreach (float dbOhms in trace.MagnitudeMaxDbOhms)
            Assert.True(ImpedanceMath.FromDbOhms(dbOhms) > SimulatedImpedanceFixture.ReactiveOhms - 0.1);
    }

    /// <summary>
    /// A stepped run reports points; a chirp run reports none. The browser tells the two progress
    /// lines apart by the field's presence rather than by the phase, so a chirp that quietly reported
    /// zero points would read as a sweep that had not started.
    /// </summary>
    [Fact]
    public async Task AChirpRun_ReportsNoPointCountAtAll()
    {
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        var started = await harness.Runner.StartAsync(Request(), CancellationToken.None);

        Assert.Null(started.PointCount);
        Assert.Null(started.FrequenciesHz);
        Assert.Null(started.EstimatedSeconds);

        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        Assert.Null(status.PointCount);
        Assert.Null(status.PointsMeasured);
        Assert.Equal(ImpedanceStimulus.Chirp, status.Stimulus);
    }

    [Fact]
    public void Validate_RejectsTheTwoLoadMethod() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (Request() with { Method = ImpedanceMethod.TwoLoad }).Validate());

    /// <summary>
    /// Noise repeats happily, unlike a stepped sweep: a pass is a handful of records, and each new
    /// one genuinely improves the estimate.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAContinuousNoiseRun() =>
        (Request(continuous: true) with { Stimulus = ImpedanceStimulus.PinkNoise }).Validate();

    [Fact]
    public void Validate_RejectsARepeatingCalibration() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Request(calTarget: ImpedanceCalTarget.Open, continuous: true).Validate());

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(2.0e7)]
    [InlineData(double.NaN)]
    public void Validate_RejectsAnImpossibleReferenceResistor(double ohms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(referenceOhms: ohms).Validate());

    /// <summary>Watts name a power, and a one-port has no output power to aim at.</summary>
    [Fact]
    public void Validate_RejectsAWattsDriveLevel() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (Request() with { LevelUnit = ThdSweepLevelUnit.Watt, LevelValue = 1.0 }).Validate());

    [Fact]
    public async Task ARunSwitchedAwayFromAndBackTo_IsStillThere()
    {
        // The sweep report of 2026-10-17 in this runner: one impedance runner serves every
        // impedance measurement, so selecting a second one and coming back used to find an empty
        // chart. See ThdSweepRunner.SwitchOwner.
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        var measured = harness.Runner.Status;
        Assert.NotEmpty(measured.Traces);

        // A different impedance measurement: this one has never run, so it gets an empty chart.
        var other = harness.Runner.SwitchOwner("1:2");
        Assert.Empty(other.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, other.Phase);
        Assert.Null(other.MedianOhms);
        Assert.Empty(harness.Runner.Status.Traces);

        var back = harness.Runner.SwitchOwner("1:1");

        Assert.Equal(measured.Traces.Count, back.Traces.Count);
        Assert.Equal(measured.Phase, back.Phase);
        Assert.Equal(measured.ReferenceOhms, back.ReferenceOhms);
        Assert.Equal(measured.MedianOhms, back.MedianOhms);
        Assert.Equal(back.Traces.Count, harness.Runner.Status.Traces.Count);
        // And the as-run request with it, or the restored run could be looked at and not saved:
        // MeasurementSaveService.SaveImpedanceAsync refuses without one.
        Assert.NotNull(harness.Runner.LastRequest);
    }

    [Fact]
    public async Task ACapturedStandardIsParkedWithTheRunThatCapturedIt()
    {
        // A calibration writes a trace like any other run, so it parks like one -- without which
        // capturing an open, glancing at another measurement and coming back would lose it before
        // the short was ever taken.
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceOpen);
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(calTarget: ImpedanceCalTarget.Open), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        Assert.NotNull(harness.Runner.Status.CapturedCalibration);

        Assert.Null(harness.Runner.SwitchOwner("1:2").CapturedCalibration);

        var back = harness.Runner.SwitchOwner("1:1");
        Assert.NotNull(back.CapturedCalibration);
        Assert.Equal(ImpedanceCalTarget.Open, back.CalTarget);
    }

    [Fact]
    public async Task SwitchingOwnerWhileARunIsGoing_IsIgnoredRatherThanRacingTheWriter()
    {
        // Unreachable from the app -- a measurement switch is refused while anything runs -- but a
        // park that lifted the traces a pass is about to replace would be far worse than a no-op.
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(continuous: true), CancellationToken.None);
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
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);
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
        var harness = await CreateAsync(SimulatedDutModel.ImpedanceResistive);

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        int measured = harness.Runner.Status.Traces.Count;

        Assert.Equal(measured, harness.Runner.SwitchOwner("1:1").Traces.Count);
    }
}
