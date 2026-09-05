using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

public class FrequencyResponseRunnerTests
{
    /// <summary>
    /// The smallest span, so the record is 32768 frames at 48 kHz — 0.68 s. Everything under test is
    /// independent of the span; a larger one only makes the test slower.
    /// </summary>
    private const double SpanHz = 24_000.0;

    private static FrequencyResponseRequest Request(
        double spanHz = SpanHz,
        int resolutionMultiplier = 1,
        ThdSweepLevelMode levelMode = ThdSweepLevelMode.GeneratorVoltage,
        double levelValue = -20.0,
        ThdSweepLevelUnit levelUnit = ThdSweepLevelUnit.Dbv,
        bool rightIsReference = true,
        int smoothing = 24,
        int averages = 1,
        bool continuous = false,
        double chirpStartHz = FrequencyResponseRequest.DefaultChirpStartHz,
        double loadOhms = 8.0) =>
        new(spanHz, resolutionMultiplier, levelMode, levelValue, levelUnit, rightIsReference, smoothing, averages, continuous, chirpStartHz, loadOhms);

    private sealed record Harness(FrequencyResponseRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

    /// <summary>
    /// A clean simulated DUT: no fabricated harmonic stack, no noise, no hum. The response under
    /// test is then a straight wire, which is the one case whose right answer is known exactly —
    /// the simulator's default profile deliberately dirties the signal to make the FFT screen look
    /// realistic, and that would put a floor under every assertion here.
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
            new FrequencyResponseRunner(session, pipeline, hub, NullLogger<FrequencyResponseRunner>.Instance),
            session,
            pipeline);
    }

    private static async Task<FrequencyResponseStatus> AwaitCompletionAsync(FrequencyResponseRunner runner, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (runner.Running)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The measurement did not finish within {timeoutSeconds} s (phase {runner.Status.Phase}).");
            }

            await Task.Delay(25);
        }

        return runner.Status;
    }

    private static void AssertFinished(FrequencyResponseStatus status) =>
        Assert.True(
            status.Phase == FrequencyResponsePhase.Finished,
            $"the measurement ended in {status.Phase}: {status.Error ?? "(no message)"}");

    private static FrequencyResponseTrace Trace(FrequencyResponseStatus status, FrequencyResponseTraceKind kind) =>
        status.Traces.Single(t => t.Kind == kind);

    /// <summary>Columns comfortably inside the reported band, avoiding the very ends where the sweep fades.</summary>
    private static IEnumerable<int> MidBandColumns(FrequencyResponseTrace trace)
    {
        for (int i = trace.FreqHz.Length / 4; i < trace.FreqHz.Length * 3 / 4; i += 20)
            yield return i;
    }

    [Fact]
    public void ChirpStartDefaultMatchesTheConstant() =>
        Assert.Equal(FrequencyResponseRequest.DefaultChirpStartHz, Request().ChirpStartHz);

    [Fact]
    public void AnalysisLowHz_KeepsItsFourHzFloorAtTheDefaultStartAndRisesWithIt()
    {
        // The default has to keep producing exactly the 4 Hz the constant used to be, or every
        // stored result changes meaning; above it the inset is proportional.
        Assert.Equal(4.0, Request().AnalysisLowHz, 6);
        Assert.Equal(4.0, Request(chirpStartHz: 3.0).AnalysisLowHz, 6);
        Assert.Equal(10.0, Request(chirpStartHz: 8.0).AnalysisLowHz, 6);
        Assert.Equal(25.0, Request(chirpStartHz: 20.0).AnalysisLowHz, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(1001.0)]
    [InlineData(double.NaN)]
    [InlineData(20_000.0)]   // not a decade below the sweep's end
    public void Validate_RejectsAnImpossibleChirpStart(double chirpStartHz) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(chirpStartHz: chirpStartHz).Validate());

    /// <summary>
    /// A raised start frequency has to reach the stimulus and the reference together — they are
    /// generated from two separate copies of the formula, and a start applied to only one of them
    /// would be deconvolved as DUT response rather than failing.
    /// </summary>
    [Fact]
    public async Task Measurement_WithARaisedChirpStart_StillReadsUnityGainAndReportsFromTheHigherBand()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(chirpStartHz: 20.0), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var gain = Trace(status, FrequencyResponseTraceKind.Gain);

        Assert.True(gain.FreqHz[0] >= 25.0, $"the trace starts at {gain.FreqHz[0]:0.0} Hz, below the raised analysis band");
        foreach (int i in MidBandColumns(gain))
        {
            Assert.InRange(gain.MagnitudeMaxDb[i], -0.5, 0.5);
            Assert.InRange(gain.MagnitudeMinDb[i], -0.5, 0.5);
        }
    }

    [Fact]
    public async Task Measurement_ProducesLeftRightAndGainTraces()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(3, status.Traces.Count);
        Assert.Contains(status.Traces, t => t.Kind == FrequencyResponseTraceKind.Left);
        Assert.Contains(status.Traces, t => t.Kind == FrequencyResponseTraceKind.Right);
        Assert.Contains(status.Traces, t => t.Kind == FrequencyResponseTraceKind.Gain);
    }

    [Fact]
    public async Task Measurement_OfAStraightLoopback_ReadsUnityGain()
    {
        // The simulator feeds the generator to both inputs unchanged, so left over right is a wire.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var gain = Trace(status, FrequencyResponseTraceKind.Gain);

        foreach (int i in MidBandColumns(gain))
        {
            Assert.InRange(gain.MagnitudeMaxDb[i], -0.5, 0.5);
            Assert.InRange(gain.MagnitudeMinDb[i], -0.5, 0.5);
        }
    }

    [Fact]
    public async Task Measurement_ReportsEachChannelAtTheDriveLevelInDbv()
    {
        // The dBV view answers "what would a sine at the generator's amplitude produce here", so a
        // loopback at -20 dBV must read -20 dBV across the band.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(levelValue: -20.0), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        foreach (var kind in new[] { FrequencyResponseTraceKind.Left, FrequencyResponseTraceKind.Right })
        {
            var trace = Trace(status, kind);
            foreach (int i in MidBandColumns(trace))
                Assert.InRange(trace.MagnitudeMaxDb[i], -21.0, -19.0);
        }
    }

    [Fact]
    public async Task Measurement_OfAFlatResponse_ReportsBothBandwidthsAsClamped()
    {
        // Nothing rolls off inside the measured band, so neither corner exists. Reporting the search
        // edge as though it were a measured corner is exactly what the clamped flags prevent.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var gain = Trace(status, FrequencyResponseTraceKind.Gain);

        Assert.True(gain.ThreeDb.LowerClamped);
        Assert.True(gain.ThreeDb.UpperClamped);
        Assert.True(gain.OneDb.LowerClamped);
        Assert.True(gain.OneDb.UpperClamped);
        Assert.True(gain.ThreeDb.BandwidthHz > 0.0);
    }

    [Fact]
    public async Task Measurement_ReportsTheAnalysisItRanWith()
    {
        var harness = await CreateAsync();

        var started = await harness.Runner.StartAsync(Request(spanHz: 24_000.0, resolutionMultiplier: 1), CancellationToken.None);

        Assert.Equal(48_000.0, started.SampleRateHz);
        Assert.Equal(32768, started.FftSize);
        Assert.Equal(FrequencyResponseRequest.BaseBinHz, started.BinHz, 6);

        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);
        Assert.Equal(32768, status.FftSize);
    }

    [Theory]
    [InlineData(24_000.0, 1, 32768)]
    [InlineData(24_000.0, 4, 131072)]
    [InlineData(48_000.0, 1, 65536)]
    [InlineData(48_000.0, 2, 131072)]
    [InlineData(96_000.0, 1, 131072)]
    [InlineData(96_000.0, 4, 524288)]
    public void FftSize_ScalesWithBothSpanAndResolution(double spanHz, int multiplier, int expectedFftSize)
    {
        var request = Request(spanHz: spanHz, resolutionMultiplier: multiplier);

        Assert.Equal(expectedFftSize, request.FftSize);

        // The point of scaling the base size with the sample rate: the bin width the UI offers is
        // the same at every span, so one resolution list covers all three.
        Assert.Equal(FrequencyResponseRequest.BaseBinHz / multiplier, request.BinHz, 9);
    }

    [Theory]
    [InlineData(1, 1.46)]
    [InlineData(2, 0.73)]
    [InlineData(4, 0.37)]
    public void BinHz_MatchesTheResolutionsTheUiOffers(int multiplier, double expectedBinHz)
    {
        foreach (double span in FrequencyResponseRequest.SupportedSpansHz)
            Assert.Equal(expectedBinHz, Request(spanHz: span, resolutionMultiplier: multiplier).BinHz, 2);
    }

    [Fact]
    public async Task Measurement_WithSeveralAverages_TakesThatManyRecords()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(averages: 3), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(3, status.AveragesTaken);
        Assert.Equal(3, status.AverageCount);
    }

    /// <summary>
    /// The record armed by the stimulus change is thrown away, so a device that is briefly wrong
    /// after that change still measures correctly. Without the throwaway this is a wrecked trace:
    /// the glitch is a quarter of the record, the window is Rectangular by necessity, and a record
    /// that is not one whole period of the stimulus leaks across the entire spectrum.
    /// </summary>
    [Fact]
    public async Task Measurement_WhenTheDeviceIsBrieflyWrongAfterAStimulusChange_StillReadsUnityGain()
    {
        var harness = await CreateAsync(new StimulusChangeGlitchDevice(CleanDevice(), glitchFrames: 32768));

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var gain = Trace(status, FrequencyResponseTraceKind.Gain);

        foreach (int i in MidBandColumns(gain))
        {
            Assert.InRange(gain.MagnitudeMaxDb[i], -0.5, 0.5);
            Assert.InRange(gain.MagnitudeMinDb[i], -0.5, 0.5);
        }
    }

    [Fact]
    public async Task Measurement_WithoutARightChannelReference_StillProducesAGainTrace()
    {
        // With nothing to compare against but the stimulus, gain is the left channel's own response
        // as a ratio -- the dBV curve shifted by the drive level.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(rightIsReference: false, levelValue: -20.0), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var gain = Trace(status, FrequencyResponseTraceKind.Gain);

        foreach (int i in MidBandColumns(gain))
            Assert.InRange(gain.MagnitudeMaxDb[i], -0.5, 0.5);
    }

    [Fact]
    public async Task Measurement_RestoresThePipelineOptionsItBorrowed()
    {
        // Rectangular windowing and complex retention are for the chirp alone; leaving either set
        // would silently change the live FFT screen for the rest of the session.
        var harness = await CreateAsync();
        var before = harness.Pipeline.Options;

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(before.FftSize, harness.Pipeline.Options.FftSize);
        Assert.Equal(before.Window, harness.Pipeline.Options.Window);
        Assert.False(harness.Pipeline.Options.RetainComplexSpectrum);
        Assert.False(harness.Pipeline.Options.SingleShot);
    }

    [Fact]
    public async Task Measurement_SilencesTheGeneratorWhenItFinishes()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.False(harness.Session.EffectiveGeneratorConfig.Left.Enabled);
        Assert.False(harness.Session.EffectiveGeneratorConfig.Right.Enabled);
    }

    [Fact]
    public async Task Measurement_ChirpPeriodMatchesTheAnalysisSize()
    {
        // The assumption the whole measurement rests on. Asserted through the session's own view of
        // the generator, so it covers the request actually reaching the device.
        var harness = await CreateAsync();
        var request = Request();

        await harness.Runner.StartAsync(request, CancellationToken.None);

        // Waits for the measurement itself: the input-range probe also plays a chirp, at its own
        // shorter size, so sampling the first enabled chirp would read the probe's period.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        EchoChirpSettings? chirp = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = harness.Runner.Status;
            chirp = harness.Session.EffectiveGeneratorConfig.Left.Chirp;
            if (status.Phase == FrequencyResponsePhase.Measuring
                && chirp is not null
                && harness.Session.EffectiveGeneratorConfig.Left.Enabled)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(chirp);
        Assert.Equal(request.FftSize, chirp.RepeatFrames);

        await harness.Runner.CancelAsync();
    }

    [Fact]
    public async Task Continuous_KeepsMeasuringUntilStopped()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(continuous: true), CancellationToken.None);

        // Wait for several complete passes, which is the thing a single run cannot produce.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.PassesCompleted < 3)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"only {harness.Runner.Status.PassesCompleted} passes in 30 s");

            Assert.True(harness.Runner.Running, "a continuous run must not stop on its own");
            await Task.Delay(50);
        }

        await harness.Runner.CancelAsync();

        var status = harness.Runner.Status;
        Assert.False(status.Running);
        Assert.True(status.PassesCompleted >= 3);

        // Cancelling a repeat is stopping it, not failing it — the curves on screen are a complete
        // measurement, not a half-finished one, which is exactly why the phase differs from a
        // cancelled single run.
        Assert.Equal(FrequencyResponsePhase.Finished, status.Phase);
        Assert.Equal(3, status.Traces.Count);
    }

    [Fact]
    public async Task Continuous_EachPassIsAWholeMeasurement_NotARunningBlend()
    {
        // The estimators are recreated per pass. If they were reused, the average would lengthen
        // without bound and the trace would stop responding to the DUT — the opposite of what
        // watching a live measurement is for.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(continuous: true, averages: 2), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.PassesCompleted < 2)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("no second pass within 30 s");
            await Task.Delay(50);
        }

        var status = harness.Runner.Status;
        await harness.Runner.CancelAsync();

        // AveragesTaken counts within the current pass and resets, rather than climbing forever.
        Assert.InRange(status.AveragesTaken, 0, 2);
    }

    [Fact]
    public async Task Single_RunsExactlyOnePass()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(continuous: false), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(1, status.PassesCompleted);
        Assert.False(status.Continuous);
    }

    [Fact]
    public async Task Continuous_SilencesTheGeneratorWhenStopped()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(continuous: true), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.PassesCompleted < 1)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("no pass within 30 s");
            await Task.Delay(50);
        }

        await harness.Runner.CancelAsync();

        Assert.False(harness.Session.EffectiveGeneratorConfig.Left.Enabled);
        Assert.False(harness.Session.EffectiveGeneratorConfig.Right.Enabled);
        Assert.False(harness.Pipeline.Options.RetainComplexSpectrum);
    }

    [Theory]
    [InlineData(-40.0, 0.0)]
    [InlineData(-20.0, 0.0)]
    [InlineData(-6.0, 0.0)]
    public async Task InputVoltageMode_ProbesForTheInputRangeBeforeMeasuring(double levelDbv, double expectedRangeDbv)
    {
        // The simulator passes the generator straight through, so the peak the probe sees is the
        // chirp's own amplitude and the range that follows is predictable. Without the probe the run
        // would start on whatever range the previous measurement left behind.
        var harness = await CreateAsync();
        await harness.Session.SetInputRangeAsync(42.0, CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.GeneratorVoltage, levelValue: levelDbv),
            CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(expectedRangeDbv, harness.Session.InputRangeDbv);
    }

    [Fact]
    public async Task InputVoltageMode_ProbeStartsOnTheLeastSensitiveRange()
    {
        // An unknown DUT can be doing anything, and a probe that clips reads a peak that is a
        // property of the range rather than of the signal — which would choose that same range again
        // and stay wrong. Observed through the ranges the session was actually asked for.
        var harness = await CreateAsync();
        await harness.Session.SetInputRangeAsync(0.0, CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.GeneratorVoltage, levelValue: -20.0),
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool sawWidest = false;
        while (harness.Runner.Running && DateTime.UtcNow < deadline)
        {
            if (harness.Session.InputRangeDbv >= 42.0)
            {
                sawWidest = true;
                break;
            }

            await Task.Delay(5);
        }

        await harness.Runner.CancelAsync();
        Assert.True(sawWidest, "the probe should park on the widest range before measuring the peak");
    }

    [Fact]
    public async Task OutputVoltageMode_PredictsTheRangeFromTheTarget_WithoutAnExtraCapture()
    {
        // The wanted DUT output *is* the level, so there is nothing to discover — the reference app
        // takes DetermineAttenuation(target) directly. -6 dBV + 5 dB of headroom lands on the 0 dBV
        // range.
        var harness = await CreateAsync();
        await harness.Session.SetInputRangeAsync(42.0, CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -6.0),
            CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(0.0, harness.Session.InputRangeDbv);
    }

    [Fact]
    public async Task OutputVoltageMode_FromATooSensitiveRange_StillSettlesAtTheRequestedLevel()
    {
        // The ordering this exists for. The level probe measures the DUT's gain from its own
        // captures, so a leftover input range too sensitive for the target clips them; the probe
        // reads that loss as missing gain and pushes the generator up, which clips harder still.
        // Measured before the range was moved ahead of the probe: a 6 dBV target against a plain
        // loopback settled the generator at 16.74 dBV — a 10.7 dB overshoot, i.e. a DUT driven ten
        // times harder than asked.
        var harness = await CreateAsync();
        await harness.Session.SetInputRangeAsync(0.0, CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: 6.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.InRange(status.GeneratorAmpDbv, 5.5, 6.5);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        foreach (int i in MidBandColumns(left))
            Assert.InRange(left.MagnitudeMaxDb[i], 5.0, 7.0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheChirp_StillReportsAPerTracePhase(bool rightIsReference)
    {
        // The guard on "leave the chirp alone". Its Left and Right traces are estimated against the
        // synthesised sweep and so carry phases of their own, unlike a stepped sine's -- which is
        // exactly what PerTrace means, and why the chart still reads the phase off the trace on
        // screen for this measurement and off the Gain trace for the other one.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(rightIsReference: rightIsReference), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(ResponsePhaseKind.PerTrace, status.PhaseKind);

        // Reported so the display panel can drop the Right trace selector -- nothing about this
        // measurement's own behaviour turns on it.
        Assert.Equal(rightIsReference, status.RightChannelIsReference);
    }

    [Fact]
    public async Task TheChirp_KeepsAPhaseOnEveryTraceRatherThanOnTheGainAlone()
    {
        // The other half of "unchanged": a stepped sine zeroes Left and Right because arg(L/R) is the
        // only relationship it can measure, and if the chirp ever started doing the same, PerTrace
        // would be a lie and the chart would draw a flat line in FR (dBV) view.
        // Against a DUT with a phase to find: a plain loopback has none, so the left channel against
        // the synthesised sweep would read ~0 either way and prove nothing.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 2_000.0));

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        Assert.Contains(left.PhaseDeg, deg => Math.Abs(deg) > 5.0);
    }

    [Fact]
    public async Task Probe_RestoresAutorangeStepDownWhenTheRunEnds()
    {
        // The suppression is scoped to the run. Leaving it set hands the live view a ranger that can
        // only ever get less sensitive — indistinguishable from a stuck range, and only fixable by a
        // restart.
        //
        // Driven through the wrapping device rather than the bare simulator: DeviceSessionService
        // reports AutoRangeStepDownEnabled as null for a SimulatedDevice (it has no hardware ranger),
        // which would leave the suppression path untaken and this asserting nothing.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.True(harness.Session.AutoRangeStepDownEnabled);
    }

    [Fact]
    public async Task Probe_RestoresAutorangeStepDownAfterACancelledRun()
    {
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));

        await harness.Runner.StartAsync(Request(averages: 10), CancellationToken.None);
        await harness.Runner.CancelAsync();

        Assert.True(harness.Session.AutoRangeStepDownEnabled);
    }

    [Fact]
    public async Task Probe_SuppressesAutorangeStepDownWhileItOwnsTheRange()
    {
        // Step-down live during the probe would walk straight off the widest range the probe parks
        // on, which is the whole point of parking there.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.GeneratorVoltage, averages: 10),
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool sawSuppressed = false;
        while (harness.Runner.Running && DateTime.UtcNow < deadline)
        {
            if (harness.Session.AutoRangeStepDownEnabled is false)
            {
                sawSuppressed = true;
                break;
            }

            await Task.Delay(5);
        }

        await harness.Runner.CancelAsync();

        Assert.True(sawSuppressed, "step-down should be suppressed while the run owns the input range");
        Assert.True(harness.Session.AutoRangeStepDownEnabled, "and restored afterwards");
    }

    [Fact]
    public async Task WithAutorangeOff_TheRunLeavesTheInputRangeAlone()
    {
        // Manual range control means manual: the user picked this deliberately. Through the
        // stand-in device, since the bare simulator reports autoranging as null (absent) rather
        // than off, and absent means the runner is free to take the range.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 20_000.0));
        harness.Session.SetAutoRangeEnabled(false);
        await harness.Session.SetInputRangeAsync(24.0, CancellationToken.None);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.GeneratorVoltage, levelValue: -20.0),
            CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(24.0, harness.Session.InputRangeDbv);
    }

    [Fact]
    public async Task Cancel_StopsTheRunAndLeavesItCancelled()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(averages: 10), CancellationToken.None);
        await harness.Runner.CancelAsync();

        var status = harness.Runner.Status;
        Assert.False(status.Running);
        Assert.Equal(FrequencyResponsePhase.Cancelled, status.Phase);
    }

    [Fact]
    public async Task Start_WhileAlreadyRunning_IsRejected()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(averages: 6), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Runner.StartAsync(Request(), CancellationToken.None));

        await harness.Runner.CancelAsync();
    }

    [Fact]
    public async Task Clear_DiscardsTheTraces()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        var cleared = harness.Runner.Clear();

        Assert.Empty(cleared.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, cleared.Phase);
    }

    [Fact]
    public async Task Clear_WhileRunning_IsRejected()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(averages: 6), CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => harness.Runner.Clear());

        await harness.Runner.CancelAsync();
    }

    [Fact]
    public async Task LastRequest_IsTheAsRunConfiguration()
    {
        var harness = await CreateAsync();
        var request = Request(smoothing: 6, averages: 2);

        await harness.Runner.StartAsync(request, CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        Assert.Equal(request, harness.Runner.LastRequest);
    }

    [Theory]
    [InlineData(12_000.0, 1, 24, 1)]      // unsupported span
    [InlineData(24_000.0, 3, 24, 1)]      // unsupported resolution multiplier
    [InlineData(24_000.0, 1, 17, 1)]      // unsupported smoothing denominator
    [InlineData(24_000.0, 1, 24, 0)]      // too few averages
    [InlineData(24_000.0, 1, 24, 11)]     // too many averages
    public void Validate_RejectsAnUnmeasurableRequest(double spanHz, int multiplier, int smoothing, int averages)
    {
        var request = Request(spanHz: spanHz, resolutionMultiplier: multiplier, smoothing: smoothing, averages: averages);
        Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
    }

    /// <summary>
    /// Watts name a voltage only once a load says which one, and that load rides on the output-power
    /// mode. Every other pairing is a unit and a mode that disagree about what was asked for, so it
    /// is rejected rather than half-honoured -- the alternative is a run that measures a level
    /// nobody chose, which is this measurement's recurring failure shape.
    /// </summary>
    [Theory]
    // Watts outside the power mode: no load is in play, so there is nothing to convert them with.
    [InlineData(ThdSweepLevelMode.GeneratorVoltage, ThdSweepLevelUnit.Watt, 1.0, 8.0)]
    [InlineData(ThdSweepLevelMode.OutputVoltage, ThdSweepLevelUnit.Watt, 1.0, 8.0)]
    // The power mode with a voltage unit: the number would be silently read as volts.
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Dbv, 0.0, 8.0)]
    // A power target with no load to define it against, and a negative one.
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Watt, 1.0, 0.0)]
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Watt, -1.0, 8.0)]
    public void Validate_RejectsAPowerLevelThatNamesNoVoltage(
        ThdSweepLevelMode levelMode, ThdSweepLevelUnit levelUnit, double levelValue, double loadOhms)
    {
        var request = Request(levelMode: levelMode, levelUnit: levelUnit, levelValue: levelValue, loadOhms: loadOhms);
        Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
    }

    [Fact]
    public void Validate_AcceptsAPowerTargetIntoALoad() =>
        Request(levelMode: ThdSweepLevelMode.OutputPower, levelUnit: ThdSweepLevelUnit.Watt, levelValue: 1.0, loadOhms: 8.0)
            .Validate();

    /// <summary>
    /// The power mode is <see cref="ThdSweepLevelMode.OutputVoltage"/> with one conversion in front
    /// of it -- V = sqrt(P.R) -- so the level the probe solves for is the only thing that separates
    /// them, and it has to be the voltage that load would dissipate at.
    /// </summary>
    [Theory]
    [InlineData(1.0, 8.0, 9.03089987)]      // sqrt(8) = 2.828 V rms
    [InlineData(1.0, 4.0, 6.02059991)]      // halving the load takes 3 dB off the same watt
    [InlineData(0.001, 8.0, -20.96910013)]  // a milliwatt, the scale a headphone output is asked about
    public void LevelDbv_ConvertsWattsThroughTheLoad(double watts, double loadOhms, double expectedDbv) =>
        Assert.Equal(
            expectedDbv,
            Request(levelMode: ThdSweepLevelMode.OutputPower, levelUnit: ThdSweepLevelUnit.Watt, levelValue: watts, loadOhms: loadOhms).LevelDbv,
            6);

    [Theory]
    [InlineData(ThdSweepLevelUnit.Volt, 0.1, -20.0)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 100.0, -20.0)]
    [InlineData(ThdSweepLevelUnit.Dbv, -20.0, -20.0)]
    public void LevelDbv_ConvertsEveryUnitTheUiOffers(ThdSweepLevelUnit unit, double value, double expectedDbv) =>
        Assert.Equal(expectedDbv, Request(levelUnit: unit, levelValue: value).LevelDbv, 6);

    [Theory]
    [InlineData(-20.0)]
    [InlineData(-6.0)]
    [InlineData(0.0)]
    public async Task OutputVoltageMode_AgainstAUnityDut_SettlesTheGeneratorAtTheRequestedLevel(double targetDbv)
    {
        // The probe has to measure a *level*, which for a chirp means deconvolving it. Reading a
        // peak bin of the raw spectrum instead put the generator 14 dB high against this very
        // loopback: a sweep spreads its energy over thousands of bins, so no single bin is anywhere
        // near the sweep's own amplitude, and correcting toward the target adds that whole gap.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: targetDbv),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.InRange(status.GeneratorAmpDbv, targetDbv - 0.5, targetDbv + 0.5);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        foreach (int i in MidBandColumns(left))
            Assert.InRange(left.MagnitudeMaxDb[i], targetDbv - 1.0, targetDbv + 1.0);
    }

    [Fact]
    public async Task OutputPowerMode_AgainstAUnityDut_SettlesTheGeneratorAtTheVoltageThatLoadWouldDissipate()
    {
        // The mode's whole claim: 10 mW into 8 ohm is 0.283 V rms, i.e. -10.97 dBV, and against a
        // straight wire that is also what the generator has to produce. Run rather than asserted on
        // the request alone, because the conversion is only half of it -- the level still has to be
        // *probed* for, which it is not unless the mode counts as output-referred (SolvesGeneratorLevel).
        const double watts = 0.01;
        const double loadOhms = 8.0;
        double expectedDbv = 20.0 * Math.Log10(Math.Sqrt(watts * loadOhms));

        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(
                levelMode: ThdSweepLevelMode.OutputPower,
                levelUnit: ThdSweepLevelUnit.Watt,
                levelValue: watts,
                loadOhms: loadOhms),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.InRange(status.GeneratorAmpDbv, expectedDbv - 0.5, expectedDbv + 0.5);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        foreach (int i in MidBandColumns(left))
            Assert.InRange(left.MagnitudeMaxDb[i], expectedDbv - 1.0, expectedDbv + 1.0);
    }

    [Fact]
    public async Task OutputVoltageMode_AgainstALossyDut_DrivesItHarderThanTheTarget()
    {
        // The whole point of the mode: the generator is raised by whatever the DUT takes away, so
        // the level asked for is the level that comes *out*. A low pass measured across the full
        // band loses a lot of it, so the generator must end up well above the target.
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz: 2_000.0));

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -20.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.True(
            status.GeneratorAmpDbv > -20.0 + 3.0,
            $"expected the probe to compensate for the filter's loss, generator settled at {status.GeneratorAmpDbv:0.00} dBV");
    }

    [Fact]
    public async Task Measurement_DrivingAOnePoleDut_FindsItsCorner()
    {
        const double cornerHz = 2_000.0;
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz));

        await harness.Runner.StartAsync(Request(smoothing: 24), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var left = Trace(status, FrequencyResponseTraceKind.Left);

        Assert.False(left.ThreeDb.UpperClamped);
        Assert.InRange(left.ThreeDb.UpperHz, cornerHz * 0.9, cornerHz * 1.1);
    }

    /// <summary>
    /// Wraps a simulated device and low-passes its left channel, so a test has a DUT with a corner
    /// the measurement is supposed to find. Only the left input is filtered — the right stays the
    /// straight reference, which is the wiring the "right channel is reference" mode assumes.
    /// </summary>
    /// <summary>
    /// A device whose left channel is wrong for a while after every generator change — standing in
    /// for the pre-change audio that is still in the round trip when
    /// <c>ConfigureGeneratorAsync</c> returns. The discard that call performs covers the audio
    /// queued for the DAC, not the trip back through the ADC, so on real hardware some of it lands
    /// at the front of the next record.
    /// </summary>
    private sealed class StimulusChangeGlitchDevice(SimulatedDevice inner, int glitchFrames) : IAnalyzerDevice
    {
        private int _remaining;

        public DeviceCapabilities Capabilities => inner.Capabilities;

        public double InputRangeDbv => inner.InputRangeDbv;

        public long DroppedRxChunks => inner.DroppedRxChunks;

        public long DroppedBlocks => inner.DroppedBlocks;

        public bool AutoRangeEnabled
        {
            get => inner.AutoRangeEnabled;
            set => inner.AutoRangeEnabled = value;
        }

        public bool AutoRangeStepDownEnabled
        {
            get => inner.AutoRangeStepDownEnabled;
            set => inner.AutoRangeStepDownEnabled = value;
        }

        public double AutoRangeStepDownDelayMs
        {
            get => inner.AutoRangeStepDownDelayMs;
            set => inner.AutoRangeStepDownDelayMs = value;
        }

        public event EventHandler? DeviceLost
        {
            add => inner.DeviceLost += value;
            remove => inner.DeviceLost -= value;
        }

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);

        public Task<bool> PingAsync(CancellationToken ct) => inner.PingAsync(ct);

        public int StimulusLatencyFrames(double sampleRateHz) => inner.StimulusLatencyFrames(sampleRateHz);

        public Task SetInputRangeAsync(double dbv, CancellationToken ct) => inner.SetInputRangeAsync(dbv, ct);

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => inner.SetOutputRangeAsync(dbv, ct);

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
        {
            Interlocked.Exchange(ref _remaining, glitchFrames);
            return inner.ConfigureGeneratorAsync(config, ct);
        }

        public Task StopAsync() => inner.StopAsync();

        public async IAsyncEnumerable<PcmBlock> StreamAsync(
            AcquisitionConfig config,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var block in inner.StreamAsync(config, ct).WithCancellation(ct))
            {
                int left = Volatile.Read(ref _remaining);
                if (left > 0)
                {
                    int frames = Math.Min(left, block.FrameCount);
                    for (int f = 0; f < frames; f++)
                        block.Samples[f * block.ChannelCount] *= 0.1f;

                    Interlocked.Add(ref _remaining, -frames);
                }

                yield return block;
            }
        }
    }
}
