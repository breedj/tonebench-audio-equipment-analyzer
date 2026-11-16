namespace Tonebench.Dsp.Tests;

/// <summary>
/// The edge trigger, and above all the two things that make it a trigger rather than a search: it
/// survives the block boundary the device delivers on, and it places the crossing to better than a
/// sample.
/// </summary>
public class EdgeTriggerTests
{
    private const double SampleRateHz = 192_000.0;

    /// <summary>
    /// A 1 kHz sine whose rising zero crossing falls at fractional sample
    /// <paramref name="crossingAt"/>. At 192 kS/s a period is 192 samples, so a 200-sample record
    /// with the crossing near its middle holds <b>exactly one</b> rising crossing, and opens below
    /// the level for long enough to arm.
    /// </summary>
    private static double[] SineCrossingAt(double crossingAt, int n = 200, double ampPk = 1.0)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = ampPk * Math.Sin(2.0 * Math.PI * 1_000.0 * (i - crossingAt) / SampleRateHz);

        return x;
    }

    /// <summary>
    /// A straight line through zero at <paramref name="crossingAt"/>. Linear interpolation is
    /// exact on it, so this separates the interpolation arithmetic from the curvature of whatever
    /// waveform it is applied to.
    /// </summary>
    private static double[] RampCrossingAt(double crossingAt, int n = 200)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = (i - crossingAt) * 0.01;

        return x;
    }

    /// <summary>Every hit in one span, resuming after each so the scanner is driven as the engine drives it.</summary>
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

    [Fact]
    public void ACrossingSplitAcrossABlockBoundary_IsFoundExactlyOnce()
    {
        // The fault this exists for: the device hands over 4096-frame blocks, so a crossing
        // routinely straddles two of them. A scanner rebuilt per block either misses it -- it never
        // sees the pair of samples that straddle the level -- or finds it twice.
        const double crossingAt = 100.37;
        var x = SineCrossingAt(crossingAt);

        var whole = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0)), x);
        Assert.Single(whole);

        // Split the stream exactly on the straddling pair: 100 is below the level, 101 is above.
        Assert.True(x[100] < 0.0 && x[101] > 0.0);

        var trigger = new EdgeTrigger(TriggerSpec.Edge(0.0));
        var split = new List<TriggerHit>();

        if (trigger.Scan(x.AsSpan(0, 101), 0, out var early))
            split.Add(early);
        if (trigger.Scan(x.AsSpan(101), 101, out var late))
            split.Add(late);

        Assert.Single(split);
        Assert.Equal(whole[0].Position, split[0].Position, 9);
        Assert.Equal(crossingAt, split[0].Position, 3);
    }

    [Fact]
    public void OnAStraightEdge_TheCrossingIsExact()
    {
        // Interpolation is exact on a line, so any error here is the arithmetic rather than the
        // waveform's curvature.
        foreach (double crossingAt in new[] { 100.0, 100.125, 100.5, 100.875 })
        {
            var trigger = new EdgeTrigger(TriggerSpec.Edge(0.0));
            Assert.True(trigger.Scan(RampCrossingAt(crossingAt), 0, out var hit));
            Assert.Equal(crossingAt, hit.Position, 9);
            Assert.InRange(hit.Fraction, 0.0, 1.0);
        }
    }

    [Fact]
    public void TheCrossingIsPlacedToBetterThanASample()
    {
        // Integer-index triggering would be out by up to a whole sample, which at 192 kS/s is
        // 5.2 us of visible sideways walk from capture to capture. On a 1 kHz sine the chord
        // through the straddling pair is near enough the curve to place it within 1e-4 of a sample.
        foreach (double crossingAt in new[] { 100.0, 100.12, 100.5, 100.9 })
        {
            var trigger = new EdgeTrigger(TriggerSpec.Edge(0.0));
            Assert.True(trigger.Scan(SineCrossingAt(crossingAt), 0, out var hit));
            Assert.Equal(crossingAt, hit.Position, 3);
            Assert.InRange(hit.Fraction, 0.0, 1.0);
        }
    }

    [Fact]
    public void EveryPeriodOfASine_IsFoundAtTheSameFractionalOffset()
    {
        // A free-running sine crosses once per period for ever. That every one of them is placed at
        // the same fraction is what a stable trace is made of -- and it is the property an
        // integer-index trigger cannot have.
        const double crossingAt = 500.37;
        var x = new double[2048];
        for (int i = 0; i < x.Length; i++)
            x[i] = Math.Sin(2.0 * Math.PI * 1_000.0 * (i - crossingAt) / SampleRateHz);

        var hits = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0)), x);

        Assert.Equal(11, hits.Count);
        foreach (var hit in hits)
            Assert.Equal(0.37, hit.Fraction, 3);
    }

    [Fact]
    public void Hysteresis_RejectsASignalWobblingOnTheLevel()
    {
        // Ten small wiggles across zero, then one real swing. Without a hysteresis band every
        // wiggle is a crossing; with one, only the real excursion arms the trigger.
        var x = new double[400];
        for (int i = 0; i < 200; i++)
            x[i] = (i % 2 == 0 ? 1 : -1) * 0.01;
        for (int i = 200; i < 300; i++)
            x[i] = -1.0;
        for (int i = 300; i < 400; i++)
            x[i] = 1.0;

        var noisy = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0)), x);
        var clean = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0, hysteresisV: 0.1)), x);

        Assert.True(noisy.Count > 10, $"without hysteresis the wobble should fire repeatedly, got {noisy.Count}");
        Assert.Single(clean);
        Assert.Equal(299, clean[0].Index);
    }

    [Fact]
    public void Holdoff_SuppressesTheEdgesInsideIt()
    {
        // A square wave of period 100 samples: 8 rising edges in the record. A holdoff of 150
        // frames is longer than a period, so every other one is swallowed.
        var x = new double[800];
        for (int i = 0; i < x.Length; i++)
            x[i] = (i / 50) % 2 == 0 ? -1.0 : 1.0;

        var free = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0)), x);
        var held = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0) with { HoldoffFrames = 150 }), x);

        Assert.Equal(8, free.Count);
        Assert.Equal(4, held.Count);
    }

    [Fact]
    public void TheSlopeDecidesWhichEdgesCount()
    {
        var x = new double[800];
        for (int i = 0; i < x.Length; i++)
            x[i] = (i / 50) % 2 == 0 ? -1.0 : 1.0;

        var rising = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0, TriggerSlope.Rising)), x);
        var falling = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0, TriggerSlope.Falling)), x);
        var either = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0, TriggerSlope.Either)), x);

        Assert.Equal(8, rising.Count);
        Assert.Equal(7, falling.Count);
        Assert.Equal(15, either.Count);
    }

    [Fact]
    public void ASignalThatStartsAboveTheLevel_DoesNotFireUntilItHasBeenBelowIt()
    {
        // Arming first is what makes this a trigger: a record that opens above the level has not
        // crossed anything, and firing on its first sample would place the trace arbitrarily.
        var x = new double[300];
        for (int i = 0; i < 100; i++)
            x[i] = 1.0;
        for (int i = 100; i < 200; i++)
            x[i] = -1.0;
        for (int i = 200; i < 300; i++)
            x[i] = 1.0;

        var hits = AllHits(new EdgeTrigger(TriggerSpec.Edge(0.0)), x);

        Assert.Single(hits);
        Assert.Equal(199, hits[0].Index);
    }

    [Fact]
    public void Reset_ForgetsTheSampleBeforeTheHole()
    {
        // After an input-range step the stream is discontinuous by ~110 ms. Carrying the previous
        // sample across that gap invents a crossing between two samples that were never adjacent.
        var before = new[] { -1.0, -1.0, -1.0 };
        var after = new[] { 1.0, 1.0, 1.0 };

        var carried = new EdgeTrigger(TriggerSpec.Edge(0.0));
        Assert.False(carried.Scan(before, 0, out _));
        Assert.True(carried.Scan(after, 1_000_000, out _));

        var reset = new EdgeTrigger(TriggerSpec.Edge(0.0));
        Assert.False(reset.Scan(before, 0, out _));
        reset.Reset();
        Assert.False(reset.Scan(after, 1_000_000, out _));
    }

    [Fact]
    public void TheAutoLevel_IsTheMidpointOfTheSwing_NotItsMean()
    {
        // A 25 % duty square: mean -0.5, midpoint 0. A trigger at the mean fires on both edges of
        // the narrow pulse and misses nothing else; the midpoint is what a 50 % button means.
        var x = new double[400];
        for (int i = 0; i < x.Length; i++)
            x[i] = (i % 100) < 25 ? 1.0 : -1.0;

        Assert.Equal(0.0, TriggerAutoLevel.FiftyPercent(x), 9);
        Assert.Equal(-0.5, WaveformMeasure.Measure(x).MeanV, 9);
        Assert.Equal(0.04, TriggerAutoLevel.HysteresisFor(x), 9);
    }

    [Fact]
    public void ARecordWithNoSwing_GetsNoHysteresisBand()
    {
        Assert.Equal(0.0, TriggerAutoLevel.HysteresisFor(new double[64]), 12);
    }
}
