using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The simulation resampler. The load-bearing test here is
/// <see cref="Decimate_AToneAboveTheNewNyquist_DoesNotReappearAtItsAliasFrequency"/>, and that is
/// not a guess: replacing the filter in <see cref="Resampler.Decimate"/> with naive nearest-sample
/// picking was tried, and <b>the other nine tests in this file all still passed</b> while that one
/// reported the 200 kHz tone coming back at 27.5 kHz at -0.6 dB -- full amplitude, in the audio
/// band, reading as distortion the circuit does not have. An in-band tone survives naive
/// resampling perfectly well, so only looking for the fold catches a missing filter. Do not loosen
/// its threshold.
/// </summary>
public class ResamplerTests
{
    // The reference simulation's own numbers: a 1 MHz simulator grid, 95 ms of it, analysed as a
    // 16384-point record, which makes the derived rate 16384/0.095 ≈ 172.5 kHz.
    private const double SourceRateHz = 1_000_000.0;
    private const int SourceSamples = 95_000;
    private const int TargetSamples = 16_384;
    private const double TargetRateHz = TargetSamples / 0.095;

    /// <summary>A frequency that is exactly on a bin of the decimated record, so a Goertzel of it reads no leakage.</summary>
    private static double CoherentHz(int bin) => bin * TargetRateHz / TargetSamples;

    [Fact]
    public void Decimate_PassesAnInBandToneAtItsOwnAmplitude()
    {
        double f = CoherentHz(95); // ~1000.2 Hz
        double[] source = Sine(SourceSamples, f, SourceRateHz, amplitude: 1.0);
        var destination = new double[TargetSamples];

        Resampler.Decimate(source, SourceRateHz, TargetRateHz, destination);

        double amplitude = Goertzel(destination, f, TargetRateHz);
        Assert.Equal(1.0, amplitude, 0.002);
    }

    /// <summary>
    /// The one that matters. 200 kHz is above the decimated Nyquist (86.3 kHz), so naive resampling
    /// folds it to |200000 − 172505| ≈ 27.5 kHz — squarely inside the audio band, at full amplitude,
    /// reading as distortion the circuit does not have. With the filter it must be gone, and
    /// "gone" is asserted at the alias frequency specifically rather than only as total energy.
    /// </summary>
    [Fact]
    public void Decimate_AToneAboveTheNewNyquist_DoesNotReappearAtItsAliasFrequency()
    {
        const double toneHz = 200_000.0;
        double[] source = Sine(SourceSamples, toneHz, SourceRateHz, amplitude: 1.0);
        var destination = new double[TargetSamples];

        Resampler.Decimate(source, SourceRateHz, TargetRateHz, destination);

        double aliasHz = Math.Abs(toneHz - TargetRateHz);
        Assert.InRange(aliasHz, 20_000.0, 40_000.0); // the fold really does land in band

        // Measured over the interior, not the whole record. The two ends carry a switch-on
        // transient that belongs to the finite record rather than to the filter: the kernel hangs
        // off the input there, and truncating a sine at all produces broadband content no filter
        // can remove. Measured: over the whole record the residue reads −56.1 dB, while over the
        // interior it is −131.6 dB at the alias bin and −128.8 dB broadband, which is the filter's
        // real rejection -- comfortably past the 100 dB it is designed for, because 200 kHz sits
        // well inside the stopband rather than at its edge. The pipeline never sees the difference,
        // because it applies an analysis window that tapers both ends of the record to nothing.
        var interior = destination.AsSpan(destination.Length / 10, destination.Length * 8 / 10);

        double atAlias = Goertzel(interior, aliasHz, TargetRateHz);
        Assert.True(
            20 * Math.Log10(Math.Max(atAlias, 1e-30)) < -110.0,
            $"alias at {aliasHz:F0} Hz came back at {20 * Math.Log10(Math.Max(atAlias, 1e-30)):F1} dB relative to the 1.0 input");

        double rms = 0.0;
        foreach (double v in interior)
            rms += v * v;
        rms = Math.Sqrt(rms / interior.Length);
        Assert.True(
            20 * Math.Log10(Math.Max(rms, 1e-30)) < -110.0,
            $"out-of-band tone left {20 * Math.Log10(Math.Max(rms, 1e-30)):F1} dB of broadband residue in the interior");
    }

