using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

public class ImpulseResponseRunnerTests
{
    /// <summary>
    /// The smallest span, so a record is 32768 frames at 48 kHz — 0.68 s. Everything under test is
    /// independent of the span; a larger one only makes the test slower.
    /// </summary>
    private const double SpanHz = 24_000.0;

    private const double SampleRateHz = 2.0 * SpanHz;

    /// <summary>Where the test DUT's reflection sits. Comfortably inside the 20 ms default gate.</summary>
    private const int ReflectionSamples = 200;

    private const double ReflectionAmplitude = 0.5;

    private static ImpulseResponseRequest Request(
        double spanHz = SpanHz,
        int resolutionMultiplier = 1,
        ThdSweepLevelMode levelMode = ThdSweepLevelMode.GeneratorVoltage,
        double levelValue = -20.0,
        ThdSweepLevelUnit levelUnit = ThdSweepLevelUnit.Dbv,
        int smoothing = 0,
        int averages = 1,
        ImpulseReferenceMode referenceMode = ImpulseReferenceMode.None,
        long? referenceCalibrationId = null,
        double gatePreMs = ImpulseResponseRequest.DefaultGatePreMs,
        double gatePostMs = ImpulseResponseRequest.DefaultGatePostMs,
        WindowType gateWindow = WindowType.Rectangular,
        bool extractHarmonics = true,
        int maxHarmonicOrder = 5,
        bool continuous = false) =>
        new(spanHz, resolutionMultiplier, levelMode, levelValue, levelUnit, smoothing, averages,
            referenceMode, referenceCalibrationId, gatePreMs, gatePostMs, gateWindow,
            extractHarmonics, maxHarmonicOrder, continuous);

    private sealed record Harness(
        ImpulseResponseRunner Runner,
        DeviceSessionService Session,
        SpectrumPipeline Pipeline,
        InMemoryLoopbackReferenceStore References);

    /// <summary>
    /// A clean simulated DUT: no fabricated harmonic stack, no noise, no hum — so the impulse
    /// response under test is exactly what the decorator puts there and nothing else.
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

        var references = new InMemoryLoopbackReferenceStore();

