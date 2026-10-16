using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Streaming;
using Tonebench.Device;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The retained snapshot follows the measurement on screen: each one's capture is parked when it is
/// switched away from and handed back when it is returned to, so a project with two FFT
/// measurements keeps both captures instead of losing the first to the second's selection.
///
/// <para>These assert the <b>snapshot</b>, which is what <c>Save measurement result</c> writes —
/// the client keeps its own copy of the drawn frames (App.vue's swapFftDisplay), and a restored
/// display over the wrong snapshot would be the 2026-09-27 bug again: Save storing another
/// measurement's capture under this one's name.</para>
/// </summary>
public class SpectrumRetentionOwnerTests
{
    private const int FftSize = 4096;
    private const double SampleRateHz = 48000.0;

    /// <summary>One bin, i.e. how close a peak search can get to a tone that is not bin-centred.</summary>
    private const double BinHz = SampleRateHz / FftSize;

    private static SpectrumPipeline NewPipeline() =>
        new(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = FftSize, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };

    /// <summary>
    /// One whole record of a tone at <paramref name="freqHz"/>, run through the pipeline and left
    /// in the retained slot — a complete acquisition, started and stopped, which is the state every
    /// measurement switch happens in (selectType stops everything first). Stopping also keeps the
    /// worker from re-filling a slot the next switch has just emptied, from a ring buffer that
    /// still holds this record.
    /// </summary>
    private static async Task CaptureAsync(SpectrumPipeline pipeline, double freqHz)
    {
        pipeline.Start(SampleRateHz, channelCount: 1);
        float[] buffer = ArrayPool<float>.Shared.Rent(FftSize);
        for (int i = 0; i < FftSize; i++)
            buffer[i] = (float)Math.Sin(2.0 * Math.PI * freqHz * i / SampleRateHz);

        pipeline.Enqueue(new PcmBlock(buffer, FftSize, SampleRateHz, channelCount: 1, sequenceNumber: 0, timestampTicks: 0));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (pipeline.LatestSnapshots().Length == 0)
            await Task.Delay(5, cts.Token);

        await pipeline.StopAsync();
    }

    /// <summary>The loudest bin of the retained left channel, in Hz — which tone this snapshot holds.</summary>
    private static double RetainedToneHz(SpectrumPipeline pipeline)
    {
        var snapshot = Assert.Single(pipeline.LatestSnapshots());
        double best = double.NegativeInfinity;
        int bestBin = 0;
        for (int bin = 1; bin < snapshot.Dbv.Length; bin++)
        {
            if (snapshot.Dbv[bin] > best)
            {
                best = snapshot.Dbv[bin];
                bestBin = bin;
            }
        }

        return bestBin * snapshot.BinSizeHz;
    }

    [Fact]
    public void ANewPipeline_BelongsToNobody()
    {
        Assert.Null(NewPipeline().RetentionOwner);
    }

    [Fact]
    public void SwitchingToTheSameOwner_ChangesNothing()
    {
        var pipeline = NewPipeline();

        Assert.True(pipeline.SwitchRetentionOwner("1:1"));
        Assert.False(pipeline.SwitchRetentionOwner("1:1"));
    }

    [Fact]
    public async Task AMeasurementSwitchedAwayFromAndBackTo_StillHoldsItsOwnCapture()
    {
        var pipeline = NewPipeline();
        pipeline.SwitchRetentionOwner("1:1");
        await CaptureAsync(pipeline, 1000.0);
        Assert.Equal(1000.0, RetainedToneHz(pipeline), BinHz);

        // The second measurement arrives to an empty slot rather than to the first one's capture --
        // which is the whole reason the client used to clear this on every switch.
        pipeline.SwitchRetentionOwner("1:2");
        Assert.Empty(pipeline.LatestSnapshots());
        await CaptureAsync(pipeline, 3000.0);
        Assert.Equal(3000.0, RetainedToneHz(pipeline), BinHz);

        pipeline.SwitchRetentionOwner("1:1");
        Assert.Equal(1000.0, RetainedToneHz(pipeline), BinHz);
        pipeline.SwitchRetentionOwner("1:2");
        Assert.Equal(3000.0, RetainedToneHz(pipeline), BinHz);
    }

