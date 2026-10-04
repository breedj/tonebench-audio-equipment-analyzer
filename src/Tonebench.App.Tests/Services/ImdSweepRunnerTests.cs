using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

public class ImdSweepRunnerTests
{
    /// <summary>
    /// 4096 points at 48 kHz: 11.7 Hz bins, so the tones below are chosen at whole multiples of
    /// that (well over the 8-bin spacing the analyzer demands) and a point completes in a fraction
    /// of a second. The maths under test does not depend on the size.
    /// </summary>
    private const int FastFftSize = 4096;

    private const double SampleRateHz = 48_000.0;

    /// <summary>A 4:1 pair 129 Hz + 7043 Hz — bins 11 and 601, odd so the coherent snap leaves them alone — i.e. SMPTE scaled onto the fast grid.</summary>
    private const double LowHz = 11 * SampleRateHz / FastFftSize;

    private const double HighHz = 601 * SampleRateHz / FastFftSize;

    private static ImdSweepRequest Request(
        double startVolt = 0.01,
        double endVolt = 1.0,
        int stepsPerOctave = 1,
        IntermodulationMethod method = IntermodulationMethod.Smpte,
        double ratio = 4.0,
        int maxOrder = 5,
        bool measureNoiseFloor = false,
        double lowHz = LowHz,
        double highHz = HighHz,
        int fftSize = FastFftSize) =>
        new(
            IntermodulationPreset.Custom, lowHz, highHz, ratio, method, maxOrder,
            startVolt, endVolt, stepsPerOctave, LoadOhms: 8.0,
            ProcessLeft: true, ProcessRight: false, AverageCount: 1, MeasureNoiseFloor: measureNoiseFloor,
            FftSize: fftSize, SampleRateHz: SampleRateHz);

    private sealed record Harness(ImdSweepRunner Runner, DeviceSessionService Session, SpectrumPipeline Pipeline);

