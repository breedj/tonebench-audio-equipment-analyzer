namespace Tonebench.Dsp.Tests;

public class BurstDecayTests
{
    private const double SampleRateHz = 48000.0;
    private const int RecordLength = 65536;
    private const int Peak = 8000;

    private static BurstDecaySettings Settings(
        double lowHz = 200.0,
        double highHz = 10000.0,
        int maxPeriods = 20,
        int pointsPerPeriod = 2,
        BurstDecayResolution resolution = BurstDecayResolution.PreferTime,
        int columns = 60) =>
        new(lowHz, highHz, maxPeriods, pointsPerPeriod, resolution, columns);

    /// <summary>
    /// An impulse response holding one exponentially decaying tone starting at <see cref="Peak"/> —
    /// which is exactly a second-order resonance's impulse response, so this is a DUT rather than a
    /// contrivance.
    /// </summary>
    private static double[] Ring(double freqHz, double decayDbPerPeriod, double amplitude = 1.0)
    {
        // dB per period fixes the time constant: 20 log10(e^(-1/(f tau))) per period.
        double tau = 8.6858896 / (decayDbPerPeriod * freqHz);

        var ir = new double[RecordLength];
        for (int i = Peak; i < RecordLength; i++)
        {
            double t = (i - Peak) / SampleRateHz;
            ir[i] = amplitude * Math.Exp(-t / tau) * Math.Sin(2.0 * Math.PI * freqHz * t);
        }

        return ir;
    }

    private static int NearestColumn(float[] freqHz, double target)
    {
        int best = 0;
        for (int c = 1; c < freqHz.Length; c++)
        {
            if (Math.Abs(Math.Log(freqHz[c] / target)) < Math.Abs(Math.Log(freqHz[best] / target)))
                best = c;
        }

        return best;
    }

    /// <summary>Level at one frequency and one depth, indexed through the slice's own valid span.</summary>
    private static double LevelAt(DecaySurface surface, int column, double periods)
    {
        var slice = surface.Slices.First(s => s.Value >= periods - 1e-9);
        Assert.True(column >= slice.FirstValidColumn, $"{surface.FreqHz[column]} Hz has no data at {periods} periods");
        return slice.LevelDb[column - slice.FirstValidColumn];
    }

    /// <summary>
    /// The whole reason this measurement exists beside the cumulative spectral decay. Two rings that
    /// die away in the same number of <em>cycles</em> are the same defect however far apart in
    /// frequency they sit — 500 Hz losing 1 dB per period takes ten times as many milliseconds to do
    /// it as 5 kHz does — and a burst decay has to draw them as one curve.
    /// </summary>
    [Fact]
    public void Compute_DrawsTwoRingsOfEqualPeriodDecayIdentically()
    {
        var ir = Ring(500.0, 1.0);
        var ring = Ring(5000.0, 1.0);
        for (int i = 0; i < RecordLength; i++)
            ir[i] += ring[i];

        var surface = BurstDecay.Compute(ir, Peak, SampleRateHz, RecordLength / 4, Settings());

        int low = NearestColumn(surface.FreqHz, 500.0);
        int high = NearestColumn(surface.FreqHz, 5000.0);

        for (double periods = 4.0; periods <= 14.0; periods += 1.0)
            Assert.Equal(LevelAt(surface, low, periods), LevelAt(surface, high, periods), 1.0);

        // And both are on the ring's own schedule rather than merely on each other's. Read as a
        // slope: the level at period zero is not 0 dB and is not meant to be, because a symmetric
        // wavelet centred on the impulse sees only half of a causal ring and has to ramp up first.
        foreach (int column in new[] { low, high })
        {
            double fell = LevelAt(surface, column, 4.0) - LevelAt(surface, column, 14.0);
            Assert.Equal(10.0, fell, 1.0);
        }
    }

