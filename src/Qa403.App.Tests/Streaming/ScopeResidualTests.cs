using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// The THD residual scope frame: the record's tail with its fitted fundamental removed, sent as a
/// second scope frame of the same seq under <see cref="StreamFlags.ScopeResidual"/>. What is left
/// after the fit has to be the DUT's distortion and nothing else -- the simulator's generator
/// harmonic stack (H2 −46 dBc, H3 −60, then −12 dB per order) is a known 0.51 % that the residual's
/// RMS must read back, with the fundamental itself cancelled tens of dB below it.
/// </summary>
public class ScopeResidualTests
{
    private const double SampleRateHz = 48000;
    private const double ToneFreqHz = 1000.0;

    [Fact]
    public async Task TheResidual_IsTheHarmonicStack_WithTheFundamentalGone()
    {
        var groups = await CaptureAsync(groupCount: 3, harmonics: true, residual: true);

        foreach (var (seq, frames) in groups)
        {
            Assert.True(frames.ContainsKey((StreamChannel.Left, false)), $"seq {seq}: no raw left frame");
            Assert.True(frames.ContainsKey((StreamChannel.Left, true)), $"seq {seq}: no residual left frame");
            Assert.True(frames.ContainsKey((StreamChannel.Right, true)), $"seq {seq}: no residual right frame");

            float[] raw = frames[(StreamChannel.Left, false)];
            float[] res = frames[(StreamChannel.Left, true)];
            Assert.Equal(raw.Length, res.Length);

            double rawRms = Rms(raw);
            double resRms = Rms(res);

            // 0 dBV tone: 1 Vrms raw. The stack is 0.51 % of that.
            Assert.InRange(rawRms, 0.97, 1.03);
            double expected = rawRms * StackRatio();
            Assert.InRange(resRms, expected * 0.85, expected * 1.15);
        }
    }

    [Fact]
    public async Task WithNothingToRemoveButTheTone_TheResidualIsTheFloor()
    {
        var groups = await CaptureAsync(groupCount: 2, harmonics: false, residual: true);

        foreach (var (_, frames) in groups)
        {
            double rawRms = Rms(frames[(StreamChannel.Left, false)]);
            double resRms = Rms(frames[(StreamChannel.Left, true)]);
            double db = 20.0 * Math.Log10(resRms / rawRms);
            Assert.True(db < -100.0, $"residual sits at {db:0.#} dB re the raw record with no distortion to show");
        }
    }

    [Fact]
    public async Task WithTheOptionOff_NoResidualFrameIsSent()
    {
        var groups = await CaptureAsync(groupCount: 2, harmonics: true, residual: false);

        foreach (var (seq, frames) in groups)
        {
            Assert.True(frames.ContainsKey((StreamChannel.Left, false)), $"seq {seq}: no raw left frame");
            Assert.DoesNotContain(frames.Keys, k => k.Residual);
        }
    }

    private static double Rms(float[] x)
    {
        double sum = 0.0;
        foreach (float v in x)
            sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }

    /// <summary>The generator stack's RMS relative to the fundamental: √Σ10^(dBc/10).</summary>
    private static double StackRatio() =>
        Math.Sqrt(SimulatedSignalProfile.DefaultTriodeHarmonicDbc.Sum(dbc => Math.Pow(10.0, dbc / 10.0)));

    /// <summary>
    /// Runs the pipeline against the simulator and collects scope frames grouped by seq, keyed by
    /// (channel, residual flag); only groups closed by a later seq are returned.
    /// </summary>
    private static async Task<List<(uint Seq, Dictionary<(StreamChannel Channel, bool Residual), float[]> Frames)>> CaptureAsync(
        int groupCount, bool harmonics, bool residual)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with { HarmonicsEnabled = harmonics, NoiseEnabled = false, HumEnabled = false };
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, 0.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = 4096, TargetFrameRateHz = 40, DisplayColumns = 200,
                ProcessLeft = true, ProcessRight = true, ScopeResidual = residual,
            },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(SampleRateHz, channelCount: 2);

        var config = new AcquisitionConfig(SampleRateHz: SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            var open = new Dictionary<uint, Dictionary<(StreamChannel, bool), float[]>>();
            var closed = new List<(uint, Dictionary<(StreamChannel, bool), float[]>)>();
            uint highestSeq = 0;

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (closed.Count < groupCount)
            {
                byte[] frame = await reader.ReadAsync(readCts.Token);
                var header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType != StreamMsgType.Scope)
                    continue;

                if (!open.TryGetValue(header.Seq, out var byKey))
                    open[header.Seq] = byKey = [];
                byKey[(header.Channel, header.Flags.HasFlag(StreamFlags.ScopeResidual))] = StreamFrame.DecodeScopeSamples(frame);

                if (header.Seq > highestSeq)
                    highestSeq = header.Seq;

                foreach (uint seq in open.Keys.Where(s => s < highestSeq).OrderBy(s => s).ToList())
                {
                    closed.Add((seq, open[seq]));
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