    private static Task<Harness> CreateAsync(SimulatedDutModel dut = SimulatedDutModel.StraightWire)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with { Dut = dut };
        return CreateAsync(device);
    }

    private static async Task<Harness> CreateAsync(IAnalyzerDevice device)
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = FastFftSize, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        return new Harness(new ImdSweepRunner(session, pipeline, hub, NullLogger<ImdSweepRunner>.Instance), session, pipeline);
    }

    private static async Task<ImdSweepStatus> AwaitCompletionAsync(ImdSweepRunner runner, int timeoutSeconds = 60)
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

    private static void AssertFinished(ImdSweepStatus status) =>
        Assert.True(status.Phase == ThdSweepPhase.Finished, $"sweep ended in {status.Phase}: {status.Error ?? "(no message)"}");

    [Fact]
    public async Task Sweep_StepsTheSineEquivalentLevel_AndPlaysTwoTonesAtThePresetRatio()
    {
        var harness = await CreateAsync();

        var started = await harness.Runner.StartAsync(Request(startVolt: 0.01, endVolt: 0.08, stepsPerOctave: 1), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(4, started.PointCount);
        Assert.Equal([0.01, 0.02, 0.04, 0.08], started.LevelsVolt.Select(v => Math.Round(v, 6)));
        Assert.Equal(4, status.Points.Count);

        // Ascending in level, one point per step on the one enabled channel, the pair at 4:1.
        Assert.Equal(started.LevelsVolt.Select(v => Math.Round(20.0 * Math.Log10(v), 4)), status.Points.Select(p => Math.Round(p.GeneratorSineEquivalentDbv, 4)));
        Assert.All(status.Points, p => Assert.Equal(0, p.Channel));
        Assert.All(status.Points, p => Assert.Equal(20.0 * Math.Log10(4.0), p.ToneLowDbv - p.ToneHighDbv, 1));
        Assert.All(status.Points, p => Assert.Equal(LowHz, p.ToneLowHz, 3));
        Assert.All(status.Points, p => Assert.Equal(HighHz, p.ToneHighHz, 3));

        // A straight wire: the output's sine-equivalent level is the generator's, i.e. unity gain.
        Assert.All(status.Points, p => Assert.Equal(0.0, p.GainDb, 1));
        Assert.All(status.Points, p => Assert.Equal(Math.Pow(10.0, p.SineEquivalentDbv / 20.0) is var v ? v * v / 8.0 : 0.0, p.PowerWatt, 6));
        Assert.Equal(IntermodulationMethod.Smpte, status.Method);
        Assert.Equal(5, status.MaxOrder);
    }

    [Fact]
    public async Task Sweep_AgainstACrossoverDut_ReadsNonZeroImdThatChangesWithLevel()
    {
        var harness = await CreateAsync(SimulatedDutModel.ClassAbCrossover);

        await harness.Runner.StartAsync(Request(startVolt: 0.01, endVolt: 1.0, stepsPerOctave: 1), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(status.Points, p => Assert.True(p.ImdPercent > 0.0));
        Assert.All(status.Points, p => Assert.NotEmpty(p.OrderPercent));

        double first = status.Points[0].ImdPercent;
        double last = status.Points[^1].ImdPercent;
        Assert.True(Math.Abs(20.0 * Math.Log10(last / first)) > 6.0, $"expected IMD to move with level, got {first} % .. {last} %");
    }

    [Fact]
    public async Task Sweep_AgainstTheStraightWire_ReadsImdBelowTheNoise()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startVolt: 0.1, endVolt: 0.4, stepsPerOctave: 1), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(status.Points, p => Assert.True(20.0 * Math.Log10(p.ImdPercent / 100.0) < -80.0, $"IMD {p.ImdPercent} % at {p.GeneratorSineEquivalentDbv:0.#} dBV"));
    }

    [Fact]
    public async Task Sweep_EveryPoint_CarriesTheFftAndScopePreview()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startVolt: 0.1, endVolt: 0.2), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(status.Points, p =>
        {
            Assert.NotNull(p.PreviewFft);
            Assert.NotNull(p.PreviewScope);
            Assert.NotEmpty(p.PreviewFft.X);
            Assert.NotEmpty(p.PreviewScope.X);
        });
    }

    [Fact]
    public async Task Sweep_WithTheNoiseFloorPass_ReportsANoisePercentPerPoint()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startVolt: 0.1, endVolt: 0.2, measureNoiseFloor: true), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.All(status.Points, p => Assert.True(p.NoisePercent > 0.0 && p.NoisePercent < 1.0, $"noise {p.NoisePercent} %"));
    }

    [Fact]
    public async Task Sweep_Cancelled_SilencesTheGeneratorAndKeepsThePoints()
    {
        var harness = await CreateAsync();

        await harness.Runner.StartAsync(Request(startVolt: 0.001, endVolt: 1.0, stepsPerOctave: 4), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Runner.Status.Points.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        await harness.Runner.CancelAsync();
        var status = harness.Runner.Status;

        Assert.Equal(ThdSweepPhase.Cancelled, status.Phase);
        Assert.False(status.Running);
        Assert.True(status.Points.Count >= 2);
        Assert.False(harness.Session.EffectiveGeneratorConfig.Left.Enabled);
        Assert.False(harness.Session.Acquiring);
    }

    [Fact]
    public async Task Sweep_RestoresThePipelineOptionsIncludingTheIntermodulationSpec()
    {
        var harness = await CreateAsync();
        var before = harness.Pipeline.Options with
        {
            Intermodulation = new IntermodulationSpec(60.0, 7000.0, IntermodulationMethod.Ccif, 3, 12_000.0),
        };
        harness.Pipeline.Options = before;

        await harness.Runner.StartAsync(Request(startVolt: 0.1, endVolt: 0.2), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.Equal(before, harness.Pipeline.Options);
    }

    /// <summary>
    /// Records what a sweep does to the input range. Wraps the simulator through
    /// <see cref="FakeAnalyzerDevice"/> for the reason <c>ThdSweepRunnerTests</c> does: the bare
    /// simulator reports no ranger of its own, which the sweep reads as "the user is driving the
    /// range by hand" and leaves it alone.
    /// </summary>
    private sealed class RangeRecordingDevice : FakeAnalyzerDevice
    {
        public List<double> InputRangeWrites { get; } = [];

        public List<bool> StepDownWrites { get; } = [];

        public override bool AutoRangeStepDownEnabled
        {
            get => base.AutoRangeStepDownEnabled;
            set
            {
                StepDownWrites.Add(value);
                base.AutoRangeStepDownEnabled = value;
            }
        }

        public override Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            InputRangeWrites.Add(dbv);
            return base.SetInputRangeAsync(dbv, ct);
        }
    }

    /// <summary>
    /// Feed-forward ranging, as the THD amplitude sweep does it: each point's range comes from the
    /// previous point's measured peak plus the generator step, and only ever moves up within one
    /// sweep. The pair's peak is the sine-equivalent level, so the prediction carries over unchanged.
    /// </summary>
    [Fact]
    public async Task Sweep_PredictsTheInputRangePerPointAndNeverRangesBackDown()
    {
        var device = new RangeRecordingDevice();
        var harness = await CreateAsync(device);
        device.InputRangeWrites.Clear();

        await harness.Runner.StartAsync(Request(startVolt: 0.01, endVolt: 1.0, stepsPerOctave: 1), CancellationToken.None);
        var status = await AwaitCompletionAsync(harness.Runner);

        AssertFinished(status);
        Assert.NotEmpty(device.InputRangeWrites);
        for (int i = 1; i < device.InputRangeWrites.Count; i++)
        {
            Assert.True(
                device.InputRangeWrites[i] > device.InputRangeWrites[i - 1],
                $"input range went {device.InputRangeWrites[i - 1]} -> {device.InputRangeWrites[i]} dBV: an amplitude sweep must never range back down");
        }

        // A 1 V sine-equivalent pair peaks like a 0 dBV tone, which needs the 6 dBV range with the
        // 5 dB of headroom added.
        Assert.True(harness.Session.InputRangeDbv >= 6.0, $"ended on the {harness.Session.InputRangeDbv} dBV range");
        Assert.Equal([false, true], device.StepDownWrites);
        Assert.True(device.AutoRangeStepDownEnabled);
    }

    [Fact]
    public async Task Running_IsTrueForExactlyTheBusyPhases()
    {
        var harness = await CreateAsync();
        Assert.False(harness.Runner.Running);

        await harness.Runner.StartAsync(Request(startVolt: 0.1, endVolt: 0.2), CancellationToken.None);
        Assert.True(harness.Runner.Running);
        Assert.Contains(harness.Runner.Status.Phase, new[] { ThdSweepPhase.DeterminingLevel, ThdSweepPhase.NoiseFloor, ThdSweepPhase.Sweeping });

        var status = await AwaitCompletionAsync(harness.Runner);
        AssertFinished(status);
        Assert.False(harness.Runner.Running);

        harness.Runner.Clear();
        Assert.Equal(ThdSweepPhase.Idle, harness.Runner.Status.Phase);
        Assert.Empty(harness.Runner.Status.Points);
    }

    [Fact]
    public async Task StartAsync_WhileRunning_IsRefused()
    {
        var harness = await CreateAsync();
        await harness.Runner.StartAsync(Request(startVolt: 0.001, endVolt: 1.0, stepsPerOctave: 4), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Runner.StartAsync(Request(), CancellationToken.None));

        await harness.Runner.CancelAsync();
    }

    [Fact]
    public void Validate_RejectsTonesCloserThanEightBins()
    {
        // 4096 at 48 kHz: 11.7 Hz bins, 8 bins = 93.75 Hz. SMPTE's own 60 Hz is under that here.
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(lowHz: 60.0, highHz: 7000.0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(lowHz: 19_000.0, highHz: 19_050.0).Validate());
        // And at the real preset both pairs pass comfortably.
        new ImdSweepRequest(IntermodulationPreset.Smpte, 60.0, 7000.0, 4.0, IntermodulationMethod.Smpte, 5, 0.001, 1.0, 5, 8.0, true, false, 1, true).Validate();
        new ImdSweepRequest(IntermodulationPreset.CcifItu, 19_000.0, 20_000.0, 1.0, IntermodulationMethod.Ccif, 3, 0.001, 1.0, 5, 8.0, true, false, 1, true).Validate();
    }

    [Fact]
    public void Validate_RejectsAnEndLevelAboveTheOutputCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(startVolt: 0.1, endVolt: 8.0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(startVolt: 0.5, endVolt: 0.25).Validate());
    }

    [Fact]
    public void Validate_RejectsAMaxOrderBelowTwo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(maxOrder: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(maxOrder: 13).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(ratio: 0.0).Validate());
    }
}
