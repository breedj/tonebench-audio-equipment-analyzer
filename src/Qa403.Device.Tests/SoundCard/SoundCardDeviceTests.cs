using Microsoft.Extensions.Logging.Abstractions;
using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// The device over a fake PortAudio, so the whole of it except the interop runs without a card.
/// </summary>
public class SoundCardDeviceTests
{
    private const int TransferFrames = 1024;

    private static (SoundCardDevice Device, FakePortAudio Pa) Build(
        SoundCardCalibration? calibration = null,
        int inputOffset = 0,
        int outputOffset = 0,
        int inputChannels = 2,
        int outputChannels = 2,
        // Zero by default. The settle is frames the device withholds, so a non-zero default
        // silently shifts every read index and flags the first measured block -- which is what
        // it did to four tests the moment the discard moved into the device. A test that cares
        // about the settle asks for one.
        double settleMs = 0.0)
    {
        var pa = new FakePortAudio();
        var api = pa.AddHostApi("ASIO", SoundCardHostApiKind.Asio);
        pa.AddDevice(api, "Test Card", inputs: inputChannels, outputs: outputChannels);

        var settings = new SoundCardSettings
        {
            InputHostApi = "ASIO",
            InputDeviceName = "Test Card",
            OutputHostApi = "ASIO",
            OutputDeviceName = "Test Card",
            InputChannelOffset = inputOffset,
            OutputChannelOffset = outputOffset,
            StreamStartSettleMs = settleMs,
        };

        var device = new SoundCardDevice(
            settings,
            calibration ?? SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance,
            pa);

        return (device, pa);
    }

    [Fact]
    public async Task Connect_ReportsTheCardAndTheRatesItAccepts()
    {
        var (device, pa) = Build();
        pa.FormatSupported = r => r.SampleRateHz != 192_000.0;

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        Assert.Contains("Test Card", info.ProductName);
        Assert.False(info.IsSimulated);
        Assert.Equal([48_000.0, 96_000.0], device.Capabilities.SampleRatesHz);
    }

    /// <summary>A card that accepts none of the three is not an analyzer this app can drive, and saying so at connect beats a 500 from a run later.</summary>
    [Fact]
    public async Task Connect_WhenTheCardAcceptsNoneOfTheThreeRates_IsRefusedWithAMessage()
    {
        var (device, pa) = Build();
        pa.FormatSupported = _ => false;

        var ex = await Assert.ThrowsAsync<SoundCardNotFoundException>(
            () => device.ConnectAsync(CancellationToken.None));
        Assert.Contains("48, 96 or 192", ex.Message);
    }

    [Fact]
    public async Task Connect_WithAMissingCard_NamesWhatItLookedFor()
    {
        var pa = new FakePortAudio();
        pa.AddHostApi("ASIO", SoundCardHostApiKind.Asio);
        var device = new SoundCardDevice(
            new SoundCardSettings { InputHostApi = "ASIO", InputDeviceName = "Gone", OutputHostApi = "ASIO", OutputDeviceName = "Gone" },
            SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance,
            pa);

        var ex = await Assert.ThrowsAsync<SoundCardNotFoundException>(
            () => device.ConnectAsync(CancellationToken.None));
        Assert.Contains("Gone", ex.Message);
    }

    /// <summary>A channel pair past what the card has is a configuration mistake, not something to clamp silently.</summary>
    [Fact]
    public async Task Connect_AskingForAChannelPairTheCardDoesNotHave_IsRefused()
    {
        var (device, _) = Build(inputOffset: 2, inputChannels: 2);

        var ex = await Assert.ThrowsAsync<SoundCardNotFoundException>(
            () => device.ConnectAsync(CancellationToken.None));
        Assert.Contains("input channels", ex.Message);
    }

