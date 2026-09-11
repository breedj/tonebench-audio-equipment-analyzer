using System.Numerics;

namespace Qa403.Dsp.Tests;

/// <summary>
/// The impedance solver against synthesised dividers whose answer is known by construction, so every
/// assertion here is against arithmetic rather than against a previous run of the same code.
/// </summary>
public class ImpedanceTests
{
    private const double ReferenceOhms = 100.0;
    private const double BinHz = 48000.0 / 16384.0;

    /// <summary>Bins the synthetic measurements are built over. Enough spread to catch a frequency-dependent error.</summary>
    private static readonly int[] Bins = [37, 149, 512, 1301, 4096, 6827];

    [Fact]
    public void Solve_OfAnIdealDivider_RecoversTheResistor()
    {
        var z = Solve(_ => new Complex(8.0, 0.0), new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
        });

        foreach (int bin in Bins)
        {
            Assert.Equal(8.0, z[bin].Magnitude, 9);
            Assert.Equal(0.0, z[bin].Phase, 9);
        }
    }

    [Fact]
    public void Solve_OfAReactiveLoad_RecoversMagnitudeAndPhase()
    {
        const double seriesOhms = 6.0;
        const double henry = 1.0e-3;

        var z = Solve(f => Load(f, seriesOhms, henry), new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
        });

        foreach (int bin in Bins)
        {
            var expected = Load(bin * BinHz, seriesOhms, henry);
            Assert.Equal(expected.Magnitude, z[bin].Magnitude, 9);
            Assert.Equal(expected.Phase, z[bin].Phase, 9);
        }
    }

    /// <summary>
    /// The whole point of the open calibration: the two ADC channels do not have identical gain or
    /// phase, and an uncorrected mismatch is reported as DUT impedance.
    /// </summary>
    [Fact]
    public void Solve_WithAnOpenCal_RemovesTheChannelMismatch()
    {
        var mismatch = Mismatch(gainDb: 0.3, phaseDeg: 4.0);

        var z = Solve(_ => new Complex(8.0, 0.0), new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            OpenCal = Curve(_ => OpenH(ReferenceOhms, null, mismatch)),
        }, mismatch);

        foreach (int bin in Bins)
        {
            Assert.Equal(8.0, z[bin].Magnitude, 9);
            Assert.Equal(0.0, z[bin].Phase, 9);
        }
    }

    /// <summary>
    /// What the open calibration is worth, stated as a number. A mismatch well inside what two ADC
    /// channels can differ by turns an 8.000 ohm resistor into <b>8.303 ohms at 4.33 degrees</b> -- a
    /// reactance that is not there at all, and the reason the phase trace is not believable without
    /// this step.
    /// </summary>
    [Fact]
    public void Solve_WithoutTheOpenCal_ReportsTheMismatchAsImpedance()
    {
        var mismatch = Mismatch(gainDb: 0.3, phaseDeg: 4.0);

        var z = Solve(_ => new Complex(8.0, 0.0), new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
        }, mismatch);

        foreach (int bin in Bins)
        {
            Assert.Equal(8.30267, z[bin].Magnitude, 4);
            Assert.Equal(4.33184, z[bin].Phase * 180.0 / Math.PI, 4);
        }
    }

    [Fact]
    public void Solve_WithAShortCal_SubtractsTheFixtureImpedance()
    {
        var cable = new Complex(0.15, 0.0);

        var z = Solve(_ => new Complex(8.0, 0.0) + cable, new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            ShortCal = Curve(_ => DividerH(cable, ReferenceOhms, null, Complex.One)),
        });

        foreach (int bin in Bins)
        {
            Assert.Equal(8.0, z[bin].Magnitude, 9);
            Assert.Equal(0.0, z[bin].Phase, 9);
        }
    }

    /// <summary>A measured short and a typed-in cable impedance must agree when they describe the same leads.</summary>
    [Fact]
    public void Solve_WithAFixedCable_SubtractsResistanceAndInductance()
    {
        const double cableOhms = 0.15;
        const double cableHenry = 1.0e-6;

        var z = Solve(f => new Complex(8.0, 0.0) + Load(f, cableOhms, cableHenry), new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            CableOhms = cableOhms,
            CableHenry = cableHenry,
        });

        foreach (int bin in Bins)
        {
            Assert.Equal(8.0, z[bin].Magnitude, 9);
            Assert.Equal(0.0, z[bin].Phase, 9);
        }
    }

    /// <summary>
    /// An open circuit is a legitimate reading, not an error: it is what the fixture measures with
    /// nothing connected. It has to come back finite, because an infinity poisons a chart autoscale
    /// and decodes out of a float BLOB as NaN.
    /// </summary>
    [Fact]
    public void Solve_AtAnOpenCircuit_ClampsInsteadOfDiverging()
    {
        var h = new Complex[8];
        Array.Fill(h, Complex.One);

        var z = ImpedanceMath.Solve(h, new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
        });

        foreach (var value in z)
        {
            Assert.True(double.IsFinite(value.Real));
            Assert.True(double.IsFinite(value.Imaginary));
            Assert.Equal(ImpedanceMath.MaxOhms, value.Magnitude, 0);
        }
    }

    /// <summary>
    /// A valve amplifier's grid input is comparable to the analyzer's own input impedance, so the
    /// analyzer sits across the DUT and takes a share of the current. Told what its input impedance
    /// is, the solver takes both that loading and its effect on the open calibration back out.
    /// </summary>
    [Fact]
    public void Solve_WithTheAnalyzerInputDeEmbedded_RecoversAHighImpedanceDut()
    {
        const double dutOhms = 470_000.0;
        const double referenceOhms = 100_000.0;
        const double analyzerOhms = 100_000.0;
        var mismatch = Mismatch(gainDb: 0.3, phaseDeg: 4.0);

        var h = Curve(_ => DividerH(new Complex(dutOhms, 0.0), referenceOhms, analyzerOhms, mismatch));

        var z = ImpedanceMath.Solve(h, new ImpedanceSolveOptions
        {
            ReferenceOhms = referenceOhms,
            BinHz = BinHz,
            OpenCal = Curve(_ => OpenH(referenceOhms, analyzerOhms, mismatch)),
            AnalyzerInputOhms = analyzerOhms,
        });

        foreach (int bin in Bins)
            Assert.Equal(1.0, z[bin].Magnitude / dutOhms, 6);
    }

    /// <summary>
    /// The same measurement with the de-embed left off, as the size of the error it exists to remove.
    /// It reads roughly <b>twice</b> the true impedance -- not a little high: the open calibration
    /// divides out <c>Z_in/(R + Z_in)</c> along with the channel mismatch, and when the reference
    /// resistor and the analyzer input are comparable that factor is a half.
    /// </summary>
    [Fact]
    public void Solve_WithoutTheDeEmbed_ReadsAHighImpedanceDutAboutTwiceTooHigh()
    {
        const double dutOhms = 470_000.0;
        const double referenceOhms = 100_000.0;
        const double analyzerOhms = 100_000.0;
        var mismatch = Mismatch(gainDb: 0.3, phaseDeg: 4.0);

        var h = Curve(_ => DividerH(new Complex(dutOhms, 0.0), referenceOhms, analyzerOhms, mismatch));

        var z = ImpedanceMath.Solve(h, new ImpedanceSolveOptions
        {
            ReferenceOhms = referenceOhms,
            BinHz = BinHz,
            OpenCal = Curve(_ => OpenH(referenceOhms, analyzerOhms, mismatch)),
        });

        foreach (int bin in Bins)
            Assert.InRange(z[bin].Magnitude / dutOhms, 1.9, 2.1);
    }

    /// <summary>
    /// And the same case at loudspeaker impedances, where the de-embed barely registers: leaving it
    /// off costs <b>0.0087 dB</b> on an 8 ohm load through a 100 ohm reference, which is the figure
    /// <see cref="ImpedanceMath.Solve"/>'s own remarks quote. That is why the correction is off by
    /// default rather than guessed at -- it matters only where the reference resistor approaches the
    /// analyzer's input impedance.
    /// </summary>
    [Fact]
    public void Solve_WithoutTheDeEmbed_IsNegligibleAtLoudspeakerImpedances()
    {
        const double dutOhms = 8.0;
        const double analyzerOhms = 100_000.0;

        var h = Curve(_ => DividerH(new Complex(dutOhms, 0.0), ReferenceOhms, analyzerOhms, Complex.One));

        var z = ImpedanceMath.Solve(h, new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            OpenCal = Curve(_ => OpenH(ReferenceOhms, analyzerOhms, Complex.One)),
        });

        foreach (int bin in Bins)
        {
            double errorDb = ImpedanceMath.ToDbOhms(z[bin].Magnitude) - ImpedanceMath.ToDbOhms(dutOhms);
            Assert.Equal(0.00868, errorDb, 5);
        }
    }

    [Fact]
    public void ToDbOhms_RoundTripsThroughFromDbOhms()
    {
        foreach (double ohms in new[] { 0.05, 1.0, 8.0, 470.0, 100_000.0 })
            Assert.Equal(ohms, ImpedanceMath.FromDbOhms(ImpedanceMath.ToDbOhms(ohms)), 9);
    }

    [Fact]
    public void MedianOhms_TakesTheMiddleOfTheRequestedBandOnly()
    {
        Complex[] z = [new(1.0, 0.0), new(2.0, 0.0), new(3.0, 0.0), new(400.0, 0.0), new(500.0, 0.0)];

        Assert.Equal(2.0, ImpedanceMath.MedianOhms(z, 0, 2), 9);
        Assert.Equal(3.0, ImpedanceMath.MedianOhms(z, 0, 4), 9);
        Assert.True(double.IsNaN(ImpedanceMath.MedianOhms(z, 4, 2)));
    }

    /// <summary>
    /// Swapped inputs, exactly as inverting the divider produces them: the 8 ohm fixture read with
    /// the channels the wrong way round gives <c>H = 108/8</c> and <c>Z = 100 H/(1 - H) = -108</c> —
    /// a negative real impedance at every bin. Read the right way round the same fixture has none.
    /// </summary>
    [Fact]
    public void NegativeRealFraction_IsOneForSwappedChannels_AndZeroForAPassiveLoad()
    {
        var swapped = Solve(_ => new Complex(8.0, 0.0), new ImpedanceSolveOptions { ReferenceOhms = ReferenceOhms, BinHz = BinHz }, invert: true);
        var correct = Solve(_ => new Complex(8.0, 0.0), new ImpedanceSolveOptions { ReferenceOhms = ReferenceOhms, BinHz = BinHz });

        Assert.Equal(1.0, ImpedanceMath.NegativeRealFraction(swapped, 0, swapped.Length - 1), 9);
        Assert.Equal(-108.0, swapped[Bins[0]].Real, 6);
        Assert.Equal(0.0, ImpedanceMath.NegativeRealFraction(correct, 0, correct.Length - 1), 9);
        Assert.True(double.IsNaN(ImpedanceMath.NegativeRealFraction(correct, 4, 2)));
    }

    // --- synthesis of the measurements the solver is meant to invert ---

    /// <summary>A series R + L branch at one frequency.</summary>
    private static Complex Load(double freqHz, double ohms, double henry) =>
        new(ohms, 2.0 * Math.PI * freqHz * henry);

    /// <summary>Inter-channel gain and phase error, as the ratio the DUT channel reads high by.</summary>
    private static Complex Mismatch(double gainDb, double phaseDeg) =>
        Complex.FromPolarCoordinates(Math.Pow(10.0, gainDb / 20.0), phaseDeg * Math.PI / 180.0);

    /// <summary>
    /// What the fixture actually measures: the branch loaded by the analyzer's input, divided against
    /// the reference resistor, times the channel mismatch.
    /// </summary>
    private static Complex DividerH(Complex branch, double referenceOhms, double? analyzerOhms, Complex mismatch)
    {
        Complex node = analyzerOhms is { } zin ? branch * zin / (branch + zin) : branch;
        return mismatch * node / (referenceOhms + node);
    }

    /// <summary>The open standard: the branch removed entirely, leaving only the analyzer's own input on the node.</summary>
    private static Complex OpenH(double referenceOhms, double? analyzerOhms, Complex mismatch) =>
        analyzerOhms is { } zin
            ? mismatch * zin / (referenceOhms + zin)
            : mismatch;

    private static Complex[] Curve(Func<double, Complex> at)
    {
        var values = new Complex[8193];
        for (int k = 0; k < values.Length; k++)
            values[k] = at(k * BinHz);
        return values;
    }

    /// <summary>Builds the measured ratio for a branch and solves it back, which is the round trip every test here makes.</summary>
    /// <summary>
    /// A stepped run's curve is not on a uniform bin grid, so the solver is handed the frequency of
    /// every point instead. Fed the uniform grid it already assumes, the two paths must be the same
    /// arithmetic -- which is what makes the new field an <i>axis</i> rather than a second mode.
    /// </summary>
    [Fact]
    public void Solve_WithAnExplicitFrequencyAxis_MatchesTheUniformBinPath()
    {
        var h = Curve(f => DividerH(new Complex(6.0, 2.0 * Math.PI * f * 1.0e-3), ReferenceOhms, null, Complex.One));

        var uniform = new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            CableOhms = 0.12,
            CableHenry = 0.6e-6,
        };

        var frequencies = new double[h.Length];
        for (int k = 0; k < frequencies.Length; k++)
            frequencies[k] = k * BinHz;

        var explicitAxis = uniform with { FrequenciesHz = frequencies };

        var a = ImpedanceMath.Solve(h, uniform);
        var b = ImpedanceMath.Solve(h, explicitAxis);

        foreach (int bin in Bins)
        {
            Assert.Equal(a[bin].Real, b[bin].Real, 12);
            Assert.Equal(a[bin].Imaginary, b[bin].Imaginary, 12);
        }
    }

    /// <summary>
    /// And the axis is actually read: the fixed cable inductance is the one term that depends on
    /// frequency, so a solver still counting bins would put the wrong reactance on every point.
    /// </summary>
    [Fact]
    public void Solve_SubtractsTheCableInductanceAtTheStatedFrequencies()
    {
        // A pure 8 ohm load at three arbitrary log-spaced frequencies, as a stepped run would produce.
        double[] frequencies = [37.0, 1017.0, 12_345.0];
        var h = new Complex[frequencies.Length];
        for (int i = 0; i < h.Length; i++)
            h[i] = DividerH(new Complex(8.0, 0.0), ReferenceOhms, null, Complex.One);

        const double CableHenry = 1.0e-6;
        var z = ImpedanceMath.Solve(h, new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            FrequenciesHz = frequencies,
            CableHenry = CableHenry,
        });

        for (int i = 0; i < frequencies.Length; i++)
        {
            Assert.Equal(8.0, z[i].Real, 9);
            Assert.Equal(-2.0 * Math.PI * frequencies[i] * CableHenry, z[i].Imaginary, 9);
        }
    }

    [Fact]
    public void Solve_WithAFrequencyAxisOfTheWrongLength_IsRejected()
    {
        var h = Curve(_ => DividerH(new Complex(8.0, 0.0), ReferenceOhms, null, Complex.One));

        Assert.Throws<ArgumentException>(() => ImpedanceMath.Solve(h, new ImpedanceSolveOptions
        {
            ReferenceOhms = ReferenceOhms,
            BinHz = BinHz,
            FrequenciesHz = [1.0, 2.0, 3.0],
        }));
    }

    /// <param name="invert">Feed the solver <c>1/H</c> — the two input channels the wrong way round.</param>
    private static Complex[] Solve(Func<double, Complex> branch, ImpedanceSolveOptions options, Complex? mismatch = null, bool invert = false)
    {
        var m = mismatch ?? Complex.One;
        var h = Curve(f => DividerH(branch(f), ReferenceOhms, null, m));
        if (invert)
        {
            for (int k = 0; k < h.Length; k++)
                h[k] = h[k] == Complex.Zero ? Complex.Zero : Complex.One / h[k];
        }

        return ImpedanceMath.Solve(h, options);
    }
}
