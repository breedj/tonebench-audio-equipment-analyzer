using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.SoundCard;

namespace Tonebench.Device.Tests.SoundCard;

/// <summary>
/// Two cards streaming at the same time, each paced by its own clock — which is what a card on
/// both halves of a split needs, and which used to be refused at construction.
///
/// <para><b>Why it was refused.</b> A transfer loop is <i>posted</i> to a dispatcher and occupies
/// that thread for the stream's whole life, so two devices sharing one dispatcher means the second
/// one's pump queues behind the first and never runs — no exception, no log line, just an
/// acquisition that produces nothing. <c>SoundCardDevice</c> therefore takes a transfer thread of
/// its own when it is told to, while every call that reaches the driver stays on the one thread
/// that enumerated it: PortAudio is not documented thread-safe for concurrent library calls, and
/// under ASIO the driver is a COM object belonging to that apartment.</para>
///
/// <para><b>Why not one loop over both streams.</b> A loop that blocks on a read and then a write
/// couples the two clocks exactly as a single duplex stream across two devices does — it
/// accumulates their difference at the ppm rate, ~19.2 frames/s at 100 ppm and 192 kHz, until a
/// ring slips. Two loops each free-run on their own device's clock, which is the whole property
/// being bought here and the one thing a shared thread cannot provide.</para>
///
/// <para><b>What these cannot cover.</b> Whether the <i>native</i> library tolerates it: a fake
/// has no apartment and no driver. That is the bench measurement, and the ASIO half of it is
/// settled already and not in our gift — the ASIO SDK has no notion of more than one driver open
/// per process, so at most one half of a two-card bench may ever be ASIO.</para>
/// </summary>
public class SoundCardTwoCardStreamTests
{
    [Fact]
    public async Task TwoCards_AskedForTheirOwnThreads_DoNotShareOne()
    {
        var (capture, playback, pa) = BuildTwoCards(ownThreads: true);

        await RunBothAsync(capture, playback, pa);

        // Two distinct threads, and neither is the library's own — which keeps enumeration, opening
        // and every other native call answerable while both pumps are occupying theirs.
        StaDispatcher[] used = [.. pa.LoopDispatchers.OfType<StaDispatcher>().Distinct()];
        Assert.Equal(2, used.Length);
        Assert.DoesNotContain(pa.Dispatcher, used);
    }

    [Fact]
    public async Task TwoCards_LeftToShare_BothLandOnTheLibrarysThread()
    {
        // Not a recommendation — a statement of the hazard. This is the arrangement that hangs on
        // real hardware, and it is the default because it is what every measurement this app has
        // taken was measured with: one card, one thread, nothing queued behind anything.
        var (capture, playback, pa) = BuildTwoCards(ownThreads: false);

        await RunBothAsync(capture, playback, pa, expectBothToRun: false);

        StaDispatcher[] used = [.. pa.LoopDispatchers.OfType<StaDispatcher>().Distinct()];
        Assert.Single(used);
        Assert.Same(pa.Dispatcher, used[0]);
    }

    [Fact]
    public async Task TwoCards_WithTheirOwnThreads_BothRunAtOnce()
    {
        // The regression this whole change exists for, and the one a shared thread fails: the
        // capturing card hands out blocks *while* the playing card is writing. Asserted on both
        // sides because either alone passes against half the bug — a playback-only stream yields
        // no blocks at all, so the capture count says nothing about whether the generator ran.
        var (capture, playback, pa) = BuildTwoCards(ownThreads: true);

        var run = await RunBothAsync(capture, playback, pa);

        Assert.True(run.CaptureBlocks >= 3, $"the capturing card produced {run.CaptureBlocks} blocks");
        Assert.True(run.CaptureStream!.ReadCount >= 3, $"it read {run.CaptureStream.ReadCount} transfers");
        Assert.True(run.PlaybackStream!.WriteCount >= 1, $"the playing card wrote {run.PlaybackStream.WriteCount} transfers");

        // Each stream carries one direction, which is what makes the pair two free-running clocks
        // rather than one lockstep loop over two.
        Assert.Equal(0, run.CaptureStream.WriteCount);
        Assert.Equal(0, run.PlaybackStream.ReadCount);
    }

