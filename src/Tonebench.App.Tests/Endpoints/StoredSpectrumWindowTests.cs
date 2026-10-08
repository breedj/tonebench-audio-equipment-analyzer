using Tonebench.App.Endpoints;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// A saved measurement keeps every bin of its analysis FFT (<c>SpectrumCodec</c>, 2 bytes each),
/// and it is only the display decimation that reduces it — which used to happen once, over the
/// whole span, so zooming a stored trace magnified its columns instead of resolving more of the
/// bins behind them. These pin the windowed form, and that the unwindowed one did not change.
/// </summary>
public class StoredSpectrumWindowTests
{
    private const int FftSize = 65536;
    private const double SampleRateHz = 48000.0;
    private const double BinSizeHz = SampleRateHz / FftSize;   // 0.7324 Hz
    private const int DisplayColumns = 400;
    private const double DisplayLoFreqHz = 10.0;

    /// <summary>A trace with a distinct value per bin, so a column's min/max says which bins it covered.</summary>
    private static StoredSpectrum Spectrum()
    {
        var dbv = new double[(FftSize / 2) + 1];
        for (int i = 0; i < dbv.Length; i++)
            dbv[i] = -100.0 + (i * 0.001);

        return new StoredSpectrum(
            0, WindowType.Hann, S1: 32768.0, S2: 24576.0, EnbwHz: 1.5, BinSizeHz: BinSizeHz,
            SpectrumNorm.Amplitude, dbv);
    }

    [Fact]
    public void WithNoWindow_SpansTheWholeTraceAsItAlwaysDid()
    {
        var columns = MeasurementsEndpoints.DecimateForDisplay(Spectrum(), DisplayLoFreqHz, DisplayColumns);

        Assert.Equal(DisplayColumns, columns.Length);
        Assert.InRange(columns[0].FreqHz, 9.0f, 16.0f);
        // The *centre* of the last column, which at the top of a log axis merges hundreds of
        // bins — so it sits a few hundred hertz below Nyquist rather than on it.
        Assert.InRange(columns[^1].FreqHz, 23000.0f, 24000.0f);
    }

    [Fact]
    public void WithAWindow_SpendsEveryColumnInsideIt()
    {
        var columns = MeasurementsEndpoints.DecimateForDisplay(
            Spectrum(), DisplayLoFreqHz, DisplayColumns, new SpectrumViewport(1000.0, 2000.0));

        Assert.All(columns, c => Assert.InRange(c.FreqHz, 999.0f, 2001.0f));
        Assert.InRange(columns[0].FreqHz, 999.0f, 1001.0f);
        Assert.InRange(columns[^1].FreqHz, 1990.0f, 2001.0f);
    }

    /// <summary>
    /// The point of the exercise: the same budget over a tenth of the span resolves far more of
    /// what is inside it.
    /// </summary>
    [Fact]
    public void AWindowResolvesFarMoreInsideItselfThanTheWholeSpanDoes()
    {
        var whole = MeasurementsEndpoints.DecimateForDisplay(Spectrum(), DisplayLoFreqHz, DisplayColumns);
        var zoomed = MeasurementsEndpoints.DecimateForDisplay(
            Spectrum(), DisplayLoFreqHz, DisplayColumns, new SpectrumViewport(1000.0, 2000.0));

        int wholeInside = whole.Count(c => c.FreqHz is >= 1000.0f and <= 2000.0f);

        Assert.True(
            zoomed.Length > wholeInside * 3,
            $"a window should resolve far more inside itself: {zoomed.Length} vs {wholeInside}");
    }

    /// <summary>
    /// Zoom far enough and a column is one bin, so its min and max are the same value — the band
    /// has nothing left to draw and what is on screen is the raw FFT.
    /// </summary>
    [Fact]
    public void ATightWindow_ReachesOneBinPerColumn()
    {
        // ~14 bins at 0.73 Hz each, far fewer than the 400-column budget.
        var columns = MeasurementsEndpoints.DecimateForDisplay(
            Spectrum(), DisplayLoFreqHz, DisplayColumns, new SpectrumViewport(1000.0, 1010.0));

        Assert.InRange(columns.Length, 5, 30);
        Assert.All(columns, c => Assert.Equal(c.MaxDbv, c.MinDbv));
    }

    /// <summary>A window wider than the trace is the whole trace, not an error.</summary>
    [Fact]
    public void AWindowBeyondTheTrace_IsClampedToIt()
    {
        var columns = MeasurementsEndpoints.DecimateForDisplay(
            Spectrum(), DisplayLoFreqHz, DisplayColumns, new SpectrumViewport(0.0, 1_000_000.0));

        Assert.Equal(DisplayColumns, columns.Length);
        Assert.InRange(columns[0].FreqHz, 9.0f, 16.0f);
        // The *centre* of the last column, which at the top of a log axis merges hundreds of
        // bins — so it sits a few hundred hertz below Nyquist rather than on it.
        Assert.InRange(columns[^1].FreqHz, 23000.0f, 24000.0f);
    }
}
