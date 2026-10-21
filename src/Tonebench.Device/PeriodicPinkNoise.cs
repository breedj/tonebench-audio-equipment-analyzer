namespace Tonebench.Device;

/// <summary>
/// Periodic pink noise: a random-phase multisine whose period is exactly the analysis record, with
/// one component on every FFT bin and a magnitude that is flat below a cut-off and falls at
/// &#8722;3 dB per octave above it.
///
/// <para><b>Why this exists beside <see cref="PinkNoiseShaper"/>.</b> That one filters a white
/// stream into 1/f and is a perfectly good pink noise — measured from the recorder's own WAVs it is
/// flat per octave to 0.9 dB from 10 Hz to 10 kHz and Gaussian to the fourth moment. What it is not
/// is <i>periodic</i>: each analysis record sees a different realisation, so every bin's amplitude
/// is Rayleigh distributed about its mean. Ten per cent of bins land 8.8 dB down in any given
/// record and one per cent land 19 dB down, and on a divider measurement — where the DUT channel
/// sits 40 dB below the reference — those are the bins that read wrong. Measured on a real fixture
/// 2026-10-21: the bins that lost coherence were the ones carrying external interference, a 1 kHz
/// comb and a 4602 Hz family sitting 26&#8211;33 dB over the floor, and the curve spiked wherever a
/// momentarily weak stimulus bin met one of them.</para>
///
/// <para>A periodic stimulus answers that: every record holds one whole period, so every bin is
/// excited by exactly the same deterministic amplitude every time, the analysis window is
/// <b>Rectangular</b> rather than Hann (a whole period transforms exactly), and nothing is left to
/// luck. It is also what ARTA's LIMP plays as "PN Pink", which is what makes a measurement here
/// comparable with one there.</para>
///
/// <para><b>Random phases, not Schroeder.</b> A quadratic-phase sequence would have a crest factor
/// near 5 dB against this one's ~12 and would therefore carry some 7 dB more power at the same peak
/// — but it is a swept signal rather than a noise, with a different distribution and a different
/// way of exciting a driver, and ARTA's own definition is explicit that pink PN is random phase
/// with a crest factor "slightly above 12 dB" and a near-normal distribution. Matching that is what
/// was asked for; the phases are chosen by a hash so the choice can be revisited in one place.</para>
///
/// <para><b>Public where <see cref="PinkNoiseShaper"/> is internal</b>, because a measurement has to
/// validate the cut-off it offers against the limits below. Nothing outside this assembly builds a
/// sequence.</para>
///
/// <para><b>The cut-off is a real control, not a DC blocker.</b> Pure 1/f piles power into the
/// bottom of the band — a third of a record's power below 3 Hz — which is spent DAC headroom and
/// DC into a voice coil. Holding the magnitude flat below a corner stops that while still exciting
/// those frequencies, and LIMP recommends setting it near the impedance maximum, 20&#8211;100 Hz for
/// a loudspeaker.</para>
/// </summary>
public sealed class PeriodicPinkNoise
{
    /// <summary>
    /// Below this the magnitude is held flat; above it the sequence is pink. LIMP's default view of
    /// the same control, and low enough that it changes nothing inside the band an impedance run
    /// analyses (which starts at 4 Hz).
    /// </summary>
    public const double DefaultCutoffHz = 5.0;

    public const double MinCutoffHz = 1.0;
    public const double MaxCutoffHz = 1000.0;

    /// <summary>
    /// One built table, keyed by everything it depends on. A run reconfigures its generator once per
    /// pass and a re-primed TX queue rebuilds the same frames, so without this the transform would
    /// run on the transfer thread.
    /// </summary>
    private static readonly Dictionary<(int Period, long RateMilliHz, long CutoffMilliHz), PeriodicPinkNoise> Cache = [];

    private static readonly Lock CacheLock = new();

    private readonly double[] _samples;

    private PeriodicPinkNoise(double[] samples, double crestFactorDb)
    {
        _samples = samples;
        CrestFactorDb = crestFactorDb;
    }

    /// <summary>Peak over RMS of this sequence, in dB. Around 12 for a random-phase multisine.</summary>
    public double CrestFactorDb { get; }

    /// <summary>How many frames one period is; the sequence repeats exactly after this many.</summary>
    public int PeriodFrames => _samples.Length;

    /// <summary>
    /// The sequence for a period, rate and cut-off, at unit RMS — a caller scales by the level it
    /// wants. Cached, so asking twice costs a dictionary lookup.
    /// </summary>
    public static PeriodicPinkNoise For(int periodFrames, double sampleRateHz, double cutoffHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(periodFrames, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var key = (periodFrames, (long)Math.Round(sampleRateHz * 1000.0), (long)Math.Round(cutoffHz * 1000.0));

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var built = Build(periodFrames, sampleRateHz, cutoffHz);
            // Bounded for the same reason the retained spectra are: a long session can walk through
            // several record lengths, and each table is up to a megabyte.
            if (Cache.Count >= 8) Cache.Clear();
            Cache[key] = built;
            return built;
        }
    }

