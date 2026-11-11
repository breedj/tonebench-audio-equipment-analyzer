using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

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
        double loadOhms = 8.0,
        BandwidthReferenceMode bandwidthReference = BandwidthReferenceMode.BandMedian,
        double bandwidthReferenceLowHz = BandwidthReferenceSpec.DefaultLowHz,
        double bandwidthReferenceHighHz = BandwidthReferenceSpec.DefaultHighHz,
        ResponseStimulus stimulus = ResponseStimulus.Chirp,
        double noiseCutoffHz = 5.0,
        double clockDriftPpm = 0.0) =>
        new(spanHz, resolutionMultiplier, levelMode, levelValue, levelUnit, rightIsReference, smoothing, averages,
            continuous, chirpStartHz, loadOhms, bandwidthReference, bandwidthReferenceLowHz, bandwidthReferenceHighHz,
            stimulus, noiseCutoffHz, clockDriftPpm);

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


    /// <summary>
    /// The clock-drift correction belongs to a trace measured against the <em>synthetic</em> chirp,
    /// and to no other.
    ///
    /// <para><b>Why this is the test that matters.</b> Left and Right are each a captured channel
    /// over the generated sweep: the sweep is on the playback clock, the capture on the capture
    /// clock, so the time scaling is in them and has to come out. The Gain trace is one captured
    /// channel over <em>another</em>, both from the same record on the same clock — the ramp has
    /// already cancelled, and correcting it puts one in. That cancellation is the same fact as this
    /// app's long-standing advice to use the right input as a reference where phase matters.</para>
    ///
    /// <para>Found on the bench rather than here, 2026-11-11, with the correction wired to all
    /// three: the gain trace of a loopback read 0 µs of group delay uncorrected and −41 to −72 µs
    /// corrected, following the sweep's own time map exactly. Right-channel-as-reference is the
    /// frequency-response panel's <em>default</em>, so that was the first thing a user would have
    /// seen.</para>
    ///
    /// <para>Asserted as a difference between two runs rather than against a formula: the simulator
    /// has one clock, so whatever the correction does to a trace here is entirely the correction's
    /// doing, and the group delay a trace is reported with has been through smoothing and a
    /// least-squares slope that no closed form here would reproduce.</para>
    /// </summary>
    [Fact]
    public async Task TheClockDriftCorrectionReachesTheSyntheticReferenceTraces_AndNotTheGainTrace()
    {
        // Far larger than any real pair of crystals, so the ramp is unmistakable against the
        // simulator's own numerical floor.
        const double ppm = -1000.0;

        var plain = await CreateAsync();
        await plain.Runner.StartAsync(Request(), CancellationToken.None);
        var uncorrected = await AwaitCompletionAsync(plain.Runner);
        AssertFinished(uncorrected);

        var corrected = await CreateAsync();
        await corrected.Runner.StartAsync(Request(clockDriftPpm: ppm), CancellationToken.None);
        var withDrift = await AwaitCompletionAsync(corrected.Runner);
        AssertFinished(withDrift);

        double worstGain = 0;
        double mostLeft = 0;
        var gainBefore = Trace(uncorrected, FrequencyResponseTraceKind.Gain);
        var gainAfter = Trace(withDrift, FrequencyResponseTraceKind.Gain);
        var leftBefore = Trace(uncorrected, FrequencyResponseTraceKind.Left);
        var leftAfter = Trace(withDrift, FrequencyResponseTraceKind.Left);

        foreach (int i in MidBandColumns(gainBefore))
        {
            worstGain = Math.Max(worstGain, Math.Abs(gainAfter.GroupDelayS![i] - gainBefore.GroupDelayS![i]));
            mostLeft = Math.Max(mostLeft, Math.Abs(leftAfter.GroupDelayS![i] - leftBefore.GroupDelayS![i]));
        }

        // The left trace must move by something like the slip the figure describes -- 1000 ppm of a
        // 0.68 s record is 683 us -- and the gain trace must not move at all. A tenth of a
        // microsecond is far below anything a measurement could read and far above the arithmetic's
        // own noise, so the two bounds cannot both be met by accident.
        Assert.True(mostLeft > 100e-6,
            $"the correction should have moved the left trace's group delay; it moved {mostLeft * 1e6:0.00} us");
        Assert.True(worstGain < 0.1e-6,
            $"the gain trace is already on one clock and must not be corrected; it moved {worstGain * 1e6:0.00} us");
    }

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

    /// <summary>
    /// The whole point of offering anything but the sweep: a measurement is a measurement whatever
    /// excited it. Every one of these plays a different spectrum — white is flat, pink falls 3 dB an
    /// octave, and the speech shapes are 25 dB down at the top of the band against their own peak —
    /// and all five have to deconvolve to the same straight wire, because the reference carries
    /// whatever shape the stimulus has.
    ///
    /// <para>It is also the test that would catch the reference and the generator disagreeing, which
    /// is the failure this measurement has no other defence against: a stimulus divided by the wrong
    /// reference is reported as DUT response and nothing complains.</para>
    /// </summary>
    [Theory]
    [InlineData(ResponseStimulus.PinkNoise)]
    [InlineData(ResponseStimulus.WhiteNoise)]
    [InlineData(ResponseStimulus.SpeechMale2011)]
    [InlineData(ResponseStimulus.SpeechFemale2011)]
    [InlineData(ResponseStimulus.SpeechMale2020)]
    public async Task Measurement_WithANoiseStimulus_ReadsTheSameUnityGainTheChirpDoes(ResponseStimulus stimulus)
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(stimulus: stimulus), CancellationToken.None);
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
        // shorter size, and the phase flips to Measuring *before* MeasureOnceAsync configures the
        // real one. Sampling on the phase alone therefore reads the probe's period about one run in
        // four -- so the wait is for the period under test to appear, and the assertion is that it
        // appeared at all. A runner that configured the wrong size never satisfies the loop and
        // fails on the deadline instead of racing.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool measuredAtTheAnalysisSize = false;
        while (DateTime.UtcNow < deadline)
        {
            var chirp = harness.Session.EffectiveGeneratorConfig.Left.Chirp;
            if (harness.Runner.Status.Phase == FrequencyResponsePhase.Measuring
                && chirp is not null
                && harness.Session.EffectiveGeneratorConfig.Left.Enabled
                && chirp.RepeatFrames == request.FftSize)
            {
                measuredAtTheAnalysisSize = true;
                break;
            }

            await Task.Delay(10);
        }

        Assert.True(
            measuredAtTheAnalysisSize,
            $"the measurement never played a chirp whose period was its {request.FftSize}-sample analysis size; "
                + $"the generator was last left at {harness.Session.EffectiveGeneratorConfig.Left.Chirp?.RepeatFrames.ToString() ?? "no chirp"}");

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

    /// <summary>
    /// The analog RIAA playback curve (3180, 318 and 75 us), in dB relative to 1 kHz — what the
    /// simulator's <c>PhonoRiaa</c> model is built to reproduce, within 0.34 dB at 48 kHz.
    /// </summary>
    private static double RiaaDb(double freqHz)
    {
        static double Magnitude(double f)
        {
            var s = new System.Numerics.Complex(0.0, 2.0 * Math.PI * f);
            return ((1.0 + (s * 318e-6)) / ((1.0 + (s * 3180e-6)) * (1.0 + (s * 75e-6)))).Magnitude;
        }

        return 20.0 * Math.Log10(Magnitude(freqHz) / Magnitude(1000.0));
    }

    private static double MagnitudeAt(FrequencyResponseTrace trace, double freqHz)
    {
        int column = 0;
        for (int i = 1; i < trace.FreqHz.Length; i++)
        {
            if (Math.Abs(trace.FreqHz[i] - freqHz) < Math.Abs(trace.FreqHz[column] - freqHz))
                column = i;
        }

        return trace.MagnitudeMaxDb[column];
    }

    [Fact]
    public async Task OutputVoltageMode_AgainstADutWhosePeakSitsFarAboveItsMedian_IsRightOnTheFirstPass()
    {
        // The output-voltage target names the DUT's *median* level, and the input range predicted
        // from it assumes the DUT's peak sits there too. A phono stage breaks that by 20 dB at the
        // bottom of the band and by more against its median: with the range left at the prediction
        // the first pass came back flat-topped at the range's clip level, and each continuous pass
        // then re-ranged from that clipped peak — one rung of the ladder per pass, 6 → 18 → 30 →
        // 42 dBV, so the curve took four passes to appear and a SINGLE run never got there. The
        // range is measured from the widest setting after the level is settled, exactly as
        // input-voltage mode does it, so a single run lands on it at once.
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { Dut = SimulatedDutModel.PhonoRiaa };
        var harness = await CreateAsync(device);

        // -5 dBV over the default 200 Hz - 2 kHz reference band puts the generator near -4 dBV and
        // the DUT's 20 Hz output near +15 dBV, which needs the 24 dBV range where the target
        // predicts 0 dBV.
        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -5.0, rightIsReference: false),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(1, status.PassesCompleted);
        Assert.InRange(harness.Session.InputRangeDbv, 18.0, 30.0);

        // The shape is the RIAA curve, not the clip level: +19 dB at 20 Hz, unity at 1 kHz, -20 dB
        // at 20 kHz. A clipped first pass reads within a few dB of flat across all three.
        // Against the synthesised reference the Left trace is the output level in dBV, so the
        // DUT's gain is that less the generator's level.
        var left = Trace(status, FrequencyResponseTraceKind.Left);
        foreach (double freqHz in new[] { 20.0, 100.0, 1000.0, 10_000.0, 20_000.0 })
        {
            double gainDb = MagnitudeAt(left, freqHz) - status.GeneratorAmpDbv;
            Assert.InRange(gainDb, RiaaDb(freqHz) - 0.6, RiaaDb(freqHz) + 0.6);
        }

        // And the level was settled against an unclipped capture: the generator is where the
        // median of that curve over the reference band says it should be, not pushed up by lost
        // low-frequency energy.
        double expectedGeneratorDbv = -5.0 - RiaaBandMedianDb(BandwidthReferenceSpec.DefaultLowHz, BandwidthReferenceSpec.DefaultHighHz);
        Assert.InRange(status.GeneratorAmpDbv, expectedGeneratorDbv - 1.0, expectedGeneratorDbv + 1.0);
    }

    /// <summary>The analog RIAA curve's median over a band, in linear bins — what the probe's own median converges to.</summary>
    private static double RiaaBandMedianDb(double lowHz, double highHz)
    {
        var gains = new List<double>();
        for (double f = lowHz; f <= highHz; f += 1.0)
            gains.Add(RiaaDb(f));
        gains.Sort();
        return gains[gains.Count / 2];
    }

    [Theory]
    [InlineData(50.0, 100.0)]
    [InlineData(5_000.0, 10_000.0)]
    public async Task OutputVoltageMode_SetsTheLevelAtTheBandwidthReference(double lowHz, double highHz)
    {
        // "0 dBV out" of a phono stage is 0 dBV at 1 kHz to anyone reading its datasheet, and the
        // Measurements panel's bandwidth reference is the app's way of saying where a DUT's nominal
        // level sits — so the output-referred probe targets that same band. Before this the probe
        // took the median over the whole analysed band, which linear bins weight toward the top of
        // the span: against the RIAA at a 96 kHz span it sat near 48 kHz, 27 dB below 1 kHz, and
        // Joost's two runs with reference bands of 50-100 Hz and 5-10 kHz drove the DUT identically
        // (both to the 18 dBV ceiling). Here the two bands sit 27 dB apart on the curve, so the
        // generator has to land 27 dB apart too, and the trace has to read the target at each band.
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { Dut = SimulatedDutModel.PhonoRiaa };
        var harness = await CreateAsync(device);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -20.0, rightIsReference: false,
                bandwidthReferenceLowHz: lowHz, bandwidthReferenceHighHz: highHz),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        double expectedGeneratorDbv = -20.0 - RiaaBandMedianDb(lowHz, highHz);
        Assert.InRange(status.GeneratorAmpDbv, expectedGeneratorDbv - 1.0, expectedGeneratorDbv + 1.0);

        // The trace is the output level in dBV against the synthesised reference, so at the band's
        // median frequency it reads the target itself. The median of a monotone curve over a
        // linear grid sits at the band's midpoint.
        var left = Trace(status, FrequencyResponseTraceKind.Left);
        double midHz = (lowHz + highHz) / 2.0;
        Assert.InRange(MagnitudeAt(left, midHz), -20.0 - 1.0, -20.0 + 1.0);
    }

    [Fact]
    public async Task OutputVoltageMode_UnderAPeakReference_SetsTheLevelAtThePeak()
    {
        // The other reading the Measurements panel offers: under Peak the corners are measured
        // down from the trace's maximum, so the target is what the maximum should be. For the RIAA
        // that is the bottom of the sweep, +19.7 dB over 1 kHz at 10 Hz.
        var device = CleanDevice();
        device.SignalProfile = device.SignalProfile with { Dut = SimulatedDutModel.PhonoRiaa };
        var harness = await CreateAsync(device);

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -20.0, rightIsReference: false,
                bandwidthReference: BandwidthReferenceMode.Peak),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);

        var left = Trace(status, FrequencyResponseTraceKind.Left);
        Assert.InRange(left.PeakDb, -20.0 - 1.0, -20.0 + 1.0);
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
    // One past the ceiling. A literal rather than the constant because InlineData needs one, so it
    // has to be kept in step by hand -- AverageCountCeilingTests is what pins the number itself.
    [InlineData(24_000.0, 1, 24, 21)]     // too many averages
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
        // the level asked for is the level that comes *out* — at the bandwidth reference. A 2 kHz
        // low pass loses 8-14 dB over a 5-10 kHz reference band, so the generator must end up well
        // above the target, by the band's median loss.
        const double cornerHz = 2_000.0;
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz));

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -20.0,
                bandwidthReferenceLowHz: 5_000.0, bandwidthReferenceHighHz: 10_000.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.True(
            status.GeneratorAmpDbv > -20.0 + 3.0,
            $"expected the probe to compensate for the filter's loss, generator settled at {status.GeneratorAmpDbv:0.00} dBV");

        var losses = new List<double>();
        for (double f = 5_000.0; f <= 10_000.0; f += 1.0)
            losses.Add(-10.0 * Math.Log10(1.0 + ((f / cornerHz) * (f / cornerHz))));
        losses.Sort();
        double expectedGeneratorDbv = -20.0 - losses[losses.Count / 2];
        Assert.InRange(status.GeneratorAmpDbv, expectedGeneratorDbv - 1.0, expectedGeneratorDbv + 1.0);
    }

    [Fact]
    public async Task ALevelTheDutCannotReach_StopsTheRunAndSaysSoInTheUnitItWasAskedIn()
    {
        // The reported failure: a DUT that is off, muted or patched to the wrong socket cannot be
        // driven to the level asked for, and the probe used to accept the generator's ceiling and
        // deconvolve the whole sweep at it -- a complete, plausible curve of the analyzer's own
        // floor. Reproduced by asking a unity DUT for more than the generator can make, which is
        // the same dead end: at the ceiling there is nothing left to correct with.
        //
        // In watts on purpose, since this is the one runner path a *power* target reaches first:
        // 80 % of a power is 0.97 dB where 80 % of a voltage is 1.94, and the message has to quote
        // the quantity the user typed. See OutputLevelReach.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputPower, levelValue: 100.0,
                levelUnit: ThdSweepLevelUnit.Watt, loadOhms: 8.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        Assert.Equal(FrequencyResponsePhase.Failed, status.Phase);
        Assert.NotNull(status.Error);
        Assert.Contains("of the 100 W asked for", status.Error);
        Assert.Contains("18 dBV ceiling", status.Error);
        Assert.Contains("powered on", status.Error);
    }

    [Fact]
    public async Task ALevelTheDutReaches_RunsEvenThoughTheProbeStopsHalfADecibelShort()
    {
        // The other side of the same rule, and the regression that would hurt most: every probe
        // stops correcting at 0.5 dB, which must never be read as a DUT that failed to reach its
        // level.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(
            Request(levelMode: ThdSweepLevelMode.OutputVoltage, levelValue: -10.0),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
    }

    [Fact]
    public async Task Measurement_DrivingAOnePoleDut_FindsItsCorner()
    {
        // Peak-referenced on purpose. This DUT's corner sits at 2 kHz, i.e. *inside* the default
        // 200 Hz - 2 kHz reference band, so a passband median taken there is already a decibel or two
        // down and the -3 dB corner it yields is honestly somewhere else. A reference band has to lie
        // where the DUT is flat; when it cannot, the peak is the reference — which is exactly what
        // the setting is for. See Measurement_WithAPassbandReference_FindsACornerAboveTheBand for the
        // default's own case.
        const double cornerHz = 2_000.0;
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz));

        await harness.Runner.StartAsync(
            Request(smoothing: 24, bandwidthReference: BandwidthReferenceMode.Peak),
            CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var left = Trace(status, FrequencyResponseTraceKind.Left);

        Assert.False(left.ThreeDb.UpperClamped);
        Assert.InRange(left.ThreeDb.UpperHz, cornerHz * 0.9, cornerHz * 1.1);
    }

    [Fact]
    public async Task Measurement_WithAPassbandReference_FindsACornerAboveTheBand()
    {
        // The default reference, on a DUT whose passband actually contains the reference band: the
        // corner is well above it, so the median is taken on the flat part and the -3 dB point is
        // the pole's own.
        const double cornerHz = 12_000.0;
        double expectedHz = OnePoleMinusThreeDbHz(cornerHz);
        var harness = await CreateAsync(new OnePoleLowPassDevice(CleanDevice(), cornerHz));

        await harness.Runner.StartAsync(Request(smoothing: 24), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        var left = Trace(status, FrequencyResponseTraceKind.Left);

        Assert.Equal(BandwidthReferenceMode.BandMedian, left.ReferenceMode);
        Assert.False(left.ReferenceFellBackToPeak);
        Assert.False(left.ThreeDb.UpperClamped);
        Assert.InRange(left.ThreeDb.UpperHz, expectedHz * 0.9, expectedHz * 1.1);
    }

    /// <summary>
    /// The −3 dB frequency <see cref="OnePoleLowPassDevice"/> actually has, which is not the corner
    /// it was constructed with.
    ///
    /// <para>Its coefficient is the impulse-invariant <c>1 − exp(−2π·fc/fs)</c>, and that filter's
    /// −3 dB point drifts above the nominal corner as the corner approaches Nyquist: 0.5 % at 2 kHz,
    /// but 34 % at 12 kHz, and at 20 kHz on a 48 kHz rate it never reaches −3 dB at all before
    /// Nyquist. A test asserting the nominal figure at those frequencies would be asserting the
    /// fixture's warping rather than the analyzer's answer — which is exactly how this helper came
    /// to exist.</para>
    /// </summary>
    private static double OnePoleMinusThreeDbHz(double cornerHz, double sampleRateHz = 48_000.0)
    {
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / sampleRateHz);
        double pole = 1.0 - alpha;

        // |H(w)|² = alpha² / (1 + pole² − 2·pole·cos w), set to ½ and solved for cos w.
        double cosW = (1.0 + (pole * pole) - (2.0 * alpha * alpha)) / (2.0 * pole);
        return Math.Acos(Math.Clamp(cosW, -1.0, 1.0)) * sampleRateHz / (2.0 * Math.PI);
    }

    [Fact]
    public async Task Measurement_WithAResonanceAboveThePassband_StillReportsThePassbandsCorner()
    {
        // The measurement this whole reference mode exists for, end to end. A DUT with a peak above
        // its passband -- an output transformer's leakage resonance -- referenced to the peak reports
        // a "bandwidth" that is the resonance's own width, a couple of kHz wide and nowhere near the
        // amplifier's low end. The passband median reports the amplifier.
        // 10 kHz corner with a 9 kHz resonance on its shoulder: the same geometry as a transformer's
        // 38 kHz peak above a 46 kHz corner, scaled to fit the harness's 24 kHz span.
        var harness = await CreateAsync(new ResonantDevice(CleanDevice(), cornerHz: 10_000.0, resonanceHz: 9_000.0));

        await harness.Runner.StartAsync(
            Request(smoothing: 24, bandwidthReference: BandwidthReferenceMode.Peak),
            CancellationToken.None);
        var peakReferenced = Trace(await AwaitCompletionAsync(harness.Runner), FrequencyResponseTraceKind.Left);

        await harness.Runner.StartAsync(Request(smoothing: 24), CancellationToken.None);
        var bandReferenced = Trace(await AwaitCompletionAsync(harness.Runner), FrequencyResponseTraceKind.Left);

        // Peak-referenced: both corners sit on the resonance's skirts, so the band does not even
        // begin until well above the passband it is supposed to describe.
        Assert.True(
            peakReferenced.OneDb.LowerHz > 3_000.0,
            $"expected the peak reference to report the resonance, lower corner was {peakReferenced.OneDb.LowerHz:0} Hz");

        // Passband-referenced: the whole ±1 dB band lies below where the peak-referenced one starts,
        // because it is describing the amplifier rather than the peak sitting above it.
        Assert.True(
            bandReferenced.OneDb.UpperHz < peakReferenced.OneDb.LowerHz,
            $"expected the passband reference to report the amplifier: ±1 dB to {bandReferenced.OneDb.UpperHz:0} Hz "
            + $"against a peak-referenced band starting at {peakReferenced.OneDb.LowerHz:0} Hz");
        Assert.True(
            bandReferenced.OneDb.LowerHz < 200.0,
            $"expected the passband reference to reach down the band, lower corner was {bandReferenced.OneDb.LowerHz:0} Hz");
    }

    [Fact]
    public async Task ApplyBandwidthReference_AfterTheRun_RefindsTheCornersWithoutRemeasuring()
    {
        // The reference is a reading of the response, not a parameter of the stimulus, so the panel
        // can change it after the run and the corners follow -- against the estimates the runner
        // kept, at full resolution, exactly as a fresh measurement against that reference finds
        // them. Same DUT as the test above; measured peak-referenced, then re-referenced to the
        // passband in place, which must agree with measuring passband-referenced outright.
        var harness = await CreateAsync(new ResonantDevice(CleanDevice(), cornerHz: 10_000.0, resonanceHz: 9_000.0));

        await harness.Runner.StartAsync(
            Request(smoothing: 24, bandwidthReference: BandwidthReferenceMode.Peak),
            CancellationToken.None);
        var peakReferenced = Trace(await AwaitCompletionAsync(harness.Runner), FrequencyResponseTraceKind.Left);

        var reapplied = Trace(
            harness.Runner.ApplyBandwidthReference(BandwidthReferenceSpec.DefaultBand),
            FrequencyResponseTraceKind.Left);

        await harness.Runner.StartAsync(Request(smoothing: 24), CancellationToken.None);
        var bandReferenced = Trace(await AwaitCompletionAsync(harness.Runner), FrequencyResponseTraceKind.Left);

        Assert.NotEqual(peakReferenced.OneDb.LowerHz, reapplied.OneDb.LowerHz);
        Assert.Equal(BandwidthReferenceMode.BandMedian, reapplied.ReferenceMode);
        Assert.Equal(bandReferenced.ReferenceDb, reapplied.ReferenceDb, 3);
        Assert.Equal(bandReferenced.OneDb.LowerHz, reapplied.OneDb.LowerHz, 1);
        Assert.Equal(bandReferenced.OneDb.UpperHz, reapplied.OneDb.UpperHz, 1);
        Assert.Equal(bandReferenced.ThreeDb.LowerHz, reapplied.ThreeDb.LowerHz, 1);
        Assert.Equal(bandReferenced.ThreeDb.UpperHz, reapplied.ThreeDb.UpperHz, 1);
    }

    /// <summary>
    /// A pure delay of 100 frames on the left channel. Against the right input as reference the
    /// gain estimator's removed rotation *is* that delay, so both delay curves read it back once
    /// the runner has put it back; against the synthesised chirp the removed rotation was the
    /// record's arbitrary start point, so the left trace reads excess delay only -- nothing.
    /// </summary>
    [Fact]
    public async Task ADelayedDut_ReadsItsDelayOnTheGainTrace_AndOnlyExcessOnTheLeft()
    {
        const int delayFrames = 100;
        var taps = new double[delayFrames + 1];
        taps[delayFrames] = 1.0;
        var harness = await CreateAsync(new KnownImpulseResponseDevice(CleanDevice(), taps));

        await harness.Runner.StartAsync(Request(rightIsReference: true, smoothing: 24), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        double expectedS = delayFrames / status.SampleRateHz;
        Assert.NotNull(status.LatencySamples);
        Assert.Equal(delayFrames, status.LatencySamples!.Value, 0.5);

        var gain = Trace(status, FrequencyResponseTraceKind.Gain);
        var left = Trace(status, FrequencyResponseTraceKind.Left);
        foreach (int i in MidBandColumns(gain))
        {
            Assert.True(Math.Abs(gain.GroupDelayS[i] - expectedS) < 0.01 * expectedS,
                $"gain group delay {gain.GroupDelayS[i] * 1e6:F1} µs at {gain.FreqHz[i]:F0} Hz, expected {expectedS * 1e6:F1}");
            Assert.True(Math.Abs(gain.PhaseDelayS[i] - expectedS) < 0.01 * expectedS,
                $"gain phase delay {gain.PhaseDelayS[i] * 1e6:F1} µs at {gain.FreqHz[i]:F0} Hz, expected {expectedS * 1e6:F1}");
            Assert.True(Math.Abs(left.GroupDelayS[i]) < 20e-6,
                $"left group delay {left.GroupDelayS[i] * 1e6:F1} µs at {left.FreqHz[i]:F0} Hz should be excess only");
        }
    }

    [Fact]
    public async Task ApplyBandwidthReference_KeepsTheDelayCurves()
    {
        // The rebuild path goes through the same trace builder as a pass, so the delays survive a
        // post-run reference change -- and the phase delay is re-anchored at the new reference,
        // which on a pure delay changes nothing.
        const int delayFrames = 100;
        var taps = new double[delayFrames + 1];
        taps[delayFrames] = 1.0;
        var harness = await CreateAsync(new KnownImpulseResponseDevice(CleanDevice(), taps));

        await harness.Runner.StartAsync(Request(rightIsReference: true), CancellationToken.None);
        var measured = Trace(await AwaitCompletionAsync(harness.Runner), FrequencyResponseTraceKind.Gain);

        var reapplied = Trace(
            harness.Runner.ApplyBandwidthReference(new BandwidthReferenceSpec(BandwidthReferenceMode.BandMedian, 500.0, 5_000.0)),
            FrequencyResponseTraceKind.Gain);

        Assert.Equal(measured.GroupDelayS.Length, reapplied.GroupDelayS.Length);
        foreach (int i in MidBandColumns(measured))
        {
            Assert.Equal(measured.GroupDelayS[i], reapplied.GroupDelayS[i], 9);
            Assert.Equal(measured.PhaseDelayS[i], reapplied.PhaseDelayS[i], 9);
        }
    }

    [Fact]
    public async Task ApplyBandwidthReference_IsRecordedOnTheRequestASaveReads()
    {
        // A result saved after the reference was changed must carry the reference its corners were
        // found against, not the one the run started with -- LastRequest is what the save reads.
        var harness = await CreateAsync(CleanDevice());
        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        await AwaitCompletionAsync(harness.Runner);

        harness.Runner.ApplyBandwidthReference(new BandwidthReferenceSpec(BandwidthReferenceMode.BandMedian, 500.0, 5_000.0));

        var request = Assert.IsType<FrequencyResponseRequest>(harness.Runner.LastRequest);
        Assert.Equal(BandwidthReferenceMode.BandMedian, request.BandwidthReference);
        Assert.Equal(500.0, request.BandwidthReferenceLowHz);
        Assert.Equal(5_000.0, request.BandwidthReferenceHighHz);
    }

    [Fact]
    public async Task ApplyBandwidthReference_WithNothingMeasured_ChangesNothingButIsNotAnError()
    {
        var harness = await CreateAsync(CleanDevice());
        var status = harness.Runner.ApplyBandwidthReference(BandwidthReferenceSpec.Peak);
        Assert.Empty(status.Traces);
        Assert.Null(harness.Runner.LastRequest);
    }

    [Fact]
    public async Task ARunSwitchedAwayFromAndBackTo_IsStillThere()
    {
        // The sweep report of 2026-10-17 in this runner: one chirp runner serves every
        // frequency-response measurement, so selecting a second one and coming back used to find an
        // empty chart. See ThdSweepRunner.SwitchOwner.
        var harness = await CreateAsync();
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        var measured = harness.Runner.Status;
        Assert.NotEmpty(measured.Traces);

        // A different response measurement: this one has never run, so it gets an empty chart.
        var other = harness.Runner.SwitchOwner("1:2");
        Assert.Empty(other.Traces);
        Assert.Equal(FrequencyResponsePhase.Idle, other.Phase);
        Assert.Empty(harness.Runner.Status.Traces);

        var back = harness.Runner.SwitchOwner("1:1");

        Assert.Equal(measured.Traces.Count, back.Traces.Count);
        Assert.Equal(measured.Phase, back.Phase);
        Assert.Equal(measured.FftSize, back.FftSize);
        // The returned status and the runner's own must agree -- the browser applies the response
        // directly rather than polling again.
        Assert.Equal(back.Traces.Count, harness.Runner.Status.Traces.Count);
        // And the as-run request with it, or the restored run could be looked at and not saved:
        // MeasurementSaveService.SaveFrequencyResponseAsync refuses without one.
        Assert.NotNull(harness.Runner.LastRequest);
        // The full-resolution estimates too, or a restored run's bandwidth reference could be
        // changed and quietly do nothing.
        Assert.NotEmpty(harness.Runner.ApplyBandwidthReference(BandwidthReferenceSpec.Peak).Traces);
    }

    [Fact]
    public async Task SwitchingOwnerWhileARunIsGoing_IsIgnoredRatherThanRacingTheWriter()
    {
        // Unreachable from the app -- a measurement switch is refused while anything runs -- but a
        // park that lifted the traces a pass is about to replace would be far worse than a no-op.
        var harness = await CreateAsync();
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
        var harness = await CreateAsync();
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
        // A page that reloaded mid-run, or a run started from a script: nobody owns it, and the
        // first measurement to arrive keeps it -- the same rule the FFT display's owner follows.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        int measured = harness.Runner.Status.Traces.Count;

        Assert.Equal(measured, harness.Runner.SwitchOwner("1:1").Traces.Count);
    }

    [Fact]
    public async Task ADestroyedMeasurementsParkedRun_IsForgotten()
    {
        // A permanent delete from the trash: nothing can select 1:1 again, so holding its run is
        // holding three arrays of FftSize/2 doubles for nobody.
        var harness = await CreateAsync();
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        harness.Runner.SwitchOwner("1:2");

        harness.Runner.ForgetOwner("1:1");

        Assert.Empty(harness.Runner.SwitchOwner("1:1").Traces);
    }

    [Fact]
    public async Task ForgettingTheMeasurementThatOwnsTheRun_EmptiesIt_SoTheNextOneCannotAdoptIt()
    {
        // The half that is about correctness rather than memory: an unowned run is adopted by the
        // next measurement to arrive, so a destroyed measurement's curves left in place would be
        // handed to whoever was selected next -- and Save would store them there.
        var harness = await CreateAsync();
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        Assert.NotEmpty(harness.Runner.Status.Traces);

        harness.Runner.ForgetOwner("1:1");

        Assert.Null(harness.Runner.Owner);
        Assert.Empty(harness.Runner.Status.Traces);
        Assert.Null(harness.Runner.LastRequest);
        Assert.Empty(harness.Runner.SwitchOwner("1:2").Traces);
    }

    [Fact]
    public async Task ForgettingAMeasurementWhoseRunIsStillGoing_LeavesTheRunAlone()
    {
        // Unreachable from the app -- a measurement has to be trashed before it can be destroyed,
        // and the run would have been cancelled -- but pulling the state out from under the pass
        // still writing it is the one thing worse than a stale owner.
        var harness = await CreateAsync();
        harness.Runner.SwitchOwner("1:1");

        await harness.Runner.StartAsync(Request(continuous: true), CancellationToken.None);
        try
        {
            harness.Runner.ForgetOwner("1:1");
            Assert.Equal("1:1", harness.Runner.Owner);
            Assert.True(harness.Runner.Running);
        }
        finally
        {
            await harness.Runner.CancelAsync();
        }
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
