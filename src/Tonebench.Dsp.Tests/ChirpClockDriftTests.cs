using System.Numerics;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The correction has one job and one way of being wrong: the sign. Everything here is built on a
/// chirp dilated by a <em>known</em> epsilon, so the right answer is arithmetic rather than opinion.
/// </summary>
public class ChirpClockDriftTests
{
    private const int N = 32768;
    private const double Fs = 48000.0;
    private const double F1 = 20.0;
    private const double F2 = 24000.0;
    private const double BinHz = Fs / N;
    private static double Period => N / Fs;

    /// <summary>
    /// An exponential sweep evaluated at an arbitrary time, so a dilated capture can be generated
    /// exactly rather than interpolated — interpolation error would be indistinguishable from the
    /// effect being measured.
    /// </summary>
    private static double Chirp(double t)
    {
        double k = Math.Log(F2 / F1);
        return Math.Sin(2 * Math.PI * F1 * Period / k * (Math.Exp(k * t / Period) - 1.0));
    }

    /// <param name="epsilon">The capture's time scaling: y(t) = x(t(1 + e)).</param>
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

    /// <summary>Worst absolute phase, in degrees, over the bins the sweep actually covers.</summary>
    private static double WorstPhaseDeg(Complex[] h)
    {
        double worst = 0;
        for (int k = 1; k < h.Length; k++)
        {
            double f = k * BinHz;
            if (f < F1 * 4 || f > F2 * 0.8) continue;   // clear of both ends of the sweep
            worst = Math.Max(worst, Math.Abs(h[k].Phase) * 180.0 / Math.PI);
        }

        return worst;
    }

    [Fact]
    public void TheCorrectionTakesOffTheRampTheClockPutOn()
    {
        const double ppm = -104.0;                      // this bench's own figure
        var h = Deconvolved(ppm);
        double before = WorstPhaseDeg(h);

        var fix = ChirpClockDrift.For(ppm, Fs, N, F1, F2);
        Assert.NotNull(fix);
        fix!.Correct(h);
        double after = WorstPhaseDeg(h);

        // The whole point: the dilation wraps the phase round many times, and correcting it leaves
        // a response that is flat to a few degrees. A sign error would roughly double `before`
        // rather than removing it, which is why this asserts a ratio and not a tolerance.
        Assert.True(before > 90, $"the dilation should wreck the phase; it only reached {before:0.0} deg");
        Assert.True(after < before / 20,
            $"correction left {after:0.0} deg of {before:0.0} — a sign error looks exactly like this");
    }

    [Fact]
    public void ItLeavesEveryMagnitudeAlone()
    {
        var h = Deconvolved(-104.0);
        var before = h.Select(c => c.Magnitude).ToArray();

        ChirpClockDrift.For(-104.0, Fs, N, F1, F2)!.Correct(h);

        // Ten places, not twelve: the correction is a cos/sin multiply, whose own rounding lands
        // around 1e-13 relative. Tighter than that would be asserting against floating point.
        for (int k = 0; k < h.Length; k++)
            Assert.Equal(before[k], h[k].Magnitude, 10);
    }

    [Fact]
    public void TheDelayIsZeroAtTheStartOfTheSweepAndEpsilonTimesTheRecordAtTheEnd()
    {
        var fix = ChirpClockDrift.For(-104.0, Fs, N, F1, F2)!;

        Assert.Equal(0.0, fix.SweepTimeSeconds(F1), 9);
        Assert.Equal(Period, fix.SweepTimeSeconds(F2), 9);

        // 104 ppm over a 0.6827 s record is 71 us -- the figure measured on the bench.
        Assert.Equal(71.0e-6, fix.SlipSeconds, 7);
        Assert.Equal(104.0e-6 * Period, Math.Abs(fix.DelaySeconds(F2)), 12);
    }

    [Fact]
    public void OutsideTheSweepTheDelayIsClampedRatherThanExtrapolated()
    {
        var fix = ChirpClockDrift.For(-104.0, Fs, N, F1, F2)!;

        // Below the start and above the end there is no stimulus and so no arrival time. A
        // logarithm extrapolated past its own range would put the largest correction of all on the
        // bins that carry only noise.
        Assert.Equal(0.0, fix.SweepTimeSeconds(F1 / 100), 9);
        Assert.Equal(Period, fix.SweepTimeSeconds(F2 * 10), 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.04)]
    [InlineData(-0.04)]
    public void ADriftTooSmallToMatterIsNotCorrectedAtAll(double ppm)
    {
        Assert.Null(ChirpClockDrift.For(ppm, Fs, N, F1, F2));
    }

    [Theory]
    [InlineData(0.0, 32768, 20.0, 24000.0)]
    [InlineData(48000.0, 0, 20.0, 24000.0)]
    [InlineData(48000.0, 32768, 0.0, 24000.0)]
    [InlineData(48000.0, 32768, 24000.0, 24000.0)]
    public void NonsenseParametersAnswerNothingRatherThanThrowing(
        double rate, int fft, double start, double end)
    {
        Assert.Null(ChirpClockDrift.For(-104.0, rate, fft, start, end));
    }

    [Fact]
    public void CorrectingTwiceInOppositeDirectionsIsTheIdentity()
    {
        var h = Deconvolved(-104.0);
        var before = h.ToArray();

        ChirpClockDrift.For(-104.0, Fs, N, F1, F2)!.Correct(h);
        ChirpClockDrift.For(104.0, Fs, N, F1, F2)!.Correct(h);

        for (int k = 0; k < h.Length; k++)
        {
            Assert.Equal(before[k].Real, h[k].Real, 10);
            Assert.Equal(before[k].Imaginary, h[k].Imaginary, 10);
        }
    }
}
