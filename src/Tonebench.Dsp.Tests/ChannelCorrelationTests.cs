using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The goniometer's readings, against pairs built from a formula.
///
/// <para>Every figure here is one a reader acts on — "the channels are out of phase", "the right
/// one is 3 dB down" — so each case states the angle or the ratio it builds in and asserts that
/// figure comes back, rather than asserting a tolerance around whatever the code happens to say.
/// </para>
/// </summary>
public class ChannelCorrelationTests
{
    private const double Fs = 48_000.0;
    private const double FreqHz = 1_000.0;

    /// <summary>A pair of 1 kHz sines where B lags A by <paramref name="phaseDeg"/>.</summary>
    private static (double[] A, double[] B) Pair(double phaseDeg, double amplitudeB = 1.0, int frames = 4_800)
    {
        var a = new double[frames];
        var b = new double[frames];
        double phase = phaseDeg * Math.PI / 180.0;
        for (int i = 0; i < frames; i++)
        {
            double t = 2.0 * Math.PI * FreqHz * i / Fs;
            a[i] = Math.Cos(t);
            b[i] = amplitudeB * Math.Cos(t - phase);
        }

        return (a, b);
    }

    private static ChannelCorrelationResult Measure(double[] a, double[] b, double? fa = FreqHz, double? fb = FreqHz) =>
        ChannelCorrelation.Measure(a, b, Fs, fa, fb);

    // -----------------------------------------------------------------------
    // Correlation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(60.0, 0.5)]
    [InlineData(90.0, 0.0)]
    [InlineData(120.0, -0.5)]
    [InlineData(180.0, -1.0)]
    public void TheCorrelationOfTwoSines_IsTheCosineOfTheirPhase(double phaseDeg, double expected)
    {
        var (a, b) = Pair(phaseDeg);

        Assert.Equal(expected, Measure(a, b).Correlation, 6);
    }

    /// <summary>
    /// It is a correlation and not a covariance, so a quieter channel does not read as a weaker
    /// relationship — which is exactly what a stereo meter must not do.
    /// </summary>
    [Fact]
    public void TheCorrelation_DoesNotFollowTheLevel()
    {
        var (loud, _) = Pair(0.0);
        var (_, quiet) = Pair(0.0, amplitudeB: 0.001);

        Assert.Equal(1.0, ChannelCorrelation.Measure(loud, quiet, Fs, FreqHz, FreqHz).Correlation, 6);
    }

    /// <summary>
    /// Taken about each channel's own mean, which on this hardware is not optional: both the QA403
    /// and a sound card are AC-coupled, so a standing offset is the front end's own and two
    /// different offsets would otherwise read as a correlation between them.
    /// </summary>
    [Fact]
    public void TheCorrelation_IsTakenAboutEachChannelsOwnMean()
    {
        var (a, b) = Pair(90.0);
        for (int i = 0; i < a.Length; i++)
        {
            a[i] += 2.0;
            b[i] -= 5.0;
        }

        Assert.Equal(0.0, Measure(a, b).Correlation, 6);
    }

    // -----------------------------------------------------------------------
    // Phase
    // -----------------------------------------------------------------------

    /// <summary>
    /// The reason phase is a quadrature correlation rather than <c>arccos(r)</c>: a lead and a lag
    /// of the same size give the same r and opposite Lissajous figures, so inverting r would lose
    /// the one thing the picture shows.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(-30.0)]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    [InlineData(150.0)]
    [InlineData(-150.0)]
    [InlineData(180.0)]
    public void ThePhase_IsSigned(double phaseDeg)
    {
        var (a, b) = Pair(phaseDeg);

        double measured = Measure(a, b).PhaseDeg!.Value;

        // ±180° is one angle written two ways, and which one comes out is a rounding decision.
        double error = Math.Abs(Math.IEEERemainder(measured - phaseDeg, 360.0));
        Assert.True(error < 0.5, $"asked for {phaseDeg}°, measured {measured}°");
    }

    [Fact]
    public void ALeadAndALagOfTheSameSize_AreTheSameCorrelationAndOppositePhases()
    {
        var lead = Measure(Pair(-30.0).A, Pair(-30.0).B);
        var lag = Measure(Pair(30.0).A, Pair(30.0).B);

        Assert.Equal(lead.Correlation, lag.Correlation, 6);
        // To nine places, not three: a shifted correlation rounds its quarter period to a whole
        // sample and so is asymmetric by that rounding (it read 30.073 one way and 29.937 the
        // other), where a single-bin transform has no grid to round onto.
        Assert.Equal(-lead.PhaseDeg!.Value, lag.PhaseDeg!.Value, 9);
    }