    /// <summary>
    /// Per-frequency normalisation, which is what deletes the DUT's frequency response from the
    /// picture: the same ring 60 dB quieter has to draw the same curve, because a burst decay
    /// answers "how long" and how loud is the other measurement's question.
    ///
    /// <para>Measured on two separate records rather than on one holding both rings, deliberately.
    /// A resonance really does have content well outside its own band — a second-order pole 40 dB
    /// louder two octaves away genuinely dominates the quieter one's band near t = 0 — so a
    /// two-ring record tests the physics of resonators rather than the normalisation.</para>
    /// </summary>
    [Fact]
    public void Compute_NormalisesEachFrequencyToItself()
    {
        var loud = BurstDecay.Compute(Ring(1000.0, 1.5), Peak, SampleRateHz, RecordLength / 4, Settings());
        var faint = BurstDecay.Compute(Ring(1000.0, 1.5, 0.001), Peak, SampleRateHz, RecordLength / 4, Settings());

        int column = NearestColumn(loud.FreqHz, 1000.0);

        for (double periods = 0.0; periods <= 20.0; periods += 0.5)
            Assert.Equal(LevelAt(loud, column, periods), LevelAt(faint, column, periods), 0.01);

        // Every column tops out at exactly 0 dB, whatever it was worth in volts.
        foreach (var surface in new[] { loud, faint })
        {
            double best = surface.Slices
                .Where(s => column >= s.FirstValidColumn)
                .Max(s => s.LevelDb[column - s.FirstValidColumn]);
            Assert.Equal(0.0, best, 0.001);
        }
    }

    /// <summary>
    /// Twenty periods of 200 Hz is 100 ms, and a record that holds 40 ms after the impulse cannot
    /// answer for them. What sits there instead is the zeros the sweep gate left, which read as a
    /// perfect decay — the most flattering possible answer, at exactly the frequencies least likely
    /// to deserve it. The deep slices stop short instead, and they stop short from the left, because
    /// the wavelet's reach is the same number of periods at every frequency.
    /// </summary>
    [Fact]
    public void Compute_WhereTheRecordRunsOut_StopsTheSliceRatherThanReadingTheGate()
    {
        var ir = Ring(1000.0, 0.5);

        // 40 ms of usable record.
        var surface = BurstDecay.Compute(ir, Peak, SampleRateHz, 1920, Settings(maxPeriods: 20));

        Assert.Equal(0, surface.Slices[0].FirstValidColumn);
        Assert.True(surface.Slices[^1].FirstValidColumn > 0, "the deepest slice should have run out at the bottom of the band");

        // Monotone, and every slice's array matches the span it claims.
        for (int s = 1; s < surface.Slices.Count; s++)
            Assert.True(surface.Slices[s].FirstValidColumn >= surface.Slices[s - 1].FirstValidColumn);

        foreach (var slice in surface.Slices)
            Assert.Equal(surface.FreqHz.Length - slice.FirstValidColumn, slice.LevelDb.Length);

        // Where it stopped is where twenty periods stop fitting in 40 ms, plus the wavelet's own
        // half-support — a fixed couple of periods on top, at every frequency.
        int firstValid = surface.Slices[^1].FirstValidColumn;
        Assert.InRange(surface.FreqHz[firstValid], 500.0, 800.0);
    }

    /// <summary>
    /// What the resolution setting does, and what it does not. Convolving an exponential decay with
    /// a Gaussian scales it by a constant, so <b>both</b> settings report the decay <i>rate</i>
    /// correctly — the wider wavelet does not make a resonance look longer-lived than it is. What it
    /// costs is the start: a four-period wavelet takes twice as many periods to reach full output,
    /// so the first cycles of every decay are the analysis rather than the DUT.
    /// </summary>
    [Fact]
    public void Compute_BothResolutions_ReadTheSameDecayRate_AndTheNarrowerOneStartsLater()
    {
        var ir = Ring(2000.0, 2.0);

        var fast = BurstDecay.Compute(ir, Peak, SampleRateHz, RecordLength / 4, Settings(resolution: BurstDecayResolution.PreferTime));
        var narrow = BurstDecay.Compute(ir, Peak, SampleRateHz, RecordLength / 4, Settings(resolution: BurstDecayResolution.PreferFrequency));

        int column = NearestColumn(fast.FreqHz, 2000.0);

        foreach (var surface in new[] { fast, narrow })
        {
            double fell = LevelAt(surface, column, 8.0) - LevelAt(surface, column, 18.0);
            Assert.Equal(20.0, fell, 1.5);
        }

        Assert.True(
            PeakPeriods(narrow, column) > PeakPeriods(fast, column),
            $"prefer-frequency peaked at {PeakPeriods(narrow, column)} periods, prefer-time at {PeakPeriods(fast, column)}");
    }

    /// <summary>Where a column reaches its own 0 dB — how long the analysis took to open up.</summary>
    private static double PeakPeriods(DecaySurface surface, int column) =>
        surface.Slices
            .Where(s => column >= s.FirstValidColumn)
            .MaxBy(s => s.LevelDb[column - s.FirstValidColumn])!
            .Value;
}
