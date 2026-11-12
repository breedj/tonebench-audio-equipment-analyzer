using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.SoundCard;
using Tonebench.Device.Split;

namespace Tonebench.Device.Tests.Split;

/// <summary>
/// The composite analyzer — stage 2 of <c>docs/separate in out interfaces plan.md</c>: one device
/// supplies the DAC, another the ADC.
///
/// <para>What these pin is <b>routing</b>, which is why both halves are fakes rather than
/// simulators: a <c>SimulatedDevice</c> answers plausibly for either side, so a member wired to the
/// wrong half would still look right. Every assertion here is "this call reached that half, and the
/// other one never saw it".</para>
///
/// <para>Deliberately not asserted: that two clocks drift. That is a property of two crystals, not
/// of this code, and the correction for it lives in the DSP and the App layers.</para>
/// </summary>
public class SplitAnalyzerDeviceTests
{
    private static SplitAnalyzerDevice Build(
        FakeAnalyzerDevice input, FakeAnalyzerDevice output, double generatorRateHz = 0.0) =>
        new(input, output,
            new SplitDeviceSettings
            {
                GeneratorSampleRateHz = generatorRateHz,
                InputLabel = "the analyzer",
                OutputLabel = "the DAC",
            },
            NullLogger<SplitAnalyzerDevice>.Instance);

    private static AcquisitionConfig Config(double rateHz = 48_000.0) => new(rateHz, 2, 1024);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new Xunit.Sdk.XunitException($"timed out waiting for {what}");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    // ---- routing ----

    [Fact]
    public async Task EveryOutputSideCall_ReachesTheGeneratorHalfAndNotTheOther()
    {
        var input = new FakeAnalyzerDevice("in");
        var output = new FakeAnalyzerDevice("out") { MaxGeneratorSampleRateHz = 192_000.0 };
        var split = Build(input, output);

        await split.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
        await split.SetOutputRangeAsync(18.0, CancellationToken.None);

        Assert.Equal([18.0], output.OutputRangeWrites);
        Assert.Empty(input.OutputRangeWrites);
        Assert.Equal(192_000.0, split.MaxGeneratorSampleRateHz);

        // The generator was configured on the output half -- the input half's only generator write
        // is the silencing one at connect, which has not happened here.
        Assert.Single(output.Generators);
        Assert.Empty(input.Generators);
    }

    [Fact]
    public async Task EveryInputSideCall_ReachesTheCapturingHalfAndNotTheOther()
    {
        var input = new FakeAnalyzerDevice("in")
        {
            InputRangeDbv = 12.0,
            HasInputAutoRanging = true,
            DroppedRxChunks = 3,
            DroppedBlocks = 7,
        };
        var output = new FakeAnalyzerDevice("out") { HasInputAutoRanging = false, DroppedRxChunks = 99 };
        var split = Build(input, output);

        await split.SetInputRangeAsync(6.0, CancellationToken.None);
        split.AutoRangeEnabled = true;
        split.AutoRangeStepDownEnabled = true;

        Assert.Equal([6.0], input.InputRangeWrites);
        Assert.Empty(output.InputRangeWrites);
        Assert.Equal(12.0, split.InputRangeDbv);
        Assert.Equal(3, split.DroppedRxChunks);
        Assert.Equal(7, split.DroppedBlocks);
        Assert.True(input.AutoRangeEnabled);
        Assert.False(output.AutoRangeEnabled);

        // The one that decides whether the session builds a ranger of its own.
        Assert.True(split.HasInputAutoRanging);
        Assert.False(Build(output, input).HasInputAutoRanging);
    }

    [Fact]
    public void TheStepDownDelay_IsWrittenToBothHalves()
    {
        var input = new FakeAnalyzerDevice("in");
        var output = new FakeAnalyzerDevice("out");
        var split = Build(input, output);

        split.AutoRangeStepDownDelayMs = 750.0;

        // The factory pushes this into every device it builds, "so a device that grows a ranger
        // cannot forget it". A half that is handed it anyway keeps that true.
        Assert.Equal(750.0, input.AutoRangeStepDownDelayMs);
        Assert.Equal(750.0, output.AutoRangeStepDownDelayMs);
        Assert.Equal(750.0, split.AutoRangeStepDownDelayMs);
    }