        return new Harness(
            new ImpulseResponseRunner(session, pipeline, references, NullLogger<ImpulseResponseRunner>.Instance),
            session,
            pipeline,
            references);
    }

    private static async Task<ImpulseResponseStatus> AwaitCompletionAsync(ImpulseResponseRunner runner, int timeoutSeconds = 90)
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

    private static void AssertFinished(ImpulseResponseStatus status) =>
        Assert.True(
            status.Phase == FrequencyResponsePhase.Finished,
            $"the measurement ended in {status.Phase}: {status.Error ?? "(no message)"}");

    private static ImpulseTrace Impulse(ImpulseResponseStatus status) =>
        status.Impulses.Single(i => i.Kind == FrequencyResponseTraceKind.Left);

    /// <summary>
    /// A DUT that is a straight wire plus one delayed echo — the classic "direct sound and a
    /// reflection" an impulse response exists to show.
    /// </summary>
    private static double[] ReflectionTaps()
    {
        var taps = new double[ReflectionSamples + 1];
        taps[0] = 1.0;
        taps[ReflectionSamples] = ReflectionAmplitude;
        return taps;
    }

    [Fact]
    public async Task Measurement_AgainstAStraightLoopback_PutsTheImpulseAtTimeZero()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        var impulse = Impulse(status);

        int peak = PeakColumn(impulse);
        Assert.InRange(impulse.TimeS[peak], -0.0001, 0.0001);

        // And it really is an impulse rather than a smear: everything more than a millisecond away
        // is far below it.
        double faraway = 0.0;
        for (int c = 0; c < impulse.TimeS.Length; c++)
            if (Math.Abs(impulse.TimeS[c]) > 0.001)
                faraway = Math.Max(faraway, Math.Max(Math.Abs(impulse.MinVolts[c]), Math.Abs(impulse.MaxVolts[c])));

        Assert.True(faraway < impulse.PeakVolts / 20.0,
            $"tail {faraway:0.000000} against a peak of {impulse.PeakVolts:0.000000}");
    }

    /// <summary>
    /// The measurement's whole reason to exist: a reflection shows up as a second impulse, at the
    /// delay that produced it and at the level that produced it. Gated rectangularly so the window
    /// does not attenuate the echo and turn an exact ratio into an approximate one.
    /// </summary>
    [Fact]
    public async Task Measurement_AgainstADutWithAnEcho_RecoversItsDelayAndLevel()
    {
        var harness = await CreateAsync(new KnownImpulseResponseDevice(CleanDevice(), ReflectionTaps()));

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        var impulse = Impulse(status);
        double expectedSeconds = ReflectionSamples / SampleRateHz;

        int direct = PeakColumn(impulse);
        int echo = PeakColumnNear(impulse, expectedSeconds);

        Assert.InRange(impulse.TimeS[direct], -0.0001, 0.0001);
        Assert.Equal(expectedSeconds, impulse.TimeS[echo], 1.0 / SampleRateHz);

        double directVolts = Extent(impulse, direct);
        double echoVolts = Extent(impulse, echo);
        Assert.Equal(ReflectionAmplitude, echoVolts / directVolts, 0.02);
    }

    /// <summary>
    /// The simulator's chirp path is linear, so every harmonic curve must sit at the floor. This is
    /// the negative half of the distortion story — <c>HarmonicPacketExtractorTests</c> has the
    /// positive half — and it is what catches an extractor that reads the linear impulse's own
    /// skirt and reports it as distortion on every DUT.
    /// </summary>
    [Fact]
    public async Task Measurement_OfALinearDut_ReportsEveryHarmonicAtTheFloor()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        Assert.Equal([2, 3, 4, 5], status.Harmonics.Select(h => h.Order));

        foreach (var curve in status.Harmonics)
        {
            double worst = curve.LevelDbc.Length == 0 ? -200.0 : curve.LevelDbc.Max();
            Assert.True(worst < -60.0, $"H{curve.Order} reached {worst:0.0} dBc on a linear DUT");
        }
    }

    /// <summary>
    /// Each harmonic curve stops at its own frequency — the k-th harmonic can only be observed for
    /// fundamentals up to the sweep's end over k — so a chart is never asked to draw a decade nothing
    /// measured.
    /// </summary>
    [Fact]
    public async Task Measurement_ReportsAHarmonicBandThatShrinksWithOrder()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        var byOrder = status.Harmonics.OrderBy(h => h.Order).ToList();
        for (int i = 1; i < byOrder.Count; i++)
            Assert.True(byOrder[i].UpperValidHz < byOrder[i - 1].UpperValidHz);

        foreach (var curve in byOrder)
            Assert.True(curve.FundamentalHz[^1] <= curve.UpperValidHz + 1e-6);
    }

    /// <summary>
    /// The stored-loopback path, end to end and through the store: capture a calibration through a
    /// deliberately non-flat "analyzer chain", then measure the same chain and assert what comes back
    /// is flat. The uncorrected run is asserted to be rippled in the same test, because a correction
    /// that flattened nothing would otherwise pass against a chain that was never bent.
    /// </summary>
    [Fact]
    public async Task Measurement_WithAStoredLoopbackReference_DividesOutTheAnalyzersOwnResponse()
    {
        // A comb: unity plus a 0.3 echo 24 samples later, so |H| swings between 0.7 and 1.3 with a
        // period of fs/24 = 2 kHz. The delay matters as much as the depth — a one-sample echo combs
        // only near Nyquist, which is outside the band this test reads and made an earlier version
        // of it pass against a correction that did nothing. Its median gain is still unity, so the
        // calibration's own "is this really a loopback?" check accepts it.
        var chain = new double[25];
        chain[0] = 1.0;
        chain[24] = 0.3;
        var harness = await CreateAsync(new KnownImpulseResponseDevice(CleanDevice(), chain));

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        var uncorrected = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(uncorrected);
        double uncorrectedRipple = RippleDb(uncorrected);

        var info = await harness.Runner.CaptureReferenceAsync(
            new ImpulseReferenceCaptureRequest("bench loopback", SpanHz, 1, GeneratorLevelDbv: -20.0),
            CancellationToken.None);

        Assert.Equal(1, harness.References.Count);
        Assert.Equal(SampleRateHz, info.SampleRateHz);

        await harness.Runner.StartAsync(
            Request(referenceMode: ImpulseReferenceMode.StoredLoopback, referenceCalibrationId: info.Id),
            CancellationToken.None);
        var corrected = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(corrected);

        Assert.Equal("bench loopback", corrected.ReferenceCalibrationName);

        double correctedRipple = RippleDb(corrected);

        // Guards the guard: a chain that was never bent would make the correction look perfect.
        Assert.True(uncorrectedRipple > 3.0, $"the stand-in chain only rippled {uncorrectedRipple:0.00} dB, so nothing was proved");
        Assert.True(correctedRipple < 0.5, $"corrected trace still ripples {correctedRipple:0.00} dB (was {uncorrectedRipple:0.00} dB)");
    }

    [Fact]
    public async Task CaptureReference_ThroughADut_IsRefusedRatherThanStored()
    {
        // A 6 dB pad stands in for "the DUT is still in the path": nothing about the capture fails,
        // it simply is not a loopback, and storing it would divide the DUT out of every later run.
        var harness = await CreateAsync(new KnownImpulseResponseDevice(CleanDevice(), [0.5]));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Runner.CaptureReferenceAsync(
                new ImpulseReferenceCaptureRequest("not a loopback", SpanHz, 1, GeneratorLevelDbv: -20.0),
                CancellationToken.None));

        Assert.Contains("loopback", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.References.Count);
    }

    [Fact]
    public async Task Start_WithAReferenceIdThatDoesNotExist_IsRejectedBeforeAnythingRuns()
    {
        var harness = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => harness.Runner.StartAsync(
                Request(referenceMode: ImpulseReferenceMode.StoredLoopback, referenceCalibrationId: 42),
                CancellationToken.None));

        Assert.False(harness.Runner.Running);
        Assert.Equal(FrequencyResponsePhase.Idle, harness.Runner.Status.Phase);
    }

    [Fact]
    public async Task Start_WhileRunning_IsRejected()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(continuous: true), CancellationToken.None);
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
    public async Task Clear_WhileRunning_Throws()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(continuous: true), CancellationToken.None);
        try
        {
            Assert.Throws<InvalidOperationException>(harness.Runner.Clear);
        }
        finally
        {
            await harness.Runner.CancelAsync();
        }
    }

    /// <summary>
    /// The readout the gate panel is built around, carried on the status so the UI states the limit
    /// of the data it is showing rather than of whatever the form currently holds.
    /// </summary>
    [Fact]
    public async Task Status_ReportsTheGateAndItsResolutionLimit()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(gatePreMs: 1.0, gatePostMs: 9.0), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);

        Assert.Equal(1.0, status.GatePreMs);
        Assert.Equal(9.0, status.GatePostMs);
        Assert.Equal(100.0, status.GateResolutionHz, 0.001);

        var impulse = Impulse(status);
        Assert.Equal(100.0, impulse.ResolutionLimitHz, 1.0);
        Assert.Equal((int)Math.Round(0.010 * SampleRateHz) + 1, impulse.FullSampleCount);
    }

    /// <summary>
    /// A display gate wider than the sweep's own anti-distortion gate would keep the harmonic packets
    /// that gate exists to delete, so the request is refused rather than quietly clamped — a clamp
    /// would report a measurement of settings the user did not ask for.
    /// </summary>
    [Fact]
    public async Task Start_WithAGateWiderThanTheSweepAllows_IsRejected()
    {
        var harness = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => harness.Runner.StartAsync(Request(gatePreMs: 60.0), CancellationToken.None));
    }

    private static int PeakColumn(ImpulseTrace impulse)
    {
        int best = 0;
        for (int c = 1; c < impulse.TimeS.Length; c++)
            if (Extent(impulse, c) > Extent(impulse, best))
                best = c;
        return best;
    }

    /// <summary>The largest column within half a millisecond of a given time — the echo, not the direct sound.</summary>
    private static int PeakColumnNear(ImpulseTrace impulse, double seconds)
    {
        int best = -1;
        for (int c = 0; c < impulse.TimeS.Length; c++)
        {
            if (Math.Abs(impulse.TimeS[c] - seconds) > 0.0005)
                continue;

            if (best < 0 || Extent(impulse, c) > Extent(impulse, best))
                best = c;
        }

        Assert.True(best >= 0, $"no column within 0.5 ms of {seconds * 1000.0:0.00} ms");
        return best;
    }

    private static double Extent(ImpulseTrace impulse, int column) =>
        Math.Max(Math.Abs(impulse.MinVolts[column]), Math.Abs(impulse.MaxVolts[column]));

    /// <summary>Peak-to-peak departure from flat, in dB, over the middle of the gain trace.</summary>
    private static double RippleDb(ImpulseResponseStatus status)
    {
        var trace = status.Traces.Single(t => t.Kind == FrequencyResponseTraceKind.Gain);

        double lo = double.PositiveInfinity;
        double hi = double.NegativeInfinity;
        for (int c = trace.FreqHz.Length / 4; c < trace.FreqHz.Length * 3 / 4; c++)
        {
            lo = Math.Min(lo, trace.MagnitudeMinDb[c]);
            hi = Math.Max(hi, trace.MagnitudeMaxDb[c]);
        }

        return hi - lo;
    }
}
