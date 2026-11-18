using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The oscilloscope runner against the simulator: that it takes the session, catches what it was
/// pointed at, hands the pipeline back, and parks its record under the measurement it belongs to.
/// </summary>
public class OscilloscopeRunnerTests
{
    private const double SampleRateHz = 48_000.0;

    private sealed class Bench
    {
        public required SimulatedDevice Device { get; init; }
        public required SpectrumPipeline Pipeline { get; init; }
        public required DeviceSessionService Session { get; init; }
        public required ScopeTriggerEngine Engine { get; init; }
        public required OscilloscopeRunner Runner { get; init; }
    }

    private static async Task<Bench> NewBenchAsync()
    {
        var device = new SimulatedDevice();
        var engine = new ScopeTriggerEngine(new ScopeCaptureBuffer(2, 8192));
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(
            device, pipeline, new CaptureDelaySetting { Ms = 0 },
            NullLogger<DeviceSessionService>.Instance, null, engine);

        await session.ConnectAsync(CancellationToken.None);

        return new Bench
        {
            Device = device,
            Pipeline = pipeline,
            Session = session,
            Engine = engine,
            Runner = new OscilloscopeRunner(session, pipeline, engine, NullLogger<OscilloscopeRunner>.Instance),
        };
    }

    private static OscilloscopeRequest Request(
        ScopeRunMode mode = ScopeRunMode.Single,
        double recordSeconds = 0.02,
        double preTriggerFraction = 0.25,
        double levelV = 0.0,
        double sampleRateHz = SampleRateHz) =>
        new(
            sampleRateHz,
            recordSeconds,
            preTriggerFraction,
            mode,
            TriggerKind.Edge,
            TriggerSource.ChannelA,
            TriggerSlope.Rising,
            levelV,
            Generator: new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(1_000.0, -6.0)]),
                new ChannelGeneratorConfig(true, [new GeneratorTone(1_000.0, -6.0)])));

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 20)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task ASingleRun_CatchesOneRecordAndFinishes()
    {
        var bench = await NewBenchAsync();
        try
        {
            var started = await bench.Runner.StartAsync(Request(), CancellationToken.None);

            Assert.Equal(SampleRateHz, started.SampleRateHz, 6);
            Assert.Equal(960, started.RecordFrames);
            Assert.Equal(240, started.PreTriggerFrames);

            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            var status = bench.Runner.Status;
            Assert.Equal(FrequencyResponsePhase.Finished, status.Phase);
            Assert.Null(status.Error);
            Assert.True(status.HasRecord);
            Assert.Equal(1, status.RecordsTaken);
            Assert.False(status.Forced);

            // The readings the status carries are the record's own, and all finite.
            Assert.NotNull(status.TriggerOffsetS);
            Assert.NotNull(status.RecordFromS);
            Assert.True(status.RecordFromS < 0.0, "a record with pre-trigger starts before t = 0");

            var record = bench.Runner.Record;
            Assert.NotNull(record);
            Assert.Equal(960, record!.FrameCount);
            Assert.Equal(0.5, WaveformMeasure.Measure(record.Channels[0]).AcRmsV, 2);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TheRunIsBusyWhileItIsArmed()
    {
        // A level nothing reaches, so it stays armed. "Waiting for trigger" is a busy phase: the
        // run holds the session, the pipeline options and the input range the whole time.
        var bench = await NewBenchAsync();
        try
        {
            await bench.Runner.StartAsync(Request(mode: ScopeRunMode.Normal, levelV: 50.0), CancellationToken.None);

            Assert.True(await WaitForAsync(() => bench.Runner.Status.Phase == FrequencyResponsePhase.WaitingForTrigger));
            Assert.True(bench.Runner.Running);
            Assert.False(bench.Runner.Status.HasRecord);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TheRunHandsThePipelineBackWhateverHappens()
    {
        // The SoundCardCalibrationRunner lesson: a run that leaves its own record length behind is
        // a setting the user never chose and cannot see.
        var bench = await NewBenchAsync();
        try
        {
            var before = bench.Pipeline.Options;
            await bench.Runner.StartAsync(Request(), CancellationToken.None);
            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            Assert.Equal(before.FftSize, bench.Pipeline.Options.FftSize);
            Assert.Equal(before.SingleShot, bench.Pipeline.Options.SingleShot);
            Assert.Equal(before.AverageCount, bench.Pipeline.Options.AverageCount);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ACancelledRunSaysSo_AndStillHandsThePipelineBack()
    {
        var bench = await NewBenchAsync();
        try
        {
            var before = bench.Pipeline.Options;
            await bench.Runner.StartAsync(Request(mode: ScopeRunMode.Normal, levelV: 50.0), CancellationToken.None);
            Assert.True(await WaitForAsync(() => bench.Runner.Running));

            await bench.Runner.CancelAsync();

            Assert.Equal(FrequencyResponsePhase.Cancelled, bench.Runner.Status.Phase);
            Assert.False(bench.Runner.Running);
            Assert.Equal(before.FftSize, bench.Pipeline.Options.FftSize);
        }
        finally
        {
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ASecondStartIsRefusedWhileOneRuns()
    {
        var bench = await NewBenchAsync();
        try
        {
            await bench.Runner.StartAsync(Request(mode: ScopeRunMode.Normal, levelV: 50.0), CancellationToken.None);
            Assert.True(await WaitForAsync(() => bench.Runner.Running));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => bench.Runner.StartAsync(Request(), CancellationToken.None));
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ARateTheAnalyzerDoesNotHave_IsRefusedToTheCallerRatherThanOnTheRunTask()
    {
        // Synchronously with the request, so it is a 409 on the start rather than an exception on a
        // task nobody is awaiting -- which is a 200 followed by a run that never produces anything.
        var bench = await NewBenchAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => bench.Runner.StartAsync(Request(sampleRateHz: 44_100.0), CancellationToken.None));
            Assert.Contains("44.1 kHz", ex.Message);

            Assert.False(bench.Runner.Running);
            Assert.Equal(FrequencyResponsePhase.Idle, bench.Runner.Status.Phase);
        }
        finally
        {
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ARecordIsParkedUnderItsOwnMeasurement()
    {
        // Two scope measurements share this one runner, so without parking the second would show
        // the first's waveform -- and Save reads the server, so it would store it too.
        var bench = await NewBenchAsync();
        try
        {
            bench.Runner.SwitchOwner("project@1:1");
            await bench.Runner.StartAsync(Request(), CancellationToken.None);
            Assert.True(await WaitForAsync(() => !bench.Runner.Running));
            Assert.True(bench.Runner.Status.HasRecord);

            // Away to a measurement that has never run: its own slot is empty.
            var away = bench.Runner.SwitchOwner("project@1:2");
            Assert.False(away.HasRecord);
            Assert.Equal(0, away.RecordsTaken);

            // And back: the first measurement's record is still its own.
            var back = bench.Runner.SwitchOwner("project@1:1");
            Assert.True(back.HasRecord);
            Assert.Equal(1, back.RecordsTaken);
            Assert.NotNull(bench.Runner.Record);
            Assert.NotNull(bench.Runner.LastRequest);
        }
        finally
        {
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AClearedRecordStaysCleared()
    {
        // A Clear is a deliberate "throw this away" and must not be undone by switching measurement
        // and back -- which is what parking a null current does.
        var bench = await NewBenchAsync();
        try
        {
            bench.Runner.SwitchOwner("project@1:1");
            await bench.Runner.StartAsync(Request(), CancellationToken.None);
            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            bench.Runner.Clear();
            Assert.False(bench.Runner.Status.HasRecord);

            bench.Runner.SwitchOwner("project@1:2");
            Assert.False(bench.Runner.SwitchOwner("project@1:1").HasRecord);
        }
        finally
        {
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ADestroyedMeasurementsRecordIsForgotten()
    {
        var bench = await NewBenchAsync();
        try
        {
            bench.Runner.SwitchOwner("project@1:1");
            await bench.Runner.StartAsync(Request(), CancellationToken.None);
            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            bench.Runner.ForgetOwner("project@1:1");

            Assert.False(bench.Runner.Status.HasRecord);
            Assert.Null(bench.Runner.Record);
            Assert.Equal(FrequencyResponsePhase.Idle, bench.Runner.Status.Phase);
        }
        finally
        {
            await bench.Session.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.0)]
    [InlineData(-0.5)]
    public void ARecordLengthOutsideTheBounds_IsRefused(double recordSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(recordSeconds: recordSeconds).Validate());
    }

    [Fact]
    public void ATriggerOnAChannelThatIsNotBeingCaptured_IsRefused()
    {
        var request = Request() with { TriggerSource = TriggerSource.ChannelB, ProcessRight = false };

        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void NoChannelAtAll_IsRefused()
    {
        var request = Request() with { ProcessLeft = false, ProcessRight = false };

        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void AWindowTriggerNeedsTwoDifferentLevels()
    {
        // Two equal bounds are not a narrow window -- they are no window, and nothing can ever be
        // inside one. The message names the control rather than the field.
        var request = Request() with { TriggerKind = TriggerKind.Window, TriggerLevelV = 0.5, TriggerLevelLowV = 0.5 };

        var ex = Assert.Throws<ArgumentException>(request.Validate);
        Assert.Contains("two levels must differ", ex.Message, StringComparison.OrdinalIgnoreCase);

        (request with { TriggerLevelLowV = -0.5 }).Validate();
    }

    [Fact]
    public void ARuntTriggerNeedsTwoDifferentThresholds_AndSaysSoInItsOwnWords()
    {
        // The same two numbers read two ways, so they are checked together -- but a runt is a pulse
        // that reaches one threshold and not the other, which is not what a band is, and a message
        // about a "window" on the runt screen would send the reader looking for a control that is
        // not there.
        var request = Request() with { TriggerKind = TriggerKind.Runt, TriggerLevelV = 0.5, TriggerLevelLowV = 0.5 };

        var ex = Assert.Throws<ArgumentException>(request.Validate);
        Assert.Contains("runt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("window", ex.Message, StringComparison.OrdinalIgnoreCase);

        (request with { TriggerLevelLowV = -0.5 }).Validate();
    }

    [Fact]
    public void ADropoutTriggerNeedsATimeout()
    {
        // Zero would fire on every sample on which no edge happened, which is almost all of them --
        // a trigger that fires constantly while claiming to report an absence.
        var request = Request() with { TriggerKind = TriggerKind.Dropout };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
        Assert.Contains("timeout above zero", ex.Message, StringComparison.OrdinalIgnoreCase);

        (request with { DropoutSeconds = 0.05 }).Validate();
    }

    [Fact]
    public void APulseWidthTriggerNeedsAWidthCondition()
    {
        // Neither bound set means every pulse qualifies, which is an edge trigger firing on
        // trailing edges instead -- and silent if it were allowed.
        var request = Request() with { TriggerKind = TriggerKind.PulseWidth };

        var ex = Assert.Throws<ArgumentException>(request.Validate);
        Assert.Contains("width condition", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Either bound on its own is a condition.
        (request with { PulseWidthHiSeconds = 50e-6 }).Validate();
        (request with { PulseWidthLoSeconds = 50e-6 }).Validate();
    }

    [Fact]
    public void APulseWidthRangeTheWrongWayRound_IsRefused()
    {
        var request = Request() with
        {
            TriggerKind = TriggerKind.PulseWidth,
            PulseWidthLoSeconds = 120e-6,
            PulseWidthHiSeconds = 30e-6,
        };

        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public async Task APulseWidthRun_CapturesARecord()
    {
        // The seam, end to end: the width in seconds on the request has to reach the scanner as
        // frames, which happens in two places -- the runner's BuildSpec and the engine's
        // BuildScanner -- and both are private. A run that commits a record is what proves they
        // agree; a request mapped into a spec that dropped the bounds would refuse at validation or
        // fire on every crossing, and neither shows up in a unit test of either half.
        //
        // The generator plays a 1 kHz tone, so the stretch above zero is 500 us: "longer than
        // 100 us" fires mid-pulse, which is also the branch no edge trigger has.
        var bench = await NewBenchAsync();
        try
        {
            var request = Request() with
            {
                TriggerKind = TriggerKind.PulseWidth,
                PulseWidthLoSeconds = 100e-6,
            };

            await bench.Runner.StartAsync(request, CancellationToken.None);

            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            var status = bench.Runner.Status;
            Assert.Equal(FrequencyResponsePhase.Finished, status.Phase);
            Assert.Null(status.Error);
            Assert.Equal(1, status.RecordsTaken);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public void TheStimulusSourceNeedsAGeneratorWhoseRepeatIsKnown()
    {
        // A plain tone has no whole-frame period -- fs/f is a whole number only for frequencies
        // nobody types -- and rounding it would walk the trace sideways a fraction of a sample per
        // cycle. It has a crossing to trigger on instead, which the message says.
        var tone = Request() with { TriggerSource = TriggerSource.Stimulus };

        var ex = Assert.Throws<ArgumentException>(tone.Validate);
        Assert.Contains("repeat is exact", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A square is built to an exact frame count, so it is one this can lock to.
        var square = tone with
        {
            Generator = new GeneratorConfig(
                new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.Square, Wave = new PeriodicWaveSettings { FrequencyHz = 1_000.0 } },
                new ChannelGeneratorConfig(false, [])),
        };

        square.Validate();

        // 48 frames: a 1 kHz square at this bench's 48 kS/s, quantised to a whole even number of
        // samples the way PeriodicWave builds it -- which is the whole reason this stimulus has a
        // period to trigger on at all.
        Assert.Equal(48, square.StimulusPeriodFrames());
    }

    [Fact]
    public void TheStimulusSourceReplacesTheKindRatherThanQualifyingIt()
    {
        // There is no level, no slope and no width in a trigger that fires on the generator's own
        // repeat, so a kind beside it would be four controls doing nothing.
        var request = Request() with
        {
            TriggerSource = TriggerSource.Stimulus,
            TriggerKind = TriggerKind.Window,
            TriggerLevelV = 0.2,
            TriggerLevelLowV = -0.2,
            Generator = new GeneratorConfig(
                new ChannelGeneratorConfig(true, []) { Kind = GeneratorKind.Square, Wave = new PeriodicWaveSettings { FrequencyHz = 1_000.0 } },
                new ChannelGeneratorConfig(false, [])),
        };

        var ex = Assert.Throws<ArgumentException>(request.Validate);
        Assert.Contains("generator's own repeat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStimulusTriggeredRun_CapturesARecordWithNoEdgeToFind()
    {
        // The seam, and the case the source exists for: periodic noise has no feature an edge
        // trigger could lock to, so a record at all is the thing to assert. The period reaches the
        // scanner through the spec, which BuildSpec fills from the staged generator -- two private
        // methods again, and a run that commits is what proves they agree.
        var bench = await NewBenchAsync();
        try
        {
            var request = Request() with
            {
                TriggerSource = TriggerSource.Stimulus,
                Generator = new GeneratorConfig(
                    new ChannelGeneratorConfig(true, [])
                    {
                        Kind = GeneratorKind.PinkNoise,
                        Noise = new WhiteNoiseSettings { LevelDbv = -20.0, PeriodFrames = 8192 },
                    },
                    new ChannelGeneratorConfig(false, [])),
            };

            Assert.Equal(8192, request.StimulusPeriodFrames());

            await bench.Runner.StartAsync(request, CancellationToken.None);

            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            var status = bench.Runner.Status;
            Assert.Equal(FrequencyResponsePhase.Finished, status.Phase);
            Assert.Null(status.Error);
            Assert.Equal(1, status.RecordsTaken);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ADropoutRun_CapturesARecordOnAnInputThatIsNotMoving()
    {
        // The seam for the kind whose timeout rides on a field of its own: the request's
        // DropoutSeconds has to reach the scanner as the spec's WidthLoS, which BuildSpec picks
        // per kind. Sent at a level the generator's own tone never crosses, so there is no edge and
        // the timeout is what commits the record -- and it must not come back Forced, which is
        // Auto's own way of drawing something and a different thing entirely.
        var bench = await NewBenchAsync();
        try
        {
            var request = Request(mode: ScopeRunMode.Normal) with
            {
                TriggerKind = TriggerKind.Dropout,
                TriggerLevelV = 5.0,
                DropoutSeconds = 0.01,
            };

            await bench.Runner.StartAsync(request, CancellationToken.None);

            Assert.True(await WaitForAsync(() => bench.Runner.Status.RecordsTaken > 0));
            Assert.Null(bench.Runner.Status.Error);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AWindowRun_CapturesARecord()
    {
        // The same seam for the other kind: both bounds have to reach the scanner, and a spec that
        // carried only one would make a band of (level, 0) and still capture something plausible.
        // The tone swings +/-0.5 V at -6 dBV, so a band of +/-0.1 V is entered twice a cycle.
        var bench = await NewBenchAsync();
        try
        {
            var request = Request() with
            {
                TriggerKind = TriggerKind.Window,
                TriggerLevelV = 0.1,
                TriggerLevelLowV = -0.1,
            };

            await bench.Runner.StartAsync(request, CancellationToken.None);

            Assert.True(await WaitForAsync(() => !bench.Runner.Running));

            var status = bench.Runner.Status;
            Assert.Equal(FrequencyResponsePhase.Finished, status.Phase);
            Assert.Null(status.Error);
            Assert.Equal(1, status.RecordsTaken);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public void TheRecordLengthIsTheSecondsAskedForAtTheRateUsed()
    {
        // Seconds and a fraction, not frame counts, so the same request means the same measurement
        // at every rate -- converted once, against the rate it will really stream at.
        var request = Request(recordSeconds: 0.01, preTriggerFraction: 0.5);

        Assert.Equal(480, request.RecordFramesAt(48_000.0));
        Assert.Equal(240, request.PreTriggerFramesAt(48_000.0));
        Assert.Equal(1920, request.RecordFramesAt(192_000.0));
        Assert.Equal(960, request.PreTriggerFramesAt(192_000.0));
    }
    /// <summary>
    /// The report this was written for, from the other end: turning a control while the scope runs
    /// must not need a stop and a start. A <c>/start</c> is refused by MeasurementRunGuard while
    /// this runner holds the session -- rightly -- so a level that could only be changed by one was
    /// a level that could not be changed at all.
    /// </summary>
    [Fact]
    public async Task ARetuneChangesASettingWithoutStoppingTheRun()
    {
        var bench = await NewBenchAsync();
        try
        {
            await bench.Runner.StartAsync(Request(mode: ScopeRunMode.Normal, levelV: 50.0), CancellationToken.None);
            Assert.True(await WaitForAsync(() => bench.Runner.Status.Phase == FrequencyResponsePhase.WaitingForTrigger));

            bench.Runner.Retune(Request(mode: ScopeRunMode.Normal, recordSeconds: 0.01, levelV: 0.0));

            // The run is the same run: no cancel, no restart, and the record counter is still this
            // run's own. What has moved is the record length and, once a block has carried the new
            // level, the trigger -- which was reaching nothing at 50 V.
            Assert.True(bench.Runner.Running);
            Assert.Equal(480, bench.Runner.Status.RecordFrames);
            Assert.True(await WaitForAsync(() => bench.Runner.Status.RecordsTaken > 0));
            Assert.True(bench.Runner.Running);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    /// <summary>
    /// A retune can move the mode, and the run loop has to read it rather than the mode its start
    /// request carried -- Normal to Single is an ordinary thing to do mid-capture.
    /// </summary>
    [Fact]
    public async Task ARetuneToSingleEndsTheRunOnTheNextRecord()
    {
        var bench = await NewBenchAsync();
        try
        {
            await bench.Runner.StartAsync(Request(mode: ScopeRunMode.Normal, levelV: 50.0), CancellationToken.None);
            Assert.True(await WaitForAsync(() => bench.Runner.Status.Phase == FrequencyResponsePhase.WaitingForTrigger));

            bench.Runner.Retune(Request(mode: ScopeRunMode.Single, levelV: 0.0));

            Assert.True(await WaitForAsync(() => !bench.Runner.Running));
            Assert.Equal(FrequencyResponsePhase.Finished, bench.Runner.Status.Phase);
            Assert.Equal(1, bench.Runner.Status.RecordsTaken);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    /// <summary>
    /// The sample rate is the one setting a running capture cannot take: it re-clocks the analyzer.
    /// Refused by name, so the screen can answer by restarting the run rather than by showing the
    /// user an error they did not cause.
    /// </summary>
    [Fact]
    public async Task ARetuneAtAnotherSampleRateIsRefusedByName()
    {
        var bench = await NewBenchAsync();
        try
        {
            await bench.Runner.StartAsync(Request(mode: ScopeRunMode.Normal, levelV: 50.0), CancellationToken.None);
            Assert.True(await WaitForAsync(() => bench.Runner.Status.Phase == FrequencyResponsePhase.WaitingForTrigger));

            var ex = Assert.Throws<InvalidOperationException>(
                () => bench.Runner.Retune(Request(mode: ScopeRunMode.Normal, sampleRateHz: 96_000.0)));

            Assert.Contains("sample rate", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(bench.Runner.Running);
            Assert.Equal(SampleRateHz, bench.Runner.Status.SampleRateHz, 6);
        }
        finally
        {
            await bench.Runner.CancelAsync();
            await bench.Session.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ARetuneWithNothingRunningIsRefused()
    {
        var bench = await NewBenchAsync();
        try
        {
            Assert.Throws<InvalidOperationException>(() => bench.Runner.Retune(Request(mode: ScopeRunMode.Normal)));
        }
        finally
        {
            await bench.Session.ShutdownAsync();
        }
    }
}
