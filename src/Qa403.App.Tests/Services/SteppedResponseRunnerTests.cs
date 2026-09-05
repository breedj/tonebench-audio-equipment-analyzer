using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

public class SteppedResponseRunnerTests
{
    /// <summary>
    /// A short grid: two octaves at two points each. Every point is a full capture plus a flush, so
    /// the point count is the whole cost of these tests — and nothing under test depends on the grid
    /// being wide.
    /// </summary>
    private static SteppedResponseRequest Request(
        double startFreqHz = 1000.0,
        double endFreqHz = 4000.0,
        int stepsPerOctave = 2,
        ThdSweepLevelMode levelMode = ThdSweepLevelMode.GeneratorVoltage,
        double levelValue = -20.0,
        ThdSweepLevelUnit levelUnit = ThdSweepLevelUnit.Dbv,
        double loadOhms = 8.0,
        bool generateLeft = true,
        bool generateRight = true,
        int averages = 1,
        bool rightIsReference = true,
        bool adaptiveResolution = true,
        int fftSize = 16384,
        double sampleRateHz = 48_000.0) =>
        new(startFreqHz, endFreqHz, stepsPerOctave, levelMode, levelValue, levelUnit, loadOhms,
            generateLeft, generateRight, fftSize, WindowType.Hann, averages, sampleRateHz,
            adaptiveResolution, rightIsReference);

