using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// The oscilloscope draws both channels from one trigger search: it finds a zero-crossing in L and
/// then indexes *both* channels' sample arrays from that same offset. That is only meaningful if the
/// two arrays describe the same instant, which is a property of how the pipeline captures them, not
/// of the signal. These guard that property end to end, through the real broadcast frames.
/// </summary>
public class ScopeChannelAlignmentTests
{
    private const double SampleRateHz = 48000;
    private const double ToneFreqHz = 1000.0;

    /// <summary>
    /// The hub's own default is 8 frames per client, and this configuration broadcasts *exactly* 8 per
    /// tick -- NoiseFigures, Spectrum, Meters, Scope, per channel, measured -- so the default leaves this
    /// reader one single tick of slack against a <c>DropOldest</c> bounded queue that reports nothing
    /// when it evicts. One tick period of scheduling delay (25 ms at
    /// TargetFrameRateHz 40) is then enough for the next tick's burst of 8 to evict the previous tick's,
    /// and because the writer emits 8 at once while this loop consumes one at a time, the eviction
    /// boundary lands *inside* a tick: one channel's scope frame goes and the other's survives, which
    /// arrives here as "seq N had no right frame" and reads exactly like the capture skew this file
    /// exists to catch. Measured with the reader deliberately parked 30 ms every 5th frame: at capacity
    /// 8, 69 of 104 frames arrived and 11 of 13 seqs lost the right channel's scope frame; at this
    /// capacity, with the identical stall, all 100 arrived and every group was intact.
    ///
    /// <para>12 groups is ~14 ticks, so the whole capture is ~112 frames -- this holds all of it nine
    /// times over, i.e. ~128 ticks of reader slack. The hub's lossiness is a production property, for a
    /// browser that cannot keep up, and it has its own test
    /// (<c>SpectrumBroadcastHubTests.Broadcast_never_blocks_on_a_stalled_reader_…</c>); it is not what
    /// this file measures, and giving this reader room removes a confound rather than hiding one. The
    /// guards at the end of <see cref="CaptureScopeGroupsAsync"/> are what keep that honest.</para>
    /// </summary>
    private const int HubCapacity = 1024;

