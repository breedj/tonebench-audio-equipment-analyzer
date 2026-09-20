using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.Simulation;
using Qa403.Dsp;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// <see cref="SpectrumPipelineOptions.Weighting"/> and the <see cref="NoiseFigures"/> frame end to
/// end through the simulator: what a weighting changes (the noise-integrated figures and the
/// trace), what it must not (the fundamental, THD), and that the figures exist without a tone.
/// </summary>
public class WeightedNoiseFiguresTests
{
    private const double SampleRateHz = 192_000.0;
    private const int FftSize = 32768;
    private const double BinSizeHz = SampleRateHz / FftSize;

    private static readonly WeightingSpec AWeighted = new(WeightingStandard.A);

    [Fact]
    public async Task NoiseFigures_AreBroadcastEveryTickWithTheWindowsEnbw()
    {
        var capture = await CaptureAsync(p => p, toneDbv: -20.0, WeightingSpec.None);

        Assert.Equal(1.5 * BinSizeHz, capture.Noise.EnbwHz, 2);
        Assert.Equal(20.0f, capture.Noise.MeasurementLoFreqHz);
        Assert.Equal(20000.0f, capture.Noise.MeasurementHiFreqHz);
        Assert.Equal(WeightingSpec.None, capture.Noise.Weighting);
        Assert.NotNull(capture.Snapshot.Noise);
    }

    [Fact]
    public async Task NoiseFigures_AreBroadcastWithTheGeneratorOff_WhereThereIsNoFundamentalToReport()
    {
        // No tone, no hum, only the white floor: the noise frame still arrives, with the density
        // at the simulator's own figure, whether or not the meters found something to call a
        // fundamental in that record.
        var capture = await CaptureAsync(
            p => p with { HumEnabled = false, PinkNoiseEnabled = false, NoiseDensityDbvPerRootHz = -150.0 },
            toneDbv: null, WeightingSpec.None);

        Assert.InRange(capture.Noise.NoiseDensityDbvPerRootHz, -150.5, -149.5);
        // 20 Hz-20 kHz of a -150 dBV/sqrt(Hz) floor is -150 + 10*log10(19980) = -107.0 dBV.
        Assert.InRange(capture.Noise.BandRmsDbv, -107.5, -106.5);
    }

    [Fact]
    public async Task BandRms_OfAnInBandTone_MatchesTheTimeDomainRms()
    {
        var capture = await CaptureAsync(
            p => p with { HumEnabled = false, NoiseEnabled = false }, toneDbv: -20.0, WeightingSpec.None);

        Assert.NotNull(capture.Meters);
        Assert.InRange(capture.Noise.BandRmsDbv, capture.Meters.Value.TotalRmsDbv - 0.1, capture.Meters.Value.TotalRmsDbv + 0.1);
        Assert.InRange(capture.Noise.BandRmsDbv, -20.1, -19.9);
    }

    [Fact]
    public async Task NoiseDensity_OfTheWhiteFloor_UnderAWeighting_DropsByTheCurvesBandMean()
    {
        // A tone so the notch sits somewhere known (0.5 octave either side of 1 kHz), white noise
        // only. The A-weighted density must drop by the mean power gain of the curve over exactly
        // the bins the density is taken from -- the band less the notch and the harmonics' lobes.
        SimulatedSignalProfile Profile(SimulatedSignalProfile p) =>
            p with { HumEnabled = false, PinkNoiseEnabled = false, NoiseDensityDbvPerRootHz = -150.0 };
        var plain = await CaptureAsync(Profile, toneDbv: -20.0, WeightingSpec.None);
        var weighted = await CaptureAsync(Profile, toneDbv: -20.0, AWeighted);

        Assert.NotNull(plain.Meters);
        Assert.InRange(plain.Noise.NoiseDensityDbvPerRootHz, -150.5, -149.5);

        Assert.NotNull(plain.Snapshot.Meters);
        double expectedDropDb = 10.0 * Math.Log10(MeanPowerGain(AWeighting.Instance, plain.Snapshot.Meters.Harmonics));
        Assert.InRange(expectedDropDb, -3.0, -1.5);
        double actualDropDb = weighted.Noise.NoiseDensityDbvPerRootHz - plain.Noise.NoiseDensityDbvPerRootHz;
        Assert.InRange(actualDropDb, expectedDropDb - 0.3, expectedDropDb + 0.3);
        Assert.Equal(AWeighted, weighted.Noise.Weighting);
    }

