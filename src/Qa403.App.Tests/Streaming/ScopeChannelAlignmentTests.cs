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
        await device.ConnectAsync(CancellationToken.None);

        // The same tone on both channels -- ChannelGeneratorConfig per channel, identical contents.
        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, 0.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
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
            uint highestSeq = 0;

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (closed.Count < groupCount)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                var header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType != StreamMsgType.Scope)
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
}
