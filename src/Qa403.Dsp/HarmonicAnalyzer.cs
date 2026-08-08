namespace Qa403.Dsp;

/// <summary>
/// Locates the fundamental and harmonics 2..H by searching near each expected bin, then
/// sums PSD power over ±<c>skirtHalfWidthBins</c> around the located peak to capture jitter
/// skirts. Summing in the PSD domain (S2-normalised, Parseval-safe) rather than the
/// amplitude domain (S1) is deliberate: for a perfectly coherent tone under a non-rectangular
/// window, the window's own mainlobe spreads real energy into the neighbouring bins, and only
/// the PSD-domain sum recovers the exact input amplitude when those bins are included. An
/// S1-based sum would double-count that spread and inflate the reading.
/// </summary>
public static class HarmonicAnalyzer
{
    public const int DefaultPeakSearchSpanBins = 2;
    public const int DefaultSkirtHalfWidthBins = 3;

    /// <summary>Bins of clearance between the skirt and the noise-estimation window, so the window's own mainlobe and a tone's phase-noise skirt don't get counted as noise floor.</summary>
    public const int DefaultNoiseGuardBins = 4;

    /// <summary>Width, in bins, of the noise-estimation window taken on each side of a harmonic.</summary>
    public const int DefaultNoiseWindowBins = 24;

    /// <summary>
    /// How far below the local noise floor a harmonic that cannot be distinguished from noise is
    /// reported at. Something finite is needed so dBV/dBc stay renderable, and 20 dB down puts it
    /// unmistakably in "below the floor" territory while contributing 1% of a noise bin's power to
    /// the THD sum — i.e. nothing.
    /// </summary>
    private const double BelowFloorAmplitudeFactor = 0.1;

    /// <summary>
    /// How far above the local noise floor a candidate peak must sit before
    /// <see cref="FindPeakBin"/>'s result is trusted over the harmonic's expected bin.
    ///
    /// Peak-picking exists to track a tone that clock drift has moved a bin or two, but taking the
    /// largest of 2·span+1 bins of pure noise is a biased estimator: for exponentially-distributed
    /// noise power the max of 5 bins averages 2.28x the mean, i.e. +3.6 dB of pure selection bias
    /// on every harmonic that isn't really there. Below this threshold there is no peak to track,
    /// so the expected bin is used and the bias disappears.
    /// </summary>
    private const double PeakAcceptanceFactor = 4.0;   // 6 dB

    /// <summary>
    /// Converts the median of the flanking bins into an estimate of their mean.
    ///
    /// PSD noise bins are exponentially distributed (|X|² of complex Gaussian noise, chi-squared
    /// with 2 DOF), and for that distribution median = ln(2)·mean. The median is what's measured —
    /// it ignores spurs and neighbouring harmonics, which a mean would not — but the *mean* is
    /// what has to be subtracted from a sum of bins, so it needs scaling back up by 1/ln(2).
    /// Skipping this under-subtracts by 10·log10(1/ln2) = 1.6 dB on every harmonic.
    /// </summary>
    private static readonly double MedianToMeanExponential = 1.0 / Math.Log(2.0);

    /// <summary>
    /// Writes up to <paramref name="harmonicCount"/> harmonics (order 1 = fundamental) into
    /// <paramref name="destination"/> and returns how many were written. Stops early if a
    /// harmonic's expected bin would exceed Nyquist or <paramref name="maxBinInclusive"/>.
    ///
    /// <paramref name="maxBinInclusive"/> is the top of the measurement band. QuantAsylum's app
    /// bounds THD the same way — its REST endpoint is <c>/ThdDb/{freq}/{stopHz}</c>, called with
    /// 20000 — so for a 5 kHz fundamental H5 at 25 kHz is out of band and not counted. Leaving it
    /// at Nyquist instead would count ultrasonic harmonics the reference deliberately excludes.
    /// </summary>
    public static int Analyze(
        PsdSpectrum psd,
        int fundamentalBin,
        int harmonicCount,
        Span<Harmonic> destination,
        int peakSearchSpanBins = DefaultPeakSearchSpanBins,
        int skirtHalfWidthBins = DefaultSkirtHalfWidthBins,
        bool subtractNoiseFloor = true,
        int maxBinInclusive = int.MaxValue)
    {
        if (harmonicCount < 1)
            throw new ArgumentOutOfRangeException(nameof(harmonicCount));
        if (destination.Length < harmonicCount)
            throw new ArgumentException("Destination too short for requested harmonic count.", nameof(destination));

        int written = 0;
        double fundamentalVrms = 0.0;

        for (int order = 1; order <= harmonicCount; order++)
        {
            int expectedBin = fundamentalBin * order;
            if (expectedBin >= psd.ValuesV2PerHz.Length || expectedBin > maxBinInclusive)
                break;

            double noiseDensity = subtractNoiseFloor ? LocalNoiseDensity(psd, expectedBin, skirtHalfWidthBins) : 0.0;

            int peak = FindPeakBin(psd.ValuesV2PerHz, expectedBin, peakSearchSpanBins);
            if (noiseDensity > 0.0 && psd.ValuesV2PerHz[peak] < noiseDensity * PeakAcceptanceFactor)
                peak = expectedBin;   // nothing here but noise — don't let max-picking invent a peak

            double vrms = HarmonicVrms(psd, peak, skirtHalfWidthBins, noiseDensity);
            double dbv = 20.0 * Math.Log10(Math.Max(vrms, double.Epsilon));

            if (order == 1)
                fundamentalVrms = vrms;

            double dbc = 20.0 * Math.Log10(Math.Max(vrms, double.Epsilon) / fundamentalVrms);

            destination[written++] = new Harmonic(order, peak, vrms, dbv, dbc);
        }

        return written;
    }

