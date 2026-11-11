using System.Numerics;
using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The estimator has one job — tell a channel that is carrying a transfer function from one that is
/// carrying noise, from a single record — and the figures it is pinned against are measured against
/// <em>known</em> signal-to-noise ratios, so the right answers are arithmetic rather than opinion.
/// </summary>
public class TransferFunctionSnrTests
{
    private const int Bins = 16000;
    private const double FirstHz = 20.0;
    private const double LastHz = 22800.0;

    private static double FreqOf(int k) => FirstHz + (LastHz - FirstHz) * k / (Bins - 1);

    private static double[] Ones()
    {
        var w = new double[Bins];
        Array.Fill(w, 1.0);
        return w;
    }

    /// <summary>Unwrapped phase of a trace, in degrees — what the estimator is handed.</summary>
    private static double[] PhaseOf(Complex[] h)
    {
        var unwrapper = new PhaseUnwrapper();
        var phase = new double[h.Length];
        for (int k = 0; k < h.Length; k++)
            phase[k] = unwrapper.NextFromRadians(h[k].Phase);
        return phase;
    }

    /// <summary>A delayed, gently filtered signal at <paramref name="snrDb"/> plus complex noise.</summary>
    private static Complex[] Trace(double snrDb, int seed = 11)
    {
        var rng = new Random(seed);
        double a = Math.Pow(10.0, snrDb / 20.0);
        var h = new Complex[Bins];

        for (int k = 0; k < Bins; k++)
        {
            double f = FreqOf(k);
            var signal = a * Complex.Exp(new Complex(0, -2 * Math.PI * f * 1e-3)) / new Complex(1, f / 60000.0);

            // Box-Muller, so the noise is genuinely Gaussian in each part rather than a sum of
            // uniforms that happens to look like it.
            double u1 = Math.Max(rng.NextDouble(), 1e-12);
            double u2 = rng.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1));
            var noise = new Complex(r * Math.Cos(2 * Math.PI * u2), r * Math.Sin(2 * Math.PI * u2)) / Math.Sqrt(2);