    /// <summary>
    /// Distortion the circuit really does have has to survive intact, which is the other half of
    /// the filter being right: 1 % of second harmonic in, 1 % out.
    /// </summary>
    [Fact]
    public void Decimate_PreservesTheHarmonicRatioThatTheSignalCameWith()
    {
        double f = CoherentHz(95);
        var source = new double[SourceSamples];
        for (int n = 0; n < source.Length; n++)
        {
            double t = n / SourceRateHz;
            source[n] = Math.Sin(2 * Math.PI * f * t) + (0.01 * Math.Sin(2 * Math.PI * 2 * f * t));
        }

        var destination = new double[TargetSamples];
        Resampler.Decimate(source, SourceRateHz, TargetRateHz, destination);

        double h1 = Goertzel(destination, f, TargetRateHz);
        double h2 = Goertzel(destination, 2 * f, TargetRateHz);
        Assert.Equal(1.0, h2 / h1 * 100.0, 0.01); // per cent
    }

    /// <summary>
    /// The weight normalisation, which is what makes the gain exactly one at any fractional phase
    /// and at the two ends where the kernel hangs off the input.
    /// </summary>
    [Fact]
    public void Decimate_HasUnityGainAtDc_IncludingAtTheEdges()
    {
        var source = new double[SourceSamples];
        Array.Fill(source, 2.5);
        var destination = new double[TargetSamples];

        Resampler.Decimate(source, SourceRateHz, TargetRateHz, destination);

        foreach (double v in destination)
            Assert.Equal(2.5, v, 1e-9);
    }

    [Fact]
    public void Decimate_AtAnIntegerRatio_IsJustAsAccurate()
    {
        const double target = 200_000.0;
        const int count = 19_000;
        double f = 1_000.0; // coherent at both rates
        double[] source = Sine(SourceSamples, f, SourceRateHz, amplitude: 1.0);
        var destination = new double[count];

        Resampler.Decimate(source, SourceRateHz, target, destination);

        Assert.Equal(1.0, Goertzel(destination, f, target), 0.002);
    }

    [Fact]
    public void Decimate_ToAHigherRate_DoesNotFilterTheBandAway()
    {
        double f = 1_000.0;
        double[] source = Sine(4_800, f, 48_000.0, amplitude: 1.0);
        var destination = new double[9_600];

        Resampler.Decimate(source, 48_000.0, 96_000.0, destination);

        Assert.Equal(1.0, Goertzel(destination, f, 96_000.0), 0.01);
    }

    /// <summary>
    /// A simulator's time axis is adaptive, so the uniform regrid has to cope with steps that vary.
    /// This jitters the step by ±40 % around 1 µs — far more than the reference run, whose steps
    /// are all 1 µs bar a couple at the start — and still expects the tone back at its own level.
    /// </summary>
    [Fact]
    public void ToUniform_FromAJitteredTimeAxis_RecoversTheToneAtItsOwnAmplitude()
    {
        const double f = 1_000.0;
        var random = new Random(20261011);
        var timeS = new double[SourceSamples];
        var values = new double[SourceSamples];
        double t = 0.0;
        for (int n = 0; n < timeS.Length; n++)
        {
            timeS[n] = t;
            values[n] = Math.Sin(2 * Math.PI * f * t);
            t += 1e-6 * (0.6 + (0.8 * random.NextDouble()));
        }

        int count = Resampler.SampleCount(timeS[^1] - timeS[0], SourceRateHz) - 1;
        var uniform = new double[count];
        Resampler.ToUniform(timeS, values, timeS[0], SourceRateHz, uniform);

        Assert.Equal(1.0, Goertzel(uniform, f, SourceRateHz), 0.001);
    }

    [Fact]
    public void ToUniform_OutsideTheSourceRange_HoldsTheEndValues()
    {
        double[] timeS = [1.0, 2.0, 3.0];
        double[] values = [10.0, 20.0, 30.0];
        var destination = new double[5];

        // Starts half a second early and runs past the end.
        Resampler.ToUniform(timeS, values, 0.5, 1.0, destination);

        // t = 0.5, 1.5, 2.5, 3.5, 4.5 against points at t = 1, 2, 3.
        Assert.Equal(10.0, destination[0]); // before the first point: held
        Assert.Equal(15.0, destination[1]); // interpolated
        Assert.Equal(25.0, destination[2]);
        Assert.Equal(30.0, destination[3]); // past the last point: held
        Assert.Equal(30.0, destination[4]);
    }

    [Fact]
    public void ToUniform_InterpolatesLinearlyBetweenPoints()
    {
        double[] timeS = [0.0, 1.0];
        double[] values = [0.0, 10.0];
        var destination = new double[5];

        Resampler.ToUniform(timeS, values, 0.0, 4.0, destination);

        Assert.Equal([0.0, 2.5, 5.0, 7.5, 10.0], destination);
    }