    [Fact]
    public async Task NoiseFloor_UnderAWeighting_DropsByTheHumStacksWeightedSum()
    {
        // Hum only (no white floor) under a 1 kHz tone: the noise floor is the hum stack, and under
        // A it drops by the power-weighted mean of the curve at 50/100/150 Hz -- about 22 dB, not
        // the 30 dB of the 50 Hz fundamental alone.
        SimulatedSignalProfile Profile(SimulatedSignalProfile p) => p with { HumEnabled = true, NoiseEnabled = false };
        var plain = await CaptureAsync(Profile, toneDbv: -10.0, WeightingSpec.None);
        var weighted = await CaptureAsync(Profile, toneDbv: -10.0, AWeighted);

        Assert.NotNull(plain.Meters);
        Assert.NotNull(weighted.Meters);

        var profile = new SimulatedDevice().SignalProfile;
        double humSum = 0.0, weightedSum = 0.0;
        for (int h = 1; h <= profile.HumHarmonicCount; h++)
        {
            double levelDb = profile.HumLevelDbv - 6.0 * (h - 1);
            double power = Math.Pow(10.0, levelDb / 10.0);
            humSum += power;
            weightedSum += power * Math.Pow(10.0, AWeighting.Instance.GainDb(profile.HumFundamentalHz * h) / 10.0);
        }
        double expectedDropDb = 10.0 * Math.Log10(weightedSum / humSum);
        Assert.InRange(expectedDropDb, -26.0, -20.0);

        double actualDropDb = weighted.Meters.Value.NoiseFloorDbv - plain.Meters.Value.NoiseFloorDbv;
        Assert.InRange(actualDropDb, expectedDropDb - 0.5, expectedDropDb + 0.5);

        // SNR is fundamental over that same floor, so it rises by the same amount.
        Assert.InRange(weighted.Meters.Value.SnrDb - plain.Meters.Value.SnrDb, -expectedDropDb - 0.5, -expectedDropDb + 0.5);
    }

    [Fact]
    public async Task Fundamental_AndThd_AreUnchangedByWeighting()
    {
        // A tone at 5 kHz, where A is +0.5 dB and RIAA playback is -8.2 dB: neither may move the
        // fundamental's level or THD, which are measured on the unweighted PSD.
        var plain = await CaptureAsync(p => p, toneDbv: -10.0, WeightingSpec.None, toneHz: 5000.0);
        var a = await CaptureAsync(p => p, toneDbv: -10.0, AWeighted, toneHz: 5000.0);
        var riaa = await CaptureAsync(p => p, toneDbv: -10.0, new WeightingSpec(WeightingStandard.RiaaPlayback), toneHz: 5000.0);

        Assert.NotNull(plain.Meters);
        Assert.NotNull(a.Meters);
        Assert.NotNull(riaa.Meters);
        foreach (var other in new[] { a.Meters.Value, riaa.Meters.Value })
        {
            Assert.Equal(plain.Meters.Value.FundamentalDbv, other.FundamentalDbv, 2);
            Assert.Equal(plain.Meters.Value.FundamentalFreqHz, other.FundamentalFreqHz);
            Assert.Equal(plain.Meters.Value.ThdPercent, other.ThdPercent, 3);
            Assert.Equal(plain.Meters.Value.PeakDbv, other.PeakDbv, 2);
        }
        // The displayed trace, on the other hand, is weighted: its 5 kHz peak sits at the curve's value.
        double riaaAt5kDb = RiaaWeighting.Playback.GainDb(5000.0);
        Assert.InRange(riaaAt5kDb, -8.5, -7.9);
        Assert.InRange(riaa.SpectrumPeakDbv - plain.SpectrumPeakDbv, riaaAt5kDb - 0.3, riaaAt5kDb + 0.3);
    }

    [Fact]
    public async Task Snapshot_RecordsTheWeightingThatProducedIt()
    {
        var capture = await CaptureAsync(p => p, toneDbv: -10.0, new WeightingSpec(WeightingStandard.ItuR468, User1: true));

        Assert.Equal(new WeightingSpec(WeightingStandard.ItuR468, User1: true), capture.Snapshot.Weighting);
        Assert.Equal(new WeightingSpec(WeightingStandard.ItuR468, User1: true), capture.Noise.Weighting);
    }

    [Fact]
    public async Task UserTable_CombinesWithTheStandardCurve()
    {
        // A flat +6 dB attenuation table in slot 1 on top of A: the density drops by the curve's
        // band mean and then another 6 dB.
        SimulatedSignalProfile Profile(SimulatedSignalProfile p) =>
            p with { HumEnabled = false, PinkNoiseEnabled = false, NoiseDensityDbvPerRootHz = -150.0 };
        var setting = new UserWeightingSetting();
        setting.Set(1, new UserWeightingTable("flat6", null, [new WeightingPoint(10.0, 6.0), new WeightingPoint(100_000.0, 6.0)], DateTimeOffset.UtcNow));

        var a = await CaptureAsync(Profile, toneDbv: -20.0, AWeighted);
        var aPlusUser = await CaptureAsync(Profile, toneDbv: -20.0, new WeightingSpec(WeightingStandard.A, User1: true), setting);
        var emptySlot = await CaptureAsync(Profile, toneDbv: -20.0, new WeightingSpec(WeightingStandard.A, User2: true), setting);

        Assert.InRange(aPlusUser.Noise.NoiseDensityDbvPerRootHz - a.Noise.NoiseDensityDbvPerRootHz, -6.3, -5.7);
        // A checked slot with nothing in it weighs nothing.
        Assert.InRange(emptySlot.Noise.NoiseDensityDbvPerRootHz - a.Noise.NoiseDensityDbvPerRootHz, -0.3, 0.3);
    }

