namespace Tonebench.Dsp.Tests;

/// <summary>
/// The trigger that waits for nothing to happen.
///
/// <para>Two of these carry the weight. <b>An input that is already dead when the scan starts fires
/// after one timeout</b> — the clock counts from the first sample seen and not from the first edge,
/// or the one case the kind exists for would wait for ever for an edge that is never coming. And
/// <b>a signal that is still running does not fire</b>, which is the half that makes the first one
/// mean anything.</para>
/// </summary>
public class DropoutTriggerTests
{
    private const double SampleRateHz = 192_000.0;

    /// <summary>A 1 kHz square: 192 samples a period at this rate, so an edge every 96.</summary>
    private static double[] Square(int frames, int halfPeriod = 96, double amplitude = 1.0)
    {
        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = (i / halfPeriod) % 2 == 0 ? amplitude : -amplitude;

        return samples;
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
        double timeoutS,
        TriggerSlope slope = TriggerSlope.Rising,
        double levelV = 0.0,
        double hysteresisV = 0.0,
        int holdoffFrames = 0) =>
        new(
            TriggerKind.Dropout,
            TriggerSource.ChannelA,
            slope,
            levelV,
            hysteresisV,
            WidthLoS: timeoutS,
            HoldoffFrames: holdoffFrames);

    private static double Seconds(double samples) => samples / SampleRateHz;

    [Fact]
    public void ASignalThatStops_FiresOneTimeoutAfterItsLastEdge()
    {
        // THE case: 10 periods of a square and then silence. The last rising edge is at the start
        // of period 10, and the trigger fires a timeout later -- deep inside the dead stretch,
        // with the whole of the signal before it sitting in the pre-trigger.
        var samples = new double[4000];
        var square = Square(1920);
        square.CopyTo(samples, 0);

        var trigger = new DropoutTrigger(Spec(Seconds(500)), SampleRateHz);

        var hits = AllHits(trigger, samples);

        Assert.NotEmpty(hits);

        // The square's last half-cycle sits at -1 and the silence that follows it is 0, so the step
        // INTO the silence is itself a rising crossing of zero -- at 1920, where the eleventh
        // period would have begun. The timeout therefore runs from there, which is the honest
        // answer and the one a reader stumbles over first: a dropout is timed from the last edge,
        // and going quiet is an edge.
        Assert.Equal(2420.0, hits[0].Position, 6);
    }

    [Fact]
    public void AnInputThatIsAlreadyDead_FiresOneTimeoutAfterTheScanStarts()
    {
        // The clock counts from the first sample seen rather than from the first edge. Counting
        // from an edge would make a dead input -- the one case this kind exists for -- wait for
        // ever for an edge that is never coming.
        var trigger = new DropoutTrigger(Spec(Seconds(300)), SampleRateHz);

        Assert.True(trigger.Scan(new double[1000], 0, out var hit));
        Assert.Equal(300.0, hit.Position, 6);
    }

    [Fact]
    public void ASignalThatKeepsRunning_NeverFires()
    {
        // The half that makes the rest mean anything. A 1 kHz square has an edge every 96 samples,
        // so a 500-sample timeout is never reached.
        var trigger = new DropoutTrigger(Spec(Seconds(500)), SampleRateHz);

        Assert.Empty(AllHits(trigger, Square(10_000)));
    }

    [Fact]
    public void ASignalThatStaysDead_GoesOnFiringOnceATimeout()
    {
        // Rather than firing once and then nothing, which on a dead input would leave the screen
        // holding one record for ever -- a bench instrument re-arms, and so does this.
        var trigger = new DropoutTrigger(Spec(Seconds(100)), SampleRateHz);

        var hits = AllHits(trigger, new double[1000]);

        Assert.Equal(9, hits.Count);
        for (int i = 0; i < hits.Count; i++)
            Assert.Equal(100.0 * (i + 1), hits[i].Position, 6);
    }

