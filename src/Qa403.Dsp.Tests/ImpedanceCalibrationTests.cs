using System.Numerics;

namespace Qa403.Dsp.Tests;

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
}