            h[k] = signal + noise;
        }

        return h;
    }

    /// <summary>
    /// The whole claim, against known ratios. Three decibels of tolerance because that is what the
    /// method delivers — and it is delivered over sixty decibels of range, where the decision it
    /// informs ("is anything on this input") needs about ten.
    /// </summary>
    [Theory]
    [InlineData(60.0)]
    [InlineData(40.0)]
    [InlineData(30.0)]
    [InlineData(20.0)]
    [InlineData(10.0)]
    public void AKnownSignalToNoiseRatio_IsRecoveredFromThePhaseAlone(double snrDb)
    {
        double? estimate = TransferFunctionSnr.FromPhaseRoughness(PhaseOf(Trace(snrDb)), Ones(), 0, Bins - 1);

        Assert.NotNull(estimate);
        Assert.Equal(snrDb, estimate!.Value, 3.0);
    }

    /// <summary>
    /// And at the bottom it saturates rather than running away. A channel with nothing on it reads
    /// "about zero", not "−40 dB", because once the phase is uniformly distributed there is no
    /// information left to grade degrees of hopeless with.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-20.0)]
    [InlineData(-60.0)]
    public void AChannelCarryingNothing_ReadsAboutZeroAndNotLess(double snrDb)
    {
        double estimate = TransferFunctionSnr.FromPhaseRoughness(PhaseOf(Trace(snrDb)), Ones(), 0, Bins - 1)!.Value;

        Assert.True(estimate < 5.0, $"a channel at {snrDb:0} dB should read as carrying nothing, and read {estimate:0.0} dB");
        Assert.True(estimate >= TransferFunctionSnr.MinSnrDb);
    }

    /// <summary>
    /// <b>The property the whole method rests on: a real path is smooth however steeply it filters.</b>
    /// An eight-pole filter at 0.94 of Nyquist is about as much phase as a converter can put on a
    /// band — and with no noise it still reads as essentially infinite signal-to-noise, because what
    /// is being measured is the <em>second</em> difference and a filter's phase bends smoothly.
    /// Without this the estimator would read a steep anti-alias filter as a disconnected input.
    /// </summary>
    [Fact]
    public void ARealPathReadsAsSignalHoweverSteeplyItFilters()
    {
        var h = new Complex[Bins];
        for (int k = 0; k < Bins; k++)
        {
            var s = new Complex(0, FreqOf(k) / (0.94 * 24000.0));
            var acc = Complex.One;
            for (int m = 0; m < 8; m++)
            {
                double theta = Math.PI * (2 * m + 1) / 16.0;
                var pole = new Complex(-Math.Sin(theta), Math.Cos(theta));
                acc /= s - pole;
            }

            h[k] = acc * Complex.Exp(new Complex(0, -2 * Math.PI * FreqOf(k) * 1e-3));
        }

        double estimate = TransferFunctionSnr.FromPhaseRoughness(PhaseOf(h), Ones(), 0, Bins - 1)!.Value;

        Assert.True(estimate > 100.0, $"a noise-free filtered path should read as signal, and read {estimate:0.0} dB");
    }

    /// <summary>
    /// A bin whose phase was <em>held</em> rather than measured has a second difference of exactly
    /// zero, so a run of them reads as a perfect channel. That is the one way this estimator can be
    /// made to lie, and the weights are what stop it: held bins weigh nothing, and the three bins of
    /// each difference all have to be measurements.
    /// </summary>
    [Fact]
    public void HeldBinsAreNotCountedAsPerfectlySmooth()
    {
        var phase = PhaseOf(Trace(0.0));
        var weight = Ones();

        // The estimator holds the phase below its magnitude floor: a constant, over the top half.
        for (int k = Bins / 2; k < Bins; k++)
        {
            phase[k] = phase[Bins / 2];
            weight[k] = 0.0;
        }

        double weighted = TransferFunctionSnr.FromPhaseRoughness(phase, weight, 0, Bins - 1)!.Value;
        double unweighted = TransferFunctionSnr.FromPhaseRoughness(phase, Ones(), 0, Bins - 1)!.Value;

        Assert.True(weighted < 5.0, $"the measured half carries nothing and should say so; read {weighted:0.0} dB");
        Assert.True(unweighted > weighted + 2.0,
            $"counting the held half should inflate the figure, which is what the weights prevent — " +
            $"{unweighted:0.0} dB against {weighted:0.0}");
    }

    [Fact]
    public void TooFewUsableBins_IsNoAnswerRatherThanABadOne()
    {
        var phase = PhaseOf(Trace(40.0));

        Assert.Null(TransferFunctionSnr.FromPhaseRoughness(phase, Ones(), 0, 10));
        Assert.Null(TransferFunctionSnr.FromPhaseRoughness(phase, new double[Bins], 0, Bins - 1));
    }

    [Fact]
    public void ABandPastTheEndOfTheArrays_IsClamped()
    {
        double? estimate = TransferFunctionSnr.FromPhaseRoughness(PhaseOf(Trace(40.0)), Ones(), 0, Bins * 4);

        Assert.NotNull(estimate);
        Assert.Equal(40.0, estimate!.Value, 3.0);
    }

    /// <summary>
    /// A perfectly smooth phase is the cap, not infinity: a figure nothing downstream could compare,
    /// log or serialise is worse than a clamped one — and an infinity on a status record truncates
    /// the whole JSON response, which this app has its own rule about.
    /// </summary>
    [Fact]
    public void APerfectlySmoothPhaseIsCappedRatherThanInfinite()
    {
        var phase = new double[Bins];
        for (int k = 0; k < Bins; k++)
            phase[k] = -0.36 * k;

        double estimate = TransferFunctionSnr.FromPhaseRoughness(phase, Ones(), 0, Bins - 1)!.Value;

        Assert.Equal(TransferFunctionSnr.MaxSnrDb, estimate);
        Assert.True(double.IsFinite(estimate));
    }

    /// <summary>
    /// What it cannot do, pinned so nobody reads more into it than it says. A loud clean signal that
    /// is not the stimulus — an unrelated source on the input, or crosstalk from the other channel —
    /// reads as excellent signal-to-noise and is still the wrong thing to divide by. This bounds the
    /// noise; it does not validate the signal.
    /// </summary>
    [Fact]
    public void ACleanButWrongSignal_StillReadsAsSignal()
    {
        var h = new Complex[Bins];
        for (int k = 0; k < Bins; k++)
            h[k] = 1e-4 * Complex.Exp(new Complex(0, -2 * Math.PI * FreqOf(k) * 4e-3));

        double estimate = TransferFunctionSnr.FromPhaseRoughness(PhaseOf(h), Ones(), 0, Bins - 1)!.Value;

        Assert.True(estimate > 100.0, $"80 dB of crosstalk is still a transfer function; read {estimate:0.0} dB");
    }
}
