using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class CumulativeSpectralDecayTests
{
    private const double SampleRateHz = 48000.0;
    private const int RecordLength = 32768;

    private static readonly IFftBackend Fft = new MathNetBackend();

    private static CsdSettings Settings(
        int fftLength = 512,
        int blockShift = 16,
        int blockCount = 20,
        CsdWindowMode mode = CsdWindowMode.Apodizing,
        double riseMs = 0.2,
        double lowHz = 100.0,
        double highHz = 20000.0,
        int columns = 300) =>
        new(fftLength, blockShift, blockCount, mode, riseMs, lowHz, highHz, columns);

    /// <summary>
    /// A record holding nothing but a single sample has no decay to show: every slice past the
    /// impulse is the window sliding off it. This is the null case, and it is what a surface that
    /// draws the window instead of the DUT looks like — the ridge is flat across the whole band
    /// because a delta is, and it falls off a cliff rather than decaying.
    /// </summary>
    [Fact]
    public void Compute_OfADelta_HasAFlatFrontSliceAndNothingBehindIt()
    {
        var ir = new double[RecordLength];
        ir[4000] = 1.0;

        var surface = CumulativeSpectralDecay.Compute(
            ir, 4000, SampleRateHz, RecordLength / 4, Settings(), Fft);

        var front = surface.Slices[0].LevelDb;
        Assert.All(front, level => Assert.InRange(level, -0.01, 0.01));

        // One block shift past the impulse the apodizing window's rise no longer covers it at all.
        Assert.All(surface.Slices[^1].LevelDb, level => Assert.True(level < -40.0, $"{level} dB"));
    }

    /// <summary>
    /// The measurement itself: a resonance that rings after the impulse must decay on the surface at
    /// the rate it rings at. A one-pole decaying sinusoid at 1 kHz with a 2 ms time constant loses
    /// 8.686/2 = 4.34 dB per millisecond, and that is what the ridge at 1 kHz has to show.
    /// </summary>
    [Fact]
    public void Compute_OfARingingResonance_DecaysAtTheRingsOwnRate()
    {
        const double freqHz = 1000.0;
        const double tauMs = 2.0;
        const int peak = 4000;

        var ir = new double[RecordLength];
        for (int i = peak; i < RecordLength; i++)
        {
            double t = (i - peak) / SampleRateHz;
            ir[i] = Math.Exp(-t / (tauMs / 1000.0)) * Math.Sin(2.0 * Math.PI * freqHz * t);
        }

        // 1 ms of slices, so the whole span is a few time constants and the window (10.7 ms) is
        // long enough to resolve 1 kHz comfortably.
        var settings = Settings(fftLength: 512, blockShift: 24, blockCount: 21);
        var surface = CumulativeSpectralDecay.Compute(ir, peak, SampleRateHz, RecordLength / 4, settings, Fft);

        int column = NearestColumn(surface.FreqHz, freqHz);
        double firstMs = surface.Slices[4].Value;
        double lastMs = surface.Slices[^1].Value;
        double fell = surface.Slices[4].LevelDb[column] - surface.Slices[^1].LevelDb[column];

        double expected = 8.6858896 / tauMs * (lastMs - firstMs);
        Assert.Equal(expected, fell, 2.0);
    }

    /// <summary>
    /// Normalisation is one reference for the whole surface, not one per slice — which is what keeps
    /// the DUT's frequency response in the front slice, so a resonance reads as a ridge that is both
    /// tall and slow. A per-slice reference would put every slice's own loudest column at 0 dB and
    /// draw a DUT with no frequency response at all.
    /// </summary>
    [Fact]
    public void Compute_NormalisesTheWholeSurfaceOnce_SoTheFrontSliceKeepsTheResponse()
    {
        const int peak = 4000;

        // A delta plus a far quieter, slowly decaying 4 kHz resonance. Quieter by a wide margin on
        // purpose: a narrowband ring concentrates its energy into a handful of bins, so it takes
        // very little of it to out-peak a flat delta once a 512-point window has integrated it.
        var ir = new double[RecordLength];
        ir[peak] = 1.0;
        for (int i = peak; i < RecordLength; i++)
        {
            double t = (i - peak) / SampleRateHz;
            ir[i] += 0.001 * Math.Exp(-t / 0.010) * Math.Sin(2.0 * Math.PI * 4000.0 * t);
        }

        var surface = CumulativeSpectralDecay.Compute(
            ir, peak, SampleRateHz, RecordLength / 4, Settings(blockShift: 32, blockCount: 16), Fft);

        // Nothing exceeds the surface reference, and the reference is reached.
        double highest = surface.Slices.SelectMany(s => s.LevelDb).Max();
        Assert.Equal(0.0, highest, 3);

        // Exactly one slice reaches it. Under a per-slice reference every one of them would, which
        // is the mistake this guards: a surface on which every moment is as loud as every other.
        Assert.All(surface.Slices.Skip(1), slice => Assert.True(slice.LevelDb.Max() < -5.0, $"{slice.Value} ms reached {slice.LevelDb.Max()} dB"));

        // And what is left ringing at the back is the resonance, not the band it sits in. Located by
        // the surface's own argmax rather than by asking for 4 kHz: the columns are narrower than
        // the transform's bins up here, so several of them read the same bin and "the column
        // nearest 4 kHz" is not necessarily the one holding the peak.
        var last = surface.Slices[^1].LevelDb;
        int ridge = Array.IndexOf(last, last.Max());
        Assert.InRange(surface.FreqHz[ridge], 3800.0, 4200.0);
    }

    /// <summary>
    /// The far end of the sweep gate is zeros, and zeros read as a perfect decay. A request whose
    /// slices would reach into them is refused rather than answered, because the answer would be a
    /// picture of the gate wearing the DUT's axis labels.
    /// </summary>
    [Fact]
    public void Compute_WhenTheSlicesWouldReachPastTheRecord_IsRefused()
    {
        var ir = new double[RecordLength];
        ir[100] = 1.0;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CumulativeSpectralDecay.Compute(
            ir, 100, SampleRateHz, 600, Settings(fftLength: 512, blockShift: 64, blockCount: 20), Fft));

        Assert.Contains("past the impulse", ex.Message);
    }

    /// <summary>
    /// A transform of L samples resolves fs/L, and there is nothing below that to draw. The band's
    /// bottom is raised to it rather than left where it was asked for, and the surface says so.
    /// </summary>
    [Fact]
    public void Compute_RaisesTheBandsBottomToWhatTheTransformResolves()
    {
        var ir = new double[RecordLength];
        ir[4000] = 1.0;

        var surface = CumulativeSpectralDecay.Compute(
            ir, 4000, SampleRateHz, RecordLength / 4, Settings(fftLength: 256, lowHz: 20.0), Fft);

        Assert.Equal(187.5, surface.ResolutionLimitHz, 3);
        Assert.True(surface.FreqHz[0] >= 187.5, $"band starts at {surface.FreqHz[0]} Hz");
    }

    /// <summary>
    /// The apodizing window rises to exactly one at the sample the slice is labelled with, and falls
    /// from there. That equality is what makes the front slice read the impulse at full weight
    /// rather than at whatever the rise happened to reach.
    /// </summary>
    [Fact]
    public void BuildWindow_Apodizing_PeaksAtTheSliceTime()
    {
        var window = CumulativeSpectralDecay.BuildWindow(
            Settings(fftLength: 1024, riseMs: 0.5), SampleRateHz, out int peakOffset);

        Assert.Equal(24, peakOffset);
        Assert.Equal(1.0, window[peakOffset], 12);
        Assert.Equal(0.0, window[0], 12);
        Assert.True(window[peakOffset + 1] < 1.0);
        Assert.Equal(window.Max(), window[peakOffset], 12);
    }

    /// <summary>A Hann slice is centred on its own time, so its peak is half a block in.</summary>
    [Fact]
    public void BuildWindow_Hann_IsCentredOnTheSliceTime()
    {
        var window = CumulativeSpectralDecay.BuildWindow(
            Settings(fftLength: 1024, mode: CsdWindowMode.Hann), SampleRateHz, out int peakOffset);

        Assert.Equal(512, peakOffset);
        Assert.Equal(1.0, window[peakOffset], 12);
    }

    private static int NearestColumn(float[] freqHz, double target)
    {
        int best = 0;
        for (int c = 1; c < freqHz.Length; c++)
        {
            if (Math.Abs(freqHz[c] - target) < Math.Abs(freqHz[best] - target))
                best = c;
        }

        return best;
    }
}