    [Fact]
    public void ASlopeOfEither_CountsBothDirectionsAsLife()
    {
        // A square at 96 samples a half-period has a crossing of SOME kind every 96, so with
        // Either a 150-sample timeout is never reached -- where Rising alone sees only every 192
        // and the same timeout fires on every other half-cycle.
        var samples = Square(2000);

        Assert.Empty(AllHits(new DropoutTrigger(Spec(Seconds(150), TriggerSlope.Either), SampleRateHz), samples));
        Assert.NotEmpty(AllHits(new DropoutTrigger(Spec(Seconds(150)), SampleRateHz), samples));
    }

    [Fact]
    public void AFallingSlope_WatchesOnlyFallingCrossings()
    {
        // A signal with rising crossings and no falling ones: a staircase that only goes up. Under
        // Falling it reads as dead, which is the honest answer to "has there been a falling edge".
        var samples = new double[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = -1.0 + ((i / 100) * 0.3);

        Assert.NotEmpty(AllHits(new DropoutTrigger(Spec(Seconds(400), TriggerSlope.Falling), SampleRateHz), samples));
    }

    [Fact]
    public void Hysteresis_StopsNoiseOnTheLevelFromCountingAsLife()
    {
        // The mirror of the edge trigger's own case, and it matters more here: a signal sitting on
        // the level crosses it constantly on its own noise, so without arming a DEAD input looks
        // perfectly alive and the trigger never fires at all.
        var rng = new Random(5);
        var samples = new double[4000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (rng.NextDouble() - 0.5) * 0.02;

        Assert.Empty(AllHits(new DropoutTrigger(Spec(Seconds(500)), SampleRateHz), samples));
        Assert.NotEmpty(AllHits(new DropoutTrigger(Spec(Seconds(500), hysteresisV: 0.1), SampleRateHz), samples));
    }

    [Fact]
    public void ADropoutSplitAcrossABlockBoundary_IsStillTimed()
    {
        // The device hands over 4096-frame blocks, so a silence routinely straddles two. A scanner
        // that forgot where it was counting from would restart the clock at every block and never
        // reach a timeout longer than one.
        var samples = new double[2000];
        var trigger = new DropoutTrigger(Spec(Seconds(1500)), SampleRateHz);

        Assert.False(trigger.Scan(samples.AsSpan(0, 1000), 0, out _));
        Assert.True(trigger.Scan(samples.AsSpan(1000), 1000, out var hit));
        Assert.Equal(1500.0, hit.Position, 6);
    }

    [Fact]
    public void Reset_StartsTheClockAgain()
    {
        // Called when the buffer's epoch moves -- a dropped block, a range step -- and what it must
        // not do is count a hole in the stream as a dropout, which is a dropout that never happened
        // to the DUT.
        var samples = new double[2000];
        var trigger = new DropoutTrigger(Spec(Seconds(1500)), SampleRateHz);

        Assert.False(trigger.Scan(samples.AsSpan(0, 1000), 0, out _));
        trigger.Reset();

        // From here the clock has only 1000 samples to run in, which is short of the timeout.
        Assert.False(trigger.Scan(samples.AsSpan(1000), 1000, out _));
    }

    [Fact]
    public void Holdoff_SuppressesTheNextTimeout()
    {
        var samples = new double[1000];
        Assert.Equal(9, AllHits(new DropoutTrigger(Spec(Seconds(100)), SampleRateHz), samples).Count);

        // The holdoff suppresses a hit without restarting the clock, so the one it swallows fires
        // the moment the holdoff lifts rather than a whole timeout later: 100, then 250 rather
        // than 200, and 150 apart from there on.
        var withHoldoff = new DropoutTrigger(Spec(Seconds(100), holdoffFrames: 150), SampleRateHz);
        var held = AllHits(withHoldoff, samples);

        Assert.Equal(6, held.Count);
        Assert.Equal(100.0, held[0].Position, 6);
        Assert.Equal(250.0, held[1].Position, 6);
        Assert.Equal(400.0, held[2].Position, 6);
    }

    [Fact]
    public void ATimeoutOfNothing_IsRefused()
    {
        // Zero would fire on every sample on which no edge happened, which is almost all of them --
        // a trigger that fires constantly while claiming to report an absence.
        Assert.Throws<ArgumentOutOfRangeException>(() => new DropoutTrigger(Spec(0.0), SampleRateHz));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DropoutTrigger(Spec(double.PositiveInfinity), SampleRateHz));
    }
}