    /// <summary>
    /// This sequence at <paramref name="frameIndex"/> frames into the stream, at unit RMS.
    ///
    /// <para>A pure function of the index, exactly as the chirp and the square wave are, which is
    /// what lets both generator channels play the same noise and a re-primed TX queue rebuild the
    /// same audio. Nothing here is recursive, unlike <see cref="PinkNoiseShaper"/>.</para>
    /// </summary>
    public double Sample(long frameIndex)
    {
        int n = _samples.Length;
        int i = (int)(((frameIndex % n) + n) % n);
        return _samples[i];
    }

    private static PeriodicPinkNoise Build(int periodFrames, double sampleRateHz, double cutoffHz)
    {
        int n = periodFrames;
        int bins = (n / 2) + 1;
        double binHz = sampleRateHz / n;

        var real = new double[n];
        var imaginary = new double[n];

        double corner = Math.Max(cutoffHz, binHz);

        // Flat to the corner, then 1/sqrt(f) -- which is -3 dB per octave in power. Continuous at
        // the corner, so there is no step in the spectrum to show up as a kink in the curve.
        var magnitudes = new double[bins];
        for (int k = 1; k < bins; k++)
        {
            double freqHz = k * binHz;
            magnitudes[k] = freqHz <= corner ? 1.0 : Math.Sqrt(corner / freqHz);
        }

        for (int k = 1; k < bins; k++)
        {
            double magnitude = magnitudes[k];

            // Nyquist has no conjugate partner to carry a phase, so it stays real -- and is simply
            // left out, since a component there is half a sample's worth of nothing useful.
            if (k == n - k) continue;

            double phase = 2.0 * Math.PI * Uniform(Mix((ulong)k));
            real[k] = magnitude * Math.Cos(phase);
            imaginary[k] = magnitude * Math.Sin(phase);

            // Conjugate symmetry, which is what makes the inverse transform real.
            real[n - k] = real[k];
            imaginary[n - k] = -imaginary[k];
        }

        InverseTransform(real, imaginary);

        double sum = 0.0;
        double peak = 0.0;
        for (int i = 0; i < n; i++)
        {
            sum += real[i] * real[i];
            peak = Math.Max(peak, Math.Abs(real[i]));
        }

        double rms = Math.Sqrt(sum / n);
        if (!(rms > 0.0)) throw new InvalidOperationException("The periodic pink sequence came out silent.");

        for (int i = 0; i < n; i++)
            real[i] /= rms;

        return new PeriodicPinkNoise(real, 20.0 * Math.Log10(peak / rms));
    }

    /// <summary>
    /// In-place inverse DFT of a conjugate-symmetric spectrum, radix-2, leaving the real signal in
    /// <paramref name="real"/>.
    ///
    /// <para>Written out here rather than taken from <c>Tonebench.Dsp</c>, which this project
    /// deliberately does not reference — the same reason the chirp formula exists three times. It
    /// runs once per table rather than per record, so none of the backend work that file documents
    /// is worth importing for it.</para>
    /// </summary>
    private static void InverseTransform(double[] real, double[] imaginary)
    {
        int n = real.Length;
        if ((n & (n - 1)) != 0)
            throw new ArgumentException($"The period must be a power of two; got {n}.", nameof(real));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        // Cooley-Tukey butterflies, with +i in the twiddle: the inverse transform's sign.
        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = 2.0 * Math.PI / length;
            double wReal = Math.Cos(angle);
            double wImaginary = Math.Sin(angle);

            for (int i = 0; i < n; i += length)
            {
                double curReal = 1.0;
                double curImaginary = 0.0;

                for (int j = 0; j < length / 2; j++)
                {
                    int a = i + j;
                    int b = a + (length / 2);

                    double tReal = (real[b] * curReal) - (imaginary[b] * curImaginary);
                    double tImaginary = (real[b] * curImaginary) + (imaginary[b] * curReal);

                    real[b] = real[a] - tReal;
                    imaginary[b] = imaginary[a] - tImaginary;
                    real[a] += tReal;
                    imaginary[a] += tImaginary;

                    double nextReal = (curReal * wReal) - (curImaginary * wImaginary);
                    curImaginary = (curReal * wImaginary) + (curImaginary * wReal);
                    curReal = nextReal;
                }
            }
        }

        // The 1/N the inverse transform owes; the caller normalises to unit RMS afterwards anyway,
        // so this is for the sake of the arithmetic meaning what it says rather than for the result.
        for (int i = 0; i < n; i++)
        {
            real[i] /= n;
            imaginary[i] /= n;
        }
    }

    /// <summary>SplitMix64's finalising mix, as <see cref="PinkNoiseShaper.WhiteAt"/> uses it: distinct bins, unrelated phases.</summary>
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static double Uniform(ulong bits) => (bits >> 11) * (1.0 / 9007199254740992.0);
}
