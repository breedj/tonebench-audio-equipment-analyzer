using System.Numerics;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

public class IntermodulationAnalyzerTests
{
    // 8192 points at 81 920 Hz: 10 Hz bins, so every tone and product below sits on an integer bin.
    private const int FftSize = 8192;
    private const double SampleRateHz = 81_920.0;
    private const int Nyquist = FftSize / 2;

    private const int SmpteLowBin = 10;     // 100 Hz — 10 bins up, clear of MinToneSpacingBins
    private const int SmpteHighBin = 700;   // 7 kHz
    private const int CcifLowBin = 1900;
    private const int CcifHighBin = 2000;

    private static PsdSpectrum Psd(WindowType windowType, double[] samples, double[] buffer)
    {
        var w = Window.Get(windowType, FftSize);
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        return PsdSpectrum.Compute(spectrum, SampleRateHz, w.Sums, buffer);
    }

    private static PsdSpectrum Psd(WindowType windowType, double[] buffer, params (double AmplitudeV, int Bin)[] components) =>
        Psd(windowType, TestSignals.CoherentTone(FftSize, components), buffer);

    private static double Dbc(double dbc) => Math.Pow(10.0, dbc / 20.0);

    [Theory]
    [InlineData(WindowType.Rectangular)]
    [InlineData(WindowType.Hann)]
    public void Analyze_Smpte_SidebandsAtMinus60Dbc_ReadsImdWithinTenthOfADb(WindowType windowType)
    {
        const double carrier = 0.25;
        double sideband = carrier * Dbc(-60.0);
        var buffer = new double[Nyquist + 1];
        var psd = Psd(windowType, buffer,
            (1.0, SmpteLowBin), (carrier, SmpteHighBin),
            (sideband, SmpteHighBin - SmpteLowBin), (sideband, SmpteHighBin + SmpteLowBin),
            (sideband, SmpteHighBin - (2 * SmpteLowBin)), (sideband, SmpteHighBin + (2 * SmpteLowBin)));

        var result = IntermodulationAnalyzer.Analyze(psd, SmpteLowBin, SmpteHighBin, IntermodulationMethod.Smpte, maxOrder: 3, maxBinInclusive: Nyquist);

        Assert.NotNull(result);
        // Four sidebands each −60 dBc re the carrier: RMS sum is −60 + 10·log10(4) = −53.98 dB.
        Assert.Equal(-60.0 + (10.0 * Math.Log10(4.0)), result.ImdDb, 1);
        Assert.Equal(4, result.Products.Count);
        Assert.Equal(2, result.PerOrder.Count);
        Assert.Equal(-60.0 + (10.0 * Math.Log10(2.0)), 20.0 * Math.Log10(result.PerOrder[0].Ratio), 1);
        Assert.Equal(2, result.PerOrder[0].Order);
        Assert.Equal(3, result.PerOrder[1].Order);
        Assert.Equal(carrier / Math.Sqrt(2.0), result.ReferenceVrms, 3);
        Assert.Equal(SmpteHighBin * 10.0, result.ToneHighFreqHz, 6);
    }

    [Theory]
    [InlineData(WindowType.Rectangular)]
    [InlineData(WindowType.Hann)]
    public void Analyze_Ccif_DifferenceProductsAtMinus60Dbc_ReadsImdWithinTenthOfADb(WindowType windowType)
    {
        const double tone = 0.5;
        double reference = Math.Sqrt(2.0 * tone * tone);
        double product = reference * Dbc(-60.0);
        var buffer = new double[Nyquist + 1];
        var psd = Psd(windowType, buffer,
            (tone, CcifLowBin), (tone, CcifHighBin),
            (product, CcifHighBin - CcifLowBin),            // order 2: 1 kHz
            (product, (2 * CcifLowBin) - CcifHighBin),      // order 3: 18 kHz
            (product, (2 * CcifHighBin) - CcifLowBin));     // order 3: 21 kHz

        var result = IntermodulationAnalyzer.Analyze(psd, CcifLowBin, CcifHighBin, IntermodulationMethod.Ccif, maxOrder: 3, maxBinInclusive: Nyquist);

        Assert.NotNull(result);
        Assert.Equal(-60.0 + (10.0 * Math.Log10(3.0)), result.ImdDb, 1);
        Assert.Equal(3, result.Products.Count);
        Assert.Equal(reference / Math.Sqrt(2.0), result.ReferenceVrms, 3);
    }

    [Fact]
    public void Analyze_Ccif_ProductAboveTheBandLimit_IsOmittedNotZero()
    {
        const double tone = 0.5;
        double product = Math.Sqrt(2.0 * tone * tone) * Dbc(-60.0);
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, buffer,
            (tone, CcifLowBin), (tone, CcifHighBin),
            (product, CcifHighBin - CcifLowBin),
            (product, (2 * CcifLowBin) - CcifHighBin),
            (product, (2 * CcifHighBin) - CcifLowBin));

        // 20 kHz band limit: 21 kHz is out, 18 kHz and 1 kHz stay.
        var result = IntermodulationAnalyzer.Analyze(psd, CcifLowBin, CcifHighBin, IntermodulationMethod.Ccif, maxOrder: 3, maxBinInclusive: 2000);