    /// <summary>
    /// The case the breadth of the rule exists for: a sweep selected between two FFT measurements
    /// drives the same pipeline and writes the same snapshot, so its own run must not land in the
    /// measurement that was on screen before it.
    /// </summary>
    [Fact]
    public async Task ARunUnderAnotherOwner_DoesNotReachTheMeasurementBeforeIt()
    {
        var pipeline = NewPipeline();
        pipeline.SwitchRetentionOwner("1:1");
        await CaptureAsync(pipeline, 1000.0);

        pipeline.SwitchRetentionOwner("1:9");
        await CaptureAsync(pipeline, 7000.0);

        pipeline.SwitchRetentionOwner("1:1");
        Assert.Equal(1000.0, RetainedToneHz(pipeline), BinHz);
    }

    /// <summary>
    /// A capture made with no measurement open belongs to the first one that claims it — the same
    /// rule the client applies to an unowned display, and what keeps a reload mid-run re-attaching
    /// instead of throwing the capture away.
    /// </summary>
    [Fact]
    public async Task ACaptureMadeWithNoOwner_IsAdoptedByTheFirstMeasurementToClaimIt()
    {
        var pipeline = NewPipeline();
        await CaptureAsync(pipeline, 1000.0);

        Assert.True(pipeline.SwitchRetentionOwner("1:1"));
        Assert.Equal(1000.0, RetainedToneHz(pipeline), BinHz);
    }

    /// <summary>
    /// ... but a measurement with a capture of its own gets that one back rather than the unowned
    /// slot, which is nobody's.
    /// </summary>
    [Fact]
    public async Task AMeasurementWithAParkedCapture_PrefersItToAnUnownedOne()
    {
        var pipeline = NewPipeline();
        pipeline.SwitchRetentionOwner("1:1");
        await CaptureAsync(pipeline, 1000.0);

        pipeline.SwitchRetentionOwner(null);
        await CaptureAsync(pipeline, 5000.0);

        pipeline.SwitchRetentionOwner("1:1");
        Assert.Equal(1000.0, RetainedToneHz(pipeline), BinHz);
    }

    /// <summary>
    /// Parking must take the slot's objects out of circulation, not copy a reference: the retained
    /// slot is mutated in place and its arrays reused, so the next capture would otherwise rewrite
    /// what was just parked.
    /// </summary>
    [Fact]
    public async Task AParkedCapture_IsNotRewrittenByTheNextOnesBins()
    {
        var pipeline = NewPipeline();
        pipeline.SwitchRetentionOwner("1:1");
        await CaptureAsync(pipeline, 1000.0);
        var parked = Assert.Single(pipeline.LatestSnapshots()).Dbv.ToArray();

        pipeline.SwitchRetentionOwner("1:2");
        await CaptureAsync(pipeline, 3000.0);

        pipeline.SwitchRetentionOwner("1:1");
        Assert.Equal(parked, Assert.Single(pipeline.LatestSnapshots()).Dbv);
    }

    /// <summary>
    /// Cleared, then switched away from and back: the slot comes back empty. A parked capture that
    /// outlived the Clear that discarded it would let Save write a trace the chart no longer shows.
    /// </summary>
    [Fact]
    public async Task AClearedMeasurement_DoesNotGetItsOldCaptureBack()
    {
        var pipeline = NewPipeline();
        pipeline.SwitchRetentionOwner("1:1");
        await CaptureAsync(pipeline, 1000.0);

        pipeline.SwitchRetentionOwner("1:2");
        pipeline.SwitchRetentionOwner("1:1");
        pipeline.Flush();
        Assert.Empty(pipeline.LatestSnapshots());

        pipeline.SwitchRetentionOwner("1:2");
        pipeline.SwitchRetentionOwner("1:1");
        Assert.Empty(pipeline.LatestSnapshots());
    }

    /// <summary>
    /// Four measurements are held aside at once and the fifth evicts the oldest — see
    /// MaxParkedRetention for why this is bounded at all. A measurement whose capture has been
    /// evicted comes back to an empty chart, which is what every measurement did before this.
    /// </summary>
    [Fact]
    public async Task OnlyTheFourMostRecentlyParkedCapturesAreKept()
    {
        var pipeline = NewPipeline();
        for (int i = 1; i <= 5; i++)
        {
            pipeline.SwitchRetentionOwner($"1:{i}");
            await CaptureAsync(pipeline, 1000.0 * i);
        }

        // 1:1 was parked first and is the one evicted; 1:2 through 1:5 are all still there.
        pipeline.SwitchRetentionOwner("1:1");
        Assert.Empty(pipeline.LatestSnapshots());

        for (int i = 2; i <= 5; i++)
        {
            pipeline.SwitchRetentionOwner($"1:{i}");
            Assert.Equal(1000.0 * i, RetainedToneHz(pipeline), BinHz);
        }
    }
}