    [Fact]
    public void TheStimulusCorner_IsTheHigherOfTheTwo()
    {
        // A card's output coupling limits the stimulus; an AC-coupled input limits the measurement
        // by the same mechanism. On a mixed bench the honest answer is the more restrictive.
        var input = new FakeAnalyzerDevice("in") { MinStimulusFrequencyHz = 20.0 };
        var output = new FakeAnalyzerDevice("out") { MinStimulusFrequencyHz = 0.0 };

        Assert.Equal(20.0, Build(input, output).MinStimulusFrequencyHz);
        Assert.Equal(20.0, Build(output, input).MinStimulusFrequencyHz);
    }

    [Fact]
    public void Telemetry_ComesFromTheMeasuringSide_AndFallsBackToTheOther()
    {
        var reading = new DeviceTelemetry(5.0, 0.5, 30.0);
        var withSensors = new FakeAnalyzerDevice("qa403") { Telemetry = reading };
        var without = new FakeAnalyzerDevice("card");

        Assert.Same(reading, Build(withSensors, without).Telemetry);
        Assert.Same(reading, Build(without, withSensors).Telemetry);
        Assert.Null(Build(without, new FakeAnalyzerDevice("card2")).Telemetry);
    }

    // ---- capabilities ----

    [Fact]
    public void Capabilities_TakeTheRatesAndInputRangesFromTheCapturingHalf_AndTheOutputRangesFromTheOther()
    {
        var input = new FakeAnalyzerDevice("in")
        {
            Capabilities = new DeviceCapabilities([48_000, 96_000], [0, 6, 12], [-99], MaxChannelCount: 2),
        };
        var output = new FakeAnalyzerDevice("out")
        {
            Capabilities = new DeviceCapabilities([48_000], [-99], [-12, -2, 8, 18], MaxChannelCount: 4),
        };

        DeviceCapabilities merged = Build(input, output).Capabilities;

        Assert.Equal([48_000.0, 96_000.0], merged.SampleRatesHz);
        Assert.Equal([0.0, 6.0, 12.0], merged.InputRangesDbv);
        Assert.Equal([-12.0, -2.0, 8.0, 18.0], merged.OutputRangesDbv);
        Assert.Equal(2, merged.MaxChannelCount);
    }

    [Fact]
    public void Capabilities_DoNotIntersectTheTwoRateLadders()
    {
        // The whole benefit of independent rates: a QA403 capturing at 384 kHz while a 192 kHz DAC
        // plays is the one configuration that makes that input mode a real measurement, and
        // intersecting the ladders would delete it.
        var input = new FakeAnalyzerDevice("qa403") { Capabilities = DeviceCapabilities.Qa403Experimental };
        var output = new FakeAnalyzerDevice("card")
        {
            Capabilities = new DeviceCapabilities([48_000, 96_000, 192_000], [0], [0], MaxChannelCount: 2),
        };

        DeviceCapabilities merged = Build(input, output).Capabilities;

        Assert.Contains(384_000.0, merged.SampleRatesHz);
        Assert.DoesNotContain(384_000.0, merged.OutputSampleRatesHz);
    }

    [Fact]
    public void TheGeneratorsRateLadder_LeavesOutWhatThatHalfMayNotGenerateAt()
    {
        // A QA403 used as the *output* must never be offered 384 kHz: its DAC emits garbage there
        // with enough near-DC content to cycle the output protection relay until the device needs a
        // power cycle, which is why MaxGeneratorSampleRateHz is a hardware rule. Offering a rate and
        // then refusing it is the worse of the two answers.
        var input = new FakeAnalyzerDevice("card")
        {
            Capabilities = new DeviceCapabilities([48_000, 96_000, 192_000], [0], [0], MaxChannelCount: 2),
        };
        var output = new FakeAnalyzerDevice("qa403")
        {
            Capabilities = DeviceCapabilities.Qa403Experimental,
            MaxGeneratorSampleRateHz = 192_000.0,
        };

        Assert.Equal(
            [48_000.0, 96_000.0, 192_000.0],
            Build(input, output).Capabilities.OutputSampleRatesHz);
    }