    /// <summary>The mean power gain of <paramref name="curve"/> over the bins the density is taken from.</summary>
    private static double MeanPowerGain(IWeightingCurve curve, Harmonic[] harmonics)
    {
        var dummy = new double[FftSize / 2 + 1];
        var psd = PsdSpectrum.FromValues(dummy, SampleRateHz, BinSizeHz, Window.Get(WindowType.Hann, FftSize).Sums);
        var band = psd.BandOf(20.0, 20000.0);
        var excluded = new BinRange[harmonics.Length];
        excluded[0] = SpectrumMetrics.FundamentalNotch(psd, harmonics[0]);
        for (int i = 1; i < harmonics.Length; i++)
            excluded[i] = new BinRange(harmonics[i].Bin - HarmonicAnalyzer.DefaultSkirtHalfWidthBins, harmonics[i].Bin + HarmonicAnalyzer.DefaultSkirtHalfWidthBins);

        double sum = 0.0;
        int count = 0;
        for (int k = band.LoBinInclusive; k <= band.HiBinInclusive; k++)
        {
            if (excluded.Any(r => r.Contains(k)))
                continue;
            sum += Math.Pow(10.0, curve.GainDb(k * BinSizeHz) / 10.0);
            count++;
        }
        return sum / count;
    }

    private sealed record Capture(DecodedNoiseFigures Noise, DecodedMeters? Meters, SpectrumSnapshot Snapshot, double SpectrumPeakDbv);

    private static async Task<Capture> CaptureAsync(
        Func<SimulatedSignalProfile, SimulatedSignalProfile> profile, double? toneDbv, WeightingSpec weighting,
        UserWeightingSetting? userWeighting = null, double toneHz = 1000.0)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = profile(device.SignalProfile);
        await device.ConnectAsync(CancellationToken.None);

        double snappedHz = CoherentFrequency.Nearest(toneHz, SampleRateHz, FftSize).FrequencyHz;
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                toneDbv is { } dbv
                    ? new ChannelGeneratorConfig(true, [new GeneratorTone(snappedHz, dbv)])
                    : ChannelGeneratorConfig.Off,
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance, userWeighting: userWeighting)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = FftSize,
                TargetFrameRateHz = 40,
                DisplayColumns = 400,
                Averaging = AveragingMode.Off,
                Weighting = weighting,
            },
        };

        var (clientId, reader) = hub.Subscribe();
        // A viewport makes the hub send this client the spectrum frames.
        hub.SetViewport(clientId, null);
        pipeline.Start(SampleRateHz, channelCount: 1);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        using var streamCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            await foreach (var block in device.StreamAsync(config, streamCts.Token).WithCancellation(streamCts.Token))
                pipeline.Enqueue(block);
        });

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Skip the first tick (a possibly partial record), then collect one whole tick: the noise
            // frame, the spectrum frame and, if there is one, the meters frame under the same seq.
            uint? firstSeq = null;
            DecodedNoiseFigures? noise = null;
            DecodedMeters? meters = null;
            double spectrumPeak = double.NegativeInfinity;
            while (true)
            {
                byte[] frame = await reader.ReadAsync(timeout.Token);
                var header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType == StreamMsgType.NoiseFigures)
                {
                    firstSeq ??= header.Seq;
                    if (header.Seq == firstSeq)
                        continue;
                    if (noise is not null)
                        break;   // the next tick started
                    noise = StreamFrame.DecodeNoiseFigures(frame);
                    continue;
                }

                if (noise is null || header.Seq != noise.Value.Seq)
                    continue;
                if (header.MsgType == StreamMsgType.Meters)
                    meters = StreamFrame.DecodeMeters(frame);
                else if (header.MsgType == StreamMsgType.Spectrum)
                    spectrumPeak = StreamFrame.DecodeSpectrumColumns(frame).Max(c => c.Value.Max);
            }

            var snapshot = pipeline.LatestSnapshots().Single(s => s.Channel == StreamChannel.Left);
            return new Capture(noise!.Value, meters, snapshot, spectrumPeak);
        }
        finally
        {
            streamCts.Cancel();
            await device.StopAsync();
            try
            {
                await drainTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await pipeline.StopAsync();
        }
    }
}
