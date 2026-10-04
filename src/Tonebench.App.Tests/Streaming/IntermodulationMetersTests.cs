using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// <see cref="SpectrumPipelineOptions.Intermodulation"/> end to end: a two-tone stimulus through
/// the simulator's nonlinear DUT, analysed by the pipeline, broadcast as its own frame and retained
/// on the snapshot.
/// </summary>
public class IntermodulationMetersTests
{
    private const double SampleRateHz = 192_000.0;
    private const int FftSize = 32768;

    private static readonly IntermodulationPresetSpec Smpte = IntermodulationPresets.Resolve(IntermodulationPreset.Smpte);

    [Fact]
    public async Task Meters_WithASmpteSpec_ReportAnIntermodulationResultThatScalesWithLevel()
    {
        var quiet = await CaptureAsync(SimulatedDutModel.ClassAbCrossover, sineEquivalentDbv: -20.0, withSpec: true);
        var loud = await CaptureAsync(SimulatedDutModel.ClassAbCrossover, sineEquivalentDbv: 0.0, withSpec: true);

        Assert.NotNull(quiet.Frame);
        Assert.NotNull(loud.Frame);
        Assert.True(quiet.Frame.Value.ImdPercent > 0.0f);
        Assert.True(loud.Frame.Value.ImdPercent > 0.0f);
        // Crossover distortion is a level-dependent nonlinearity, so the two readings must differ
        // by more than noise; which way it goes is the model's business, not this test's.
        Assert.True(
            Math.Abs(20.0 * Math.Log10(loud.Frame.Value.ImdPercent / quiet.Frame.Value.ImdPercent)) > 3.0,
            $"expected IMD to move with level, got {quiet.Frame.Value.ImdPercent} % vs {loud.Frame.Value.ImdPercent} %");

        // The sidebands sit where SMPTE says they do: f2 ± n·f1, with f1/f2 the snapped tones the
        // frame itself reports (the coherent snap moves 60 Hz to a coprime bin).
        var f = loud.Frame.Value;
        Assert.All(f.Products, p => Assert.True(Math.Abs(Math.Abs(p.FreqHz - f.ToneHighHz) - (p.N * f.ToneLowHz)) < 1.0, $"product at {p.FreqHz} Hz"));
        Assert.Equal(IntermodulationMethod.Smpte, loud.Frame.Value.Method);
    }

    [Fact]
    public async Task Meters_WithoutASpec_CarryNoIntermodulation()
    {
        var capture = await CaptureAsync(SimulatedDutModel.ClassAbCrossover, sineEquivalentDbv: 0.0, withSpec: false);

        Assert.Null(capture.Frame);
        Assert.Null(capture.Snapshot.Intermodulation);
    }

    [Fact]
    public async Task Meters_AgainstTheStraightWire_ReadImdBelowMinus90Db()
    {
        var capture = await CaptureAsync(SimulatedDutModel.StraightWire, sineEquivalentDbv: 0.0, withSpec: true);

        Assert.NotNull(capture.Frame);
        double imdDb = 20.0 * Math.Log10(capture.Frame.Value.ImdPercent / 100.0);
        Assert.True(imdDb < -90.0, $"expected the floor, got {imdDb:F1} dB");
    }

    [Fact]
    public async Task Snapshot_CarriesTheSameIntermodulationTheFrameBroadcast()
    {
        var capture = await CaptureAsync(SimulatedDutModel.ClassAbCrossover, sineEquivalentDbv: 0.0, withSpec: true);

        Assert.NotNull(capture.Frame);
        var onSnapshot = capture.Snapshot.Intermodulation;
        Assert.NotNull(onSnapshot);
        // The retained snapshot may already be a later tick than the frame we decoded, so compare
        // what is tick-invariant: the same spec produced both.
        Assert.True(onSnapshot.ImdPercent > 0.0);
        Assert.Equal(onSnapshot.Products.Count, capture.Frame.Value.Products.Length);
        Assert.Equal((float)onSnapshot.ToneLowFreqHz, capture.Frame.Value.ToneLowHz);
        Assert.Equal((float)onSnapshot.ToneHighFreqHz, capture.Frame.Value.ToneHighHz);
        Assert.Equal(onSnapshot.Method, capture.Frame.Value.Method);
    }

    /// <summary>
    /// At 4096 points the 60 Hz spacing is 1.3 bins: the analyzer refuses it, and the pipeline has
    /// to say so on the wire — a client that hears nothing keeps the last reading it got.
    /// </summary>
    [Fact]
    public async Task Meters_WithTonesTooCloseForTheFft_BroadcastAnUnresolvedMarkerInsteadOfNothing()
    {
        var capture = await CaptureAsync(SimulatedDutModel.ClassAbCrossover, sineEquivalentDbv: -6.0, withSpec: true, fftSize: 4096);

        Assert.Null(capture.Frame);
        Assert.True(capture.Unresolved);
        Assert.Null(capture.Snapshot.Intermodulation);
    }

    private sealed record Capture(DecodedIntermodulation? Frame, SpectrumMeters Snapshot, bool Unresolved = false);

    private static async Task<Capture> CaptureAsync(SimulatedDutModel dut, double sineEquivalentDbv, bool withSpec, int fftSize = FftSize)
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with { Dut = dut };
        await device.ConnectAsync(CancellationToken.None);

        var (lowDbv, highDbv) = IntermodulationPresets.ToneLevelsDbv(sineEquivalentDbv, Smpte.LowToHighRatio);
        double lowHz = CoherentFrequency.Nearest(Smpte.LowHz, SampleRateHz, fftSize).FrequencyHz;
        double highHz = CoherentFrequency.Nearest(Smpte.HighHz, SampleRateHz, fftSize).FrequencyHz;
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(lowHz, lowDbv), new GeneratorTone(highHz, highDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions
            {
                FftSize = fftSize,
                TargetFrameRateHz = 40,
                DisplayColumns = 200,
                Averaging = AveragingMode.Off,
                FundamentalMinSearchHz = 30.0,
                Intermodulation = withSpec ? new IntermodulationSpec(lowHz, highHz, Smpte.Method, Smpte.DefaultMaxOrder) : null,
            },
        };

        var (_, reader) = hub.Subscribe();
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
            // Wait for the second meters tick so the first (possibly partial) record is behind us,
            // then take whatever intermodulation frame follows it under the same seq.
            uint? firstSeq = null;
            uint? metersSeq = null;
            DecodedIntermodulation? imdFrame = null;
            bool unresolved = false;
            while (true)
            {
                byte[] frame = await reader.ReadAsync(timeout.Token);
                var header = StreamFrame.DecodeHeader(frame);
                if (header.MsgType == StreamMsgType.Meters)
                {
                    firstSeq ??= header.Seq;
                    if (header.Seq == firstSeq)
                        continue;
                    if (metersSeq is not null)
                        break;   // the next tick started; no IMD frame followed the last meters
                    metersSeq = header.Seq;
                    continue;
                }

                if (header.MsgType == StreamMsgType.Intermodulation && metersSeq is { } seq && header.Seq == seq)
                {
                    if ((header.Flags & StreamFlags.IntermodulationUnresolved) != 0)
                        unresolved = true;
                    else
                        imdFrame = StreamFrame.DecodeIntermodulation(frame);
                    break;
                }
            }

            var snapshot = pipeline.LatestSnapshots().Single(s => s.Channel == StreamChannel.Left);
            Assert.NotNull(snapshot.Meters);
            return new Capture(imdFrame, snapshot.Meters, unresolved);
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
