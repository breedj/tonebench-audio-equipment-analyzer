using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The external input gain (<see cref="ExternalGainSetting"/>) is undone at the pipeline's door,
/// per channel, so every level downstream is the DUT's -- while the one number input ranging reads,
/// <see cref="SpectrumMeters.AnalyzerPeakDbv"/>, stays the analyzer's. A gain that reached the
/// ranging path would park a divider-fed input 20 dB too wide; one that missed the meters would
/// make the divider invisible, which is the state the app was in before the setting existed.
/// </summary>
public class ExternalGainTests
{
    private const double SampleRateHz = 48_000.0;
    private const int FftSize = 4096;

    /// <summary>Bin 85 at 48 kHz / 4096: coherent, so the skirt sum reads the tone's exact level.</summary>
    private const double ToneHz = 85 * SampleRateHz / FftSize;

    /// <summary>One coherent ~1 kHz, 1 V peak (−3.01 dBV) tone on both channels through the pipeline with the given gains.</summary>
    private static async Task<(SpectrumMeters Left, SpectrumMeters Right)> MetersWithGainAsync(double leftDb, double rightDb)
    {
        var gain = new ExternalGainSetting { LeftDb = leftDb, RightDb = rightDb };
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance, externalGain: gain)
        {
            Options = new SpectrumPipelineOptions { FftSize = FftSize, TargetFrameRateHz = 40, DisplayColumns = 200, ProcessLeft = true, ProcessRight = true },
        };

        var (_, reader) = hub.Subscribe();
        pipeline.Start(SampleRateHz, channelCount: 2);
        try
        {
            const int blockFrames = 512;
            long totalFrames = 0;
            while (totalFrames < FftSize * 2)
            {
                float[] buffer = ArrayPool<float>.Shared.Rent(blockFrames * 2);
                for (int i = 0; i < blockFrames; i++)
                {
                    double t = (totalFrames + i) / SampleRateHz;
                    float v = (float)Math.Cos(2.0 * Math.PI * ToneHz * t);
                    buffer[2 * i] = v;
                    buffer[2 * i + 1] = v;
                }

                pipeline.Enqueue(new PcmBlock(buffer, blockFrames * 2, SampleRateHz, channelCount: 2, sequenceNumber: totalFrames, timestampTicks: 0));
                totalFrames += blockFrames;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            SpectrumSnapshot[] snapshots;
            do
            {
                await reader.ReadAsync(cts.Token);
                snapshots = pipeline.LatestSnapshots();
            }
            while (snapshots.Length < 2 || snapshots.Any(s => s.Meters is null));

            return (snapshots[0].Meters!, snapshots[1].Meters!);
        }
        finally
        {
            await pipeline.StopAsync();
        }
    }

    [Fact]
    public async Task ADividerOnTheLeftInput_ReadsTheDutsLevelOnTheLeft_AndLeavesTheRightAlone()
    {
        var (left, right) = await MetersWithGainAsync(leftDb: -20.0, rightDb: 0.0);

        // −3.01 dBV reached the analyzer; the DUT put out 20 dB more than that.
        Assert.InRange(left.FundamentalDbv, 16.5f, 17.5f);
        Assert.InRange(right.FundamentalDbv, -3.5f, -2.5f);
        Assert.InRange(left.PeakDbv, 16.5f, 17.5f);
        Assert.InRange(right.PeakDbv, -3.5f, -2.5f);
    }

    [Fact]
    public async Task TheAnalyzerPeak_IsWhatReachedTheInput_WhateverTheGain()
    {
        var (left, right) = await MetersWithGainAsync(leftDb: -20.0, rightDb: +6.0);

        Assert.InRange(left.AnalyzerPeakDbv, -3.5f, -2.5f);
        Assert.InRange(right.AnalyzerPeakDbv, -3.5f, -2.5f);
        Assert.Equal(-20f, left.ExternalGainDb);
        Assert.Equal(6f, right.ExternalGainDb);

        // The two ranging helpers read the analyzer's peak, so a probe through the divider ranges
        // for what the analyzer sees, not for the DUT's 17 dBV.
        var snapshots = new[]
        {
            new SpectrumSnapshot(StreamChannel.Left, 1, DateTimeOffset.UtcNow, SampleRateHz, SampleRateHz / FftSize, FftSize, Tonebench.Dsp.WindowType.Hann, default, [], left),
            new SpectrumSnapshot(StreamChannel.Right, 1, DateTimeOffset.UtcNow, SampleRateHz, SampleRateHz / FftSize, FftSize, Tonebench.Dsp.WindowType.Hann, default, [], right),
        };
        Assert.InRange(SweepCaptureSupport.MeasuredPeakDbv(snapshots), -3.5, -2.5);
        Assert.InRange(ResponseCaptureSupport.PeakDbv(snapshots), -3.5, -2.5);
    }

    [Fact]
    public async Task NoGain_ChangesNothing()
    {
        var (left, right) = await MetersWithGainAsync(0.0, 0.0);

        Assert.InRange(left.FundamentalDbv, -3.5f, -2.5f);
        Assert.InRange(right.FundamentalDbv, -3.5f, -2.5f);
        Assert.Equal(left.PeakDbv, left.AnalyzerPeakDbv);
    }

    [Fact]
    public void ToAnalyzerDbv_PutsTheChannelsGainBack_AndTheWiderOneWithoutAChannel()
    {
        var gain = new ExternalGainSetting { LeftDb = -20.0, RightDb = -6.0 };
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance, externalGain: gain);
        var session = new DeviceSessionService(
            new Tonebench.Device.Simulation.SimulatedDevice(randomSeed: 1), pipeline, new CaptureDelaySetting(),
            NullLogger<DeviceSessionService>.Instance, gain);

        // A 0 dBV target at the DUT is −20 dBV at the left input, −6 at the right.
        Assert.Equal(-20.0, session.ToAnalyzerDbv(0.0, StreamChannel.Left), precision: 9);
        Assert.Equal(-6.0, session.ToAnalyzerDbv(0.0, StreamChannel.Right), precision: 9);
        // Without a channel: the louder of the two at the analyzer decides, since one range serves both.
        Assert.Equal(-6.0, session.ToAnalyzerDbv(0.0), precision: 9);
        Assert.Equal(-20.0, session.ExternalGainLeftDb);
        Assert.Equal(-6.0, session.ExternalGainRightDb);
    }

    [Fact]
    public void TheSetting_RefusesAnythingPastItsBound()
    {
        var gain = new ExternalGainSetting();
        Assert.Throws<ArgumentOutOfRangeException>(() => gain.LeftDb = 61.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => gain.RightDb = double.NaN);
        gain.LeftDb = -20.0;
        Assert.Equal(10.0, gain.LinearScale(0), precision: 9);
        Assert.Equal(1.0, gain.LinearScale(1), precision: 9);
        Assert.True(gain.Any);
    }
}