    [Fact]
    public async Task AOneCardBench_StillStreamsOnTheLibrarysThread()
    {
        // The path every existing measurement takes, unchanged: no thread of its own, so the loop
        // and the stream's own reads and writes are the same direct calls they always were.
        var pa = new FakePortAudio();
        SoundCardHostApi api = pa.AddHostApi("ASIO", SoundCardHostApiKind.Asio);
        pa.AddDevice(api, "MOTU M4", inputs: 2, outputs: 2);

        var device = new SoundCardDevice(
            new SoundCardSettings
            {
                InputHostApi = "ASIO",
                InputDeviceName = "MOTU M4",
                OutputHostApi = "ASIO",
                OutputDeviceName = "MOTU M4",
                StreamStartSettleMs = 0,
            },
            SoundCardCalibration.Uncalibrated,
            NullLogger<SoundCardDevice>.Instance,
            pa);

        pa.NextStream.InputSample = (_, _) => 0.5f;
        await device.ConnectAsync(CancellationToken.None);

        int blocks = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            block.Dispose();
            if (++blocks >= 2)
                break;
        }

        Assert.Equal(2, blocks);
        Assert.All(pa.LoopDispatchers, d => Assert.Same(pa.Dispatcher, d));
    }

    private sealed record Run(int CaptureBlocks, FakePortAudioStream? CaptureStream, FakePortAudioStream? PlaybackStream);

    /// <summary>
    /// Streams both cards at once: the playing one in the background for the length of the run, the
    /// capturing one read until it has handed out three blocks.
    ///
    /// <para><paramref name="expectBothToRun"/> false is the shared-thread case, where one pump
    /// never gets the thread — so the capture read is allowed to time out instead of failing, the
    /// point of that test being which dispatcher was asked for rather than what came back.</para>
    /// </summary>
    private static async Task<Run> RunBothAsync(
        SoundCardDevice capture, SoundCardDevice playback, FakePortAudio pa, bool expectBothToRun = true)
    {
        // The shared-thread case is *expected* to time out, so it gets a short budget: both opens
        // have already been recorded by then (OpenAndStart runs before the pump is posted), and the
        // rest of the wait is for blocks that are never coming.
        TimeSpan budget = expectBothToRun ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2);

        FakePortAudioStream? captureStream = null, playbackStream = null;

        pa.StreamFactory = request =>
        {
            var stream = new FakePortAudioStream { InputSample = (_, _) => 0.5f };

            if (request.Direction == SoundCardStreamDirection.InputOnly)
            {
                captureStream = stream;
            }
            else
            {
                // Paced, or an unpaced playback-only loop spins as fast as the thread allows and
                // keeps a copy of every buffer it writes.
                stream.PaceWrites = true;
                playbackStream = stream;
            }

            return stream;
        };

        await capture.ConnectAsync(CancellationToken.None);
        await playback.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(budget);

        Task playing = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (PcmBlock block in playback.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
                        block.Dispose();
                }
                catch (OperationCanceledException)
                {
                    // The run is over; a playback-only stream only ever ends by being cancelled.
                }
            },
            CancellationToken.None);

        int blocks = 0;
        try
        {
            await foreach (PcmBlock block in capture.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
            {
                block.Dispose();
                if (++blocks >= 3)
                    break;
            }
        }
        catch (OperationCanceledException) when (!expectBothToRun)
        {
            // Expected: with one shared thread the capturing pump is queued behind the playing one.
        }

        await cts.CancelAsync();
        await playing;

        capture.Dispose();
        playback.Dispose();

        return new Run(blocks, captureStream, playbackStream);
    }

    private static (SoundCardDevice Capture, SoundCardDevice Playback, FakePortAudio Pa) BuildTwoCards(bool ownThreads)
    {
        var pa = new FakePortAudio();
        SoundCardHostApi wasapi = pa.AddHostApi("WASAPI", SoundCardHostApiKind.Wasapi);
        pa.AddDevice(wasapi, "Cosmos ADC", inputs: 2, outputs: 0);
        pa.AddDevice(wasapi, "Topping D50 III", inputs: 0, outputs: 2);

        // WASAPI on both sides deliberately: it is the one pairing a two-card bench can actually
        // have on this host, ASIO allowing one driver per process.
        //
        // No settle, for the reason SoundCardDeviceTests' own helper defaults it to 0: the withheld
        // frames are still read, and FakePortAudioStream paces a read at the rate it claims, so the
        // default 2 s would spend ~2 s of a 10 s budget on a discard none of these tests is about.
        var capture = new SoundCardSettings
        {
            InputHostApi = "WASAPI",
            InputDeviceName = "Cosmos ADC",
            StreamStartSettleMs = 0,
        };

        var playback = new SoundCardSettings
        {
            OutputHostApi = "WASAPI",
            OutputDeviceName = "Topping D50 III",
            StreamStartSettleMs = 0,
        };

        return (
            new SoundCardDevice(
                capture, SoundCardCalibration.Uncalibrated, NullLogger<SoundCardDevice>.Instance, pa, ownThreads),
            new SoundCardDevice(
                playback, SoundCardCalibration.Uncalibrated, NullLogger<SoundCardDevice>.Instance, pa, ownThreads),
            pa);
    }
}