    // ---- the two rates ----

    [Fact]
    public void WithNoGeneratorRateOfItsOwn_ASplitBehavesLikeOneDevice()
    {
        var split = Build(new FakeAnalyzerDevice("in"), new FakeAnalyzerDevice("out"));

        Assert.Null(split.GeneratorSampleRateHz);
        Assert.Equal(Config(192_000.0), split.OutputConfigFor(Config(192_000.0)));
    }

    [Fact]
    public void TheGeneratorHalfIsStreamedAtItsOwnRate()
    {
        var split = Build(new FakeAnalyzerDevice("in"), new FakeAnalyzerDevice("out"), generatorRateHz: 48_000.0);

        Assert.Equal(48_000.0, split.GeneratorSampleRateHz);

        AcquisitionConfig output = split.OutputConfigFor(Config(192_000.0));
        Assert.Equal(48_000.0, output.SampleRateHz);

        // Everything but the rate is the analysis config's, so block sizing and channel count stay
        // one decision.
        Assert.Equal(2, output.ChannelCount);
        Assert.Equal(1024, output.BlockFrameCount);
    }

    [Fact]
    public void TheStimulusLatency_ComesFromTheGeneratorHalf_ConvertedByTheRateRatio()
    {
        var input = new FakeAnalyzerDevice("in") { StimulusLatency = 1 };
        var output = new FakeAnalyzerDevice("out") { StimulusLatency = 3200 };
        var split = Build(input, output, generatorRateHz: 48_000.0);

        // 3200 frames of committed audio on a 48 kHz DAC is 12800 frames of a 192 kHz capture, and
        // it is the capture's pipeline the discard is applied to.
        Assert.Equal(12_800, split.StimulusLatencyFrames(192_000.0));

        // Asked at the generator's own rate, not the analysis rate -- a device reports its queue in
        // its own frames.
        Assert.Equal([48_000.0], output.LatencyAskedAt);
    }

    [Fact]
    public void TheSettleReported_IsTheGreaterOfTheTwo_InCaptureFrames()
    {
        var input = new FakeAnalyzerDevice("in") { SettleFrames = 1000 };
        var output = new FakeAnalyzerDevice("out") { SettleFrames = 500 };

        // 500 generator frames at 48 kHz is 2000 capture frames at 192 kHz, which outweighs the
        // capture half's own 1000.
        Assert.Equal(2000, Build(input, output, generatorRateHz: 48_000.0).StreamStartSettleFrames(192_000.0));

        // And the other way round the capture half's own figure wins.
        Assert.Equal(1000, Build(input, new FakeAnalyzerDevice("out")).StreamStartSettleFrames(48_000.0));
    }

    // ---- connect ----

    [Fact]
    public async Task ASimulatedCapturingHalf_IsHandedTheStimulusToo_BecauseThatIsTheCable()
    {
        // Two simulators are two devices with nothing wired between them: the one that plays plays
        // into nothing, and the one that captures hears its own silenced generator. Measured the
        // first time it was run -- a bare noise floor and THD in the hundreds of per cent. A
        // simulator models what arrives at its input by synthesizing its own generator config, so
        // mirroring the stimulus onto it *is* the wire, and it has no DAC to drive anything with.
        var input = new FakeAnalyzerDevice("sim") { IsSimulated = true };
        var output = new FakeAnalyzerDevice("card");
        var split = Build(input, output);
        await split.ConnectAsync(CancellationToken.None);

        // Not silenced at connect, unlike a real one.
        Assert.Empty(input.Generators);

        await split.ConfigureGeneratorAsync(GeneratorConfig.Off with { SnapToBin = false }, CancellationToken.None);

        Assert.Single(output.Generators);
        Assert.Single(input.Generators);
    }

