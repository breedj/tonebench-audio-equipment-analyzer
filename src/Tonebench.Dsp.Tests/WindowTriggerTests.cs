namespace Tonebench.Dsp.Tests;

/// <summary>
/// Entering and leaving a voltage band.
///
/// <para>Three of these are the rules rather than the cases. <b>A signal that jumps clean through
/// the band in one sample is not an entry</b> — each sample is classified in or out and only a
/// transition fires, which is what keeps a tight band from firing on every edge. <b>The two levels
/// are a pair of bounds and not an order</b>, so typing them the other way round means the same
/// band. And <b>the hysteresis is clamped to the band</b>, because unclamped a hysteresis wider
/// than the band leaves the exit unarmable and the trigger silently never fires.</para>
/// </summary>
public class WindowTriggerTests
{
    /// <summary>Every hit in one span, resuming after each, the way the engine drives a scanner.</summary>
    private static List<TriggerHit> AllHits(IScanTrigger trigger, double[] samples, long firstIndex = 0)
    {
        var hits = new List<TriggerHit>();
        int offset = 0;
        while (offset < samples.Length
            && trigger.Scan(samples.AsSpan(offset), firstIndex + offset, out var hit))
        {
            hits.Add(hit);
            offset = (int)(hit.Index - firstIndex) + 2;
        }

        return hits;
    }

    private static TriggerSpec Spec(
        double highV = 1.0,
        double lowV = -1.0,
        TriggerSlope slope = TriggerSlope.Rising,
        double hysteresisV = 0.0,
        int holdoffFrames = 0) =>
        new(
            TriggerKind.Window,
            TriggerSource.ChannelA,
            slope,
            highV,
            hysteresisV,
            LevelLowV: lowV,
            HoldoffFrames: holdoffFrames);

    /// <summary>A ramp from <paramref name="from"/> to <paramref name="to"/> over <paramref name="n"/> samples.</summary>
    private static double[] Ramp(double from, double to, int n)
    {
        var samples = new double[n];
        for (int i = 0; i < n; i++)
            samples[i] = from + ((to - from) * i / (n - 1.0));

        return samples;
    }