    private sealed record Harness(SteppedResponseRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

    /// <summary>
    /// A clean simulated DUT: no fabricated harmonic stack, no noise, no hum — a straight wire, the
    /// one case whose right answer is known exactly. The simulator's default profile deliberately
    /// dirties the signal to make the FFT screen look realistic, which would put a floor under every
    /// assertion here.
    /// </summary>
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
            new SteppedResponseRunner(session, pipeline, hub, NullLogger<SteppedResponseRunner>.Instance),
            session,
            pipeline);
    }

    private static async Task<FrequencyResponseStatus> AwaitCompletionAsync(SteppedResponseRunner runner, int timeoutSeconds = 120)
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
    public async Task ARun_ProducesTheSameThreeTracesTheChirpDoes_OnePointEach()
    {
        var harness = await CreateAsync();

        var started = await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(started.PointCount, status.PointCount);
        Assert.Equal(started.PointCount, status.PointsMeasured);

        // The Left/Right/Gain triple is what makes a stepped result readable by every path that
        // already reads a chirp one -- the chart, response_trace, the CSV exporter.
        Assert.Equal(
            new[] { FrequencyResponseTraceKind.Left, FrequencyResponseTraceKind.Right, FrequencyResponseTraceKind.Gain },
            status.Traces.Select(t => t.Kind).ToArray());

        foreach (var trace in status.Traces)
        {
            Assert.Equal(started.PointCount, trace.FreqHz.Length);
            Assert.Equal(started.PointCount, trace.MagnitudeMaxDb.Length);
            Assert.Equal(started.PointCount, trace.PhaseDeg.Length);
            Assert.Equal(started.PointCount, trace.Coherence.Length);

            // Nothing is decimated: every sample here *is* a measurement, so the min and the max of
            // a column are the same number rather than the ends of a range of bins.
            Assert.Equal(trace.MagnitudeMaxDb, trace.MagnitudeMinDb);

            // One deterministic capture per point has no cross-record variance to report.
            Assert.All(trace.Coherence, c => Assert.Equal(1.0f, c));

            Assert.Equal(trace.FreqHz[0], (float)trace.StartFreqHz);
            Assert.Equal(trace.FreqHz[^1], (float)trace.EndFreqHz);
        }
    }

    [Fact]
    public async Task AgainstAUnityDut_GainIsZeroDbAndPhaseIsZero()
    {
        // The simulator loops the generator straight back, and identical per-channel config yields
        // bit-identical L and R -- so "0 dB, 0°" is exact here rather than approximate, which is
        // what makes it worth asserting tightly.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        var gain = Trace(status, FrequencyResponseTraceKind.Gain);
        for (int i = 0; i < gain.MagnitudeMaxDb.Length; i++)
        {
            Assert.InRange(gain.MagnitudeMaxDb[i], -0.1, 0.1);
            Assert.InRange(gain.PhaseDeg[i], -1.0, 1.0);
        }

        // And the channels' own traces read the generator level they were driven at, in dBV -- the
        // same units the FFT screen reads in.
        var left = Trace(status, FrequencyResponseTraceKind.Left);
        Assert.All(left.MagnitudeMaxDb, db => Assert.InRange(db, -20.5, -19.5));
    }

    [Fact]
    public async Task WithoutAMeasuredReference_ThereIsNoPhaseToReport()
    {
        // A stepped sine has no capture trigger, so one channel's absolute angle is wherever the
        // record happened to land in the tone -- uniform over a period and different every point.
        // Reporting it would look like a wildly non-minimum-phase DUT.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(rightIsReference: false), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(Trace(status, FrequencyResponseTraceKind.Gain).PhaseDeg, deg => Assert.Equal(0.0f, deg));
    }

    [Fact]
    public async Task AChannelsOwnTrace_NeverClaimsAPhase()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(Trace(status, FrequencyResponseTraceKind.Left).PhaseDeg, deg => Assert.Equal(0.0f, deg));
        Assert.All(Trace(status, FrequencyResponseTraceKind.Right).PhaseDeg, deg => Assert.Equal(0.0f, deg));
    }

    [Fact]
    public async Task OutputVoltageMode_FromATooSensitiveRange_StillSettlesAtTheRequestedLevel()
    {
        // The ordering this exists for, and the same trap the chirp path fell into. The level probe
        // measures the DUT's gain from its own captures, so a leftover input range too sensitive for
        // the target clips them; the probe reads that loss as missing gain and pushes the generator
        // up, which clips harder still. Measured on the chirp path before the range was moved ahead
        // of the probe: a 6 dBV target settled the generator at 16.74 dBV, a 10.7 dB overshoot.
        var harness = await CreateAsync();
        await harness.Session.SetInputRangeAsync(0.0, CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: 6.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.InRange(status.GeneratorAmpDbv, 5.5, 6.5);
        Assert.All(Trace(status, FrequencyResponseTraceKind.Left).MagnitudeMaxDb, db => Assert.InRange(db, 5.0, 7.0));
    }

    [Fact]
    public async Task TheLevelProbe_ReadsTheLeftChannelRatherThanTheLouderOne()
    {
        // The right input carries the reference at the *generator's* level. Taking the louder of the
        // two -- which is what ThdSweepRunner does, correctly, because there both channels are DUT
        // paths -- would lock the probe onto that reference and solve for the generator level
        // instead of for the DUT's output, silently turning "1 W into 8 ohm" into "the generator at
        // 2.83 V".
        //
        // Discriminating case: the tone plays on the right channel only, so the reference is present
        // at the generator's level and the DUT's own path is silent. A probe reading the maximum
        // would report the target met on its first pass and the run would finish with a Left trace
        // of nothing; reading the left channel, it finds silence and stops.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(generateLeft: false, levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -6.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        Assert.Equal(FrequencyResponsePhase.Failed, status.Phase);
        Assert.Contains("left input", status.Error);

        // And it stopped rather than correcting: winding the generator to the DAC's ceiling because
        // nothing came back is full scale into whatever *is* connected.
        Assert.True(
            status.GeneratorAmpDbv < 0.0,
            $"the probe pushed the generator to {status.GeneratorAmpDbv:0.##} dBV against a silent input");
    }

    [Fact]
    public async Task Running_IsTrueWhileTheLevelProbeIsStillGoing()
    {
        // A phase missing from `Running` reads as "finished" the moment it begins -- which is how
        // the chirp runner's own input-range probe broke every runner test at once when it was
        // added. STOP would go dead and a second start would be accepted mid-run.
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
        // A run abandoned halfway still measured the part it covered, the same way a cancelled sweep
        // keeps its points.
        var harness = await CreateAsync();
        await harness.Runner.StartAsync(Request(startFreqHz: 500.0, endFreqHz: 16000.0, stepsPerOctave: 4), CancellationToken.None);

        // Long enough for the first few points, short enough that the grid is nowhere near done.
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
        Assert.InRange(status.Traces[0].FreqHz.Length, 2, status.PointCount - 1);
    }

    [Fact]
    public async Task Clear_EmptiesTheCurvesAndTheProgressWithThem()
    {
        var harness = await CreateAsync();
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        await AwaitCompletionAsync(harness.Runner);

        var cleared = harness.Runner.Clear();

        Assert.Empty(cleared.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, cleared.Phase);
        Assert.Equal(0, cleared.PointsMeasured);
        Assert.Equal(0, cleared.PointCount);

        // The as-run configuration deliberately survives: it describes a run that is gone, and the
        // save path is already gated on the traces being non-empty.
        Assert.NotNull(harness.Runner.LastRequest);
    }

    [Fact]
    public async Task Clear_IsRefusedWhileARunIsGoing()
    {
        var harness = await CreateAsync();
        await harness.Runner.StartAsync(Request(), CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => harness.Runner.Clear());

        await AwaitCompletionAsync(harness.Runner);
    }

    [Fact]
    public async Task WithADirectGeneratorLevel_TheRangeIsProbedBeforeTheSweepRatherThanDiscoveredByIt()
    {
        // The gap this closes. With the generator level given directly there is no level probe, so
        // nothing measured the DUT before the sweep started: the first point ran on whatever range
        // the previous measurement left behind, the predictor stayed unseeded until that point
        // finished, and step-down stayed live through it -- so the device's own ranger hunted its way
        // to the right range, at ~110 ms and one re-captured record per step.
        //
        // Left on the widest range, which is the wrong one for a -20 dBV DUT: if the probe ran, the
        // sweep does not start there.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));
        var ranges = harness.Session.Capabilities.InputRangesDbv;
        await harness.Session.SetInputRangeAsync(ranges.Max(), CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.GeneratorVoltage, levelValue: -20.0),
            CancellationToken.None);

        // The probe is a phase of its own, reported from the first status poll rather than a moment
        // later -- and one the runner has to count as Running, since a phase missing from that reads
        // as "finished" the moment it begins.
        Assert.True(harness.Runner.Running);
        Assert.Equal(FrequencyResponsePhase.DeterminingRange, harness.Runner.Status.Phase);

        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        // And it settled on the range the shared ladder picks for what the DUT actually produces,
        // rather than on the one it was parked at.
        Assert.Equal(InputRangeSelector.For(ranges, -20.0), harness.Session.InputRangeDbv, 3);
        Assert.NotEqual(ranges.Max(), harness.Session.InputRangeDbv);
    }

    [Fact]
    public async Task TheRangeProbe_SuppressesStepDownFromTheFirstPointRatherThanTheSecond()
    {
        // The seed matters as much as the range: TakeOverRangingFromStepDown only fires once
        // something has measured the DUT, so before the probe existed the first point of every
        // directly-driven run was measured with the ranger free to walk.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));
        harness.Session.SetAutoRangeStepDownEnabled(true);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.GeneratorVoltage, levelValue: -20.0),
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        bool suppressedBeforeTheFirstPoint = false;
        while (harness.Runner.Running && DateTime.UtcNow < deadline)
        {
            if (harness.Session.AutoRangeStepDownEnabled is false && harness.Runner.Status.PointsMeasured == 0)
            {
                suppressedBeforeTheFirstPoint = true;
                break;
            }

            await Task.Delay(10);
        }

        await AwaitCompletionAsync(harness.Runner);

        Assert.True(suppressedBeforeTheFirstPoint, "step-down was still live when the first point was measured");
        Assert.True(harness.Session.AutoRangeStepDownEnabled, "step-down was not restored");
    }

    [Fact]
    public async Task TheRangeProbe_StaysInsideTheSweptBand()
    {
        // 1 kHz is the default probe tone, but ranging at a frequency the sweep never visits would
        // range for a level the run never sees -- which on a DUT with any response at all is a
        // different level. A sweep that stops at 200 Hz has to be probed at 200 Hz.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));

        await harness.Runner.StartAsync(
            Request(startFreqHz: 50.0, endFreqHz: 200.0, levelMode: ThdSweepLevelMode.GeneratorVoltage),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(
            Trace(status, FrequencyResponseTraceKind.Left).MagnitudeMaxDb,
            db => Assert.InRange(db, -20.5, -19.5));
    }

    [Fact]
    public async Task TheInputRange_NeverGoesBackDownOnceTheSweepHasStarted()
    {
        // A Bode plot compares levels *across* frequency, so a range change mid-sweep writes the
        // attenuator's range-to-range calibration error straight into the curve as a step, at exactly
        // the frequency it happened. Sensitivity is not the competing concern it is for a THD sweep:
        // the tone sits 20-30 dB above any floor either way.
        //
        // A DUT that rolls off well inside the swept band is the case that used to walk the range
        // down: the probe ranges for 1 kHz, and every point above the corner then measures lower than
        // the last.
        //
        // Two things are needed to make that actually cross a range boundary, and without either one
        // this test passes against the bug. The drive has to be high enough that the *top* of the
        // sweep sits several steps up the ladder -- the ladder bottoms out at 0 dBV, so everything
        // below -5 dBV picks the same range and a quiet DUT never moves it. And the reference channel
        // has to be off: the predictor takes the loudest of the two, and a flat reference would hold
        // the prediction up all by itself.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 500.0));

        await harness.Runner.StartAsync(
            Request(
                startFreqHz: 200.0, endFreqHz: 20_000.0, stepsPerOctave: 3, adaptiveResolution: true,
                levelValue: 10.0, generateRight: false, rightIsReference: false),
            CancellationToken.None);

        // Sampled only while the sweep proper is running: the probe deliberately parks on the widest
        // range and comes down from it, which is the one legitimate downward step in a run.
        var seen = new List<double>();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (harness.Runner.Running && DateTime.UtcNow < deadline)
        {
            if (harness.Runner.Status.Phase == FrequencyResponsePhase.Sweeping)
            {
                double range = harness.Session.InputRangeDbv;
                if (seen.Count == 0 || Math.Abs(seen[^1] - range) > 0.01)
                    seen.Add(range);
            }

            await Task.Delay(5);
        }

        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        var downward = new List<string>();
        for (int i = 1; i < seen.Count; i++)
        {
            if (seen[i] < seen[i - 1])
                downward.Add($"{seen[i - 1]:0.#} -> {seen[i]:0.#} dBV");
        }

        // Guards the guard: if this DUT never crossed a range boundary the monotonicity check below
        // would pass against anything, which is exactly how the first version of this test passed
        // with the ratchet disabled.
        Assert.True(
            seen.Count > 1,
            $"the range never moved at all ({string.Join(", ", seen.Select(r => r.ToString("0.#")))}), so this proves nothing");

        Assert.True(
            downward.Count == 0,
            $"the range stepped down {downward.Count} time(s) mid-sweep: {string.Join("; ", downward)} "
                + $"(sequence: {string.Join(", ", seen.Select(r => r.ToString("0.#")))})");
    }

    [Fact]
    public async Task ARun_RestoresTheAutorangeStepDownItSuppressed()
    {
        // The suppression is scoped to the run. Leaving it set hands the live view a ranger that can
        // only ever get less sensitive -- indistinguishable from a stuck range, and only fixable by
        // a restart.
        //
        // Driven through the wrapping device rather than the bare simulator: DeviceSessionService
        // reports AutoRangeStepDownEnabled as null for a SimulatedDevice (it has no hardware
        // ranger), which would leave the suppression path untaken and this asserting nothing.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -10.0),
            CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.True(harness.Session.AutoRangeStepDownEnabled);
    }

    [Fact]
    public async Task ARun_RestoresTheAutorangeStepDownAfterACancelledRun()
    {
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));

        await harness.Runner.StartAsync(
            Request(startFreqHz: 500.0, endFreqHz: 16_000.0, stepsPerOctave: 4, averages: 4),
            CancellationToken.None);
        await harness.Runner.CancelAsync();

        Assert.True(harness.Session.AutoRangeStepDownEnabled);
    }

    [Fact]
    public async Task AgainstARollingOffDut_TheThreeDbCornerLandsWhereTheDutsIs()
    {
        // The end-to-end shape of the whole thing: a one-pole low pass on the left channel only, so
        // the gain trace is the DUT's own response and its -3 dB corner is known in closed form.
        // The grid is 5 points per octave -- coarse, which is the point: the corner is interpolated
        // in log frequency between the two bracketing points, not snapped to the nearest one.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 4_000.0));

        await harness.Runner.StartAsync(
            Request(startFreqHz: 200.0, endFreqHz: 20_000.0, stepsPerOctave: 5, adaptiveResolution: true),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        var gain = Trace(status, FrequencyResponseTraceKind.Gain);
        Assert.False(gain.ThreeDb.UpperClamped);
        Assert.InRange(gain.ThreeDb.UpperHz, 3_600.0, 4_400.0);

        // Flat below the corner, and the peak is down there rather than at some noise excursion.
        Assert.InRange(gain.PeakDb, -0.5, 0.5);
        Assert.True(gain.PeakFreqHz < 1_000.0, $"the peak landed at {gain.PeakFreqHz:0} Hz");

        // Nothing rolls off at the bottom, so the lower corner is the edge of the search and says so.
        Assert.True(gain.ThreeDb.LowerClamped);
    }

    [Fact]
    public async Task ARun_RestoresThePipelineOptionsItChanged()
    {
        var harness = await CreateAsync();
        var before = harness.Pipeline.Options;

        await harness.Runner.StartAsync(Request(averages: 2), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(before, harness.Pipeline.Options);
    }

    [Fact]
    public async Task ARun_SilencesTheGeneratorWhenItEnds()
    {
        // A run that ended must not leave a tone playing into someone's amplifier.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
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
        var runner = new SteppedResponseRunner(session, pipeline, hub, NullLogger<SteppedResponseRunner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(Request(), CancellationToken.None));
    }

    [Theory]
    // A grid with nowhere to go.
    [InlineData(1000.0, 1000.0, 5, 1, true, true, true)]
    // Fewer than two distinct points is not a curve; BandwidthAnalyzer has no slope to walk down.
    [InlineData(0.0, 1000.0, 5, 1, true, true, true)]
    // Averages outside the offered range.
    [InlineData(1000.0, 4000.0, 5, 0, true, true, true)]
    [InlineData(1000.0, 4000.0, 5, 99, true, true, true)]
    // The tone has to be played somewhere.
    [InlineData(1000.0, 4000.0, 5, 1, false, false, false)]
    // ...and the reference input has to actually be fed it, or "gain" is the left channel divided by
    // whatever noise the right input is sitting on.
    [InlineData(1000.0, 4000.0, 5, 1, true, false, true)]
    public void AnUnmeasurableRequest_IsRejectedBeforeAnythingTouchesTheDevice(
        double startHz, double endHz, int stepsPerOctave, int averages,
        bool generateLeft, bool generateRight, bool rightIsReference)
    {
        var request = Request(
            startFreqHz: startHz, endFreqHz: endHz, stepsPerOctave: stepsPerOctave, averages: averages,
            generateLeft: generateLeft, generateRight: generateRight, rightIsReference: rightIsReference);

        Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
    }

    [Theory]
    [InlineData(1)]
    // Averaging exercises the other half of the throwaway: its options are swapped to un-averaged and
    // have to be restored before the measured capture arms, or the run silently measures one record
    // where the user asked for three.
    [InlineData(3)]
    public async Task AdaptiveResolution_ReadsEveryPointAtTheLevelItWasDrivenAt(int averages)
    {
        // Every point is a loopback of a -20 dBV tone, so every point must read -20 dBV — at every
        // FFT size the adaptive sizer steps through. Gain alone cannot catch a fault here: both
        // channels take the same path on the simulator, so a level error common to the two cancels
        // exactly and leaves a perfectly flat 0 dB gain trace over wrong readings.
        //
        // This is what caught the missing throwaway record: the record armed by a stimulus change
        // still holds part of the previous tone, and the damage scales as 1/FftSize, so adaptive
        // sizing put points at 4096 where most of the record was the wrong frequency. Measured
        // before the fix: 6.0 dB low at 996 Hz and a total miss (-205 dBV) at 2836 Hz.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(startFreqHz: 200.0, endFreqHz: 40_000.0, stepsPerOctave: 3, averages: averages, adaptiveResolution: true),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        var wrong = new List<string>();
        for (int i = 0; i < left.FreqHz.Length; i++)
        {
            if (Math.Abs(left.MagnitudeMaxDb[i] + 20.0) > 0.2)
                wrong.Add($"{left.FreqHz[i]:0.#} Hz read {left.MagnitudeMaxDb[i]:0.##} dBV");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count}/{left.FreqHz.Length} points off: {string.Join("; ", wrong)}");
    }

    [Fact]
    public async Task AgainstAnAsymmetricDut_EveryPointFollowsTheAnalyticResponse()
    {
        // The loopback tests cannot see a fault that hits one channel harder than the other: both
        // take the same path there, so the error cancels in the gain trace. This one puts a one-pole
        // filter on the left channel only -- different level, different group delay, and (through
        // OnePoleLowPassDevice's own ranging state) the feed-forward input-range changes that a bare
        // SimulatedDevice never exercises.
        //
        // Every point is then checked against the closed-form response rather than against its
        // neighbours, so a smooth-looking excursion is caught as readily as a single-point spike.
        const double CornerHz = 20_000.0;
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), CornerHz));

        await harness.Runner.StartAsync(
            Request(startFreqHz: 200.0, endFreqHz: 60_000.0, stepsPerOctave: 5, adaptiveResolution: true),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        // The filter runs at a fixed 48 kHz coefficient (see OnePoleLowPassDevice), so its corner in
        // the run's own sample rate is what the analytic form has to use.
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * CornerHz / 48_000.0);

        var gain = Trace(status, FrequencyResponseTraceKind.Gain);
        var wrong = new List<string>();
        for (int i = 0; i < gain.FreqHz.Length; i++)
        {
            // |H| of y[n] = y[n-1] + alpha*(x[n] - y[n-1]) at this frequency.
            double w = 2.0 * Math.PI * gain.FreqHz[i] / status.SampleRateHz;
            var denominator = System.Numerics.Complex.One - ((1.0 - alpha) * System.Numerics.Complex.Exp(new System.Numerics.Complex(0.0, -w)));
            double expectedDb = 20.0 * Math.Log10((alpha / denominator).Magnitude);

            if (Math.Abs(gain.MagnitudeMaxDb[i] - expectedDb) > 0.25)
                wrong.Add($"{gain.FreqHz[i]:0.#} Hz read {gain.MagnitudeMaxDb[i]:0.00} dB against {expectedDb:0.00}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count}/{gain.FreqHz.Length} points off: {string.Join("; ", wrong)}");
    }

    [Theory]
    // Reference on: the right input is the yardstick, so arg(L/R) is the DUT's own phase response.
    [InlineData(true, true, ResponsePhaseKind.DutResponse)]
    // Reference off but still generating on the right: both inputs measure signals of their own, so
    // the same arg(L/R) is a difference between two paths rather than a DUT's response.
    [InlineData(false, true, ResponsePhaseKind.ChannelDifference)]
    // Nothing on the right input to compare against, and a single channel's own angle is no answer.
    [InlineData(false, false, ResponsePhaseKind.None)]
    public void ThePhaseKind_FollowsWhatTheRunCanActuallyMeasure(
        bool rightIsReference, bool generateRight, ResponsePhaseKind expected)
    {
        Assert.Equal(
            expected,
            Request(rightIsReference: rightIsReference, generateRight: generateRight).PhaseKind);
    }

    [Fact]
    public async Task WithoutAReferenceButWithAStimulusOnTheRight_ThePhaseIsStillMeasured()
    {
        // The behaviour this adds. arg(L/R) is the same computation whether the right input is a
        // reference or a second measured path -- what the switch changes is what the number means,
        // so zeroing it in the unreferenced case threw away a real measurement.
        //
        // A one-pole filter on the left channel only gives a phase that is genuinely non-zero and
        // genuinely monotone, so a flat or a noisy curve both fail.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 2_000.0));

        await harness.Runner.StartAsync(
            Request(startFreqHz: 200.0, endFreqHz: 20_000.0, stepsPerOctave: 3, rightIsReference: false),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(ResponsePhaseKind.ChannelDifference, status.PhaseKind);
        Assert.False(status.RightChannelIsReference);

        var gain = Trace(status, FrequencyResponseTraceKind.Gain);
        Assert.Contains(gain.PhaseDeg, deg => Math.Abs(deg) > 5.0);

        // Checked against the closed form rather than for monotonicity: this is a *digital* one pole
        // (see OnePoleLowPassDevice), and its phase does not asymptote to -90 deg the way the analogue
        // form does -- it turns and comes back toward 0 at Nyquist, so "only ever falls" is wrong
        // about the DUT rather than about the measurement.
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * 2_000.0 / 48_000.0);
        for (int i = 0; i < gain.PhaseDeg.Length; i++)
        {
            double w = 2.0 * Math.PI * gain.FreqHz[i] / status.SampleRateHz;
            var denominator = System.Numerics.Complex.One
                - ((1.0 - alpha) * System.Numerics.Complex.Exp(new System.Numerics.Complex(0.0, -w)));
            double expectedDeg = -denominator.Phase * 180.0 / Math.PI;

            Assert.True(
                Math.Abs(gain.PhaseDeg[i] - expectedDeg) < 2.0,
                $"{gain.FreqHz[i]:0.#} Hz read {gain.PhaseDeg[i]:0.0} deg against {expectedDeg:0.0}");
        }
    }

    [Fact]
    public async Task WithNoStimulusOnTheRightInput_NoPhaseIsClaimed()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(generateRight: false, rightIsReference: false), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(ResponsePhaseKind.None, status.PhaseKind);
        Assert.All(Trace(status, FrequencyResponseTraceKind.Gain).PhaseDeg, deg => Assert.Equal(0.0f, deg));
    }

    [Fact]
    public async Task AReferencedRun_SaysSoOnItsStatusSoTheRightTraceIsNotOffered()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(rightIsReference: true), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.True(status.RightChannelIsReference);
        Assert.Equal(ResponsePhaseKind.DutResponse, status.PhaseKind);
    }

    [Fact]
    public void AStatusFrom_AnIdleRunner_ReportsNoPhaseRatherThanTheChirpsDefault()
    {
        // The record's own default is PerTrace, which is the chirp's behaviour -- this runner must
        // not inherit it just because it has not run yet.
        var runner = new SteppedResponseRunner(
            new DeviceSessionService(CleanDevice(), new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance), NullLogger<DeviceSessionService>.Instance),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            new SpectrumBroadcastHub(),
            NullLogger<SteppedResponseRunner>.Instance);

        Assert.Equal(ResponsePhaseKind.None, runner.Status.PhaseKind);
    }

    [Fact]
    public void AdaptiveResolution_PicksTheLowestRateThatKeepsTheToneItselfInBand()
    {
        // One harmonic, not the sweep's five: there are no harmonics in this measurement. A 20 kHz
        // run then costs a quarter of the USB and FFT load of the same run at 192 kHz.
        Assert.Equal(48_000.0, Request(endFreqHz: 20_000.0, adaptiveResolution: true).EffectiveSampleRateHz);
        Assert.Equal(96_000.0, Request(endFreqHz: 40_000.0, adaptiveResolution: true).EffectiveSampleRateHz);

        // Off, the requested rate stands exactly as given -- which is what makes the fixed mode a
        // reproducible reference.
        Assert.Equal(
            192_000.0,
            Request(endFreqHz: 20_000.0, adaptiveResolution: false, sampleRateHz: 192_000.0).EffectiveSampleRateHz);
    }

    [Fact]
    public void AnOrdinarySweep_KeepsTheSweepsOwnFftCeilingAndItsFourPointFourHertzFloor()
    {
        // Starting at 20 Hz asks for nothing the sweeps' record-time ceiling cannot give, so nothing
        // is raised and no point pays for a longer record than it needs.
        var request = Request(startFreqHz: 20.0, endFreqHz: 20_000.0, adaptiveResolution: true);

        Assert.Equal(48_000.0, request.EffectiveSampleRateHz);
        Assert.Equal(32768, request.StandardFftCeiling);
        Assert.Equal(32768, request.FftCeiling);
        Assert.Equal(4.39, request.LowestMeasurableHz, 2);

        // And the lowest point is sized by the standard ceiling, not the extended one.
        Assert.Equal(32768, request.FftSizeAt(20.0));
    }

    [Theory]
    [InlineData(48_000.0, 20_000.0)]
    [InlineData(96_000.0, 40_000.0)]
    [InlineData(192_000.0, 80_000.0)]
    public void AStartBelowTheStandardFloor_RaisesTheCeilingToOnePointOneHertzAtEveryRate(
        double expectedSampleRateHz, double endFreqHz)
    {
        // The sweeps cap every record at 0.6827 s, which is 1.465 Hz bins and so a 4.4 Hz floor at
        // *every* supported rate -- turning adaptive resolution off does not help either, because the
        // fixed preset is that same cap at its own rate. Asking to start below it raises the cap to
        // the chirp's own longest record, 2.73 s.
        var request = Request(startFreqHz: 2.0, endFreqHz: endFreqHz, adaptiveResolution: true);

        Assert.Equal(expectedSampleRateHz, request.EffectiveSampleRateHz);
        Assert.Equal(4.39, request.StandardLowestHz, 2);

        // A *time* bound, so the floor is the same number at every rate -- the property a sample-count
        // bound would not have.
        Assert.Equal(1.10, request.LowestMeasurableHz, 2);
        Assert.Equal(2.73, request.FftCeiling / expectedSampleRateHz, 2);

        // Only the points that need it. The bottom clamps to the raised ceiling; a mid-band point is
        // sized by the ladder exactly as it was before, and stays inside the standard ceiling.
        Assert.Equal(request.FftCeiling, request.FftSizeAt(request.StartFreqHz));
        Assert.True(
            request.FftSizeAt(1000.0) <= request.StandardFftCeiling,
            $"1 kHz was sized at {request.FftSizeAt(1000.0)} against a standard ceiling of {request.StandardFftCeiling}");

        request.Validate();
    }

    [Fact]
    public async Task AStartBelowTheStandardFloor_ActuallyMeasuresDownThere()
    {
        // End to end, because the raised ceiling is only worth anything if the pipeline will run the
        // longer record and the runner's own capture timeout tolerates it. Two points, because at
        // 2.73 s per record (twice over, per point) this is the slowest thing the runner can be asked
        // to do.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(startFreqHz: 2.0, endFreqHz: 4.0, stepsPerOctave: 1, adaptiveResolution: true),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner, timeoutSeconds: 180);

        AssertFinished(status);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        Assert.True(left.StartFreqHz < 2.5, $"the lowest point measured was {left.StartFreqHz:0.###} Hz");
        Assert.All(left.MagnitudeMaxDb, db => Assert.InRange(db, -20.5, -19.5));
    }

    [Fact]
    public void ALowFrequencyStart_IsNotJudgedAgainstTheHarmonicAnalyzersLimit()
    {
        // ThdSweepRequest needs the fundamental 5 bins up so H2's skirt clears it. This measurement
        // reads no harmonics, so 10 Hz at 1.465 Hz bins (6.8 bins up) is perfectly measurable and
        // must not be rejected -- the bottom of the band is exactly what it was added to look at.
        Request(startFreqHz: 10.0, endFreqHz: 100.0, adaptiveResolution: false, fftSize: 131072, sampleRateHz: 192_000.0)
            .Validate();
    }
}
