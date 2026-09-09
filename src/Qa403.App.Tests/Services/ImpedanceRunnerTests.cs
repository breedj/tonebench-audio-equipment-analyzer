using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The impedance runner end to end against the simulator's fixture models, whose impedance is known
/// by construction.
///
/// <para><b>The reference channel is Right throughout</b>, which looks backwards against a bench
/// fixture and is not a mistake: the simulator applies its DUT to the left channel only, so its
/// divider output is on the left and the untouched generator is on the right. See
/// <see cref="SimulatedImpedanceFixture"/>.</para>
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
        double chirpStartHz = 10.0) =>
        new(ImpedanceMethod.ReferenceResistor,
            ImpedanceStimulus.Chirp,
            referenceOhms,

            // Right, because the simulator's fixture is on the left channel.
            RightChannelIsReference: true,
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

    [Theory]
    [InlineData(ImpedanceMethod.TwoLoad, ImpedanceStimulus.Chirp)]
    [InlineData(ImpedanceMethod.ReferenceResistor, ImpedanceStimulus.SteppedSine)]
    [InlineData(ImpedanceMethod.ReferenceResistor, ImpedanceStimulus.PinkNoise)]
    public void Validate_RejectsWhatIsNotImplementedYet(ImpedanceMethod method, ImpedanceStimulus stimulus) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (Request() with { Method = method, Stimulus = stimulus }).Validate());

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
}