    /// <summary>One fixed range means no runner should range it — see DeviceSessionService.RunnerOwnsInputRange.</summary>
    [Fact]
    public async Task Capabilities_AreOneRungPerSide_AndItDeclaresNoRangerOfItsOwn()
    {
        var calibration = new SoundCardCalibration
        {
            OutputFullScalePeakV = 2.0,
            InputFullScalePeakVLeft = 1.5,
            InputFullScalePeakVRight = 1.5,
        };
        var (device, _) = Build(calibration);
        await device.ConnectAsync(CancellationToken.None);

        Assert.False(device.HasInputAutoRanging);
        Assert.Single(device.Capabilities.InputRangesDbv);
        Assert.Single(device.Capabilities.OutputRangesDbv);
        Assert.Equal(calibration.InputRangeDbv, device.Capabilities.InputRangesDbv[0], 1e-9);
        Assert.Equal(calibration.InputRangeDbv, device.InputRangeDbv, 1e-9);
    }

    [Fact]
    public async Task SetInputRange_ToAnythingButTheOneRung_IsRefused()
    {
        var (device, _) = Build();
        await device.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.SetInputRangeAsync(42.0, CancellationToken.None));

        // The one it does have is accepted.
        await device.SetInputRangeAsync(device.Capabilities.InputRangesDbv[0], CancellationToken.None);
    }

    [Fact]
    public async Task StreamAsync_AtARateTheCardRefuses_IsRejected()
    {
        var (device, pa) = Build();
        pa.FormatSupported = r => r.SampleRateHz == 48_000.0;
        await device.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var block in device.StreamAsync(new AcquisitionConfig(192_000.0, 2), CancellationToken.None))
                block.Dispose();
        });
    }

    [Fact]
    public async Task StreamAsync_HandsOutBlocksInVolts()
    {
        var calibration = new SoundCardCalibration
        {
            OutputFullScalePeakV = 2.0,
            InputFullScalePeakVLeft = 4.0,
            InputFullScalePeakVRight = 4.0,
        };
        var (device, pa) = Build(calibration);
        pa.NextStream.InputSample = (_, _) => 0.5f;
        await device.ConnectAsync(CancellationToken.None);

        var blocks = await TakeAsync(device, count: 3);

        Assert.Equal(3, blocks.Count);
        foreach (var block in blocks)
        {
            // 0.5 of a 4 V peak full scale is 2 V. Calibration is applied exactly once, here.
            Assert.All(block.Samples, v => Assert.Equal(2.0f, v, 1e-5f));
        }
    }

    /// <summary>
    /// The rule the whole type hangs on: consumers routinely break out of the await foreach, and
    /// disposal runs finally handlers and nothing else. A stream left open holds the card — with
    /// ASIO, exclusively — which reads to a user as hardware needing a power cycle.
    /// </summary>
    [Fact]
    public async Task StreamAsync_WhenTheConsumerBreaksEarly_StillStopsAndClosesTheStream()
    {
        var (device, pa) = Build();
        await device.ConnectAsync(CancellationToken.None);

        await foreach (var block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), CancellationToken.None))
        {
            block.Dispose();
            break;
        }

        Assert.True(pa.NextStream.Stopped, "the stream was never stopped");
        Assert.True(pa.NextStream.Disposed, "the stream was never closed");
        Assert.Equal(["start", "stop", "dispose"], pa.NextStream.Calls);
    }

    /// <summary>
    /// An input overflow is a hole in the sample stream, not a skipped display frame: a record
    /// spanning the splice reads as a smeared tone on a lifted floor, so the next block has to say
    /// so and the pipeline has to break its window there.
    /// </summary>
    [Fact]
    public async Task StreamAsync_OnAnInputOverflow_FlagsTheNextBlockAndCountsIt()
    {
        var (device, pa) = Build();
        pa.NextStream.OverflowOnRead.Add(1);
        await device.ConnectAsync(CancellationToken.None);

        var blocks = await TakeAsync(device, count: 3);

        Assert.False(blocks[0].Discontinuity);
        Assert.True(blocks[1].Discontinuity, "the block after the overflow did not say the stream broke");
        Assert.False(blocks[2].Discontinuity);
        Assert.True(device.DroppedRxChunks >= 1);
    }

    /// <summary>
    /// <b>An output underflow is the DAC's own gap, and it was invisible.</b>
    ///
    /// <para><c>Pa_WriteStream</c> returns <c>paOutputUnderflowed</c> when the callback found the
    /// ring empty and played silence — a hole in the stimulus, and therefore a splice in what comes
    /// back through the DUT. The pump discarded that bool: not counted, not flagged, not logged.
    /// Measured on a MOTU M4 at 192 kHz, it is half of what a glitch actually looks like — an input
    /// overflow and an output underflow logged 0.2 ms apart, both directions of one stalled pump —
    /// so a device reporting only the overflow told half the story.</para>
    /// </summary>
    [Fact]
    public async Task StreamAsync_OnAnOutputUnderflow_FlagsTheNextBlock()
    {
        var (device, pa) = Build();
        await device.ConnectAsync(CancellationToken.None);

        // Every write from the first loop iteration on, so this cannot depend on how many the
        // priming made -- an underflow anywhere after the priming must reach the next block.
        for (int i = 1; i <= 12; i++)
            pa.NextStream.UnderflowOnWrite.Add(i);

        var blocks = await TakeAsync(device, count: 3);

        Assert.False(blocks[0].Discontinuity);
        Assert.True(blocks[1].Discontinuity, "the block after the DAC ran dry did not say the stream broke");
    }

    /// <summary>
    /// <b>The DAC's margin against a stall is the priming, and one transfer was not a margin.</b>
    ///
    /// <para>5.3 ms at 192 kHz — less than a scheduler quantum — against a ring PortAudio had
    /// sized at 31 ms, so any stall at all emptied it. The single transfer was believed to be
    /// forced by the lockstep loop, from a measurement where priming 64 ms killed the stream with
    /// "Wait timed out"; that failed because 64 ms does not fit in a 31 ms ring, not because
    /// lockstep allows only one. What it allows is whatever the ring holds with room for the next
    /// write, which is what <c>OutputRingFraction</c> expresses.</para>
    ///
    /// <para>Asserted as properties, not against a constant: what matters is that the margin is a
    /// <b>time</b> that does not shrink as the rate rises — the same lesson as the QA403's queue
    /// depth — and that the ring keeps room for the next write. The three rings are the MOTU M4's
    /// own, measured 2026-10-02. One transfer gave 21.3 / 10.7 / 5.3 ms at 48 / 96 / 192 kHz, which
    /// is exactly why the glitch was worst at the top rate.</para>
    /// </summary>
    [Theory]
    [InlineData(0.069, 48_000.0)]
    [InlineData(0.036, 96_000.0)]
    [InlineData(0.031, 192_000.0)]
    public async Task StreamAsync_PrimesTheOutputWithATimeBudget_LeavingTheRingRoomForTheNextWrite(
        double outputLatencyS, double sampleRateHz)
    {
        var (device, pa) = Build();
        FakePortAudioStream stream = pa.NextStream;
        stream.OutputLatencyS = outputLatencyS;
        stream.SampleRateHz = sampleRateHz;
        await device.ConnectAsync(CancellationToken.None);

        await TakeAsync(device, count: 1, sampleRateHz: sampleRateHz);

        int ringFrames = (int)(outputLatencyS * sampleRateHz);
        int primed = stream.WritesBeforeFirstRead;
        double primedMs = primed * TransferFrames * 1000.0 / sampleRateHz;

        Assert.True(primed >= 1, "nothing was written before the first read, so the DAC started dry");
        Assert.True(
            primedMs >= 15.0,
            $"the output was primed with {primed} transfer(s) = {primedMs:0.0} ms of a {ringFrames}-frame ring — "
                + "less than the stalls this pump is known to take");
        Assert.True(
            (primed + 1) * TransferFrames <= ringFrames,
            $"priming {primed} transfers leaves no room in a {ringFrames}-frame ring for the next write, which is what times out");
    }

    /// <summary>
    /// <b>Waiting for the stream to start is not a dropped chunk.</b>
    ///
    /// <para>An ASIO stream takes a few hundred milliseconds to deliver its first buffer and every
    /// read until then returns <c>paTimedOut</c>. Counting those as drops put a standing figure in
    /// the status bar on every run — measured on a MOTU M4: 3 at 96 kHz, 7 at 192 kHz, every one of
    /// them before block 0, and none afterwards. It scaled with the sample rate only because the
    /// stream's own read timeout is shorter there, so more waits fit into the same delay, which is
    /// what made it look like a rate-dependent streaming fault.</para>
    ///
    /// <para>It matters because of what the counter means: a hole in the sample stream, which
    /// invalidates a noise measurement. It is the number a user checks to decide whether to believe
    /// the screen, so a startup wait reported as one is worse than no number at all.</para>
    /// </summary>
    /// <summary>
    /// The settle is a <b>time</b>, converted per rate — the lesson the dropped-chunk count and the
    /// QA403's queue depth both taught. The transient it covers lasted a consistent ~1.5 s on a
    /// MOTU M4 whatever the rate, which was 5 records at 48 kHz and 23 at 192 kHz.
    /// </summary>
    [Theory]
    [InlineData(48_000.0, 2000.0, 96_000)]
    [InlineData(96_000.0, 2000.0, 192_000)]
    [InlineData(192_000.0, 2000.0, 384_000)]
    [InlineData(48_000.0, 0.0, 0)]
    public async Task StreamStartSettleFrames_IsTheConfiguredTimeAtThatRate(
        double sampleRateHz, double settleMs, int expectedFrames)
    {
        var (device, _) = Build(settleMs: settleMs);
        await device.ConnectAsync(CancellationToken.None);

        Assert.Equal(expectedFrames, device.StreamStartSettleFrames(sampleRateHz));
    }

    /// <summary>
    /// The throwaway open that re-clocks the card.
    ///
    /// <para>An ASIO driver re-clocks the hardware for a rate it is not already running at, and the
    /// MOTU M4's does not have the new one ready by the time the first stream starts: the reads come
    /// back partially filled and the unfilled part reaches the analyzer as audio. Opening once and
    /// discarding it is what makes the open that matters find the card already there.</para>
    /// </summary>
    [Fact]
    public async Task AStreamAtANewRate_IsOpenedOnceAndThrownAwayFirst()
    {
        var (device, pa) = Build();
        await device.ConnectAsync(CancellationToken.None);

        await TakeAsync(device, count: 1);

        Assert.Equal(2, pa.OpenCount);
        Assert.NotNull(pa.WarmUpStream);
        Assert.True(pa.WarmUpStream!.Started, "the throwaway stream was never started, so the driver was never asked to run at the new rate");
        Assert.True(pa.WarmUpStream.Stopped);
        Assert.True(pa.WarmUpStream.Disposed, "the throwaway stream was left open");
    }

    [Fact]
    public async Task StreamAsync_WaitingForTheFirstBuffer_IsNotCountedAsADroppedChunk()
    {
        var (device, pa) = Build();

        // The first three reads time out, as a real ASIO stream's do while the driver spins up.
        pa.NextStream.TimeOutOnRead.Add(0);
        pa.NextStream.TimeOutOnRead.Add(1);
        pa.NextStream.TimeOutOnRead.Add(2);
        await device.ConnectAsync(CancellationToken.None);

        var blocks = await TakeAsync(device, count: 2);

        Assert.Equal(0, device.DroppedRxChunks);
        Assert.Equal(0, device.DroppedBlocks);

        // And the first block is not flagged either: there is no continuity to have broken, and a
        // flagged first block makes the pipeline throw away the record it was about to fill.
        Assert.False(blocks[0].Discontinuity, "the first block claimed the stream had broken before it began");
    }

    /// <summary>
    /// A timeout <i>after</i> the stream is running is a different thing and still counts: the
    /// driver fell behind while audio was flowing, so the window really does span a gap.
    /// </summary>
    [Fact]
    public async Task StreamAsync_OnATimeoutOnceRunning_CountsItAndFlagsTheNextBlock()
    {
        var (device, pa) = Build();
        pa.NextStream.TimeOutOnRead.Add(1);
        await device.ConnectAsync(CancellationToken.None);

        var blocks = await TakeAsync(device, count: 2);

        Assert.True(device.DroppedRxChunks >= 1, "a timeout mid-stream was not counted");
        Assert.True(blocks[1].Discontinuity, "the block after the gap did not say the stream broke");
    }

    /// <summary>
    /// Per-acquisition, not cumulative. The only question these numbers are ever asked is whether
    /// the measurement on screen can be trusted, and a stale 8 from an earlier run reads exactly
    /// like 8 that just happened.
    /// </summary>
    [Fact]
    public async Task DropCounters_AreZeroedWhenAStreamStarts()
    {
        var (device, pa) = Build();
        pa.NextStream.OverflowOnRead.Add(0);
        await device.ConnectAsync(CancellationToken.None);

        await TakeAsync(device, count: 2);
        Assert.True(device.DroppedRxChunks > 0);

        pa.NextStream = new FakePortAudioStream();
        await TakeAsync(device, count: 2);
        Assert.Equal(0, device.DroppedRxChunks);
    }

    [Fact]
    public async Task Blocks_AreStereoAtTheRequestedRate_WithContiguousSequenceNumbers()
    {
        var (device, _) = Build();
        await device.ConnectAsync(CancellationToken.None);

        var blocks = await TakeAsync(device, count: 4);

        for (int i = 0; i < blocks.Count; i++)
        {
            Assert.Equal(i, blocks[i].Sequence);
            Assert.Equal(2, blocks[i].Channels);
        }

        // Contiguous because nothing was dropped. A producer outrunning the consumer would drop the
        // oldest and leave a gap here, which is the fake pacing its reads is there to prevent.
        Assert.Equal(0, device.DroppedBlocks);
    }

    /// <summary>
    /// The queue this device holds ahead of the DAC, which is what every stimulus flush trusts. The
    /// simulator claimed 0 for months and silently corrupted THD sweep points, so it must at least
    /// be non-zero and scale with the rate.
    /// </summary>
    [Fact]
    public void StimulusLatencyFrames_IsATimeBudget_SoItGrowsWithTheSampleRate()
    {
        var (device, _) = Build();

        int at48 = device.StimulusLatencyFrames(48_000.0);
        int at192 = device.StimulusLatencyFrames(192_000.0);

        Assert.True(at48 > 0);
        Assert.True(at192 > at48, $"192 kHz booked {at192} frames against {at48} at 48 kHz, so the queue is a transfer count rather than a time budget.");
    }

    /// <summary>The generator is read per transfer, so a change is audible in the next one rather than mid-buffer.</summary>
    [Fact]
    public async Task ConfigureGenerator_ReachesTheWrittenAudio()
    {
        var (device, pa) = Build(new SoundCardCalibration
        {
            OutputFullScalePeakV = 10.0,
            InputFullScalePeakVLeft = 1.0,
            InputFullScalePeakVRight = 1.0,
        });
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -6.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        await TakeAsync(device, count: 4);

        // Something non-zero was played, and it stayed inside full scale.
        var written = pa.NextStream.Written;
        Assert.NotEmpty(written);
        Assert.Contains(written, buffer => buffer.Any(v => Math.Abs(v) > 1e-6));
        Assert.All(written, buffer => Assert.All(buffer, v => Assert.InRange(v, -1.0f, 1.0f)));
    }

    /// <summary>
    /// <b>The re-clock settle is frames the device never hands out, and only after a re-clock.</b>
    ///
    /// <para>It used to be a Flush booked by the session, which the first tone a runner configured
    /// then overwrote with the much shorter capture delay — so the card's transient landed in the
    /// measurement. Measured on a MOTU M4 entering each run from a different sample rate, 100 %
    /// reproducible: THD 4.3 % on a sweep's first point against 0.0006 % on its second, and a
    /// frequency-response trace spread over 91 dB with coherence 0.17 against 0.51 dB and 1.0000
    /// on the next identical run. Frames that are never handed out cannot be un-discarded.</para>
    ///
    /// <para>The second case is the one that keeps it affordable: a stream reopened at the rate it
    /// was already running at has nothing to settle, so it must pay nothing.</para>
    /// </summary>
    [Fact]
    public async Task StreamAsync_AfterAReClock_WithholdsTheSettleAndThenMeasures()
    {
        var (device, pa) = Build(settleMs: 100.0);
        await device.ConnectAsync(CancellationToken.None);

        // 100 ms at 48 kHz is 4800 frames, i.e. the first 4 transfers of 1024 and part of a fifth.
        int expected = (int)Math.Ceiling(4800.0 / TransferFrames);

        var blocks = await TakeAsync(device, count: 2);

        Assert.True(
            pa.NextStream.ReadCount >= expected + 2,
            $"only {pa.NextStream.ReadCount} reads were made, so the settle was not read through");
        Assert.True(blocks[0].Discontinuity, "the first measured block did not say the stream had broken");
        Assert.Equal(0, blocks[0].Sequence);

        // Withheld, not counted: nothing was lost, so the number the user checks must stay clean.
        Assert.Equal(0, device.DroppedRxChunks);
    }

    [Fact]
    public async Task StreamAsync_ReopenedAtTheSameRate_WithholdsNothing()
    {
        var (device, pa) = Build(settleMs: 100.0);
        await device.ConnectAsync(CancellationToken.None);

        await TakeAsync(device, count: 1);              // the first open re-clocks
        int afterFirst = pa.NextStream.ReadCount;

        await TakeAsync(device, count: 1);              // the second is the same rate
        // The delta, because the fake counts reads across both streams.
        int second = pa.NextStream.ReadCount - afterFirst;
        Assert.True(
            second <= 3,
            $"a same-rate reopen withheld {second} reads' worth; it has nothing to settle");
        Assert.True(afterFirst >= 5, "the first open should have settled");
    }

    /// <summary>
    /// A channel offset selects a pair *within* one device, which under ASIO is the only way to
    /// reach channels 3/4 at all — the whole card is one device. The slots outside the pair must be
    /// left silent, or the tone goes out of outputs the bench is not patched to.
    /// </summary>
    [Fact]
    public async Task WithAChannelOffset_TheToneGoesOutOfThatPairAndNoOther()
    {
        var (device, pa) = Build(
            new SoundCardCalibration
            {
                OutputFullScalePeakV = 1.0,
                InputFullScalePeakVLeft = 1.0,
                InputFullScalePeakVRight = 1.0,
            },
            outputOffset: 2,
            outputChannels: 4);
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -6.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        await TakeAsync(device, count: 3);

        var written = pa.NextStream.Written.Where(b => b.Length >= 4 * 4).ToList();
        Assert.NotEmpty(written);

        double[] peak = new double[4];
        foreach (float[] buffer in written)
        {
            for (int f = 0; f + 3 < buffer.Length; f += 4)
            {
                for (int c = 0; c < 4; c++)
                    peak[c] = Math.Max(peak[c], Math.Abs(buffer[f + c]));
            }
        }

        Assert.Equal(0.0, peak[0], 1e-9);
        Assert.Equal(0.0, peak[1], 1e-9);
        Assert.True(peak[2] > 0.1, $"channel 3 carried nothing (peak {peak[2]})");
        Assert.True(peak[3] > 0.1, $"channel 4 carried nothing (peak {peak[3]})");
    }

    /// <summary>
    /// A disabled generator channel must be silent, and separately so: the whole of the
    /// channel-order check on real hardware rests on being able to play one side alone.
    /// </summary>
    [Fact]
    public async Task WithOnlyTheLeftGeneratorEnabled_TheRightOutputStaysSilent()
    {
        var (device, pa) = Build(new SoundCardCalibration
        {
            OutputFullScalePeakV = 1.0,
            InputFullScalePeakVLeft = 1.0,
            InputFullScalePeakVRight = 1.0,
        });
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -6.0)]);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(tone, ChannelGeneratorConfig.Off), CancellationToken.None);

        await TakeAsync(device, count: 3);

        double left = 0.0;
        double right = 0.0;
        foreach (float[] buffer in pa.NextStream.Written)
        {
            for (int f = 0; f + 1 < buffer.Length; f += 2)
            {
                left = Math.Max(left, Math.Abs(buffer[f]));
                right = Math.Max(right, Math.Abs(buffer[f + 1]));
            }
        }

        Assert.True(left > 0.1, $"the left output carried nothing (peak {left})");
        Assert.Equal(0.0, right, 1e-9);
    }

    /// <summary>Reads the first <paramref name="count"/> blocks and then leaves, which also exercises the teardown.</summary>
    /// <summary>
    /// <b>The rings are asked for as a time, and that is the whole of the stall fix.</b>
    ///
    /// <para>Left to itself PortAudio sizes both from the driver's own <c>defaultLow*Latency</c>,
    /// which an ASIO driver reports as a frame count: on a MOTU M4 at 192 kHz that was 43 ms of
    /// input ring and 30 ms of output, of which the pump could prime 16 ms. A scheduling stall
    /// longer than 16 ms therefore emptied the DAC and one longer than 43 ms also lost input —
    /// measured in the wild, one dropped chunk per ~24 sweeps on an idle machine and several per
    /// ten minutes on a busy one, each wrecking one sweep point. Asking for the time instead gave
    /// 171 ms in and 180 ms out at every rate, and a 64 ms prime.</para>
    ///
    /// <para>Asserted as a property rather than against a figure: what must hold is that the same
    /// number of <i>seconds</i> is requested whatever the rate, since a request that scaled with
    /// the rate would be the frame count this replaced.</para>
    /// </summary>
    [Theory]
    [InlineData(48_000.0)]
    [InlineData(192_000.0)]
    public async Task StreamAsync_AsksForTheRingsAsATimeBudget_NotTheDriversOwnFrameCount(double sampleRateHz)
    {
        var (device, pa) = Build();
        await device.ConnectAsync(CancellationToken.None);

        await TakeAsync(device, count: 1, sampleRateHz: sampleRateHz);

        double? requested = pa.OpenedWith?.SuggestedLatencyS;
        Assert.NotNull(requested);
        Assert.True(
            requested >= 0.1,
            $"only {requested * 1000:0} ms of ring was asked for, which is inside the stalls this pump is known to take");

        // The same seconds at four times the rate — a frame count would be a quarter of the time.
        Assert.Equal(0.128, requested!.Value, 6);
    }

    /// <summary>
    /// <b><c>StimulusLatencyFrames</c> is the queue primed ahead of the DAC, not the ring's
    /// capacity</b> — which is what its own remarks always said and what the code did not do.
    ///
    /// <para>It reported <c>OutputLatencyS</c>, and the two were close enough to pass unnoticed
    /// while the ring was the driver's 30 ms and the prime 16. Once the ring is asked for as a
    /// time they are 180 ms against 64, so every stimulus change on every point of every sweep
    /// would discard nearly three times what it has to; on WASAPI exclusive, which reports 131072
    /// frames whatever is asked of it, it would be 2.7 s a tone.</para>
    ///
    /// <para>Over-discarding is only slow, never wrong, which is exactly why nothing else would
    /// have caught it.</para>
    /// </summary>
    [Fact]
    public async Task StimulusLatencyFrames_IsTheQueuePrimedAheadOfTheDac_NotTheRingsCapacity()
    {
        const double sampleRateHz = 192_000.0;
        var (device, pa) = Build();
        FakePortAudioStream stream = pa.NextStream;
        // A ring far larger than the queue the device chooses to hold in it — the shape the time
        // budget produces, and the shape WASAPI exclusive produces to an absurd degree.
        stream.OutputLatencyS = 0.180;
        stream.SampleRateHz = sampleRateHz;
        await device.ConnectAsync(CancellationToken.None);

        int reported = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(sampleRateHz, 2), cts.Token))
        {
            block.Dispose();
            reported = device.StimulusLatencyFrames(sampleRateHz);
            break;
        }

        int primed = stream.WritesBeforeFirstRead;
        int ringFrames = (int)(stream.OutputLatencyS * sampleRateHz);

        Assert.Equal((primed + 1) * TransferFrames, reported);
        Assert.True(
            reported < ringFrames / 2,
            $"{reported} frames reported against a {ringFrames}-frame ring — that is the capacity, not the queue");
    }

    private static async Task<List<(float[] Samples, long Sequence, int Channels, bool Discontinuity)>> TakeAsync(
        SoundCardDevice device, int count, double sampleRateHz = 48_000.0)
    {
        var taken = new List<(float[], long, int, bool)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(sampleRateHz, 2), cts.Token))
        {
            using (block)
                taken.Add(([.. block.Samples], block.SequenceNumber, block.ChannelCount, block.StartsAfterDiscontinuity));

            if (taken.Count >= count)
                break;
        }

        return [.. taken.Select(t => (t.Item1, t.Item2, t.Item3, t.Item4))];
    }
}
