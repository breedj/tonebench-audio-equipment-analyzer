using System.Numerics;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The stored-and-restored calibration curve. What matters is not that the interpolation reproduces
/// its input to some tolerance, but that a calibration which has been through the JSON-sized
/// round trip still removes what the full-resolution one removed.
/// </summary>
public class ImpedanceCalibrationTests
{
    private const double BinHz = 48000.0 / 16384.0;
    private const int BinCount = 8193;
    private const double LowHz = 20.0;
    private const double HighHz = 20000.0;

    /// <summary>Bins comfortably inside the calibrated band.</summary>
    private static readonly int[] Bins = [37, 149, 512, 1301, 4096, 6000];

    /// <summary>
    /// A plausible open-fixture reading: the DUT channel 0.3 dB high, and a microsecond of skew
    /// between the two ADC channels, which is 7 degrees by 20 kHz.
    /// </summary>
    private static Complex OpenReading(double freqHz) =>
        Complex.FromPolarCoordinates(Math.Pow(10.0, 0.3 / 20.0), 2.0 * Math.PI * freqHz * 1.0e-6);

    /// <summary>
    /// What the decimation costs, as bounds rather than a tolerance pulled out of the air. Storing
    /// 256 log-spaced points instead of 8193 bins loses under <b>1e-6</b> in magnitude and under
    /// <b>0.001 degrees</b> of phase, which is three orders below anything the measurement can
    /// resolve -- the curves are smooth, so the sampling is nowhere near the limit.
    /// </summary>
    [Fact]
    public void Capture_ThenResample_ReproducesTheCurveItSampled()
    {
        var full = FullResolution(OpenReading);
        var restored = ImpedanceCalibration.Capture(full, BinHz, LowHz, HighHz).Resample(BinHz, BinCount);

        foreach (int bin in Bins)
        {
            var expected = OpenReading(bin * BinHz);

            Assert.InRange(Math.Abs(restored[bin].Magnitude - expected.Magnitude), 0.0, 1.0e-6);
            Assert.InRange(
                Math.Abs(restored[bin].Phase - expected.Phase) * 180.0 / Math.PI, 0.0, 0.001);
        }
    }

    /// <summary>
    /// The contract that matters. A calibration decimated to 256 points, as it would be after a
    /// restart, still turns the measured ratio back into 8.000 ohms at 0 degrees.
    /// </summary>
    [Fact]
    public void ARestoredCalibration_StillRemovesTheMismatchItWasCapturedFor()
    {
        var open = ImpedanceCalibration.Capture(FullResolution(OpenReading), BinHz, LowHz, HighHz);

        var measured = FullResolution(f => OpenReading(f) * (8.0 / 108.0));

        var z = ImpedanceMath.Solve(measured, new ImpedanceSolveOptions
        {
            ReferenceOhms = 100.0,
            BinHz = BinHz,
            OpenCal = open.Resample(BinHz, BinCount),
        });

        foreach (int bin in Bins)
        {
            Assert.Equal(8.0, z[bin].Magnitude, 4);
            Assert.Equal(0.0, z[bin].Phase * 180.0 / Math.PI, 3);
        }
    }

    /// <summary>
    /// Outside the band it saw, a calibration holds its end value. Extrapolating a channel mismatch
    /// past the sweep would be a confident statement about frequencies that were never measured.
    /// </summary>
    [Fact]
    public void Resample_OutsideTheCapturedBand_HoldsTheEndValues()
    {
        var cal = ImpedanceCalibration.Capture(FullResolution(OpenReading), BinHz, LowHz, HighHz);
        var restored = cal.Resample(BinHz, BinCount);

        var low = new Complex(cal.Real[0], cal.Imag[0]);
        var high = new Complex(cal.Real[^1], cal.Imag[^1]);

        Assert.Equal(low, restored[0]);
        Assert.Equal(low, restored[1]);
        Assert.Equal(high, restored[BinCount - 1]);
    }

    /// <summary>A band wider than the capture is clamped to it, rather than reading off the end of the spectrum.</summary>
    [Fact]
    public void Capture_AboveTheMeasuredSpectrum_StopsAtItsTopBin()
    {
        var cal = ImpedanceCalibration.Capture(FullResolution(OpenReading), BinHz, LowHz, highHz: 500_000.0);

        Assert.True(cal.IsUsable);
        Assert.InRange(cal.FreqHz[^1], 0.0, (BinCount - 1) * BinHz);
    }

    [Fact]
    public void IsUsable_IsFalseForAnEmptyOrRaggedCurve()
    {
        Assert.False(new ImpedanceCalibration([], [], []).IsUsable);
        Assert.False(new ImpedanceCalibration([1.0, 2.0], [0.0], [0.0, 0.0]).IsUsable);
        Assert.True(new ImpedanceCalibration([1.0, 2.0], [0.0, 0.0], [0.0, 0.0]).IsUsable);
    }

    private static Complex[] FullResolution(Func<double, Complex> at)
    {
        var values = new Complex[BinCount];
        for (int k = 0; k < BinCount; k++)
            values[k] = at(k * BinHz);
        return values;
    }