    [Fact]
    public void SampleCount_IsTheWholeSamplesTheDurationHolds()
    {
        Assert.Equal(95_000, Resampler.SampleCount(0.095, 1_000_000.0));
        Assert.Equal(16_384, Resampler.SampleCount(16_384 / 172_505.0, 172_505.0));
    }

    /// <summary>
    /// The image of the source grid, which is what a simulation import shows on screen as lines
    /// that are harmonics of nothing.
    ///
    /// <para>Decimating is a resampling, so the output carries a replica of the input modulated by
    /// the source <i>grid</i> rate: a component at <c>|m·f_source ± k·f_tone|</c> folded into the
    /// new band, whose level is the kernel's own sidelobe floor rather than its design stopband
    /// edge. Reported 2026-10-19 from a real LTspice run: a 1 MHz grid, 245 ms of it, analysed as
    /// a 16384-point record — so the grid folded to 3102.04 Hz and put a line beside the 1 kHz
    /// tone at <b>4102.04 Hz, −163 dBc</b>, with the whole family at k·1 kHz ± m·3102 Hz under it.
    /// Analysing the same file at its native rate with no resampling puts those frequencies at
    /// −193 dBV, the simulator's own numerical floor: the lines were ours.</para>
    ///
    /// <para>Raising <see cref="Resampler.StopbandDb"/> from 100 to 160 is what removed them —
    /// −163 → −216 dBc at the reported run's ratio, −159 → −210 at this test's, and −145 → −188 at
    /// the tightest ratio an importable file can have — for about a third more time in the one
    /// place this runs (779 → 1030 ms for 245 k samples, Debug). That is what the constant buys,
    /// and this test is what stops it being shortened again.</para>
    /// </summary>
    [Fact]
    public void Decimate_LeavesTheSourceGridsImage_BelowWhatASimulationCanShow()
    {
        double f0 = CoherentHz(245);
        double[] source = Sine(SourceSamples, f0, SourceRateHz, amplitude: 1.0);
        var destination = new double[TargetSamples];

        Resampler.Decimate(source, SourceRateHz, TargetRateHz, destination);

        // Where the source grid folds to: |f_source − f0|, brought into the new band.
        double image = Math.Abs(SourceRateHz - f0);
        while (image > TargetRateHz / 2.0)
            image = Math.Abs(image - TargetRateHz);
        Assert.InRange(image, 1_000.0, TargetRateHz / 2.0); // it really does land in the band

        // Windowed, because the tone beside it is 160 dB louder and a rectangular projection
        // would read its leakage instead of the image. Kaiser7's sidelobes are −171 dB.
        double tone = WindowedBin(destination, f0, TargetRateHz);
        double imageLevel = WindowedBin(destination, image, TargetRateHz);
        double dbc = 20.0 * Math.Log10(imageLevel / tone);

        Assert.True(dbc < -200.0, $"image at {image:F1} Hz is {dbc:F1} dBc, which a chart would show");
    }

    // ---- helpers ----

    private static double[] Sine(int count, double freqHz, double sampleRateHz, double amplitude)
    {
        var x = new double[count];
        for (int n = 0; n < count; n++)
            x[n] = amplitude * Math.Sin(2 * Math.PI * freqHz * n / sampleRateHz);
        return x;
    }

    /// <summary>Peak amplitude of the component at <paramref name="freqHz"/>, by direct projection.</summary>
    private static double Goertzel(ReadOnlySpan<double> x, double freqHz, double sampleRateHz)
    {
        double w = 2 * Math.PI * freqHz / sampleRateHz;
        double re = 0.0, im = 0.0;
        for (int n = 0; n < x.Length; n++)
        {
            re += x[n] * Math.Cos(w * n);
            im -= x[n] * Math.Sin(w * n);
        }
        return 2.0 * Math.Sqrt((re * re) + (im * im)) / x.Length;
    }

    /// <summary>
    /// Peak amplitude at <paramref name="freqHz"/> read through a Kaiser7 window, so a line far
    /// below a tone nearby is measured rather than that tone's leakage.
    /// </summary>
    private static double WindowedBin(ReadOnlySpan<double> x, double freqHz, double sampleRateHz)
    {
        var window = Window.Get(WindowType.Kaiser7, x.Length);
        var windowed = new double[x.Length];
        Window.Apply(x, window, windowed);

        double w = 2 * Math.PI * freqHz / sampleRateHz;
        double re = 0.0, im = 0.0;
        for (int n = 0; n < windowed.Length; n++)
        {
            re += windowed[n] * Math.Cos(w * n);
            im -= windowed[n] * Math.Sin(w * n);
        }
        return 2.0 * Math.Sqrt((re * re) + (im * im)) / window.Sums.S1;
    }
}
