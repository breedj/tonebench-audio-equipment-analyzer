using System.Numerics;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The residual estimator has one job — read a known epsilon back off a measurement — and three
/// ways of being wrong: the sign, confusing the ramp with a bulk delay, and reporting a DUT's own
/// phase as drift. Every case here is built on a chirp dilated by a <em>known</em> amount, the same
/// way <see cref="ChirpClockDriftTests"/> is, so the right answer is arithmetic.
/// </summary>
public class ChirpDriftResidualTests
{
    private const int N = 32768;
    private const double Fs = 48000.0;
    private const double F1 = 20.0;
    private const double F2 = 24000.0;
    private const double BinHz = Fs / N;
    private static double Period => N / Fs;

    private static ChirpTimeMap Map => ChirpTimeMap.For(Fs, N, F1, F2)!.Value;

    /// <summary>The analysed band, in bins — 20 Hz to 24 kHz, the panel's own default span.</summary>
    private static int FirstBin => (int)Math.Ceiling(F1 / BinHz);
    private static int LastBin => (int)Math.Floor(F2 * 0.95 / BinHz);

    private static double Chirp(double t)
    {
        double k = Math.Log(F2 / F1);
        return Math.Sin(2 * Math.PI * F1 * Period / k * (Math.Exp(k * t / Period) - 1.0));
    }

    private static Complex[] Spectrum(double epsilon)
    {
        var x = new double[N];
        for (int n = 0; n < N; n++)
            x[n] = Chirp(n / Fs * (1.0 + epsilon));

        var spec = new Complex[(N / 2) + 1];
        new FftFlatBackend().Forward(x, spec);
        return spec;
    }

    /// <summary>The deconvolved transfer function of a capture dilated by <paramref name="ppm"/>.</summary>
    private static Complex[] Deconvolved(double ppm)
    {
        var reference = Spectrum(0.0);
        var captured = Spectrum(ppm * 1e-6);

        double maxRef = reference.Max(c => c.Magnitude);
        double lambda = 1e-6 * maxRef * maxRef;

        var h = new Complex[reference.Length];
        for (int k = 0; k < h.Length; k++)
            h[k] = captured[k] * Complex.Conjugate(reference[k]) / ((reference[k] * Complex.Conjugate(reference[k])).Real + lambda);
        return h;
    }

    /// <summary>Unwrapped phase in degrees, bin 0 upwards — what the estimator hands the fit.</summary>
    private static double[] UnwrappedPhaseDeg(Complex[] h)
    {
        var unwrapper = new PhaseUnwrapper();
        var phase = new double[h.Length];
        for (int k = 0; k < h.Length; k++)
            phase[k] = unwrapper.NextFromRadians(h[k].Phase);
        return phase;
    }

    /// <summary>One, over the analysed band — the weighting a flat trace gets.</summary>
    private static double[] FlatWeights()
    {
        var w = new double[(N / 2) + 1];
        for (int k = FirstBin; k <= LastBin; k++)
            w[k] = 1.0;
        return w;
    }

    private static ChirpDriftResidualFit Fit(double[] phaseDeg, double[]? weight = null) =>
        ChirpDriftResidual.Fit(phaseDeg, weight ?? FlatWeights(), BinHz, Map, FirstBin, LastBin)
            ?? throw new InvalidOperationException("The fit should have been possible.");

    /// <summary>The ramp a given drift puts on the phase, in degrees — the thing being looked for.</summary>
    private static double[] RampPhaseDeg(double ppm, double bulkDelayS = 0.0, double offsetDeg = 0.0)
    {
        var map = Map;
        var phase = new double[(N / 2) + 1];
        for (int k = 0; k < phase.Length; k++)
        {
            double f = k * BinHz;
            double rad = ppm * 1e-6 * map.RampRadiansPerEpsilon(f) - 2 * Math.PI * f * bulkDelayS;
            phase[k] = rad * 180.0 / Math.PI + offsetDeg;
        }

        return phase;
    }

    [Theory]
    [InlineData(-104.038)]
    [InlineData(104.038)]
    [InlineData(-8.5)]
    [InlineData(1.0)]
    public void ARampOfAKnownSize_IsReadBackAsThatSize(double ppm)
    {
        var fit = Fit(RampPhaseDeg(ppm));

        Assert.Equal(ppm, fit.ResidualPpm, 6);
        Assert.Equal(0.0, fit.FitRmsDeg, 6);
    }

    /// <summary>
    /// The one confusion this fit exists to avoid. A bulk delay is linear in frequency and the ramp
    /// goes as <c>f·ln f</c>; over a span of three decades those are similar enough that a two-term
    /// fit hands a DUT's propagation delay straight to epsilon. 500 µs is a long cable and a slow
    /// amplifier, and far larger than this bench's own 71 µs of slip.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(50e-6)]
    [InlineData(500e-6)]
    [InlineData(-200e-6)]
    public void ABulkDelay_IsNotReadAsDrift(double delayS)
    {
        var fit = Fit(RampPhaseDeg(-104.038, bulkDelayS: delayS));

        Assert.Equal(-104.038, fit.ResidualPpm, 6);
    }

