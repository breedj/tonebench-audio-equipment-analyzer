using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

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

        // The window's leading edge has long since passed the impulse, so the last slice holds
        // nothing whatever — which is the cliff, and is what a delta has instead of a decay.
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
    /// ARTA's apodizing window, which is a rectangle with a Blackman4 taper over each end — and the
    /// flat middle is the whole of it. The window this replaced fell as a raised cosine from the
    /// slice's own time to zero at the end of the block, so the second half of every block was
    /// thrown away; a cumulative spectrum is the transform of the response <i>from</i> the slice's
    /// time onwards, and that is what a flat top integrates.
    /// </summary>
    [Fact]
    public void BuildWindow_Apodizing_IsARectangleTaperedAtBothEnds()
    {
        const int length = 1024;
        var window = CumulativeSpectralDecay.BuildWindow(
            Settings(fftLength: length, riseMs: 0.5), SampleRateHz, out int peakOffset);

        // 0.5 ms at 48 kHz, and the slice is labelled by the first sample it weights fully.
        Assert.Equal(24, peakOffset);
        Assert.Equal(1.0, window[peakOffset], 12);
        Assert.Equal(1.0, window.Max(), 12);

        // Flat from there to the trailing taper. The regression this is here for is a window that
        // starts falling at `peakOffset` instead.
        for (int i = peakOffset; i < length - peakOffset; i++)
            Assert.Equal(1.0, window[i], 12);

        // Blackman4 ends at 6e-5 rather than at zero, and the two tapers mirror each other.
        Assert.InRange(window[0], 0.0, 1e-4);
        for (int i = 0; i < peakOffset; i++)
            Assert.Equal(window[i], window[length - 1 - i], 12);

        Assert.True(window[1] > window[0], "the taper rises");
    }

    /// <summary>
    /// An echo half a block after the impulse is weighted exactly as much as the impulse, because
    /// the window between its tapers is flat — so the two interfere to a comb with true nulls. It
    /// is the plainest statement of what the flat top buys: under the decaying window this
    /// replaced, that echo arrived at 0.52 of its real size and the deepest null in the front slice
    /// was 10 dB rather than nothing at all.
    /// </summary>
    [Fact]
    public void Compute_WeighsAnEchoInsideTheBlockAsMuchAsTheImpulseItself()
    {
        const int peak = 4000;
        const int fftLength = 512;

        // Half the transform apart, so every odd bin of the comb is an exact null and every even
        // one is twice the impulse. Both samples land in the flat part of the window.
        var ir = new double[RecordLength];
        ir[peak] = 1.0;
        ir[peak + (fftLength / 2)] = 1.0;

        var surface = CumulativeSpectralDecay.Compute(
            ir, peak, SampleRateHz, RecordLength / 4, Settings(fftLength: fftLength), Fft);

        var front = surface.Slices[0].LevelDb;
        Assert.Equal(0.0, front.Max(), 3);
        Assert.True(front.Min() < -60.0, $"deepest null read {front.Min()} dB");
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
