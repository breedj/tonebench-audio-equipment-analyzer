namespace Tonebench.Dsp.Tests;

/// <summary>
/// The trigger that reads no samples at all.
///
/// <para>Two properties carry it, and both are about <em>where</em> the phase comes from. It fires
/// on the <b>absolute frame clock</b>, so successive records of a run are the same stretch of the
/// stimulus however the engine resumed or reset in between — anchor it on the first sample seen and
/// the trace re-phases after every dropped block, which on a waveform reads as the DUT having
/// moved. And it fires <b>exactly</b>, with no fraction: the event is a frame index rather than a
/// crossing between two samples.</para>
/// </summary>
public class StimulusTriggerTests
{
    /// <summary>Every hit in one span, resuming after each, the way the engine drives a scanner.</summary>
    private static List<TriggerHit> AllHits(IScanTrigger trigger, int frames, long firstIndex = 0)
    {
        var samples = new double[frames];
        var hits = new List<TriggerHit>();
        int offset = 0;
        while (offset < frames && trigger.Scan(samples.AsSpan(offset), firstIndex + offset, out var hit))
        {
            hits.Add(hit);
            offset = (int)(hit.Index - firstIndex) + 2;
        }

        return hits;
    }

    [Fact]
    public void ItFiresOnTheStimulusGridAndNowhereElse()
    {
        var trigger = new StimulusTrigger(periodFrames: 100);

        var hits = AllHits(trigger, frames: 450);

        Assert.Equal([0.0, 100.0, 200.0, 300.0, 400.0], hits.Select(h => h.Position));
    }

    [Fact]
    public void TheHitIsExact_WithNoFractionToInterpolate()
    {
        // Unlike every other kind here: there is no crossing between two samples, so a fraction
        // would be an invented one.
        var trigger = new StimulusTrigger(periodFrames: 64);

        Assert.True(trigger.Scan(new double[200], 0, out var hit));
        Assert.Equal(0.0, hit.Fraction);
    }

    [Fact]
    public void ThePhaseIsTheAbsoluteClocksRatherThanTheFirstSampleItSees()
    {
        // THE property. A scan that starts part way through a period fires at the next grid point
        // of the absolute clock, not one period after wherever it was handed. Otherwise every
        // re-arm would re-phase the trace.
        var trigger = new StimulusTrigger(periodFrames: 100);

        // The span has to reach the grid point for it to be reported at all -- a scanner answers
        // about the samples it was handed -- so it runs from 1030 to 1180 and the hit is at 1100,
        // 70 frames in rather than one period after the start.
        Assert.True(trigger.Scan(new double[150], 1030, out var hit));
        Assert.Equal(1100.0, hit.Position, 9);
    }

    [Fact]
    public void Reset_LeavesThePhaseExactlyWhereItWas()
    {
        // The generator did not stop counting either. A reset follows a dropped block or a range
        // step, and re-phasing there would move the trace for a reason that has nothing to do with
        // the DUT.
        var trigger = new StimulusTrigger(periodFrames: 100);

        Assert.True(trigger.Scan(new double[150], 0, out var before));
        trigger.Reset();
        Assert.True(trigger.Scan(new double[150], 1000, out var after));

        Assert.Equal(0.0, before.Position, 9);
        Assert.Equal(1000.0, after.Position, 9);
    }

    [Fact]
    public void TwoScannersOfTheSamePeriod_AgreeWhateverSpansTheyWereGiven()
    {
        // The same property from the other side, and the one that makes an accumulation worth
        // anything: records taken by different arms of the same run have to be the same stretch of
        // the stimulus, or averaging them low-passes the signal.
        var fedWhole = new StimulusTrigger(periodFrames: 96);
        var fedInPieces = new StimulusTrigger(periodFrames: 96);

        var whole = AllHits(fedWhole, frames: 500).Select(h => h.Position).ToArray();

        var pieces = new List<double>();
        for (int block = 0; block < 5; block++)
        {
            int offset = 0;
            while (offset < 100
                && fedInPieces.Scan(new double[100 - offset], (block * 100L) + offset, out var hit))
            {
                pieces.Add(hit.Position);
                offset = (int)(hit.Index - (block * 100L)) + 2;
            }
        }

        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void TheSameIndexNeverFiresTwice()
    {
        // A scanner whose answer depends on where it was resumed from is one that will eventually
        // be resumed somewhere else. Scanned from the hit's own index, the next answer is the next
        // grid point rather than the same one again.
        var trigger = new StimulusTrigger(periodFrames: 100);

        Assert.True(trigger.Scan(new double[10], 100, out var first));
        Assert.Equal(100.0, first.Position, 9);

        Assert.True(trigger.Scan(new double[300], 100, out var second));
        Assert.Equal(200.0, second.Position, 9);
    }

    [Fact]
    public void ASpanWithNoGridPointInIt_ReportsNothing()
    {
        var trigger = new StimulusTrigger(periodFrames: 1000);

        Assert.False(trigger.Scan(new double[50], 101, out _));
    }

    [Fact]
    public void Holdoff_SkipsWholePeriods()
    {
        // The holdoff is a count on the same clock, so it suppresses the grid points inside it and
        // the next hit is still on the grid rather than at the end of the holdoff.
        var trigger = new StimulusTrigger(periodFrames: 100, holdoffFrames: 250);

        Assert.Equal([0.0, 300.0, 600.0, 900.0], AllHits(trigger, frames: 1000).Select(h => h.Position));
    }

    [Fact]
    public void APeriodOfNothing_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StimulusTrigger(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StimulusTrigger(-100));
    }

    [Fact]
    public void ANegativeHoldoff_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StimulusTrigger(100, -1));
    }
}