    [Fact]
    public async Task ARealCapturingHalf_IsNeverHandedTheStimulus()
    {
        var input = new FakeAnalyzerDevice("qa403");
        var output = new FakeAnalyzerDevice("card");
        var split = Build(input, output);
        await split.ConnectAsync(CancellationToken.None);

        await split.ConfigureGeneratorAsync(GeneratorConfig.Off with { SnapToBin = false }, CancellationToken.None);

        // One write, and it is the silencing one from connect -- the stimulus went to the generating
        // half alone.
        GeneratorConfig only = Assert.Single(input.Generators);
        Assert.False(only.Left.Enabled);
        Assert.True(only.SnapToBin);
    }

    [Fact]
    public async Task Connect_SilencesTheCapturingHalvesOwnGenerator()
    {
        var input = new FakeAnalyzerDevice("qa403");
        var output = new FakeAnalyzerDevice("card");
        var split = Build(input, output);

        await split.ConnectAsync(CancellationToken.None);

        // A QA403 used as the input still has a DAC, and a tone left configured by an earlier
        // session would drive whatever is wired to its output with nothing on screen naming it.
        GeneratorConfig silenced = Assert.Single(input.Generators);
        Assert.False(silenced.Left.Enabled);
        Assert.False(silenced.Right.Enabled);

        // The generator half is left alone: its stimulus is the session's business.
        Assert.Empty(output.Generators);
    }

    [Fact]
    public async Task TheMergedIdentity_NamesWhatPlaysThenWhatMeasuresIt()
    {
        var split = Build(new FakeAnalyzerDevice("in"), new FakeAnalyzerDevice("out"));

        DeviceInfo info = await split.ConnectAsync(CancellationToken.None);

        Assert.Equal("out product → in product", info.ProductName);
        Assert.Contains("out-serial", info.SerialNumber);
        Assert.Contains("in-serial", info.SerialNumber);
        Assert.NotNull(split.InputInfo);
        Assert.NotNull(split.OutputInfo);
    }

    [Fact]
    public async Task AHalfSimulatedBench_IsNotASimulation()
    {
        // IsSimulated is what the status pill reads, and real hardware is in the path.
        var real = new FakeAnalyzerDevice("qa403");
        var sim = new FakeAnalyzerDevice("sim") { IsSimulated = true };

        DeviceInfo info = await Build(real, sim).ConnectAsync(CancellationToken.None);
        Assert.False(info.IsSimulated);

        DeviceInfo both = await Build(
                new FakeAnalyzerDevice("sim") { IsSimulated = true },
                new FakeAnalyzerDevice("sim2") { IsSimulated = true })
            .ConnectAsync(CancellationToken.None);
        Assert.True(both.IsSimulated);
    }

    [Fact]
    public async Task Connect_NamesWhichHalfDidNotOpen()
    {
        var generatorGone = Build(
            new FakeAnalyzerDevice("in"),
            new FakeAnalyzerDevice("out") { ConnectFault = new InvalidOperationException("no card") });

        SplitSideException ex = await Assert.ThrowsAsync<SplitSideException>(
            () => generatorGone.ConnectAsync(CancellationToken.None));
        Assert.Equal("generator side", ex.Side);
        Assert.Contains("the DAC", ex.Message);
        Assert.Contains("no card", ex.Message);

        var captureGone = Build(
            new FakeAnalyzerDevice("in") { ConnectFault = new InvalidOperationException("no analyzer") },
            new FakeAnalyzerDevice("out"));

        SplitSideException second = await Assert.ThrowsAsync<SplitSideException>(
            () => captureGone.ConnectAsync(CancellationToken.None));
        Assert.Equal("capture side", second.Side);
        Assert.Contains("the analyzer", second.Message);
    }

    [Fact]
    public async Task Connect_WhenTheSecondHalfFails_LetsGoOfTheFirst()
    {
        var input = new FakeAnalyzerDevice("in") { ConnectFault = new InvalidOperationException("no analyzer") };
        var output = new FakeAnalyzerDevice("out");
        var split = Build(input, output);

        await Assert.ThrowsAsync<SplitSideException>(() => split.ConnectAsync(CancellationToken.None));

        // Left claimed, a retry finds it taken -- which on an exclusive-mode card or a libusb
        // interface reads as hardware that needs a power cycle.
        Assert.Equal(1, output.Stops);
    }