        Assert.NotNull(result);
        Assert.Equal(2, result.Products.Count);
        Assert.DoesNotContain(result.Products, p => p.Bin == (2 * CcifHighBin) - CcifLowBin);
        var third = Assert.Single(result.PerOrder, o => o.Order == 3);
        Assert.Equal(-60.0, 20.0 * Math.Log10(third.Ratio), 1);
        Assert.Equal(-60.0 + (10.0 * Math.Log10(2.0)), result.ImdDb, 1);
    }

    [Fact]
    public void Analyze_Ccif_Reference_IsTheRmsSumOfBothTones()
    {
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Rectangular, buffer, (0.5, CcifLowBin), (0.5, CcifHighBin));

        var result = IntermodulationAnalyzer.Analyze(psd, CcifLowBin, CcifHighBin, IntermodulationMethod.Ccif, maxOrder: 3, maxBinInclusive: Nyquist);

        Assert.NotNull(result);
        double v = 0.5 / Math.Sqrt(2.0);
        Assert.Equal(Math.Sqrt(2.0) * v, result.ReferenceVrms, 6);
        Assert.Equal(2.0 * v, result.SineEquivalentVrms, 6);
        // For equal tones the true RMS sits 3.01 dB below the sine-equivalent level.
        Assert.Equal(-3.01, result.CombinedRmsDbv - result.SineEquivalentDbv, 2);
    }

    [Fact]
    public void Analyze_LocatesEachToneWithinTwoBins_AndFormsProductsFromTheLocatedBins()
    {
        double sideband = 0.25 * Dbc(-50.0);
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Rectangular, buffer,
            (1.0, SmpteLowBin), (0.25, SmpteHighBin),
            (sideband, SmpteHighBin - SmpteLowBin), (sideband, SmpteHighBin + SmpteLowBin));

        // Asked for bins one and two off the truth.
        var result = IntermodulationAnalyzer.Analyze(psd, SmpteLowBin - 1, SmpteHighBin + 2, IntermodulationMethod.Smpte, maxOrder: 2, maxBinInclusive: Nyquist);

        Assert.NotNull(result);
        Assert.Equal(SmpteLowBin, result.ToneLow.Bin);
        Assert.Equal(SmpteHighBin, result.ToneHigh.Bin);
        Assert.Equal([SmpteHighBin - SmpteLowBin, SmpteHighBin + SmpteLowBin], result.Products.Select(p => p.Bin));
        Assert.Equal(-50.0 + (10.0 * Math.Log10(2.0)), result.ImdDb, 1);
    }

    [Fact]
    public void Analyze_ProductCoincidingWithATone_IsOmitted()
    {
        // f2 = 2·f1: the 2nd-order difference lands on f1 itself, and 2f1 − f2 is DC.
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Rectangular, buffer, (0.5, 100), (0.5, 200));

        var result = IntermodulationAnalyzer.Analyze(psd, 100, 200, IntermodulationMethod.Ccif, maxOrder: 3, maxBinInclusive: Nyquist);

        Assert.NotNull(result);
        var only = Assert.Single(result.Products);
        Assert.Equal(300, only.Bin);
        Assert.Equal(3, only.Order);
    }

    [Fact]
    public void Analyze_NoProductsAboveTheFloor_ReadsWellBelowTheFloorNotZero()
    {
        var samples = TestSignals.CoherentTone(FftSize, (1.0, SmpteLowBin), (0.25, SmpteHighBin));
        var noise = TestSignals.GaussianWhiteNoise(FftSize, stdDev: 1e-5, seed: 7);
        for (int n = 0; n < FftSize; n++)
            samples[n] += noise[n];

        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, samples, buffer);

        var result = IntermodulationAnalyzer.Analyze(psd, SmpteLowBin, SmpteHighBin, IntermodulationMethod.Smpte, maxOrder: 5, maxBinInclusive: Nyquist);

        Assert.NotNull(result);
        Assert.True(result.Imd > 0.0);
        Assert.True(result.ImdDb < -90.0, $"expected the floor well below −90 dB, got {result.ImdDb:F1} dB");
    }

    [Fact]
    public void Analyze_TonesCloserThanEightBins_ReturnsNull()
    {
        var buffer = new double[Nyquist + 1];
        var psd = Psd(WindowType.Hann, buffer, (1.0, 6), (0.25, SmpteHighBin));

        Assert.Null(IntermodulationAnalyzer.Analyze(psd, 6, SmpteHighBin, IntermodulationMethod.Smpte, maxOrder: 5, maxBinInclusive: Nyquist));
        Assert.Null(IntermodulationAnalyzer.Analyze(psd, 1995, 2000, IntermodulationMethod.Ccif, maxOrder: 3, maxBinInclusive: Nyquist));
    }

    [Fact]
    public void EnumerateProductBins_Smpte_OrderFive_IsFourSidebandPairs()
    {
        var bins = IntermodulationAnalyzer.EnumerateProductBins(IntermodulationMethod.Smpte, 10, 700, 5).ToArray();

        Assert.Equal(8, bins.Length);
        Assert.Equal([690, 710, 680, 720, 670, 730, 660, 740], bins.Select(b => b.Bin));
        Assert.Equal([2, 2, 3, 3, 4, 4, 5, 5], bins.Select(b => b.Order));
        Assert.Equal((2, -1, 1, 690), bins[0]);
        Assert.Equal((5, +1, 4, 740), bins[7]);
    }

    [Fact]
    public void EnumerateProductBins_Ccif_OrderFour_IsThreeDifferenceProducts()
    {
        var bins = IntermodulationAnalyzer.EnumerateProductBins(IntermodulationMethod.Ccif, 1900, 2000, 4).ToArray();

        Assert.Equal([2, 3, 3, 4, 4, 4], bins.Select(b => b.Order));
        Assert.Equal([100, 2100, 1800, 4100, 200, 3700], bins.Select(b => b.Bin));
        Assert.Equal((4, 2, 2, 200), bins[4]);
    }
}