    /// <summary>
    /// Four refusals, each one a case where a single phase would be a number about nothing. The
    /// correlation itself survives all of them, which is why it is the figure that is always shown.
    /// </summary>
    [Fact]
    public void ThePhase_IsAbsentWhereNoSingleNumberIsTheFiguresPhase()
    {
        var (a, b) = Pair(45.0);

        // No measured frequency at all: noise, DC, anything with one settled level.
        Assert.Null(Measure(a, b, fa: null).PhaseDeg);
        Assert.Null(Measure(a, b, fb: null).PhaseDeg);

        // Two different tones -- the figure is a knot rather than an ellipse.
        Assert.Null(Measure(a, b, fb: 3_000.0).PhaseDeg);

        // A silent channel: the correlation would be of the noise floor.
        Assert.Null(Measure(a, new double[a.Length]).PhaseDeg);

        // A tone above the band this analyzer analyses, where the frequency the phase would be
        // measured at is itself shaky.
        var fast = new double[4_800];
        for (int i = 0; i < fast.Length; i++)
            fast[i] = Math.Cos(2.0 * Math.PI * 22_000.0 * i / Fs);
        Assert.Null(ChannelCorrelation.Measure(fast, fast, Fs, 22_000.0, 22_000.0).PhaseDeg);

        // And a 20 kHz tone, whose quarter period is 0.6 samples, now reads correctly -- which is
        // what the single-bin transform bought over a shifted correlation.
        Assert.Equal(0.0, ChannelCorrelation.Measure(fast, fast, Fs, 20_000.0, 20_000.0).PhaseDeg!.Value, 6);

        // And every one of them still reads a correlation.
        Assert.Equal(Math.Cos(45.0 * Math.PI / 180.0), Measure(a, b, fa: null).Correlation, 6);
    }

    /// <summary>
    /// The agreement test is a <em>fraction</em> rather than a number of hertz, so it means the
    /// same thing at 20 Hz and at 20 kHz.
    /// </summary>
    [Fact]
    public void TwoFrequenciesWithinTheAgreementFraction_StillReadAPhase()
    {
        var (a, b) = Pair(45.0);

        Assert.NotNull(Measure(a, b, fb: FreqHz * 1.01).PhaseDeg);
        Assert.Null(Measure(a, b, fb: FreqHz * 1.05).PhaseDeg);
    }

    // -----------------------------------------------------------------------
    // Amplitude ratio
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void TheAmplitudeRatio_IsBsDeviationOverAs(double amplitudeB)
    {
        var (a, b) = Pair(0.0, amplitudeB);

        Assert.Equal(amplitudeB, Measure(a, b).AmplitudeRatio!.Value, 6);
    }

    [Fact]
    public void AnEmptyOrSilentChannelA_HasNoRatioToQuote()
    {
        var silent = new double[1_024];

        Assert.Null(ChannelCorrelation.Measure(silent, silent, Fs, null, null).AmplitudeRatio);
        Assert.Equal(0.0, ChannelCorrelation.Measure(silent, silent, Fs, null, null).Correlation);
    }

    [Fact]
    public void AWindowTooShortToSayAnything_ReadsNothing()
    {
        var result = ChannelCorrelation.Measure([1.0], [1.0], Fs, FreqHz, FreqHz);

        Assert.Equal(0.0, result.Correlation);
        Assert.Null(result.PhaseDeg);
        Assert.Null(result.AmplitudeRatio);
    }

    /// <summary>Nothing it can produce is a NaN, which is the rule the whole scope's wire is built around.</summary>
    [Fact]
    public void NothingItProduces_IsAnythingButANumber()
    {
        foreach (var (a, b) in new[]
                 {
                     (new double[8], new double[8]),
                     (Pair(0.0).A, new double[4_800]),
                     (new double[4_800], Pair(0.0).B),
                 })
        {
            var result = ChannelCorrelation.Measure(a, b, Fs, FreqHz, FreqHz);
            Assert.True(double.IsFinite(result.Correlation));
            Assert.True(result.PhaseDeg is null || double.IsFinite(result.PhaseDeg.Value));
            Assert.True(result.AmplitudeRatio is null || double.IsFinite(result.AmplitudeRatio.Value));
        }
    }
}