    /// <summary>
    /// One harmonic's amplitude, with the local noise floor removed from the skirt sum.
    ///
    /// Without the subtraction the skirt sum is signal + noise, and for a harmonic anywhere near
    /// the floor that is overwhelmingly noise: ±3 bins across 8 harmonics integrates 56 bins of
    /// noise floor, which on the bench reported THD = -106 dB for a signal with no harmonic
    /// content at all. <see cref="FindPeakBin"/> makes it worse by picking the largest of several
    /// noise bins before the skirt is summed. Subtracting an estimate of the noise actually
    /// present in those bins is what makes THD a measurement of distortion rather than of the
    /// analyzer's own floor.
    ///
    /// The estimate is the *median* PSD density of a window either side of the skirt (median, not
    /// mean, so an adjacent spur or another harmonic can't inflate it), scaled up to the skirt's
    /// bin count.
    /// </summary>
    private static double HarmonicVrms(PsdSpectrum psd, int peakBin, int skirtHalfWidthBins, double noiseDensity)
    {
        double totalVrms = psd.IntegrateVrms(peakBin - skirtHalfWidthBins, peakBin + skirtHalfWidthBins);
        if (noiseDensity <= 0.0)
            return totalVrms;

        int skirtBins = (2 * skirtHalfWidthBins) + 1;
        double noisePower = noiseDensity * skirtBins * psd.BinSizeHz;
        double signalPower = (totalVrms * totalVrms) - noisePower;

        // At or below the floor there is no measurable harmonic — report it well under the floor
        // rather than reporting the floor itself as if it were distortion.
        double floorVrms = Math.Sqrt(noisePower) * BelowFloorAmplitudeFactor;
        return signalPower <= 0.0 ? floorVrms : Math.Max(Math.Sqrt(signalPower), floorVrms);
    }

    /// <summary>Median PSD density (V²/Hz) of the bins flanking a harmonic, skipping a guard band around its skirt.</summary>
    private static double LocalNoiseDensity(
        PsdSpectrum psd,
        int peakBin,
        int skirtHalfWidthBins,
        int guardBins = DefaultNoiseGuardBins,
        int windowBins = DefaultNoiseWindowBins)
    {
        var values = psd.ValuesV2PerHz;
        int inner = skirtHalfWidthBins + guardBins;

        Span<double> samples = stackalloc double[2 * windowBins];
        int n = 0;

        for (int offset = inner; offset < inner + windowBins; offset++)
        {
            int lo = peakBin - offset;
            if (lo >= 1)
                samples[n++] = values[lo];

            int hi = peakBin + offset;
            if (hi < values.Length)
                samples[n++] = values[hi];
        }

        if (n == 0)
            return 0.0;

        samples = samples[..n];
        samples.Sort();
        double median = n % 2 == 1 ? samples[n / 2] : 0.5 * (samples[(n / 2) - 1] + samples[n / 2]);
        return median * MedianToMeanExponential;
    }

    /// <summary>Finds the true peak within ±searchSpanBins of an expected bin (clock jitter can shift a tone by a bin or two).</summary>
    public static int FindPeakBin(ReadOnlySpan<double> psdValues, int expectedBin, int searchSpanBins)
    {
        int lo = Math.Max(0, expectedBin - searchSpanBins);
        int hi = Math.Min(psdValues.Length - 1, expectedBin + searchSpanBins);

        int peak = lo;
        double peakValue = psdValues[lo];
        for (int k = lo + 1; k <= hi; k++)
        {
            if (psdValues[k] > peakValue)
            {
                peakValue = psdValues[k];
                peak = k;
            }
        }

        return peak;
    }
}
