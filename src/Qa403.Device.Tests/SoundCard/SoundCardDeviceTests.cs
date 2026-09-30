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
        int outputChannels = 2)
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
    private static async Task<List<(float[] Samples, long Sequence, int Channels, bool Discontinuity)>> TakeAsync(
        SoundCardDevice device, int count)
    {
        var taken = new List<(float[], long, int, bool)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            using (block)
                taken.Add(([.. block.Samples], block.SequenceNumber, block.ChannelCount, block.StartsAfterDiscontinuity));

            if (taken.Count >= count)
                break;
        }

        return [.. taken.Select(t => (t.Item1, t.Item2, t.Item3, t.Item4))];
    }
}
