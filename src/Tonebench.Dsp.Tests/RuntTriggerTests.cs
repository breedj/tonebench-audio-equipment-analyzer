namespace Tonebench.Dsp.Tests;

/// <summary>
/// The trigger for an edge that did not make it.
///
/// <para>The one that carries the weight is the <b>selection</b>: a short pulse among full-height
/// ones, reported on its own. A runt is exactly as wide as a good pulse and crosses exactly the
/// same lower threshold, so neither a level nor a width can pick it out — which is the whole reason
/// the kind exists, and the thing a test has to show rather than assume.</para>
/// </summary>
public class RuntTriggerTests
{
    /// <summary>
    /// A train of rectangular pulses, each as tall as <paramref name="heights"/> says, separated by
    /// stretches at the base level.
    ///
    /// <para>Every pulse is the same width, so a width condition could not tell them apart even if
    /// this kind had one.</para>
    /// </summary>
    private static double[] PulseTrain(double[] heights, int width = 100, int gap = 100, double baseV = -1.0)
    {
        var samples = new List<double>();
        for (int i = 0; i < gap; i++)
            samples.Add(baseV);

        foreach (double height in heights)
        {
            for (int i = 0; i < width; i++)
                samples.Add(height);
            for (int i = 0; i < gap; i++)
                samples.Add(baseV);
        }

        return [.. samples];
    }

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
        double highV = 0.8,
        double lowV = -0.2,
        TriggerSlope slope = TriggerSlope.Rising,
        double hysteresisV = 0.0,
        int holdoffFrames = 0) =>
        new(
            TriggerKind.Runt,
            TriggerSource.ChannelA,
            slope,
            highV,
            hysteresisV,
            LevelLowV: lowV,
            HoldoffFrames: holdoffFrames);

    [Fact]
    public void AShortPulseAmongFullOnes_IsTheOnlyThingReported()
    {
        // THE case. Four pulses of identical width, one of which only reaches 0.4 against the
        // 0.8 threshold -- so it crosses the lower one and never the upper. Nothing about its
        // timing distinguishes it.
        var samples = PulseTrain([1.0, 1.0, 0.4, 1.0]);
        var trigger = new RuntTrigger(Spec());

        var hits = AllHits(trigger, samples);

        Assert.Single(hits);

        // Fired on the way back down through the lower threshold, which is where "it never got
        // there" becomes known: the runt starts at 100 + 200 + 200 = 500 and is 100 wide, and the
        // return crossing of -0.2 sits between the last high sample and the first low one.
        Assert.InRange(hits[0].Position, 599.0, 600.0);
    }

    [Fact]
    public void AFullPulse_ReportsNothing()
    {
        // The half that makes the selection mean anything: a pulse that reaches the far threshold
        // is an ordinary pulse, however long it stays there.
        Assert.Empty(AllHits(new RuntTrigger(Spec()), PulseTrain([1.0, 1.0, 1.0])));
    }

    [Fact]
    public void APulseThatNeverReachesTheLowerThreshold_ReportsNothingEither()
    {
        // A runt has to start: a wobble that never crosses the entry threshold is not a pulse that
        // failed, it is no pulse at all.
        Assert.Empty(AllHits(new RuntTrigger(Spec()), PulseTrain([-0.5, -0.6])));
    }

    [Fact]
    public void AFallingRunt_IsAPulseDownThatDoesNotReachTheLowerThreshold()
    {
        // The mirror: the signal sits high, dips through the upper threshold and comes back without
        // reaching the lower one. Under Falling that is the runt; under Rising it is nothing.
        var samples = PulseTrain([0.5, -1.0], width: 100, gap: 100, baseV: 1.0);

        var falling = AllHits(new RuntTrigger(Spec(slope: TriggerSlope.Falling)), samples);
        var rising = AllHits(new RuntTrigger(Spec()), samples);

        Assert.Single(falling);
        Assert.InRange(falling[0].Position, 199.0, 200.0);
        Assert.Empty(rising);
    }

    [Fact]
    public void Either_ReportsARuntInWhicheverDirectionItHappens()
    {
        var up = PulseTrain([0.4]);
        var down = PulseTrain([0.5], baseV: 1.0);

        Assert.Single(AllHits(new RuntTrigger(Spec(slope: TriggerSlope.Either)), up));
        Assert.Single(AllHits(new RuntTrigger(Spec(slope: TriggerSlope.Either)), down));
    }

    [Fact]
    public void TheTwoLevelsAreAPairOfThresholdsRatherThanAnOrder()
    {
        var samples = PulseTrain([1.0, 0.4]);

        var asGiven = AllHits(new RuntTrigger(Spec(highV: 0.8, lowV: -0.2)), samples);
        var swapped = AllHits(new RuntTrigger(Spec(highV: -0.2, lowV: 0.8)), samples);

        Assert.Single(asGiven);
        Assert.Single(swapped);
        Assert.Equal(asGiven[0].Position, swapped[0].Position, 9);
    }

    [Fact]
    public void AFullPulseDoesNotDisqualifyTheRuntAfterIt()
    {
        // The arming rule, stated as the fault it prevents: a pulse that reached the far threshold
        // is no longer a candidate, and it must stop being one as soon as the signal has gone back
        // to the base level -- or the first full pulse would mask every runt after it.
        var samples = PulseTrain([1.0, 0.4, 1.0, 0.4]);

        Assert.Equal(2, AllHits(new RuntTrigger(Spec()), samples).Count);
    }

    [Fact]
    public void ARuntSplitAcrossABlockBoundary_IsStillRecognised()
    {
        // The device hands over 4096-frame blocks, so a pulse routinely straddles two. A scanner
        // rebuilt or reset per block sees the second half alone, which looks like a signal that
        // started above the threshold -- no entry crossing, and the runt is lost.
        var samples = PulseTrain([0.4]);
        var trigger = new RuntTrigger(Spec());

        // The pulse runs from 100 to 199; split it in the middle.
        Assert.False(trigger.Scan(samples.AsSpan(0, 150), 0, out _));
        Assert.True(trigger.Scan(samples.AsSpan(150), 150, out var hit));
        Assert.InRange(hit.Position, 199.0, 200.0);
    }

    [Fact]
    public void Reset_ForgetsARuntItWasInTheMiddleOf()
    {
        // Called when the buffer's epoch moves. Carrying a half-seen pulse across the hole would
        // report a runt whose two crossings are on either side of samples that never arrived.
        var samples = PulseTrain([0.4]);
        var trigger = new RuntTrigger(Spec());

        Assert.False(trigger.Scan(samples.AsSpan(0, 150), 0, out _));
        trigger.Reset();

        Assert.False(trigger.Scan(samples.AsSpan(150), 150, out _));
    }

    [Fact]
    public void Hysteresis_RejectsNoiseOnTheEntryThreshold()
    {
        // A signal sitting on the lower threshold crosses it constantly on its own noise, and every
        // one of those excursions is a pulse that never reached the upper one -- i.e. a runt by the
        // letter. Arming is what keeps the reading about the DUT.
        var rng = new Random(13);
        var samples = new double[4000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = -0.2 + ((rng.NextDouble() - 0.5) * 0.02);

        Assert.NotEmpty(AllHits(new RuntTrigger(Spec()), samples));
        Assert.Empty(AllHits(new RuntTrigger(Spec(hysteresisV: 0.1)), samples));
    }

    [Fact]
    public void Holdoff_SuppressesTheNextRunt()
    {
        var samples = PulseTrain([0.4, 0.4, 0.4]);

        Assert.Equal(3, AllHits(new RuntTrigger(Spec()), samples).Count);
        Assert.Equal(2, AllHits(new RuntTrigger(Spec(holdoffFrames: 250)), samples).Count);
    }

    [Fact]
    public void TwoEqualThresholds_AreRefused()
    {
        // A runt is a pulse that reaches one threshold and not the other, so one threshold is no
        // runt condition at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntTrigger(Spec(highV: 0.5, lowV: 0.5)));
    }
}
