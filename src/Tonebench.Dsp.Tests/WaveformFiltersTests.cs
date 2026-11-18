using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The display-path filters, against the response they claim and against the one thing a scope
/// filter must not do: move the waveform.
/// </summary>
public class WaveformFiltersTests
{
    private const double Fs = 48_000.0;

    private static double[] Sine(double freqHz, int frames = 24_000, double amplitude = 1.0, double phase = 0.0)
    {
        var x = new double[frames];
        for (int i = 0; i < frames; i++)
            x[i] = amplitude * Math.Sin((2.0 * Math.PI * freqHz * i / Fs) + phase);
        return x;
    }

    private static double[] Filter(ScopeFilterSpec spec, double[] source)
    {
        var destination = new double[source.Length];
        WaveformFilters.Apply(spec, source, Fs, destination);
        return destination;
    }

    /// <summary>The amplitude of <paramref name="x"/> over its middle, away from whatever the ends do.</summary>
    private static double Amplitude(double[] x)
    {
        int from = x.Length / 4;
        int to = x.Length - from;
        double peak = 0.0;
        for (int i = from; i < to; i++)
            peak = Math.Max(peak, Math.Abs(x[i]));
        return peak;
    }

    // -----------------------------------------------------------------------
    // What it does to a tone
    // -----------------------------------------------------------------------

    /// <summary>
    /// A tone in the passband comes through at its own amplitude, and one in the stopband is cut by
    /// what <see cref="WaveformFilters.ResponseDb"/> says it will be.
    ///
    /// <para>Asserted against the stated response rather than against a tolerance, so the two
    /// cannot drift: the preview the display rail draws is that same function.</para>
    /// </summary>
    [Theory]
    [InlineData(ScopeFilterKind.LowPass, 1_000.0, 100.0)]
    [InlineData(ScopeFilterKind.LowPass, 1_000.0, 4_000.0)]
    [InlineData(ScopeFilterKind.HighPass, 1_000.0, 8_000.0)]
    [InlineData(ScopeFilterKind.HighPass, 1_000.0, 200.0)]
    [InlineData(ScopeFilterKind.BandPass, 1_000.0, 1_000.0)]
    [InlineData(ScopeFilterKind.BandPass, 1_000.0, 4_000.0)]
    [InlineData(ScopeFilterKind.Notch, 1_000.0, 4_000.0)]
    public void ATone_ComesThroughAtTheStatedResponse(ScopeFilterKind kind, double cornerHz, double toneHz)
    {
        var spec = new ScopeFilterSpec(kind, cornerHz, Q: 2.0, Order: 4);
        double expectedDb = WaveformFilters.ResponseDb(spec, toneHz, Fs);

        // Against the unfiltered tone measured the same way, not against 1.0: a sampled sine's
        // own largest sample falls short of its amplitude at a few samples a period -- 0.866 at
        // six of them, which is 1.25 dB of the measurement rather than of the filter.
        var tone = Sine(toneHz);
        double measuredDb = 20.0 * Math.Log10(Amplitude(Filter(spec, tone)) / Amplitude(tone));

        Assert.Equal(expectedDb, measuredDb, 1);
    }

    /// <summary>
    /// The corner is −6 dB and not −3, because every section runs forward and backward: the stated
    /// response is |H|², and that is the response the trace really gets.
    /// </summary>
    [Fact]
    public void TheCornerIsSixDecibelsDown_BecauseTheFilterRunsTwice()
    {
        var spec = new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0, Order: 2);

        Assert.Equal(-6.02, WaveformFilters.ResponseDb(spec, 1_000.0, Fs), 2);