    [Fact]
    public void ThePhaseAnchorDoesNotMatter()
    {
        Assert.Equal(-104.038, Fit(RampPhaseDeg(-104.038, offsetDeg: 0.0)).ResidualPpm, 6);
        Assert.Equal(-104.038, Fit(RampPhaseDeg(-104.038, offsetDeg: 137.0)).ResidualPpm, 6);
        Assert.Equal(-104.038, Fit(RampPhaseDeg(-104.038, offsetDeg: -4000.0)).ResidualPpm, 6);
    }

    /// <summary>
    /// The end-to-end case, and the one that would catch the correction and the estimator disagreeing
    /// about the ramp's shape: a real dilated capture, deconvolved, read for its drift. Nothing
    /// synthesises the phase here — it comes out of an FFT of a sweep sampled at the wrong rate.
    /// </summary>
    [Fact]
    public void ADilatedCapture_ReadsItsOwnDilation()
    {
        var fit = Fit(UnwrappedPhaseDeg(Deconvolved(-104.038)));

        // 0.3 ppm rather than exact: a deconvolution of a real sweep carries the regulariser's own
        // bias and the sweep's imperfect periodicity, and 0.15 ppm of 104 is what that costs. It is
        // also two orders below anything this detector is asked to decide.
        Assert.Equal(-104.038, fit.ResidualPpm, 0.3);
        Assert.True(fit.FitRmsDeg < 1.0, $"the model should describe a bare dilation; missed by {fit.FitRmsDeg:0.00} deg");
    }

    /// <summary>
    /// <b>The test this whole feature rests on.</b> A measurement corrected by exactly the right
    /// figure must read a residual of zero — otherwise the staleness detector tells a user with a
    /// perfectly fresh figure to go and measure it again, which is worse than saying nothing. It
    /// holds by construction rather than by coincidence, because
    /// <see cref="ChirpClockDrift.Correct"/> and the fit read the ramp's shape from the same
    /// <see cref="ChirpTimeMap"/>.
    /// </summary>
    [Theory]
    [InlineData(-104.038)]
    [InlineData(37.5)]
    public void ACorrectlyCorrectedCapture_HasNoResidualLeft(double ppm)
    {
        var h = Deconvolved(ppm);
        ChirpClockDrift.For(ppm, Fs, N, F1, F2)!.Correct(h);

        var fit = Fit(UnwrappedPhaseDeg(h));

        Assert.True(Math.Abs(fit.ResidualPpm) < 0.2, $"read {fit.ResidualPpm:0.000} ppm off a correctly corrected capture");
    }

    /// <summary>
    /// And the case it exists for: corrected by a stale figure, the residual is the <em>error</em> in
    /// that figure — which is exactly what a reader has to be told.
    /// </summary>
    [Theory]
    [InlineData(-104.0, -80.0, -24.0)]
    [InlineData(-104.0, -130.0, 26.0)]
    [InlineData(-104.0, 0.0, -104.0)]
    public void CorrectedByAStaleFigure_TheResidualIsThatFiguresError(double truePpm, double storedPpm, double expected)
    {
        var h = Deconvolved(truePpm);
        ChirpClockDrift.For(storedPpm, Fs, N, F1, F2)?.Correct(h);

        var fit = Fit(UnwrappedPhaseDeg(h));

        Assert.Equal(expected, fit.ResidualPpm, 0.3);
    }

    /// <summary>
    /// <b>How much a DUT's own phase can manufacture, which is why the App layer refuses to read a
    /// trace with a DUT in it.</b> A bare one-pole low-pass — nothing else in the path, no clock
    /// drift at all — is reported as a hundred-odd ppm of drift, against the 104 ppm this project's
    /// own bench actually has. So a reading taken off the measured channel could not be told from a
    /// stale figure, and <c>ResponseCaptureSupport.EstimateDriftResidual</c> makes none: it reads
    /// the reference channel, which carries the stimulus rather than the DUT's output.
    ///
    /// <para>The cause is not numerical and cannot be fitted away. Over three decades <c>f</c> and
    /// <c>f·ln f</c> are similar enough that a saturating phase curve is genuinely well described by
    /// a large ramp plus a large offsetting bulk delay — note the model misses by only a few degrees
    /// while being wrong about ε by more than the whole quantity. That is also why
    /// <see cref="ChirpDriftResidualFit.FitRmsDeg"/>, useful as it is, cannot be the gate on its
    /// own.</para>
    ///
    /// <para>Pinned as a figure rather than as a bound so that a change in the fit which quietly
    /// improved this would have to be noticed and the App layer's restriction reconsidered.</para>
    /// </summary>
    [Theory]
    [InlineData(2000.0, 132.7)]
    [InlineData(200.0, 51.2)]
    [InlineData(20000.0, 20.4)]
    public void ADutsOwnPhase_CanManufactureDriftThatIsNotThere(double cornerHz, double readsAsPpm)
    {
        var phase = new double[(N / 2) + 1];
        var unwrapper = new PhaseUnwrapper();
        for (int k = 0; k < phase.Length; k++)
        {
            // One pole: H = 1/(1 + jf/fc). No drift anywhere in this trace.
            var h = Complex.One / new Complex(1.0, k * BinHz / cornerHz);
            phase[k] = unwrapper.NextFromRadians(h.Phase);
        }

        var fit = Fit(phase);

        Assert.Equal(readsAsPpm, fit.ResidualPpm, 0.2);
        Assert.True(fit.FitRmsDeg < 6.0, $"and it fits well while being wrong: missed by {fit.FitRmsDeg:0.00} deg");
    }

