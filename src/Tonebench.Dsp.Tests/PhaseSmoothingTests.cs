using System.Numerics;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The phase half of <see cref="FractionalOctaveSmoother"/>: the vector mean an impedance curve's
/// angle takes, and the magnitude-weighted mean an unwrapped frequency-response phase takes.
/// </summary>
public class PhaseSmoothingTests
{
    private const double BinHz = 1.46484375;

    private static Complex[] Polar(IEnumerable<(double Magnitude, double Deg)> bins) =>
        bins.Select(b => Complex.FromPolarCoordinates(b.Magnitude, b.Deg * Math.PI / 180.0)).ToArray();

    private static Complex[] Constant(int n, double magnitude, double deg) =>
        Polar(Enumerable.Repeat((magnitude, deg), n));

    [Fact]
    public void SmoothPhaseDeg_WithDenominatorZero_IsTheBinsOwnAngle()
    {
        var values = Polar([(1.0, 0.0), (2.0, 45.0), (0.5, -120.0), (3.0, 170.0)]);

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(values, BinHz, denominator: 0);

        Assert.Equal(0.0, phase[0], 9);
        Assert.Equal(45.0, phase[1], 9);
        Assert.Equal(-120.0, phase[2], 9);
        Assert.Equal(170.0, phase[3], 9);
    }

    [Fact]
    public void SmoothPhaseDeg_OfAConstantAngle_LeavesIt()
    {
        var values = Constant(4096, 0.25, 33.0);

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(values, BinHz, denominator: 3);

        for (int k = 1; k < values.Length; k++)
            Assert.Equal(33.0, phase[k], 9);
    }

    [Fact]
    public void SmoothPhaseDeg_AcrossTheWrap_StaysAtTheWrap()
    {
        // The whole reason this is a vector mean. +179 and -179 are two degrees apart; averaging the
        // numbers gives 0 -- the opposite direction -- while adding the phasors gives 180.
        var values = Polar(Enumerable.Range(0, 2048).Select(k => (1.0, k % 2 == 0 ? 179.0 : -179.0)));

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(values, BinHz, denominator: 6);

        // Not exactly 180: the band is geometrically centred, so it holds a few more bins above the
        // centre than below and the alternation does not cancel to the bin.
        for (int k = 512; k < 1536; k++)
            Assert.True(Math.Abs(phase[k]) > 179.5, $"bin {k} left the wrap at {phase[k]}");
    }

    [Fact]
    public void SmoothPhaseDeg_IsWeightedByMagnitude_SoAnEmptyBinDoesNotVote()
    {
        // One bin in ten carries the signal; the rest are a thousandth of it, pointing anywhere. The
        // answer has to be the loud bins' angle, not the average of eleven directions.
        var random = new Random(20261020);
        var bins = Enumerable.Range(0, 4096).Select(k => k % 10 == 0
            ? (1.0, 20.0)
            : (0.001, random.NextDouble() * 360.0 - 180.0));

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(Polar(bins), BinHz, denominator: 12);

        for (int k = 1024; k < 3072; k++)
            Assert.True(Math.Abs(phase[k] - 20.0) < 1.0, $"bin {k} was pulled to {phase[k]}");
    }

    [Fact]
    public void SmoothPhaseDeg_StaysInsideItsBandsOwnAngles()
    {
        // The property rather than a reference implementation: a vector mean of phasors spanning
        // under half a turn lies between the extreme directions it was taken over.
        var bins = Enumerable.Range(0, 4096).Select(k => (1.0, -40.0 + (k % 7) * 10.0)).ToArray();

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(Polar(bins), BinHz, denominator: 6);

        for (int k = 1; k < bins.Length; k++)
        {
            Assert.True(phase[k] >= -40.0 - 1e-9, $"bin {k} below the band at {phase[k]}");
            Assert.True(phase[k] <= 20.0 + 1e-9, $"bin {k} above the band at {phase[k]}");
        }
    }

    [Fact]
    public void SmoothPhaseDeg_WhereTheBandCancels_NeverPointsSomewhereNoBinPoints()
    {
        // Every phasor here lies on the real axis, so every answer must too. A band holding equal
        // numbers of each cancels exactly, and the residue left behind is the 1.2e-16 imaginary part
        // of Complex.FromPolarCoordinates(1, PI) summed over the band -- which is a rounding error
        // pointing at **90 degrees**, a direction not one bin had. That is what the cancellation
        // floor is for, and this is the case that found it.
        var values = Polar(Enumerable.Range(0, 2048).Select(k => (1.0, k % 2 == 0 ? 0.0 : 180.0)));

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(values, BinHz, denominator: 6);

        for (int k = 1; k < values.Length; k++)
        {
            double offAxis = Math.Min(Math.Abs(phase[k]), 180.0 - Math.Abs(phase[k]));
            Assert.True(offAxis < 1e-6, $"bin {k} read {phase[k]}, which no bin in its band points at");
        }
    }

    [Fact]
    public void SmoothPhaseDeg_NarrowerThanABin_KeepsTheBinsOwnAngle()
    {
        // The bottom of the band, where a 1/24-octave window does not span a whole bin.
        var values = Polar(Enumerable.Range(0, 512).Select(k => (1.0, (k % 3) * 25.0)));

        var phase = FractionalOctaveSmoother.SmoothPhaseDeg(values, BinHz, denominator: 24);

        for (int k = 1; k < 8; k++)
            Assert.Equal((k % 3) * 25.0, phase[k], 6);
    }

