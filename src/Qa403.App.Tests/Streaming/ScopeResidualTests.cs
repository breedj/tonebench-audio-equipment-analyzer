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

    /// <summary>
    /// With the residual on, a tick broadcasts five frames per channel -- NoiseFigures, Spectrum,
    /// Meters, Scope, Scope+Residual -- so two channels is **ten frames into the hub's default queue of
    /// eight**, and this reader only ever kept up because it usually drains one tick inside the next
    /// tick's period. It does not have to: the queue is <c>DropOldest</c> and says nothing when it
    /// evicts, and a lost residual frame arrives here as "seq N: no residual right frame", i.e. as the
    /// pipeline having failed to send one. See <c>ScopeChannelAlignmentTests.HubCapacity</c>, where the
    /// same arithmetic on four frames per channel was measured losing 35 of 104 frames under a
    /// deliberately parked reader.
    /// </summary>
    private const int HubCapacity = 1024;

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

    /// <summary>
    /// The fit window is bounded, and the bound is what keeps the residual on the same tick as the
    /// raw frame it belongs to. Fitting the whole record took 2.65 s per channel at 1,048,576
    /// points -- against a 50 ms tick and the client's 40 ms scope-group deadline -- so the
    /// oscilloscope alternated between an unpairable residual and a raw frame reported as having
    /// "no fundamental to remove". See SpectrumPipeline.ResidualFitMaxSamples.
    /// </summary>
    [Theory]
    [InlineData(1048576, 192000.0, 1000.0)]
    [InlineData(1048576, 48000.0, 1000.0)]
    [InlineData(131072, 192000.0, 20.0)]
    [InlineData(65536, 48000.0, 19000.0)]
    public void TheFitWindow_IsBounded_AndAlwaysHoldsTheTailItIsSubtractedFrom(int recordLength, double sampleRateHz, double fundamentalFreqHz)
    {
        int count = SpectrumPipeline.ResidualFitCount(recordLength, sampleRateHz, fundamentalFreqHz);

        Assert.InRange(count, 2048, 32768);
        Assert.True(count <= recordLength, $"the window ({count}) reaches past the record ({recordLength})");
    }

    /// <summary>
    /// The one thing the window may not trade away: a fit over much less than a period cannot
    /// separate amplitude, phase and the DC term, and absorbs the distortion it was meant to leave
    /// behind. Measured on a dirty 20 Hz record, the residual read 14.4 dB *under* the harmonic
    /// stack at 0.4 periods and 2.7 dB under at 0.9, reaching 0.2 dB from 3.4 periods up.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(50.0)]
    [InlineData(1000.0)]
    public void TheFitWindow_HoldsSeveralWholePeriodsOfTheFundamental(double fundamentalFreqHz)
    {
        const double sampleRateHz = 192000.0;
        int count = SpectrumPipeline.ResidualFitCount(1048576, sampleRateHz, fundamentalFreqHz);

        double periods = count * fundamentalFreqHz / sampleRateHz;
        Assert.True(periods >= 3.0, $"{fundamentalFreqHz} Hz gets only {periods:0.0} periods of fit window");
    }

    /// <summary>A record shorter than the window is the whole window there is.</summary>
    [Fact]
    public void TheFitWindow_OnARecordShorterThanItself_IsTheWholeRecord()
    {
        Assert.Equal(4096, SpectrumPipeline.ResidualFitCount(4096, 48000.0, 1000.0));
    }

    /// <summary>
    /// The bound's other half: cancellation must survive it. At 65536 points the fit sees 8192 --
    /// an eighth of the record -- and the fundamental still has to go, as
    /// <see cref="WithNothingToRemoveButTheTone_TheResidualIsTheFloor"/> demands at 4096 where the
    /// window was the whole record.
    /// </summary>
    [Fact]
    public async Task OverARecordFarLongerThanTheFitWindow_TheFundamentalStillCancels()
    {
        Assert.True(SpectrumPipeline.ResidualFitCount(65536, SampleRateHz, ToneFreqHz) < 65536, "the window must be shorter than the record for this to test anything");

        var groups = await CaptureAsync(groupCount: 1, harmonics: false, residual: true, fftSize: 65536);

        foreach (var (_, frames) in groups)
        {
            double rawRms = Rms(frames[(StreamChannel.Left, false)]);
            double resRms = Rms(frames[(StreamChannel.Left, true)]);
            double db = 20.0 * Math.Log10(resRms / rawRms);
            Assert.True(db < -100.0, $"residual sits at {db:0.#} dB re the raw record with no distortion to show");
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
        int groupCount, bool harmonics, bool residual, int fftSize = 4096)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with { HarmonicsEnabled = harmonics, NoiseEnabled = false, HumEnabled = false };
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, 0.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        // Explicit, and not the default 8 -- see HubCapacity.
        var hub = new SpectrumBroadcastHub(capacityPerClient: HubCapacity);
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = fftSize, TargetFrameRateHz = 40, DisplayColumns = 200,
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