    /// <summary>
    /// A stepped run's points already are a sparse log-spaced grid, so they are stored as measured.
    /// Pushing them through <see cref="ImpedanceCalibration.Capture"/>'s intermediate curve would
    /// only interpolate an interpolation.
    /// </summary>
    [Fact]
    public void FromPoints_StoresTheMeasuredPointsExactly()
    {
        double[] freqHz = [20.0, 200.0, 2000.0, 20000.0];
        Complex[] h = [new(1.0, 0.0), new(0.99, 0.01), new(0.98, 0.03), new(0.95, 0.08)];

        var calibration = ImpedanceCalibration.FromPoints(freqHz, h);

        Assert.True(calibration.IsUsable);
        Assert.Equal(freqHz, calibration.FreqHz);

        for (int i = 0; i < freqHz.Length; i++)
        {
            Assert.Equal(h[i].Real, calibration.At(freqHz[i]).Real, 12);
            Assert.Equal(h[i].Imaginary, calibration.At(freqHz[i]).Imaginary, 12);
        }
    }

    /// <summary>
    /// <see cref="ImpedanceCalibration.At"/> binary-searches an ascending array, so an unsorted grid
    /// would interpolate silently wrong values rather than fail.
    /// </summary>
    [Theory]
    [InlineData(new double[] { 200.0, 20.0 })]
    [InlineData(new double[] { 20.0, 20.0 })]
    [InlineData(new double[] { 0.0, 20.0 })]
    public void FromPoints_RejectsAGridThatIsNotStrictlyAscending(double[] freqHz)
    {
        var h = new Complex[freqHz.Length];
        Array.Fill(h, Complex.One);

        Assert.Throws<ArgumentException>(() => ImpedanceCalibration.FromPoints(freqHz, h));
    }

    /// <summary>
    /// The whole reason the calibration stimulus is a control of its own: a standard captured by a
    /// chirp is a curve in frequency, so a stepped measurement asks it for its own points and gets
    /// the same answers a chirp would have got at those frequencies.
    /// </summary>
    [Fact]
    public void ResampleAt_AnArbitraryGrid_AgreesWithTheUniformResample()
    {
        var captured = ImpedanceCalibration.FromPoints(
            [10.0, 100.0, 1000.0, 10000.0],
            [new(1.0, 0.0), new(0.99, 0.01), new(0.97, 0.04), new(0.93, 0.10)]);

        const double BinHz = 48000.0 / 32768.0;
        var uniform = captured.Resample(BinHz, 4096);

        double[] arbitrary = [50.0 * BinHz, 517.0 * BinHz, 4000.0 * BinHz];
        var sampled = captured.ResampleAt(arbitrary);

        Assert.Equal(uniform[50].Real, sampled[0].Real, 12);
        Assert.Equal(uniform[517].Imaginary, sampled[1].Imaginary, 12);
        Assert.Equal(uniform[4000].Real, sampled[2].Real, 12);
    }

    /// <summary>
    /// A standard says which one it is, whatever the user meant to connect: an open is the two ADC
    /// channels' gain mismatch and reads within a fraction of a dB of unity, a short is a lead
    /// resistance over the reference resistor and reads some 60 dB down. The figures here are the
    /// ones measured on a real fixture on 2026-09-24 -- 0.9998 for a good open, 0.0013 for a good
    /// short, and 0.00084 for an "open" that was captured with the leads still shorted.
    /// </summary>
    [Theory]
    [InlineData(0.9998, true)]
    [InlineData(0.9583, true)]
    [InlineData(0.0013, false)]
    [InlineData(0.00084, false)]
    public void LooksLikeOpen_SeparatesTheTwoStandards(double magnitude, bool expected)
    {
        var cal = Flat(magnitude);

        Assert.Equal(magnitude, cal.MedianMagnitude, 6);
        Assert.Equal(expected, cal.LooksLikeOpen);
    }

    /// <summary>
    /// The median ignores a few wild points at the ends of the captured band, which is where a
    /// standard is noisiest -- so one bad end bin cannot flip the verdict.
    /// </summary>
    [Fact]
    public void MedianMagnitude_IsNotDraggedByTheEndsOfTheBand()
    {
        var freqHz = new double[] { 10.0, 100.0, 1000.0, 10000.0, 20000.0 };
        var real = new double[] { 0.0, 0.99, 1.0, 1.01, 40.0 };
        var imag = new double[freqHz.Length];

        var cal = new ImpedanceCalibration(freqHz, real, imag);

        Assert.Equal(1.0, cal.MedianMagnitude, 6);
        Assert.True(cal.LooksLikeOpen);
    }

    /// <summary>A curve with nothing in it has no shape to report, rather than a misleading zero.</summary>
    [Fact]
    public void MedianMagnitude_OfAnUnusableCurve_IsNaN()
    {
        Assert.True(double.IsNaN(new ImpedanceCalibration([], [], []).MedianMagnitude));
    }

    private static ImpedanceCalibration Flat(double magnitude)
    {
        var freqHz = new double[] { 10.0, 100.0, 1000.0, 10000.0 };
        var real = new double[freqHz.Length];
        var imag = new double[freqHz.Length];
        Array.Fill(real, magnitude);
        return new ImpedanceCalibration(freqHz, real, imag);
    }
}
