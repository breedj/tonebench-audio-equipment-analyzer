using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class HarmonicAnalyzerTests
{
    private const int FftSize = 1024;
    private const double SampleRateHz = 40960.0;
    private const int FundamentalBin = 25;

    private static (Complex[] Spectrum, WindowSums Sums) ForwardWindowed(WindowType windowType, params (double AmplitudeV, int Bin)[] components)
    {
        var w = Window.Get(windowType, FftSize);
        var samples = TestSignals.CoherentTone(FftSize, components);
        var windowed = new double[FftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[FftSize / 2 + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        return (spectrum, w.Sums);
    }

    [Theory]
    [InlineData(WindowType.Rectangular)]
    [InlineData(WindowType.Hann)]
    [InlineData(WindowType.FlatTop)]
    public void Analyze_FundamentalOnly_ReportsExactAmplitudeAndZeroDbc(WindowType windowType)
    {
        var (spectrum, sums) = ForwardWindowed(windowType, (1.0, FundamentalBin));
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, sums, psdBuffer);

        Span<Harmonic> harmonics = stackalloc Harmonic[1];
        int written = HarmonicAnalyzer.Analyze(psd, FundamentalBin, harmonicCount: 1, harmonics);

        Assert.Equal(1, written);
        Assert.Equal(1, harmonics[0].Order);
        Assert.Equal(FundamentalBin, harmonics[0].Bin);

        // FlatTop's 5-term mainlobe spreads a coherent tone's energy wider than the default
        // +/-3 bin skirt captures (Rectangular/Hann's energy is fully within +/-1 bin, so they
        // recover the exact amplitude to numerical precision) — a few 1e-4 Vrms of truncated
        // tail is expected there, not a bug.
        double tolerance = windowType == WindowType.FlatTop ? 1e-4 : 1e-6;
        Assert.True(
            Math.Abs(1.0 / Math.Sqrt(2.0) - harmonics[0].AmplitudeVrms) < tolerance,
            $"{windowType}: expected ~{1.0 / Math.Sqrt(2.0):F6} Vrms, got {harmonics[0].AmplitudeVrms:F6}");
        Assert.Equal(0.0, harmonics[0].Dbc, 9);
    }

    [Fact]
    public void Analyze_LocatesPeakWithinSearchSpan_WhenTargetIsJitterShifted()
    {
        // Tone actually lands on bin 27, not the nominal fundamentalBin=25.
        var (spectrum, sums) = ForwardWindowed(WindowType.Rectangular, (1.0, 27));
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, sums, psdBuffer);

        Span<Harmonic> harmonics = stackalloc Harmonic[1];
        HarmonicAnalyzer.Analyze(psd, fundamentalBin: 25, harmonicCount: 1, harmonics, peakSearchSpanBins: 2);

        Assert.Equal(27, harmonics[0].Bin);
    }

    [Fact]
    public void Analyze_FundamentalPlusSecondHarmonic_SecondHarmonicDbcMatchesRatio()
    {
        // H2 at exactly 1% amplitude of the fundamental -> -40 dBc.
        var (spectrum, sums) = ForwardWindowed(WindowType.Hann, (1.0, FundamentalBin), (0.01, FundamentalBin * 2));
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, sums, psdBuffer);

        Span<Harmonic> harmonics = stackalloc Harmonic[2];
        int written = HarmonicAnalyzer.Analyze(psd, FundamentalBin, harmonicCount: 2, harmonics);

        Assert.Equal(2, written);
        Assert.Equal(2, harmonics[1].Order);
        Assert.Equal(FundamentalBin * 2, harmonics[1].Bin);
        Assert.Equal(-40.0, harmonics[1].Dbc, 2);
    }

    [Fact]
    public void Analyze_StopsEarly_WhenHarmonicBinWouldExceedNyquist()
    {
        var (spectrum, sums) = ForwardWindowed(WindowType.Hann, (1.0, 400)); // *3 = 1200 > nyquist(512)
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, sums, psdBuffer);

        Span<Harmonic> harmonics = stackalloc Harmonic[5];
        int written = HarmonicAnalyzer.Analyze(psd, fundamentalBin: 400, harmonicCount: 5, harmonics);

        Assert.Equal(1, written); // only the fundamental fits; H2 at bin 800 > 512 nyquist
    }

    [Fact]
    public void Analyze_ThrowsWhenDestinationTooShort()
    {
        var (spectrum, sums) = ForwardWindowed(WindowType.Hann, (1.0, FundamentalBin));
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, sums, psdBuffer);

        var harmonics = new Harmonic[1];

        // psd is a ref struct and can't be captured by a lambda, so Assert.Throws is out.
        Exception? caught = null;
        try
        {
            HarmonicAnalyzer.Analyze(psd, FundamentalBin, harmonicCount: 3, harmonics);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsType<ArgumentException>(caught);
    }

    [Fact]
    public void MeasureTone_MatchesAnalyzeForTheFundamental()
    {
        // Analyze is defined as MeasureTone applied per order, so the two must agree bin for bin
        // and volt for volt — IntermodulationAnalyzer relies on it.
        var (spectrum, sums) = ForwardWindowed(WindowType.Hann, (1.0, 27), (0.01, 54));
        var psdBuffer = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, SampleRateHz, sums, psdBuffer);

        Span<Harmonic> harmonics = stackalloc Harmonic[2];
        HarmonicAnalyzer.Analyze(psd, fundamentalBin: 25, harmonicCount: 2, harmonics);
        var tone = HarmonicAnalyzer.MeasureTone(psd, expectedBin: 25);
        var second = HarmonicAnalyzer.MeasureTone(psd, expectedBin: 50);

        Assert.Equal(harmonics[0].Bin, tone.Bin);
        Assert.Equal(harmonics[0].AmplitudeVrms, tone.AmplitudeVrms);
        Assert.Equal(harmonics[1].Bin, second.Bin);
        Assert.Equal(harmonics[1].AmplitudeVrms, second.AmplitudeVrms);
    }
}