    /// <summary>
    /// Noise moves the answer by far less than it moves the reported miss, which is what makes
    /// <see cref="ChirpDriftResidualFit.FitRmsDeg"/> the useful confidence figure rather than a
    /// curiosity.
    /// </summary>
    [Fact]
    public void PhaseNoise_CostsLittleAccuracyAndIsReported()
    {
        var rng = new Random(20261111);
        var phase = RampPhaseDeg(-104.038);
        for (int k = 0; k < phase.Length; k++)
            phase[k] += 5.0 * (rng.NextDouble() - 0.5);

        var fit = Fit(phase);

        Assert.Equal(-104.038, fit.ResidualPpm, 0.3);
        Assert.True(fit.FitRmsDeg > 0.5, $"the noise should show in the miss, which read {fit.FitRmsDeg:0.00} deg");
    }

    [Fact]
    public void BinsWeighedAtZero_AreNotFitted()
    {
        // The top half of the band says epsilon is something else entirely, and weighs nothing.
        var phase = RampPhaseDeg(-104.038);
        int middle = (FirstBin + LastBin) / 2;
        for (int k = middle; k <= LastBin; k++)
            phase[k] = 12345.0;

        var weight = FlatWeights();
        for (int k = middle; k <= LastBin; k++)
            weight[k] = 0.0;

        var fit = Fit(phase, weight);

        Assert.Equal(-104.038, fit.ResidualPpm, 4);
        Assert.Equal(middle - FirstBin, fit.Bins);
    }

    [Fact]
    public void TooFewUsableBins_IsNoAnswerRatherThanABadOne()
    {
        var phase = RampPhaseDeg(-104.038);

        Assert.Null(ChirpDriftResidual.Fit(phase, FlatWeights(), BinHz, Map, FirstBin, FirstBin + 4));

        var nothing = new double[(N / 2) + 1];
        Assert.Null(ChirpDriftResidual.Fit(phase, nothing, BinHz, Map, FirstBin, LastBin));
    }

    [Fact]
    public void ANonFinitePhaseBinIsSkippedRatherThanPoisoningTheFit()
    {
        var phase = RampPhaseDeg(-104.038);
        phase[FirstBin + 10] = double.NaN;
        phase[FirstBin + 11] = double.PositiveInfinity;

        var fit = Fit(phase);

        Assert.Equal(-104.038, fit.ResidualPpm, 4);
        Assert.Equal(LastBin - FirstBin - 1, fit.Bins);
    }

    [Fact]
    public void AnImpossibleBinSpacing_IsRefused()
    {
        Assert.Null(ChirpDriftResidual.Fit(RampPhaseDeg(0.0), FlatWeights(), 0.0, Map, FirstBin, LastBin));
        Assert.Null(ChirpDriftResidual.Fit(RampPhaseDeg(0.0), FlatWeights(), double.NaN, Map, FirstBin, LastBin));
    }

    /// <summary>
    /// The band may be asked for past the end of the arrays — a caller derives it from a frequency
    /// and a bin spacing — and that must clamp rather than throw.
    /// </summary>
    [Fact]
    public void ABandPastTheEndOfTheArrays_IsClamped()
    {
        var fit = ChirpDriftResidual.Fit(RampPhaseDeg(-104.038), FlatWeights(), BinHz, Map, FirstBin, N * 4);

        Assert.NotNull(fit);
        Assert.Equal(-104.038, fit!.ResidualPpm, 4);
    }

    /// <summary>
    /// The sign is the one thing a reader acts on, so it is pinned on its own rather than left to
    /// the equality assertions above: a capture sampled <em>fast</em> comes home low, which is a
    /// negative drift, and must not be reported as a positive one.
    /// </summary>
    [Fact]
    public void TheSignFollowsToneFrequencyDriftPpm()
    {
        double played = 1000.0;
        double captured = played * (1.0 - 104.038e-6);
        Assert.True(ToneFrequency.DriftPpm(captured, played) < 0);

        Assert.True(Fit(UnwrappedPhaseDeg(Deconvolved(-104.038))).ResidualPpm < 0);
    }
}