    [Fact]
    public async Task Ping_AsksBothHalves_AndIsFalseWhenEitherIsGone()
    {
        var input = new FakeAnalyzerDevice("in");
        var output = new FakeAnalyzerDevice("out");
        var split = Build(input, output);

        Assert.True(await split.PingAsync(CancellationToken.None));
        Assert.Equal(1, input.Pings);
        Assert.Equal(1, output.Pings);

        output.PingAnswer = false;
        Assert.False(await split.PingAsync(CancellationToken.None));

        // Both are still asked, because the ping is also where each half's telemetry is refreshed.
        Assert.Equal(2, input.Pings);
        Assert.Equal(2, output.Pings);
    }

    // ---- streaming ----

    [Fact]
    public async Task TheStream_YieldsOnlyTheCapturingHalvesBlocks()
    {
        var input = new FakeAnalyzerDevice("in") { BlocksToYield = 3, HoldOpen = false };
        var output = new FakeAnalyzerDevice("out") { BlocksToYield = 0 };
        var split = Build(input, output, generatorRateHz: 48_000.0);

        var yielded = new List<long>();
        await foreach (PcmBlock block in split.StreamAsync(Config(192_000.0), CancellationToken.None))
        {
            yielded.Add(block.SequenceNumber);
            block.Dispose();
        }

        Assert.Equal([0L, 1L, 2L], yielded);

        // Each half was streamed at its own rate.
        Assert.Equal(192_000.0, input.StreamedWith!.Value.SampleRateHz);
        Assert.Equal(48_000.0, output.StreamedWith!.Value.SampleRateHz);
    }

    [Fact]
    public async Task TheStream_DrainsAndDisposesTheGeneratorHalvesBlocks()
    {
        // A card running output-only yields no blocks at all, but a QA403 or the simulator used as
        // the generator yields them -- and for a QA403 that continuous reader is the DAC's clock,
        // not bookkeeping. They carry pooled buffers, so dropping them instead of disposing them
        // leaks the pool quietly.
        var input = new FakeAnalyzerDevice("in") { BlocksToYield = 2 };
        var output = new FakeAnalyzerDevice("out") { BlocksToYield = 4 };
        var split = Build(input, output);

        var seen = 0;
        await foreach (PcmBlock block in split.StreamAsync(Config(), CancellationToken.None))
        {
            block.Dispose();
            if (++seen < 2)
                continue;

            await WaitUntil(() => output.Produced.Count == 4, "the generator half to be drained");
            break;
        }

        Assert.Equal(4, output.Produced.Count);
        Assert.All(output.Produced, block => Assert.Throws<ObjectDisposedException>(() => block.Samples.Length));
    }

    [Fact]
    public async Task TheStream_WithholdsTheGeneratorHalvesSettleFromTheCapture()
    {
        // The hazard no previous bench has had: the card is the *generator*, its re-clock transient
        // is on the wire, the other device is capturing it, and a device cannot withhold frames it
        // does not produce. So the composite withholds them from the capture instead.
        var input = new FakeAnalyzerDevice("in") { BlocksToYield = 5, BlockFrames = 1024, HoldOpen = false };
        var output = new FakeAnalyzerDevice("out") { SettleFrames = 2048 };
        var split = Build(input, output);

        var yielded = new List<long>();
        await foreach (PcmBlock block in split.StreamAsync(Config(), CancellationToken.None))
        {
            yielded.Add(block.SequenceNumber);
            block.Dispose();
        }

        // Two blocks of 1024 frames withheld, whole blocks at a time.
        Assert.Equal([2L, 3L, 4L], yielded);
        Assert.All(input.Produced.Take(2), block => Assert.Throws<ObjectDisposedException>(() => block.Samples.Length));
    }

    [Fact]
    public async Task TheWithheldSettle_IsConvertedFromTheGeneratorsOwnFrames()
    {
        var input = new FakeAnalyzerDevice("in") { BlocksToYield = 5, BlockFrames = 1024, HoldOpen = false };
        var output = new FakeAnalyzerDevice("out") { SettleFrames = 512 };
        var split = Build(input, output, generatorRateHz: 48_000.0);

        var yielded = new List<long>();
        await foreach (PcmBlock block in split.StreamAsync(Config(192_000.0), CancellationToken.None))
        {
            yielded.Add(block.SequenceNumber);
            block.Dispose();
        }

        // 512 frames at 48 kHz is 2048 at 192 kHz: two blocks, not half of one.
        Assert.Equal([2L, 3L, 4L], yielded);
    }