        var tone = Sine(1_000.0);
        double measured = 20.0 * Math.Log10(Amplitude(Filter(spec, tone)) / Amplitude(tone));
        Assert.Equal(-6.02, measured, 1);
    }

    /// <summary>
    /// And the skirt is twice the order's: a second-order low-pass run twice falls 24 dB an octave,
    /// not 12.
    /// </summary>
    [Fact]
    public void TheSkirtIsTwiceTheOrders()
    {
        var spec = new ScopeFilterSpec(ScopeFilterKind.LowPass, 100.0, Order: 2);

        double atOne = WaveformFilters.ResponseDb(spec, 1_000.0, Fs);
        double atTwo = WaveformFilters.ResponseDb(spec, 2_000.0, Fs);

        // 24 dB is the analog asymptote and it is a floor rather than an equality: the bilinear
        // mapping compresses the axis toward Nyquist, so a digital skirt is always at least that
        // steep and visibly steeper near the top of the band.
        double octave = atTwo - atOne;
        Assert.InRange(octave, -25.0, -23.9);
    }

    /// <summary>
    /// A higher order is a <b>Butterworth</b> of that order rather than a stack of identical
    /// sections, which is a different filter: two sections at Q = 0.707 give −6 dB at the corner
    /// where a fourth-order Butterworth gives −3, before the second pass doubles either.
    /// </summary>
    [Fact]
    public void AHigherOrderIsAButterworthOfThatOrder_NotAStackOfIdenticalSections()
    {
        var second = new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0, Order: 2);
        var fourth = new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0, Order: 4);

        // Both are -6.02 dB at the corner: a Butterworth of any order is -3 dB there, doubled.
        Assert.Equal(-6.02, WaveformFilters.ResponseDb(second, 1_000.0, Fs), 2);
        Assert.Equal(-6.02, WaveformFilters.ResponseDb(fourth, 1_000.0, Fs), 2);

        // A stack of identical 0.707 sections would read -12.04 instead, which is the mistake this
        // pins: twice the corner attenuation and a rounded knee.
        Assert.True(
            WaveformFilters.ResponseDb(fourth, 2_000.0, Fs) < WaveformFilters.ResponseDb(second, 2_000.0, Fs) - 10.0,
            "the fourth-order skirt has to be far steeper than the second's");
    }

    [Fact]
    public void ANotchRemovesItsOwnFrequencyAndLeavesTheRest()
    {
        var spec = new ScopeFilterSpec(ScopeFilterKind.Notch, 1_000.0, Q: 10.0);

        Assert.True(WaveformFilters.ResponseDb(spec, 1_000.0, Fs) < -60.0);
        Assert.Equal(0.0, WaveformFilters.ResponseDb(spec, 100.0, Fs), 1);
        Assert.Equal(0.0, WaveformFilters.ResponseDb(spec, 10_000.0, Fs), 1);

        // A tone plus hum: the hum goes and the tone stays.
        var tone = Sine(5_000.0);
        var hum = Sine(1_000.0, amplitude: 0.5);
        var mixed = new double[tone.Length];
        for (int i = 0; i < mixed.Length; i++)
            mixed[i] = tone[i] + hum[i];

        Assert.Equal(1.5, Amplitude(mixed), 2);
        Assert.Equal(1.0, Amplitude(Filter(spec, mixed)), 2);
    }

    // -----------------------------------------------------------------------
    // Zero phase
    // -----------------------------------------------------------------------

    /// <summary>
    /// The decision the whole thing rests on: the waveform does not move.
    ///
    /// <para>A causal filter delays what it passes, and on this screen the x axis is time relative
    /// to the trigger — so an edge would slide off the very crossing it fired on. Running the
    /// filter forward and then backward cancels the delay exactly, which this asserts by looking
    /// for the peak of a pulse before and after.</para>
    /// </summary>
    [Fact]
    public void TheWaveformDoesNotMove()
    {
        var pulse = new double[8_192];
        for (int i = 4_000; i < 4_200; i++)
            pulse[i] = 1.0;

        var filtered = Filter(new ScopeFilterSpec(ScopeFilterKind.LowPass, 2_000.0, Order: 4), pulse);

        // Its centre of mass, not its largest sample: the filtered pulse has a flat top, so which
        // sample is biggest is arbitrary where the centroid is not.
        double mass = 0.0, moment = 0.0;
        for (int i = 0; i < filtered.Length; i++)
        {
            mass += filtered[i];
            moment += i * filtered[i];
        }

        // The pulse spans 4000..4199, so its centre is 4099.5 -- and that is where it stays.
        Assert.Equal(4_099.5, moment / mass, 1);

        // And it is symmetric about that centre, which a causal filter's output is not.
        for (int d = 10; d <= 300; d += 10)
            Assert.Equal(filtered[4_099 - d], filtered[4_100 + d], 9);
    }

    [Fact]
    public void ASineKeepsItsPhase()
    {
        var spec = new ScopeFilterSpec(ScopeFilterKind.LowPass, 2_000.0, Order: 4);
        var source = Sine(1_000.0);
        var filtered = Filter(spec, source);

        // Scaled by the response and otherwise the same wave: the cross-correlation at lag zero is
        // the whole of it, where any phase shift would reduce it.
        double gain = Math.Pow(10.0, WaveformFilters.ResponseDb(spec, 1_000.0, Fs) / 20.0);
        for (int i = source.Length / 4; i < source.Length * 3 / 4; i += 37)
            Assert.Equal(source[i] * gain, filtered[i], 3);
    }

    // -----------------------------------------------------------------------
    // The ends
    // -----------------------------------------------------------------------

    /// <summary>
    /// The record is a window cut out of a continuous stream, so the signal has no edges — only the
    /// array does. Without the reflected padding the filter starts from rest and rings at both
    /// ends, which draws as a transient the DUT never produced, at the very start of the record
    /// where a pre-trigger view is read.
    /// </summary>
    [Fact]
    public void ItDoesNotRingOnTheRecordsOwnEnds()
    {
        var spec = new ScopeFilterSpec(ScopeFilterKind.LowPass, 4_000.0, Order: 4);
        // A record that begins and ends far from zero, which is what an arbitrary window of a sine
        // does and what a filter starting from rest would step from.
        var source = Sine(100.0, frames: 9_600, phase: Math.PI / 2.0);
        var filtered = Filter(spec, source);

        double gain = Math.Pow(10.0, WaveformFilters.ResponseDb(spec, 100.0, Fs) / 20.0);
        // The first and last samples, which are where the fault would be, and are what the
        // padding exists for.
        Assert.Equal(source[0] * gain, filtered[0], 3);
        Assert.Equal(source[^1] * gain, filtered[^1], 3);
    }

    // -----------------------------------------------------------------------
    // Doing nothing
    // -----------------------------------------------------------------------

    [Fact]
    public void AFilterThatDoesNothing_Copies()
    {
        var source = Sine(1_000.0, frames: 512);

        foreach (var spec in new[]
                 {
                     ScopeFilterSpec.Off,
                     new ScopeFilterSpec(ScopeFilterKind.LowPass, 0.0),
                     // A corner at or above Nyquist has no sections to build.
                     new ScopeFilterSpec(ScopeFilterKind.LowPass, Fs),
                 })
        {
            var filtered = Filter(spec, source);
            Assert.Equal(source, filtered);
            Assert.Equal(0.0, WaveformFilters.ResponseDb(spec, 1_000.0, Fs));
        }
    }

    [Fact]
    public void AWindowTooShortToFilter_IsCopied()
    {
        double[] source = [1.0, 2.0, 3.0];
        var destination = new double[3];
        WaveformFilters.Apply(new ScopeFilterSpec(ScopeFilterKind.LowPass, 1_000.0), source, Fs, destination);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void TheOrderIsEvenAndBounded()
    {
        Assert.Equal(2, WaveformFilters.NormalisedOrder(1));
        Assert.Equal(2, WaveformFilters.NormalisedOrder(2));
        Assert.Equal(4, WaveformFilters.NormalisedOrder(3));
        Assert.Equal(8, WaveformFilters.NormalisedOrder(99));
        Assert.Equal(2, WaveformFilters.NormalisedOrder(-5));
    }

    /// <summary>Nothing it produces is anything but a number, whatever it is handed.</summary>
    [Fact]
    public void NothingItProducesIsAnythingButANumber()
    {
        foreach (var spec in new[]
                 {
                     new ScopeFilterSpec(ScopeFilterKind.BandPass, 1_000.0, Q: 1e9),
                     new ScopeFilterSpec(ScopeFilterKind.Notch, 1e-9, Q: 0.0),
                     new ScopeFilterSpec(ScopeFilterKind.HighPass, 0.001, Order: 8),
                 })
        {
            var filtered = Filter(spec, Sine(1_000.0, frames: 4_096));
            Assert.All(filtered, v => Assert.True(double.IsFinite(v), $"{v} is not finite"));
            foreach (double f in new[] { 0.0, 1.0, 1_000.0, 20_000.0, 23_999.0 })
                Assert.True(double.IsFinite(WaveformFilters.ResponseDb(spec, f, Fs)));
        }
    }
}
