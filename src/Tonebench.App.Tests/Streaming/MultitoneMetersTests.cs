using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// <see cref="SpectrumPipelineOptions.Multitone"/> end to end, the way the app sets it: the session
/// derives the spec from the effective (snapped) comb on every generator configure, the pipeline
/// measures the gaps between the tones, and the result rides its own frame. The simulator's
/// single-ended triode is on the left channel only, so the left's TD+N has to sit well above the
/// right's, which sees the generator's clean comb.
/// </summary>
public class MultitoneMetersTests
{
    private const double SampleRateHz = 48_000.0;
    private const int FftSize = 32768;

    // Quiet on purpose: the simulator sums the comb's cosines in phase, so 24 tones at -26 dBV
    // total peak at 0.34 V. At -6 dBV the same comb peaks at 3.4 V and the simulated ADC's soft
    // clip distorts both channels alike -- measured, TD+N -22.8 dB on each -- which is the crest
    // factor of a multitone stimulus, not a bug in either the simulator or the analyzer.
    private static readonly MultitoneSettings Comb = new() { TonesPerOctave = 2, TotalRmsDbv = -26.0 };

    private static ChannelGeneratorConfig CombChannel() =>
        new(true, MultitoneGenerator.Build(Comb)) { Kind = GeneratorKind.Multitone, Multitone = Comb };

    [Fact]
    public async Task TheSession_SetsTheSpecFromTheSnappedComb_AndClearsItForAnyOtherStimulus()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = FftSize, TargetFrameRateHz = 40, DisplayColumns = 200 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);
        var hint = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);

        await session.ConfigureGeneratorAsync(new GeneratorConfig(CombChannel(), ChannelGeneratorConfig.Off), CancellationToken.None, hint);

        var spec = pipeline.Options.Multitone;
        Assert.NotNull(spec);
        Assert.Equal(MultitoneGenerator.Build(Comb).Count, spec.ToneFreqsHz.Count);
        // Snapped: every frequency is an integer bin at this FFT size and rate.
        double binHz = SampleRateHz / FftSize;
        Assert.All(spec.ToneFreqsHz, f => Assert.Equal(Math.Round(f / binHz), f / binHz, 6));

        // A comb on the right alone counts too.
        await session.ConfigureGeneratorAsync(new GeneratorConfig(ChannelGeneratorConfig.Off, CombChannel()), CancellationToken.None, hint);
        Assert.NotNull(pipeline.Options.Multitone);

        // A sine clears it; so does silence.
        var sine = new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -10.0)]);
        await session.ConfigureGeneratorAsync(new GeneratorConfig(sine, ChannelGeneratorConfig.Off), CancellationToken.None, hint);
        Assert.Null(pipeline.Options.Multitone);
        await session.ConfigureGeneratorAsync(new GeneratorConfig(CombChannel(), ChannelGeneratorConfig.Off), CancellationToken.None, hint);
        Assert.NotNull(pipeline.Options.Multitone);
        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None, hint);
        Assert.Null(pipeline.Options.Multitone);

        await session.ShutdownAsync();
    }

    [Fact]
    public async Task AgainstTheTriode_TheLeftsTdnSitsWellAboveTheRights_AndTheFrameCarriesEveryTone()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with { Dut = SimulatedDutModel.TriodeSingleEnded, HumEnabled = false };
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize, TargetFrameRateHz = 40, DisplayColumns = 200,
                Averaging = AveragingMode.Off, ProcessLeft = true, ProcessRight = true,
            },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);
        var acquisition = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);

        var (_, reader) = hub.Subscribe();
        try
        {
            // The same comb on both generator channels: the right input sees it clean, the left
            // through the triode.
            await session.ConfigureGeneratorAsync(new GeneratorConfig(CombChannel(), CombChannel()), CancellationToken.None, acquisition);
            await session.StartAcquisitionAsync(acquisition, CancellationToken.None);

            // The first record or two may straddle the stimulus change (more of them under a loaded
            // test host), so this waits for a tick whose *right* channel reads the comb at the
            // level it was played -- steady state -- and takes both channels of that tick.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var bySeq = new Dictionary<uint, Dictionary<StreamChannel, DecodedMultitone>>();
            Dictionary<StreamChannel, DecodedMultitone>? settled = null;
            while (settled is null)
            {
                byte[] frame = await reader.ReadAsync(timeout.Token);
                var header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType != StreamMsgType.Multitone)
                    continue;
                if (!bySeq.TryGetValue(header.Seq, out var tick))
                    bySeq[header.Seq] = tick = [];
                tick[header.Channel] = StreamFrame.DecodeMultitone(frame);
                if (tick.Count == 2 && tick[StreamChannel.Right].CombDbv is > -27.0f and < -25.0f)
                    settled = tick;
            }

            var left = settled[StreamChannel.Left];
            var right = settled[StreamChannel.Right];
            // The bottom of the comb sits a bin or two apart at this resolution, where the peak
            // search walks two neighbours onto one bin and they merge -- every other tone is there.
            int expectedTones = pipeline.Options.Multitone!.ToneFreqsHz.Distinct().Count();
            Assert.InRange(left.Tones.Length, expectedTones - 3, expectedTones);
            Assert.Equal(left.Tones.Length - 1, left.Bands.Length);
            Assert.True(left.ResolvedBands > 0, "no gap was resolved at this FFT size");

            double leftDb = 20.0 * Math.Log10(left.Tdn);
            double rightDb = 20.0 * Math.Log10(right.Tdn);
            string debug = string.Join(" | ", right.Tones.Select((t, i) => $"t{i} {t.FreqHz:0.##}Hz {t.Dbv:0.#}dBV" + (i < right.Bands.Length ? $" -> b{i} {(right.Bands[i].Resolved ? $"{right.Bands[i].HighestFreqHz:0.##}Hz {right.Bands[i].HighestDbr:0.#} dn {right.Bands[i].DnDbr:0.#}" : "unres")}" : "")));
            Assert.True(leftDb > rightDb + 6.0, $"left TD+N {leftDb:0.#} dB, right {rightDb:0.#} dB :: {debug}");

            // The comb reads back at the level it was played on the left too: -26 dBV total.
            Assert.InRange(left.CombDbv, -27.0f, -25.0f);

            // Every resolved gap's highest line is at least its D+N's share, and lies between its tones.
            for (int i = 0; i < left.Bands.Length; i++)
            {
                var band = left.Bands[i];
                if (!band.Resolved)
                    continue;
                Assert.InRange(band.HighestFreqHz, left.Tones[i].FreqHz, left.Tones[i + 1].FreqHz);
                Assert.True(band.HighestDbr <= band.DnDbr + 0.01, $"gap {i}: highest {band.HighestDbr:0.#} above its own D+N {band.DnDbr:0.#}");
            }

            // The retained snapshot carries the same result, which is what a save reads.
            var snapshot = pipeline.LatestSnapshots().Single(s => s.Channel == StreamChannel.Left);
            Assert.NotNull(snapshot.Meters?.Multitone);
            var rows = MeasurementSaveService.MultitoneMetrics(0, snapshot.Meters.Multitone).ToList();
            Assert.Contains(rows, r => r.Name == "tdn_percent");
            Assert.All(rows, r => Assert.True(double.IsFinite(r.Value), $"{r.Name} is not finite"));
        }
        finally
        {
            await session.ShutdownAsync();
        }
    }
}
