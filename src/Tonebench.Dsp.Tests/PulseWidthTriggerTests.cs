namespace Tonebench.Dsp.Tests;

/// <summary>
/// The glitch trigger, against waveforms built with a width that is known by construction.
///
/// <para>Two of these carry the weight. <b>The selection</b> — a narrow pulse found in a train of
/// wide ones and nothing else reported — is the whole feature, and it is the one thing an edge
/// trigger cannot do. And <b>the unbounded case firing mid-pulse</b>: a level that has stuck never
/// produces a trailing edge, so a trigger that waited for one would never fire on exactly the fault
/// a "longer than" condition is set for.</para>
/// </summary>
public class PulseWidthTriggerTests
{
    private const double SampleRateHz = 192_000.0;

    /// <summary>
    /// A train of rectangular pulses: <paramref name="widths"/> gives each pulse's width in samples
    /// and they are separated by <paramref name="gap"/> samples at the low level.
    ///
    /// <para>Rectangular on purpose. A pulse's width is then a whole number of samples and the
    /// expected answer is arithmetic rather than a reading of a curve — the crossing fractions are
    /// pinned separately below, on a ramp, where linear interpolation is exact.</para>
    /// </summary>
    private static double[] PulseTrain(int[] widths, int gap = 100, double high = 1.0, double low = -1.0)
    {
        var samples = new List<double>();
        for (int i = 0; i < gap; i++)
            samples.Add(low);

        foreach (int width in widths)
        {
            for (int i = 0; i < width; i++)
                samples.Add(high);
            for (int i = 0; i < gap; i++)
                samples.Add(low);
        }

        return [.. samples];
    }

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
        double widthLoS = 0.0,
        double widthHiS = double.PositiveInfinity,
        TriggerSlope slope = TriggerSlope.Rising,
        double levelV = 0.0,
        double hysteresisV = 0.0,
        int holdoffFrames = 0) =>
        new(
            TriggerKind.PulseWidth,
            TriggerSource.ChannelA,
            slope,
            levelV,
            hysteresisV,
            WidthLoS: widthLoS,
            WidthHiS: widthHiS,
            HoldoffFrames: holdoffFrames);

    private static double Seconds(double samples) => samples / SampleRateHz;

    [Fact]
    public void ANarrowPulseAmongWideOnes_IsTheOnlyThingReported()
    {
        // THE feature. An edge trigger fires on all four of these; this fires on the one that is
        // the fault. The glitch is 8 samples wide (42 us at 192 kS/s) against 200-sample pulses.
        var samples = PulseTrain([200, 200, 8, 200]);
        var trigger = new PulseWidthTrigger(Spec(widthHiS: Seconds(50)), SampleRateHz);

        var hits = AllHits(trigger, samples);

        Assert.Single(hits);

        // The glitch's own trailing edge: 100 of gap, 200 + 100, 200 + 100, then 8 of pulse.
        // The trailing crossing sits half a sample past the last high one, between +1 and -1:
        // the glitch's own width is exactly 8 samples, measured crossing to crossing.
        const int glitchStart = 100 + 300 + 300;
        Assert.Equal(glitchStart + 8 - 0.5, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void AWidePulseAmongNarrowOnes_IsTheOnlyThingReported()
    {
        // The mirror, and the condition a protection circuit or a stuck output trips.
        var samples = PulseTrain([8, 8, 200, 8]);
        var trigger = new PulseWidthTrigger(Spec(widthLoS: Seconds(50), widthHiS: Seconds(400)), SampleRateHz);

        var hits = AllHits(trigger, samples);

        Assert.Single(hits);
        Assert.Equal(100 + 108 + 108 + 200 - 0.5, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void WithNothingBoundingItAbove_ItFiresAsSoonAsTheWidthPassesTheBound_WithoutWaitingForAnEnd()
    {
        // The decision this kind's remarks argue for, stated as a test: a pulse that never ends
        // would never fire if this waited for a trailing edge -- and a level that has stuck is
        // exactly what somebody sets "longer than" to catch. The record here has no trailing edge
        // at all: it goes high and stays high.
        var samples = new double[400];
        for (int i = 0; i < 100; i++)
            samples[i] = -1.0;
        for (int i = 100; i < samples.Length; i++)
            samples[i] = 1.0;

        var trigger = new PulseWidthTrigger(Spec(widthLoS: Seconds(50)), SampleRateHz);

        Assert.True(trigger.Scan(samples, 0, out var hit));

        // The leading crossing is at 99.5 (midway between -1 and +1), so 50 samples later is 149.5;
        // the first whole sample at or past it is 150.
        Assert.Equal(150.0, hit.Index + hit.Fraction, 6);
    }

    [Fact]
    public void WithNothingBoundingItAbove_OnePulseFiresOnce()
    {
        // Or a stuck level would report a hit per sample for as long as it stayed stuck, which is
        // the same fault the edge trigger's arming exists to prevent one kind down.
        var samples = PulseTrain([200], gap: 50);
        var trigger = new PulseWidthTrigger(Spec(widthLoS: Seconds(20)), SampleRateHz);

        Assert.Single(AllHits(trigger, samples));
    }

    [Fact]
    public void APulseOfTheWrongWidth_ReportsNothingAtAll()
    {
        // Absent rather than approximate: a width condition that answered "near enough" would make
        // the whole kind an edge trigger with extra steps.
        var samples = PulseTrain([200, 200]);
        var trigger = new PulseWidthTrigger(Spec(widthHiS: Seconds(50)), SampleRateHz);

        Assert.Empty(AllHits(trigger, samples));
    }

    [Fact]
    public void ABetweenCondition_SelectsOnlyThePulseInsideIt()
    {
        var samples = PulseTrain([8, 60, 400]);
        var trigger = new PulseWidthTrigger(
            Spec(widthLoS: Seconds(30), widthHiS: Seconds(120)), SampleRateHz);

        var hits = AllHits(trigger, samples);

        Assert.Single(hits);
        Assert.Equal(100 + 108 + 60 - 0.5, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void AFallingSlope_MeasuresTheStretchBelowTheLevel()
    {
        // "A pulse is level crossings, not polarity" -- so Falling reads the negative pulse's own
        // width. Here the waveform is mostly high with two dips in it, one of them narrow.
        var samples = new List<double>();
        samples.AddRange(Enumerable.Repeat(1.0, 100));
        samples.AddRange(Enumerable.Repeat(-1.0, 200));
        samples.AddRange(Enumerable.Repeat(1.0, 100));
        samples.AddRange(Enumerable.Repeat(-1.0, 8));
        samples.AddRange(Enumerable.Repeat(1.0, 100));

        var trigger = new PulseWidthTrigger(
            Spec(widthHiS: Seconds(50), slope: TriggerSlope.Falling), SampleRateHz);

        var hits = AllHits(trigger, [.. samples]);

        Assert.Single(hits);
        Assert.Equal(100 + 200 + 100 + 8 - 0.5, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void ARisingSlope_IgnoresANarrowDip()
    {
        // The other half of the same rule: the dip above is a qualifying NEGATIVE pulse and no
        // qualifying positive one, so Rising must report nothing. Get the polarity backwards and
        // this is the test that says so.
        var samples = new List<double>();
        samples.AddRange(Enumerable.Repeat(1.0, 200));
        samples.AddRange(Enumerable.Repeat(-1.0, 8));
        samples.AddRange(Enumerable.Repeat(1.0, 200));

        var trigger = new PulseWidthTrigger(Spec(widthHiS: Seconds(50)), SampleRateHz);

        Assert.Empty(AllHits(trigger, [.. samples]));
    }

    [Fact]
    public void AWidthIsMeasuredBetweenInterpolatedCrossings_NotBetweenSamples()
    {
        // One sample is 5.2 us at 192 kS/s, so a width rounded to whole samples is wrong by up to
        // that much -- which on a 50 us glitch is 10 %. Built from ramps, where the interpolation
        // is exact, with the two crossings at 100.25 and 140.75: a width of 40.5 samples, which
        // only an interpolated reading can tell from 40 or 41.
        var samples = new double[300];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = -1.0;

        // Rise through zero at 100.25 and fall back through it at 140.75.
        samples[100] = -0.25;
        samples[101] = 0.75;
        for (int i = 102; i < 140; i++)
            samples[i] = 1.0;
        samples[140] = 0.75;
        samples[141] = -0.25;

        // Between 40.4 and 40.6 samples: a scanner reading whole samples answers 40 or 41 and
        // reports nothing.
        var trigger = new PulseWidthTrigger(
            Spec(widthLoS: Seconds(40.4), widthHiS: Seconds(40.6)), SampleRateHz);

        var hits = AllHits(trigger, samples);

        Assert.Single(hits);
        Assert.Equal(140.75, hits[0].Index + hits[0].Fraction, 6);
    }

    [Fact]
    public void APulseSplitAcrossABlockBoundary_IsStillMeasuredWhole()
    {
        // The device hands over 4096-frame blocks, so a pulse routinely straddles two. A scanner
        // rebuilt or reset per block measures the fragment instead -- which reads as a glitch that
        // is not there, the worst possible answer for this kind.
        var samples = PulseTrain([200], gap: 100);
        var trigger = new PulseWidthTrigger(
            Spec(widthLoS: Seconds(190), widthHiS: Seconds(210)), SampleRateHz);

        // The first half of the pulse only: nothing to report yet.
        Assert.False(trigger.Scan(samples.AsSpan(0, 200), 0, out _));

        // The rest, as its own block.
        Assert.True(trigger.Scan(samples.AsSpan(200), 200, out var hit));
        Assert.Equal(299.5, hit.Index + hit.Fraction, 6);
    }

    [Fact]
    public void Hysteresis_RejectsAPulseThatIsOnlyNoiseOnTheLevel()
    {
        // A signal sitting on the level crosses it repeatedly on its own noise, and every one of
        // those crossings is a very narrow "pulse" -- i.e. exactly what a glitch condition would
        // report. Arming is what makes the difference, and this is where it earns its keep most.
        var rng = new Random(7);
        var samples = new double[2000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (rng.NextDouble() - 0.5) * 0.02;

        var trigger = new PulseWidthTrigger(
            Spec(widthHiS: Seconds(50), hysteresisV: 0.1), SampleRateHz);

        Assert.Empty(AllHits(trigger, samples));
    }

    [Fact]
    public void Holdoff_SuppressesTheNextQualifyingPulse()
    {
        var samples = PulseTrain([8, 8, 8], gap: 100);
        var withoutHoldoff = new PulseWidthTrigger(Spec(widthHiS: Seconds(50)), SampleRateHz);
        Assert.Equal(3, AllHits(withoutHoldoff, samples).Count);

        // Long enough to swallow the one after each hit, not the one after that.
        var withHoldoff = new PulseWidthTrigger(
            Spec(widthHiS: Seconds(50), holdoffFrames: 150), SampleRateHz);

        Assert.Equal(2, AllHits(withHoldoff, samples).Count);
    }

    [Fact]
    public void Reset_ForgetsAPulseItWasInTheMiddleOf()
    {
        // Reset is called when the buffer's epoch moves -- a dropped block, a range step -- and
        // what it must not do is carry a leading crossing from before the break across it, which
        // would measure a width that spans a hole in the stream.
        var samples = PulseTrain([200], gap: 100);
        var trigger = new PulseWidthTrigger(
            Spec(widthLoS: Seconds(190), widthHiS: Seconds(210)), SampleRateHz);

        Assert.False(trigger.Scan(samples.AsSpan(0, 200), 0, out _));
        trigger.Reset();

        Assert.False(trigger.Scan(samples.AsSpan(200), 200, out _));
    }

    [Fact]
    public void Either_ReportsEveryQualifyingPulseOfBothPolarities()
    {
        // The regression this kind came closest to shipping with. A positive pulse's trailing
        // crossing is the negative pulse's leading one, so a scan that returned the moment the
        // first polarity fired left the second having never seen the pair its own pulse starts on
        // -- and since the scan resumes past that pair, every other pulse vanished. Measured
        // against the old ordering this record gave 4 hits where it holds 6.
        var samples = new List<double>();
        for (int cycle = 0; cycle < 4; cycle++)
        {
            samples.AddRange(Enumerable.Repeat(1.0, 40));
            samples.AddRange(Enumerable.Repeat(-1.0, 40));
        }

        var trigger = new PulseWidthTrigger(
            Spec(widthLoS: Seconds(30), widthHiS: Seconds(50), slope: TriggerSlope.Either),
            SampleRateHz);

        var hits = AllHits(trigger, [.. samples]);

        // Eight half-cycles; the first has no leading crossing (the record opens already high) and
        // the last has no trailing one (it runs to the end), so six.
        Assert.Equal(6, hits.Count);

        // Every half-cycle boundary from 79.5 on, 40 samples apart: alternating polarities, each
        // reported once.
        for (int i = 0; i < hits.Count; i++)
            Assert.Equal(79.5 + (i * 40), hits[i].Index + hits[i].Fraction, 6);
    }

    [Fact]
    public void AWidthRangeTheWrongWayRound_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PulseWidthTrigger(Spec(widthLoS: Seconds(100), widthHiS: Seconds(50)), SampleRateHz));
    }

    [Fact]
    public void ASampleRateOfNothing_IsRefused()
    {
        // The rate is what converts both bounds, so a zero would make every width condition
        // "anything at all" -- a trigger that fires on every pulse while claiming not to.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PulseWidthTrigger(Spec(widthHiS: Seconds(50)), 0.0));
    }
}
