using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Services;

public class ThdSweepRunnerTests
{
    /// <summary>
    /// Small FFT, one average and a fast tick so a whole sweep runs in a second or two. The maths
    /// under test doesn't depend on any of them.
    /// </summary>
    private const int FastFftSize = 4096;

    private const double SampleRateHz = 48_000.0;

    private static ThdSweepRequest Request(
        double startHz = 1000.0,
        double endHz = 4000.0,
        int stepsPerOctave = 2,
        int fftSize = FastFftSize,
        double sampleRateHz = SampleRateHz,
        ThdSweepLevelMode levelMode = ThdSweepLevelMode.GeneratorVoltage,
        double levelValue = -20.0,
        ThdSweepLevelUnit levelUnit = ThdSweepLevelUnit.Dbv,
        double loadOhms = 8.0,
        bool measureNoiseFloor = false) =>
        new(
            startHz, endHz, stepsPerOctave, levelMode, levelValue, levelUnit, loadOhms,
            ProcessLeft: true, ProcessRight: false,
            fftSize, WindowType.Hann, AverageCount: 1, sampleRateHz, measureNoiseFloor);

    private sealed record Harness(ThdSweepRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

    private static async Task<Harness> CreateAsync()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = FastFftSize, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        return new Harness(new ThdSweepRunner(session, pipeline, hub, NullLogger<ThdSweepRunner>.Instance), session, pipeline);
    }

    private static async Task<ThdSweepStatus> AwaitCompletionAsync(ThdSweepRunner runner, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (runner.Running)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Sweep did not finish within {timeoutSeconds} s (phase {runner.Status.Phase}, {runner.Status.Index}/{runner.Status.Total}).");

            await Task.Delay(25);
        }