    [Fact]
    public async Task TheStream_WhenTheGeneratorHalfFaults_EndsCleanlyAndReportsTheLoss()
    {
        var input = new FakeAnalyzerDevice("in") { BlocksToYield = 3, StartGate = new TaskCompletionSource() };
        var output = new FakeAnalyzerDevice("out") { StreamFault = new InvalidOperationException("card gone") };
        var split = Build(input, output);

        var lost = new TaskCompletionSource();
        split.DeviceLost += (_, _) => lost.TrySetResult();

        var yielded = 0;

        // The contract: a device loss is an event, and the enumeration simply stops. It must never
        // be thrown into the caller's await foreach.
        async Task Consume()
        {
            await foreach (PcmBlock block in split.StreamAsync(Config(), CancellationToken.None))
            {
                yielded++;
                block.Dispose();
            }
        }

        Task consumer = Consume();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.StartGate!.SetResult();

        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, yielded);
    }

    [Fact]
    public async Task LeavingTheLoopEarly_StopsTheGeneratorHalfToo()
    {
        var input = new FakeAnalyzerDevice("in") { BlocksToYield = 10 };
        var output = new FakeAnalyzerDevice("out") { BlocksToYield = 0 };
        var split = Build(input, output);

        await foreach (PcmBlock block in split.StreamAsync(Config(), CancellationToken.None))
        {
            block.Dispose();
            break;
        }

        // In a finally, which is the rule the whole type hangs on: consumers routinely break out of
        // the loop, and disposal runs finally handlers and nothing else. A stream left open holds
        // the card -- exclusively, under ASIO.
        await WaitUntil(() => output.StreamedWith is not null, "the generator half to have streamed");
    }

    // ---- lifecycle ----

    [Fact]
    public async Task Stop_StopsBothHalves_AndDispose_DisposesThem()
    {
        var input = new FakeAnalyzerDevice("in");
        var output = new FakeAnalyzerDevice("out");
        var split = Build(input, output);

        await split.StopAsync();
        Assert.Equal(1, input.Stops);
        Assert.Equal(1, output.Stops);

        split.Dispose();
        Assert.True(input.Disposed);
        Assert.True(output.Disposed);
    }

    [Fact]
    public async Task ALossFromEitherHalf_IsRaisedOnce()
    {
        var input = new FakeAnalyzerDevice("in");
        var output = new FakeAnalyzerDevice("out");
        var split = Build(input, output);
        await split.ConnectAsync(CancellationToken.None);

        var raised = 0;
        split.DeviceLost += (_, _) => raised++;

        input.RaiseDeviceLost();
        output.RaiseDeviceLost();
        input.RaiseDeviceLost();

        // The session tears the whole bench down on the first one, so the rest are noise.
        Assert.Equal(1, raised);

        // A reconnect arms it again.
        await split.ConnectAsync(CancellationToken.None);
        output.RaiseDeviceLost();
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ASoundCardOnBothSides_IsRefusedRatherThanHung()
    {
        var settings = new SoundCardSettings
        {
            InputHostApi = "WASAPI", InputDeviceName = "In 1-2 (A)",
            OutputHostApi = "WASAPI", OutputDeviceName = "Out 1-2 (B)",
        };

        SoundCardDevice Card() => new(
            settings, SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance, new Tonebench.Device.Tests.SoundCard.FakePortAudio());

        // Everything PortAudio does runs on one thread, and SoundCardDevice posts its whole transfer
        // loop to it for the stream's life -- so the second card's pump would queue behind the first
        // and never run. No exception, no log line, just an acquisition that never produces a block.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new SplitAnalyzerDevice(
                Card(), Card(), SplitDeviceSettings.Default, NullLogger<SplitAnalyzerDevice>.Instance));

        Assert.Contains("one thread", ex.Message);
        Assert.Contains("Sound card", ex.Message);
    }
}