    [Fact]
    public void ARampUpThroughTheBand_EntersAtItsLowerBound()
    {
        // The band is -1..+1 and the ramp runs -4..+4 over 81 samples, i.e. 0.1 V a sample, so the
        // lower bound is crossed at exactly sample 30.
        var trigger = new WindowTrigger(Spec());

        var hits = AllHits(trigger, Ramp(-4.0, 4.0, 81));

        Assert.Single(hits);
        Assert.Equal(30.0, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void ARampDownThroughTheBand_EntersAtItsUpperBound()
    {
        // Entering from above crosses the OTHER bound, so the trigger point has to be read off the
        // edge the signal actually went through rather than off a nominal level -- which is the
        // whole reason the hit picks its threshold rather than always using LevelV.
        var trigger = new WindowTrigger(Spec());

        var hits = AllHits(trigger, Ramp(4.0, -4.0, 81));

        Assert.Single(hits);
        Assert.Equal(30.0, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void LeavingTheBand_FiresOnTheBoundItWentOutThrough()
    {
        var trigger = new WindowTrigger(Spec(slope: TriggerSlope.Falling));

        var hits = AllHits(trigger, Ramp(-4.0, 4.0, 81));

        Assert.Single(hits);

        // Out through the upper bound, 20 samples past the lower one at 0.1 V a sample.
        Assert.Equal(50.0, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void Either_ReportsTheEntryAndTheExit()
    {
        var trigger = new WindowTrigger(Spec(slope: TriggerSlope.Either));

        var hits = AllHits(trigger, Ramp(-4.0, 4.0, 81));

        Assert.Equal(2, hits.Count);
        Assert.Equal(30.0, hits[0].Index + hits[0].Fraction, 6);
        Assert.Equal(50.0, hits[1].Index + hits[1].Fraction, 6);
    }

    [Fact]
    public void ASignalThatJumpsCleanThroughTheBandInOneSample_IsNotAnEntry()
    {
        // The rule that keeps a tight band from firing on every edge. A step from -4 to +4 is
        // outside-to-outside: nothing was ever observed inside, so nothing is reported -- which is
        // also the honest answer, since a band narrower than one sample's slew genuinely cannot be
        // seen to have been entered.
        var samples = new double[200];
        for (int i = 0; i < 100; i++)
            samples[i] = -4.0;
        for (int i = 100; i < samples.Length; i++)
            samples[i] = 4.0;

        Assert.Empty(AllHits(new WindowTrigger(Spec(slope: TriggerSlope.Either)), samples));
    }

    [Fact]
    public void TheTwoLevelsAreAPairOfBoundsRatherThanAnOrder()
    {
        // A user who types the high figure in the low box means the same band, and refusing that
        // would be a refusal about which box a number went in rather than about the measurement.
        var ramp = Ramp(-4.0, 4.0, 81);

        var asGiven = AllHits(new WindowTrigger(Spec(highV: 1.0, lowV: -1.0)), ramp);
        var swapped = AllHits(new WindowTrigger(Spec(highV: -1.0, lowV: 1.0)), ramp);

        Assert.Single(asGiven);
        Assert.Single(swapped);
        Assert.Equal(
            asGiven[0].Index + asGiven[0].Fraction,
            swapped[0].Index + swapped[0].Fraction,
            9);
    }

    [Fact]
    public void AnAsymmetricBand_IsEnteredAtItsOwnBounds()
    {
        // A band does not have to straddle zero: the case this kind is actually for is a bias
        // point, which sits wherever the circuit puts it. 2.0..3.0 on the same 0.1 V-a-sample
        // ramp is sample 60 in and sample 70 out.
        var trigger = new WindowTrigger(Spec(highV: 3.0, lowV: 2.0, slope: TriggerSlope.Either));

        var hits = AllHits(trigger, Ramp(-4.0, 4.0, 81));

        Assert.Equal(2, hits.Count);
        Assert.Equal(60.0, hits[0].Index + hits[0].Fraction, 6);
        Assert.Equal(70.0, hits[1].Index + hits[1].Fraction, 6);
    }

    [Fact]
    public void ASignalThatStaysInsideTheBand_ReportsNothing()
    {
        // The reading a bias-point watch sits at for hours: in tolerance, nothing to say.
        var samples = new double[500];
        var rng = new Random(11);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (rng.NextDouble() - 0.5) * 0.4;

        Assert.Empty(AllHits(new WindowTrigger(Spec(slope: TriggerSlope.Either)), samples));
    }

    [Fact]
    public void ACrossingSplitAcrossABlockBoundary_IsFoundExactlyOnce()
    {
        // The device hands over 4096-frame blocks. A scanner rebuilt per block either misses the
        // transition -- it never sees the pair that straddles the split -- or finds it twice.
        var samples = Ramp(-4.0, 4.0, 81);
        var trigger = new WindowTrigger(Spec());

        // The entry is at sample 30, so split the span right across it.
        Assert.False(trigger.Scan(samples.AsSpan(0, 30), 0, out _));
        Assert.True(trigger.Scan(samples.AsSpan(30), 30, out var hit));
        Assert.Equal(30.0, hit.Index + hit.Fraction, 6);

        // And not again in the rest of the same ramp.
        Assert.False(trigger.Scan(samples.AsSpan(32), 32, out _));
    }

    [Fact]
    public void Hysteresis_RejectsNoiseOnTheBandsEdge()
    {
        // A signal sitting on a bound crosses it repeatedly on its own noise, and every one of
        // those is an entry and an exit. Arming is what makes the difference: the signal has to
        // have been clear of the band before an entry counts.
        var rng = new Random(3);
        var samples = new double[2000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 1.0 + ((rng.NextDouble() - 0.5) * 0.02);

        Assert.Empty(AllHits(
            new WindowTrigger(Spec(slope: TriggerSlope.Either, hysteresisV: 0.1)), samples));
    }

    [Fact]
    public void AHysteresisWiderThanTheBand_StillLeavesTheExitArmable()
    {
        // The clamp, stated as the fault it prevents. Unclamped, "inside the band by the
        // hysteresis" is unreachable when the hysteresis is wider than the band -- the exit arm is
        // never set, and the trigger is perfectly configured and silently never fires, which is the
        // worst way for a control to be wrong.
        var trigger = new WindowTrigger(Spec(highV: 1.0, lowV: -1.0, slope: TriggerSlope.Falling, hysteresisV: 5.0));

        var hits = AllHits(trigger, Ramp(-4.0, 4.0, 81));

        Assert.Single(hits);
        Assert.Equal(50.0, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void Holdoff_SuppressesTheNextTransition()
    {
        // Two passes through the band, 100 samples apart; a holdoff long enough to swallow the
        // second's entry.
        var samples = new List<double>();
        samples.AddRange(Ramp(-4.0, 4.0, 81));
        samples.AddRange(Ramp(4.0, -4.0, 81));

        var withoutHoldoff = new WindowTrigger(Spec());
        Assert.Equal(2, AllHits(withoutHoldoff, [.. samples]).Count);

        var withHoldoff = new WindowTrigger(Spec(holdoffFrames: 200));
        Assert.Single(AllHits(withHoldoff, [.. samples]));
    }

    [Fact]
    public void Reset_ForgetsWhichSideOfTheBandTheSignalWasOn()
    {
        // Called when the buffer's epoch moves -- a dropped block, a range step -- and what it must
        // not do is read a transition across the hole, which is a transition that never happened.
        var samples = Ramp(-4.0, 4.0, 81);
        var trigger = new WindowTrigger(Spec());

        Assert.False(trigger.Scan(samples.AsSpan(0, 30), 0, out _));
        trigger.Reset();

        // With no previous sample there is no transition to see at 30, so the entry is gone: the
        // first sample of the resumed scan is already inside.
        Assert.False(trigger.Scan(samples.AsSpan(30, 2), 30, out _));
    }

    [Fact]
    public void TwoEqualLevels_AreRefused()
    {
        // Not a narrow window -- no window, and nothing can ever be inside one.
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowTrigger(Spec(highV: 1.0, lowV: 1.0)));
    }
}