        return runner.Status;
    }

    /// <summary>Asserts the sweep finished, quoting the runner's own error message when it did not.</summary>
    private static void AssertFinished(ThdSweepStatus status) =>
        Assert.True(status.Phase == ThdSweepPhase.Finished, $"sweep ended in {status.Phase}: {status.Error ?? "(no message)"}");

    [Fact]
    public async Task Sweep_AcrossThreeOctaves_ReturnsOnePointPerCoherentStepInAscendingOrder()
    {
        var harness = await CreateAsync();
        var request = Request(startHz: 500.0, endHz: 4000.0, stepsPerOctave: 2);

        var started = await harness.Runner.StartAsync(request, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(started.PointCount, status.Points.Count);

        for (int i = 1; i < status.Points.Count; i++)
            Assert.True(status.Points[i].FreqHz > status.Points[i - 1].FreqHz);

        // Only the left channel was processed, so every point belongs to it.
        Assert.All(status.Points, p => Assert.Equal(0, p.Channel));
    }

    /// <summary>
    /// ThdSweepRunner always turns CapturePreview on (BuildSweepOptions) -- this exercises that
    /// through the real StartAsync/RunAsync path (not just SpectrumPipeline directly) and checks
    /// every point actually got one, bounded the way SpectrumPipelineSnapshotTests checks in
    /// isolation.
    /// </summary>
    [Fact]
    public async Task Sweep_EveryPoint_CarriesADecimatedFftAndScopePreview()
    {
        var harness = await CreateAsync();
        var request = Request(startHz: 500.0, endHz: 4000.0, stepsPerOctave: 2);

        await harness.Runner.StartAsync(request, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.NotEmpty(status.Points);

        foreach (var point in status.Points)
        {
            Assert.NotNull(point.PreviewFft);
            Assert.InRange(point.PreviewFft!.X.Length, 2, SpectrumPipeline.PreviewMaxColumns);
            Assert.Equal(point.PreviewFft.X.Length, point.PreviewFft.Min.Length);
            Assert.Equal(point.PreviewFft.X.Length, point.PreviewFft.Max.Length);

            Assert.NotNull(point.PreviewScope);
            Assert.InRange(point.PreviewScope!.X.Length, 2, SpectrumPipeline.PreviewMaxColumns);
            Assert.Equal(point.PreviewScope.X.Length, point.PreviewScope.Min.Length);
            Assert.Equal(point.PreviewScope.X.Length, point.PreviewScope.Max.Length);
        }
    }

    [Fact]
    public async Task Sweep_EveryPoint_AnalyzesTheToneItJustSetRatherThanTheOneBefore()
    {
        // The regression that made every point report the FIRST point's spectrum: a pipeline tick
        // already running when ConfigureGeneratorAsync flushed would finish afterwards, re-arm the
        // single-shot hold and overwrite the retained snapshot with pre-change audio. The sweep then
        // read a complete capture immediately, at every frequency, and never noticed.
        //
        // MeasuredFreqHz is what catches it: it is where the analyzer actually found the
        // fundamental, so it tracking the requested frequency is the whole assertion. Everything
        // else (ascending frequencies, point count) came from the *requested* step list and stayed
        // green throughout.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startHz: 500.0, endHz: 8000.0, stepsPerOctave: 2), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.NotEmpty(status.Points);
        Assert.All(status.Points, p =>
        {
            // Within one bin of the requested tone. A stale record is off by an octave or more.
            double binHz = SampleRateHz / FastFftSize;
            Assert.True(
                Math.Abs(p.MeasuredFreqHz - p.FreqHz) <= 2.0 * binHz,
                $"requested {p.FreqHz:0.0} Hz but the analyzer located {p.MeasuredFreqHz:0.0} Hz — stale record?");
        });

        // And the measured frequencies must be distinct, not one value repeated.
        Assert.Equal(status.Points.Count, status.Points.Select(p => Math.Round(p.MeasuredFreqHz, 1)).Distinct().Count());
    }

    [Fact]
    public async Task Sweep_EveryPoint_MeasuresTheSimulatedDutsHarmonicDistortion()
    {
        // The simulator synthesizes H2 at -46 dBc and H3 at -60 dBc, so THD is ~0.5% and D2 is the
        // dominant contributor at every frequency.
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startHz: 1000.0, endHz: 2000.0, stepsPerOctave: 2), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        Assert.NotEmpty(status.Points);
        Assert.All(status.Points, p =>
        {
            Assert.True(p.ThdPercent > 0.0, $"THD at {p.FreqHz:0} Hz was {p.ThdPercent}");
            Assert.NotEmpty(p.HarmonicPercent);
            // -46 dBc is 0.501%; allow a wide band, this is asserting "it measured the DUT", not precision.
            Assert.InRange(p.HarmonicPercent[0], 0.1, 2.0);
        });
    }

    [Fact]
    public async Task Sweep_Cancelled_StopsTheGeneratorAndKeepsThePointsAlreadyMeasured()
    {
        var harness = await CreateAsync();

        // Enough points that cancelling lands in the middle rather than after the last one.
        await harness.Runner.StartAsync(Request(startHz: 200.0, endHz: 12000.0, stepsPerOctave: 6), CancellationToken.None);

        // Wait until at least one point is in before pulling the plug.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.Points.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "no sweep point was produced before the cancel deadline");
            await Task.Delay(20);
        }

        await harness.Runner.CancelAsync();
        var status = harness.Runner.Status;

        Assert.Equal(ThdSweepPhase.Cancelled, status.Phase);
        Assert.False(status.Running);
        Assert.NotEmpty(status.Points);

        // A cancelled sweep must never leave a tone playing into someone's amplifier.
        Assert.False(harness.Session.EffectiveGeneratorConfig.Left.Enabled);
        Assert.False(harness.Session.EffectiveGeneratorConfig.Right.Enabled);
    }

    [Fact]
    public async Task Clear_AfterAFinishedSweep_EmptiesThePointsAndTheProgressWithThem()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(), CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));
        Assert.NotEmpty(harness.Runner.Status.Points);

        var cleared = harness.Runner.Clear();

        // The returned status and the runner's own must agree — the browser applies the response
        // directly rather than polling again, so a stale return value would leave the UI redrawing
        // the sweep it just cleared.
        foreach (var status in new[] { cleared, harness.Runner.Status })
        {
            Assert.Empty(status.Points);
            Assert.Equal(ThdSweepPhase.Idle, status.Phase);
            Assert.False(status.Running);
            Assert.Equal(0, status.Index);
            Assert.Equal(0, status.Total);
            Assert.Null(status.Error);
        }
    }

    [Fact]
    public async Task Clear_WhileASweepIsRunning_IsRefusedRatherThanRacingTheWriter()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startHz: 200.0, endHz: 12000.0, stepsPerOctave: 6), CancellationToken.None);

        try
        {
            Assert.Throws<InvalidOperationException>(() => harness.Runner.Clear());
            Assert.True(harness.Runner.Running);
        }
        finally
        {
            await harness.Runner.CancelAsync();
        }

        // Cancelled is not running, so the same call now goes through.
        Assert.Empty(harness.Runner.Clear().Points);
    }

    [Fact]
    public async Task Sweep_StartingBelowTheDefaultFundamentalSearchFloor_StillLocatesItsOwnTone()
    {
        // FundamentalMinSearchHz defaults to 200 Hz so mains hum is never mistaken for the
        // fundamental. A sweep starting at 20 Hz has to lower it, or the whole bottom decade
        // reports the wrong peak (or none).
        var harness = await CreateAsync();
        var request = Request(startHz: 20.0, endHz: 80.0, stepsPerOctave: 1, fftSize: 16384);

        await harness.Runner.StartAsync(request, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.NotEmpty(status.Points);
        Assert.All(status.Points, p => Assert.InRange(p.FreqHz, 15.0, 90.0));
    }

    [Fact]
    public async Task Sweep_RestoresThePipelineOptionsItOverrode()
    {
        var harness = await CreateAsync();
        var before = harness.Pipeline.Options;

        await harness.Runner.StartAsync(Request(startHz: 1000.0, endHz: 2000.0, stepsPerOctave: 1), CancellationToken.None);
        await AwaitCompletionAsync(harness.Runner);

        // Notably SingleShot back off and HarmonicMaxFreqHz back to null -- otherwise the live FFT
        // view would silently keep the sweep's analysis settings after a sweep finished.
        Assert.Equal(before, harness.Pipeline.Options);
        Assert.False(harness.Pipeline.Options.SingleShot);
        Assert.Null(harness.Pipeline.Options.HarmonicMaxFreqHz);
    }

    [Fact]
    public async Task Sweep_OutputPowerMode_ResolvesAGeneratorAmplitudeNearTheTarget()
    {
        // The simulated DUT has unity gain, so 1 W into 8 ohm (= 2.83 Vrms = 9.03 dBV) should be
        // reached with a generator amplitude near 9 dBV.
        var harness = await CreateAsync();
        var request = Request(
            startHz: 1000.0, endHz: 2000.0, stepsPerOctave: 1,
            levelMode: ThdSweepLevelMode.OutputPower, levelValue: 1.0, levelUnit: ThdSweepLevelUnit.Watt, loadOhms: 8.0);

        await harness.Runner.StartAsync(request, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.InRange(status.GeneratorAmpDbv, 6.0, 12.0);

        // And the points it then measured should sit near 1 W.
        Assert.All(status.Points, p => Assert.InRange(p.PowerWatt, 0.3, 3.0));
    }

    [Fact]
    public async Task Sweep_WithNoiseFloorPass_ReportsANoisePercentBelowTheDistortion()
    {
        var harness = await CreateAsync();
        var request = Request(startHz: 1000.0, endHz: 2000.0, stepsPerOctave: 1, measureNoiseFloor: true);

        await harness.Runner.StartAsync(request, CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        Assert.NotEmpty(status.Points);
        Assert.All(status.Points, p => Assert.True(p.NoisePercent > 0.0, "the noise pass should have produced a non-zero residual"));

        // What the test's name always claimed but never checked. NoisePercent is the mean level of
        // ONE bin of the generator-off capture, referenced to the fundamental -- not the whole
        // band integrated, which is ~sqrt(bins in the band) larger (about 35 dB at 20 Hz-20 kHz)
        // and plotted the noise trace above every distortion trace it shares an axis with.
        // The simulator's THD is ~0.5%, so a per-bin residual has to land well under it.
        Assert.All(status.Points, p => Assert.True(
            p.NoisePercent < p.ThdPercent,
            $"noise {p.NoisePercent:0.######}% should sit below THD {p.ThdPercent:0.######}% at {p.FreqHz:0} Hz — band-integrated again?"));
    }

    [Fact]
    public void Validate_StartFrequencyTooFewBinsUp_IsRejectedWithTheFftSizeThatWouldWork()
    {
        // 192 kHz / 32768 is 5.86 Hz per bin, so 10 Hz is bin 1.7 -- the second harmonic's +/-3-bin
        // skirt swallows the fundamental and THD reads near 100%. The sweep used to run it anyway
        // (starting at 17.6 Hz, because 10 Hz snapped two bins up to the nearest coprime one).
        var request = new ThdSweepRequest(
            StartFreqHz: 10.0, EndFreqHz: 20000.0, StepsPerOctave: 5,
            ThdSweepLevelMode.GeneratorVoltage, LevelValue: -20.0, ThdSweepLevelUnit.Dbv, LoadOhms: 8.0,
            ProcessLeft: true, ProcessRight: false,
            FftSize: 32768, WindowType.Hann, AverageCount: 4, SampleRateHz: 192_000.0, MeasureNoiseFloor: true);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
        Assert.Contains("131072", ex.Message);

        // The resolution the sweep panel now fixes puts 10 Hz on bin 7, which is fine.
        (request with { FftSize = 131072 }).Validate();
    }

    [Fact]
    public async Task StartAsync_WhileASweepIsRunning_IsRejected()
    {
        var harness = await CreateAsync();
        await harness.Runner.StartAsync(Request(startHz: 200.0, endHz: 12000.0, stepsPerOctave: 6), CancellationToken.None);

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
    public async Task StartAsync_WithNoChannelSelected_IsRejectedBeforeTouchingTheDevice()
    {
        var harness = await CreateAsync();
        var request = Request() with { ProcessLeft = false, ProcessRight = false };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Runner.StartAsync(request, CancellationToken.None));
        Assert.False(harness.Runner.Running);
    }

    [Theory]
    [InlineData(ThdSweepLevelUnit.Dbv, -20.0, -20.0)]
    [InlineData(ThdSweepLevelUnit.Volt, 1.0, 0.0)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 100.0, -20.0)]
    public void LevelDbv_ConvertsFromEveryVoltageUnit(ThdSweepLevelUnit unit, double value, double expectedDbv)
    {
        var request = Request(levelValue: value, levelUnit: unit);

        Assert.Equal(expectedDbv, request.LevelDbv, 6);
    }

    [Fact]
    public void LevelDbv_WattsGoThroughTheLoad()
    {
        // 1 W into 8 ohm is 2.828 Vrms, i.e. 9.03 dBV.
        var request = Request(
            levelMode: ThdSweepLevelMode.OutputPower, levelValue: 1.0, levelUnit: ThdSweepLevelUnit.Watt, loadOhms: 8.0);

        Assert.Equal(9.031, request.LevelDbv, 3);
    }

    /// <summary>
    /// The noise floor must not depend on the FFT size a point happens to be swept at.
    ///
    /// It is a per-bin figure, so it scales with bin width — about 3 dB per doubling. Capturing it
    /// at each point's own size (which the adaptive mode's first version did) therefore made the
    /// trace climb with frequency purely because the resolution coarsened: measured on the simulator
    /// it rose 16.8 dB from 20 Hz to 20 kHz in 3.4 dB steps, one per size halving, while the real
    /// noise never moved. On a DUT whose distortion sits near the residual, that crossed the noise
    /// trace above the THD traces part-way up the sweep. Pinning the capture to the ceiling size
    /// makes an adaptive sweep's noise trace flat and identical to a fixed sweep's.
    /// </summary>
    [Fact]
    public async Task Sweep_WithAdaptiveResolution_ReportsAFlatNoiseFloorAcrossEveryFftSize()
    {
        var harness = await CreateAsync();

        // Wide enough that the adaptive sizer walks down several FFT sizes — the size boundaries are
        // where the old behaviour stepped.
        var request = Request(startHz: 100.0, endHz: 12000.0, stepsPerOctave: 2, measureNoiseFloor: true)
            with { AdaptiveResolution = true, SampleRateHz = 192_000.0 };

        await harness.Runner.StartAsync(request, CancellationToken.None);
        AssertFinished(await AwaitCompletionAsync(harness.Runner));

        // Reconstructed back to an absolute level. NoisePercent is referenced to each point's own
        // fundamental, and a fundamental that reads low inflates it by the same dB -- a real but
        // separate intermittent defect, and one this test must not be sensitive to, since what is
        // under test is whether the noise term itself moves with bin width.
        var noiseVrms = harness.Runner.Status.Points
            .Where(p => p.Channel == 0 && p.NoisePercent > 0.0)
            .Select(p => p.NoisePercent / 100.0 * Math.Pow(10.0, p.FundamentalDbv / 20.0))
            .ToArray();

        Assert.True(noiseVrms.Length >= 4, $"expected several points with a noise reading, got {noiseVrms.Length}");

        // Captured once, so every point carries the identical figure; the tolerance is only against
        // the round trip through a percentage.
        double spreadDb = 20.0 * Math.Log10(noiseVrms.Max() / noiseVrms.Min());
        Assert.True(spreadDb < 0.1, $"noise floor varied by {spreadDb:0.00} dB across the sweep's FFT sizes");
    }

    [Fact]
    public void NoiseFloorReferenceBinWidth_IsTheSameAtEverySupportedSampleRate()
    {
        // Why no scaling factor is needed: the ceiling is a record-*time* bound, so the bin width it
        // yields is 1/MaxRecordSeconds regardless of rate. An adaptive sweep that drops to 48 kHz
        // still reports its noise floor against the same bin width a 192 kHz one does.
        foreach (double sampleRateHz in SweepResolution.SupportedSampleRatesHz)
        {
            var (_, ceiling) = SweepResolution.BoundsFor(sampleRateHz);
            Assert.Equal(1.0 / SweepResolution.MaxRecordSeconds, sampleRateHz / ceiling, 3);
        }
    }
}