    /// <summary>
    /// <see cref="SimulatedDevice"/> computes every channel of a frame from one <c>t</c>, and shares
    /// its hum/noise across channels, so an identical generator config on L and R produces sample-
    /// identical audio on both. Any difference between the two broadcast scope arrays is therefore
    /// pure capture skew, with no signal-dependent tolerance to argue about.
    ///
    /// <para>Before the fix this failed two independent ways. Snapshotting inside ProcessChannel let
    /// R's window advance by however long L's FFT took; snapshotting both up front but outside
    /// <c>_sampleSetLock</c> still let one 512-frame Enqueue block land in L's ring buffer and not
    /// yet R's, which at 48 kHz is 10.7 ms — over ten periods of this tone, so it aliases to an
    /// arbitrary-looking phase error rather than an obviously broken trace.</para>
    /// </summary>
    [Fact]
    public async Task ScopeFrames_ForOneTick_AreSampleAlignedAcrossChannels()
    {
        var groups = await CaptureScopeGroupsAsync(groupCount: 12);

        foreach (var (seq, byChannel) in groups)
        {
            Assert.True(byChannel.ContainsKey(StreamChannel.Left), $"seq {seq} had no left frame.");
            Assert.True(byChannel.ContainsKey(StreamChannel.Right), $"seq {seq} had no right frame.");

            float[] left = byChannel[StreamChannel.Left];
            float[] right = byChannel[StreamChannel.Right];
            Assert.Equal(left.Length, right.Length);

            // Identical input on both channels through an identical DSP path -- this is exact
            // equality of the same float arithmetic, not a similarity threshold.
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    Assert.Fail($"seq {seq}: channels diverge at sample {i} (L={left[i]}, R={right[i]}) — the two windows were captured at different instants.");
            }
        }
    }

    /// <summary>
    /// The client pairs L and R purely by the header's seq (see <c>useSpectrumStream.ts</c>): it
    /// publishes a group only once the next seq arrives, so a seq allocated per broadcast rather
    /// than per tick would put every channel in a group of its own and defeat the pairing entirely.
    /// </summary>
    [Fact]
    public async Task ScopeFrames_ForOneTick_ShareOneSequenceNumber()
    {
        var groups = await CaptureScopeGroupsAsync(groupCount: 6);

        // Both channels landing under one seq is the whole point; a per-broadcast seq would make
        // every group single-channel and this the failing assertion.
        Assert.All(groups, g => Assert.Equal(2, g.Value.Count));

        // And the tick's identity must still advance, or the client would coalesce separate ticks.
        var seqs = groups.Select(g => g.Key).ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    /// <summary>
    /// Runs the real pipeline against the simulator and collects broadcast scope frames grouped by
    /// the header's seq, returning only groups that are definitely complete (a group is closed once
    /// a later seq has been seen). Ordered by seq.
    /// </summary>
    private static async Task<List<KeyValuePair<uint, Dictionary<StreamChannel, float[]>>>> CaptureScopeGroupsAsync(int groupCount)
    {
        var device = new SimulatedDevice(randomSeed: 1);

        // Pinned, not inherited from the defaults: these are the *only* parts of the simulator that
        // treat the two channels differently -- the DUT runs on one channel alone (which is what makes
        // the impulse response's L/R reference work), the output stage on channel 0, and crosstalk mixes
        // one into the other. Each is off by default today, so the equality assertion below rested on
        // those defaults rather than on anything it states. If one of them ever changed, this test would
        // fail on *that* assertion, deterministically, with a message that reads exactly like the capture
        // skew it exists to catch. Saying so here means a changed default breaks the simulator's own
        // tests instead. ScopeResidualTests pins its profile for the same kind of reason.
        device.SignalProfile = device.SignalProfile with
        {
            Dut = SimulatedDutModel.StraightWire,
            OutputStage = SimulatedOutputStageModel.None,
            Load = SimulatedLoad.Open,
            CrosstalkEnabled = false,
        };

        await device.ConnectAsync(CancellationToken.None);

        // The same tone on both channels -- ChannelGeneratorConfig per channel, identical contents.
        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, 0.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        // Explicit, and not the default 8 -- see HubCapacity.
        var hub = new SpectrumBroadcastHub(capacityPerClient: HubCapacity);
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            // ProcessRight is off by default (left-only, matching the generator default), and this
            // is the two-channel case by definition. A small FFT and a fast tick so many ticks
            // happen per second: the skew this guards against was a race, and more ticks are more
            // chances for it to land.
            Options = new SpectrumPipelineOptions
            {
                FftSize = 4096, TargetFrameRateHz = 40, DisplayColumns = 200,
                ProcessLeft = true, ProcessRight = true,
            },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(SampleRateHz, channelCount: 2);

        // 512 frames per block, matching Qa40xUsbDevice.TransferChunkFrames -- the skew was exactly
        // one such block, so a test that enqueued the whole window at once could not reproduce it.
        var config = new AcquisitionConfig(SampleRateHz: SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            var open = new Dictionary<uint, Dictionary<StreamChannel, float[]>>();
            var closed = new List<KeyValuePair<uint, Dictionary<StreamChannel, float[]>>>();
            // Which channels produced *any* frame under each seq, scope or not. ProcessChannel's
            // flush-generation check sits ahead of every one of its broadcasts, so a channel either
            // sends its whole set for a tick or sends nothing at all -- which makes "witnessed but no
            // scope frame" proof that the scope frame was evicted in transport rather than never sent.
            var witnessed = new Dictionary<uint, HashSet<StreamChannel>>();
            uint highestSeq = 0;

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (closed.Count < groupCount)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                var header = StreamFrame.DecodeHeader(frame);

                if (!witnessed.TryGetValue(header.Seq, out var channelsSeen))
                    witnessed[header.Seq] = channelsSeen = [];
                channelsSeen.Add(header.Channel);

                // Raw frames only. A residual rides on this same MsgType and differs from the raw frame
                // only by its flag (see ScopeResidualTests, which keys on it), so without this an
                // enabled ScopeResidual would overwrite the raw frame under the same channel key and the
                // equality loop would be comparing residuals. The option is off here; the filter says so.
                if (header.MsgType != StreamMsgType.Scope || header.Flags.HasFlag(StreamFlags.ScopeResidual))
                    continue;

                if (!open.TryGetValue(header.Seq, out var byChannel))
                    open[header.Seq] = byChannel = [];
                byChannel[header.Channel] = StreamFrame.DecodeScopeSamples(frame);

                // A seq is complete once a later one has appeared -- the same rule the client uses,
                // and it avoids assuming how many channels a tick sends.
                if (header.Seq > highestSeq)
                    highestSeq = header.Seq;

                foreach (uint seq in open.Keys.Where(s => s < highestSeq).OrderBy(s => s).ToList())
                {
                    closed.Add(new KeyValuePair<uint, Dictionary<StreamChannel, float[]>>(seq, open[seq]));
                    open.Remove(seq);
                }
            }

            AssertNothingWasLostInTransport(closed, witnessed);
            return closed;
        }
        finally
        {
            streamCts.Cancel();
            try { await drainTask; } catch (OperationCanceledException) { }
            await device.StopAsync();
            await pipeline.StopAsync();
        }
    }

    /// <summary>
    /// Fails if the hub evicted anything, so that the assertions above can keep meaning what they say.
    /// Both checks exist because both loss shapes were measured against a capacity-8 queue with this
    /// reader parked 30 ms every 5th frame, and because both arrive at the assertions disguised as the
    /// capture skew this file is about -- a missing channel, or a divergence that cannot be explained.
    ///
    /// <para>Whole ticks: <c>ProcessTick</c> allocates one seq per tick that has any pending channel,
    /// every tick here has both (the two ring buffers are appended under one lock, with one discard, so
    /// their IsFull, SamplesWritten and hop clocks are identical, and the only Flush is the one inside
    /// Start, before the worker exists), the scope broadcast is unconditional, and a subscriber's channel
    /// is FIFO -- so a seq that produced nothing at all is a tick that never reached this reader.
    /// Measured: 1,2,4,5,… with seq 3 gone. Eviction is the likely cause and the only one worth a hint,
    /// but not the only one: <c>ProcessTick</c>'s per-channel loop has no <c>catch</c>, so a throw inside
    /// the first channel's analysis also ends a tick silently -- into a <c>NullLogger</c> here. Hence the
    /// message names both rather than asserting the one.</para>
    ///
    /// <para>Half ticks: a channel that contributed some other frame type under a seq but no scope frame
    /// had its scope frame evicted, because that broadcast is unconditional and sits past the only early
    /// return in <c>ProcessChannel</c>. Measured: 11 of 13 seqs holding <c>R:[NoiseFigures]</c> and
    /// nothing more. Note this is the discriminator the test needs and the pipeline cannot trip: a
    /// channel genuinely skipped for a tick leaves *no* frame under that seq, and then the
    /// ContainsKey assertions above are the right ones to fail.</para>
    /// </summary>
    private static void AssertNothingWasLostInTransport(
        List<KeyValuePair<uint, Dictionary<StreamChannel, float[]>>> closed,
        Dictionary<uint, HashSet<StreamChannel>> witnessed)
    {
        for (int i = 1; i < closed.Count; i++)
        {
            uint previous = closed[i - 1].Key, current = closed[i].Key;
            Assert.True(current == previous + 1,
                $"seq {previous} -> {current}: the ticks in between produced no frame at all here, so "
                    + "nothing can be concluded about channel alignment. Either the hub evicted them "
                    + "whole (raise HubCapacity) or a channel's analysis threw and ProcessTick swallowed "
                    + "it -- which this NullLogger discards.");
        }

        foreach (var (seq, byChannel) in closed)
        {
            foreach (var channel in witnessed[seq])
            {
                Assert.True(byChannel.ContainsKey(channel),
                    $"seq {seq}: the {channel} channel's other frames arrived but its scope frame did not, "
                        + "so it was evicted between the hub and this reader rather than never sent. Raise HubCapacity.");
            }
        }
    }
}