    [Fact]
    public void SmoothUnwrappedPhaseDeg_KeepsTheTurnsAVectorMeanWouldDelete()
    {
        // A delayed DUT runs through many turns in its passband, and the bandwidth anchor and the
        // client's restore-delay checkbox both act on them. An argument would hand back (-180, 180].
        var phaseDeg = Enumerable.Range(0, 4096).Select(k => -3.5 * k).ToArray();
        var magnitude = Enumerable.Repeat(1.0, 4096).ToArray();

        var smoothed = FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg(phaseDeg, magnitude, BinHz, denominator: 24);

        // Within a few degrees of the ramp's own value: the band is geometrically centred on the bin,
        // so it is not arithmetically symmetric and a ramp does not average to exactly the centre.
        Assert.True(Math.Abs(smoothed[2048] - (-3.5 * 2048)) < 20.0, $"the ramp moved: {smoothed[2048]}");
        Assert.True(smoothed[4000] < -13000.0, $"the turns were lost: {smoothed[4000]}");
    }

    [Fact]
    public void SmoothUnwrappedPhaseDeg_IsWeightedByMagnitude_SoANotchDoesNotDragItsBand()
    {
        // A notch is a few bins carrying almost nothing while the phase swings through half a turn.
        // Unweighted, those bins pull the whole band they sit in; weighted, they cannot.
        var phaseDeg = new double[4096];
        var magnitude = new double[4096];
        for (int k = 0; k < phaseDeg.Length; k++)
        {
            bool inNotch = k is >= 2040 and <= 2056;
            phaseDeg[k] = inNotch ? 170.0 : -10.0;
            magnitude[k] = inNotch ? 1e-4 : 1.0;
        }

        var smoothed = FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg(phaseDeg, magnitude, BinHz, denominator: 12);

        // The band around bin 2100 covers the notch; the answer is still the passband's angle.
        Assert.Equal(-10.0, smoothed[2100], 2);
    }

    [Fact]
    public void SmoothUnwrappedPhaseDeg_WithNoMagnitudeAtAll_KeepsTheBinsOwnPhase()
    {
        var phaseDeg = Enumerable.Range(0, 2048).Select(k => (double)(k % 11)).ToArray();
        var magnitude = new double[2048];

        var smoothed = FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg(phaseDeg, magnitude, BinHz, denominator: 6);

        Assert.Equal(phaseDeg, smoothed);
    }

    [Fact]
    public void SmoothUnwrappedPhaseDeg_WithDenominatorZero_PassesThrough()
    {
        double[] phaseDeg = [0.0, 12.0, -40.0, 900.0, 7.0];
        double[] magnitude = [1.0, 1.0, 1.0, 1.0, 1.0];

        var smoothed = FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg(phaseDeg, magnitude, BinHz, denominator: 0);

        Assert.Equal(phaseDeg, smoothed);
    }

    [Fact]
    public void SmoothUnwrappedPhaseDeg_WithAMismatchedMagnitude_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg([0.0, 1.0, 2.0], [1.0, 1.0], BinHz, denominator: 6));
    }

    [Fact]
    public void BothSmoothers_OverARealisticallyNoisyCurve_FollowTheSignalRatherThanTheNoise()
    {
        // The reported case: a sub-ohm impedance measured by chirp, where the ratio is 40 dB down and
        // whole stretches of the band have no usable angle at all. The smoothed curve has to sit near
        // the true angle while the raw one wanders over tens of degrees.
        var random = new Random(4039);
        const int n = 8192;
        const double trueDeg = 12.0;

        var values = new Complex[n];
        var phaseDeg = new double[n];
        var magnitude = new double[n];
        for (int k = 0; k < n; k++)
        {
            double noiseDeg = (random.NextDouble() - 0.5) * 60.0;
            values[k] = Complex.FromPolarCoordinates(1.0, (trueDeg + noiseDeg) * Math.PI / 180.0);
            phaseDeg[k] = trueDeg + noiseDeg;
            magnitude[k] = 1.0;
        }

        var vector = FractionalOctaveSmoother.SmoothPhaseDeg(values, BinHz, denominator: 12);
        var weighted = FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg(phaseDeg, magnitude, BinHz, denominator: 12);

        double rawSpread = phaseDeg[4096..6144].Max() - phaseDeg[4096..6144].Min();
        double vectorSpread = vector[4096..6144].Max() - vector[4096..6144].Min();
        double weightedSpread = weighted[4096..6144].Max() - weighted[4096..6144].Min();

        Assert.True(rawSpread > 50.0, $"the raw phase was not noisy enough to be a test: {rawSpread}");
        Assert.True(vectorSpread < rawSpread / 10.0, $"the vector mean barely smoothed: {vectorSpread}");
        Assert.True(weightedSpread < rawSpread / 10.0, $"the weighted mean barely smoothed: {weightedSpread}");
        // Within a few degrees of the truth, against +/-30 of noise per bin.
        Assert.True(Math.Abs(vector[5000] - trueDeg) < 3.0, $"the vector mean read {vector[5000]}");
        Assert.True(Math.Abs(weighted[5000] - trueDeg) < 3.0, $"the weighted mean read {weighted[5000]}");
    }
}
