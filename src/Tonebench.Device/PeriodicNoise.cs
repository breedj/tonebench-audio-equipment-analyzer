namespace Tonebench.Device;

/// <summary>
/// The spectral shape of a <see cref="PeriodicNoise"/> sequence — ARTA's own list, since the point
/// of these is that a measurement here is comparable with one there.
/// </summary>
public enum PeriodicNoiseShape
{
    /// <summary>
    /// Flat below <c>cutoffHz</c> and &#8722;3 dB per octave above it: equal power per
    /// constant-percentage band, which is what makes it read as a straight line under octave
    /// smoothing. ARTA's "PN Pink", and the default.
    /// </summary>
    Pink = 0,

    /// <summary>
    /// Every bin at the same amplitude. ARTA recommends it for electronic systems, where there is
    /// no reason to spend the stimulus on the bottom of the band: it puts the same energy on every
    /// bin the analysis reads, so the top of the band is measured as well as the bottom. The
    /// cut-off means nothing here, there being no slope for it to start.
    /// </summary>
    White = 1,

    /// <summary>
    /// The male speech spectrum of IEC 60268-16:2011, for speech-intelligibility work — ARTA's
    /// "Male PN[2011]". See <see cref="PeriodicNoise"/> for the table and where it comes from.
    /// </summary>
    SpeechMale2011 = 2,

    /// <summary>The female speech spectrum of the same edition — ARTA's "Female PN[2011]".</summary>
    SpeechFemale2011 = 3,

    /// <summary>
    /// The male spectrum as revised for IEC 60268-16:2020 — ARTA's "Male PN[2020]", which differs
    /// from the 2011 one only at 125 and 250 Hz.
    /// </summary>
    SpeechMale2020 = 4,
}

/// <summary>
/// Periodic noise: a random-phase multisine whose period is exactly the analysis record, with one
/// component on every FFT bin and a magnitude given by <see cref="PeriodicNoiseShape"/> — pink by
/// default, which is flat below a cut-off and falls at &#8722;3 dB per octave above it.
///
/// <para><b>The speech shapes are octave-band <i>levels</i> from IEC 60268-16</b>, which is what
/// that standard tabulates and what ARTA says its Speech PN follows. A band level is turned into
/// the per-bin density every other shape is stated in by dividing by the band's width, and an
/// octave band's width is proportional to its centre — so equal band levels at every octave *is*
/// pink, which is the arithmetic check that the two shapes agree. Between the seven centres the
/// density is interpolated linearly in log f; outside them each end's own slope is continued, and
/// the cut-off still holds the bottom flat, or 1/f noise would pile power into DC exactly as it
/// does for pink. Provenance, since none of it can be re-derived from the code: the 2011 male and
/// female rows are IEC 60268-16:2011 as reproduced in ODEON's STI application note (the female
/// 125 Hz value being ISO 9921's, which that standard leaves out), and the 2020 male row is the
/// 2011 one with the change adopted for Edition 5 — &#8722;6 dB at 125 Hz and &#8722;3 dB at
/// 250 Hz, "spectrum S4" of Steeneken and Mapp's 2016 paper for the IoA, which that revision took.
/// Only the <i>shape</i> is used, never the absolute levels, so the standard's normalisation to
/// 0 dBA drops out: the sequence is scaled to unit RMS here and to the asked-for level by its
/// caller.</para>
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
public sealed class PeriodicNoise
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
    private static readonly Dictionary<(int Period, long RateMilliHz, long CutoffMilliHz, PeriodicNoiseShape Shape), PeriodicNoise> Cache = [];

    /// <summary>
    /// Octave-band centres the speech tables below are stated at — IEC 60268-16 defines the speech
    /// spectrum over exactly these seven bands and no others.
    /// </summary>
    private static readonly double[] SpeechBandHz = [125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0];

    /// <summary>IEC 60268-16:2011, male, octave-band levels in dB relative to the A-weighted level.</summary>
    private static readonly double[] SpeechMale2011Db = [2.9, 2.9, -0.8, -6.8, -12.8, -18.8, -24.8];

    /// <summary>The same edition's female row; its 125 Hz value is ISO 9921's, the standard having none.</summary>
    private static readonly double[] SpeechFemale2011Db = [-4.4, 5.3, -1.9, -9.1, -15.8, -16.7, -18.0];

    /// <summary>The 2011 male row with Edition 5's change: &#8722;6 dB at 125 Hz, &#8722;3 dB at 250 Hz.</summary>
    private static readonly double[] SpeechMale2020Db = [-3.1, -0.1, -0.8, -6.8, -12.8, -18.8, -24.8];

    private static readonly Lock CacheLock = new();

    private readonly double[] _samples;

    private PeriodicNoise(double[] samples, double crestFactorDb)
    {
        _samples = samples;
        CrestFactorDb = crestFactorDb;
    }

    /// <summary>Peak over RMS of this sequence, in dB. Around 12 for a random-phase multisine.</summary>
    public double CrestFactorDb { get; }

    /// <summary>How many frames one period is; the sequence repeats exactly after this many.</summary>
    public int PeriodFrames => _samples.Length;

    /// <summary>
    /// The sequence for a period, rate, cut-off and shape, at unit RMS — a caller scales by the
    /// level it wants. Cached, so asking twice costs a dictionary lookup.
    /// </summary>
    public static PeriodicNoise For(
        int periodFrames, double sampleRateHz, double cutoffHz, PeriodicNoiseShape shape = PeriodicNoiseShape.Pink)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(periodFrames, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var key = (periodFrames, (long)Math.Round(sampleRateHz * 1000.0), (long)Math.Round(cutoffHz * 1000.0), shape);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var built = Build(periodFrames, sampleRateHz, cutoffHz, shape);
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

    private static PeriodicNoise Build(int periodFrames, double sampleRateHz, double cutoffHz, PeriodicNoiseShape shape)
    {
        int n = periodFrames;
        int bins = (n / 2) + 1;
        double binHz = sampleRateHz / n;

        var real = new double[n];
        var imaginary = new double[n];

        double corner = Math.Max(cutoffHz, binHz);

        var magnitudes = new double[bins];
        if (SpeechLevelsDb(shape) is { } speechDb)
        {
            FillSpeechMagnitudes(magnitudes, binHz, corner, speechDb);
        }
        else
        {
            for (int k = 1; k < bins; k++)
                magnitudes[k] = MagnitudeAt(shape, k * binHz, corner);
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
        ReduceCrestFactor(real, imaginary, magnitudes);

        double sum = 0.0;
        double peak = 0.0;
        for (int i = 0; i < n; i++)
        {
            sum += real[i] * real[i];
            peak = Math.Max(peak, Math.Abs(real[i]));
        }

        double rms = Math.Sqrt(sum / n);
        if (!(rms > 0.0)) throw new InvalidOperationException("The periodic noise sequence came out silent.");

        for (int i = 0; i < n; i++)
            real[i] /= rms;

        return new PeriodicNoise(real, 20.0 * Math.Log10(peak / rms));
    }

    /// <summary>
    /// One bin's amplitude, for the two shapes that are stated as a density. Relative, not absolute:
    /// the sequence is normalised to unit RMS afterwards, so only the ratios between bins mean
    /// anything here.
    /// </summary>
    private static double MagnitudeAt(PeriodicNoiseShape shape, double freqHz, double cornerHz) => shape switch
    {
        PeriodicNoiseShape.White => 1.0,

        // Flat to the corner, then 1/sqrt(f) -- which is -3 dB per octave in power. Continuous at
        // the corner, so there is no step in the spectrum to show up as a kink in the curve.
        PeriodicNoiseShape.Pink => freqHz <= cornerHz ? 1.0 : Math.Sqrt(cornerHz / freqHz),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not a shape stated as a density."),
    };

    /// <summary>That shape's octave-band table, or null where the shape is stated as a density.</summary>
    private static double[]? SpeechLevelsDb(PeriodicNoiseShape shape) => shape switch
    {
        PeriodicNoiseShape.SpeechMale2011 => SpeechMale2011Db,
        PeriodicNoiseShape.SpeechFemale2011 => SpeechFemale2011Db,
        PeriodicNoiseShape.SpeechMale2020 => SpeechMale2020Db,
        _ => null,
    };

    /// <summary>
    /// Fills the per-bin amplitudes of a speech shape, whose table is <b>octave-band levels</b>
    /// rather than a density.
    ///
    /// <para>Between the seven centres the density is a straight line in log f, and outside them
    /// each end's own slope is continued. But a smooth density drawn through a table's centre points
    /// does <i>not</i> integrate to that table's band levels: a band whose density falls away on
    /// both sides holds less than its centre suggests, and that is not a rounding error &#8212; the
    /// female row peaks at 250 Hz between a 9.7 dB rise and a 7.2 dB fall, and reads <b>2.4 dB</b>
    /// low. So the centre densities are solved for rather than assumed, by measuring each band and
    /// pushing its own point by the error it showed; a few passes converge, neighbouring bands
    /// barely interacting. What comes out is smooth <i>and</i> carries the standard's levels, where
    /// a piecewise-constant density gives the levels with 9 dB steps between bands and a plain
    /// interpolation gives neither.</para>
    /// </summary>
    private static void FillSpeechMagnitudes(double[] magnitudes, double binHz, double cornerHz, double[] levelsDb)
    {
        int bands = SpeechBandHz.Length;

        // The starting guess: each band's level spread over the band's own width, an octave band's
        // width being proportional to its centre.
        var densityDb = new double[bands];
        for (int b = 0; b < bands; b++)
            densityDb[b] = levelsDb[b] - (10.0 * Math.Log10(SpeechBandHz[b]));

        // 1 kHz is held still and the rest move around it: only the shape is used here, the level
        // being set by the unit-RMS normalisation below and then by whoever plays the sequence.
        const int anchor = 3;

        for (int pass = 0; pass < 8; pass++)
        {
            Fill(magnitudes, binHz, cornerHz, densityDb);

            double anchorLevel = BandLevelDb(magnitudes, binHz, anchor);
            if (double.IsNegativeInfinity(anchorLevel)) return;

            double offset = anchorLevel - levelsDb[anchor];
            double worst = 0.0;

            for (int b = 0; b < bands; b++)
            {
                double measured = BandLevelDb(magnitudes, binHz, b);

                // A band with no bin of its own at this period has nothing to measure and nothing
                // to correct; its density still comes from the line through its neighbours.
                if (double.IsNegativeInfinity(measured)) continue;

                double error = measured - offset - levelsDb[b];
                densityDb[b] -= error;
                worst = Math.Max(worst, Math.Abs(error));
            }

            if (worst < 0.001) break;
        }

        Fill(magnitudes, binHz, cornerHz, densityDb);
    }

    private static void Fill(double[] magnitudes, double binHz, double cornerHz, double[] densityDb)
    {
        for (int k = 1; k < magnitudes.Length; k++)
        {
            // Held flat below the corner for pink's reason rather than for the standard's: a speech
            // spectrum rises towards 250 Hz and the extrapolation below it goes on rising, which is
            // power piled into DC.
            double freqHz = Math.Max(k * binHz, cornerHz);
            magnitudes[k] = Math.Pow(10.0, InterpolateDb(densityDb, freqHz) / 20.0);
        }
    }

    /// <summary>A straight line in log f through the centre densities, continued past either end.</summary>
    private static double InterpolateDb(double[] densityDb, double freqHz)
    {
        int last = SpeechBandHz.Length - 1;
        int lower = 0;
        while (lower < last - 1 && SpeechBandHz[lower + 1] < freqHz) lower++;

        // The centres are exactly an octave apart, so the position between them is a log2 distance.
        return densityDb[lower] + (Math.Log2(freqHz / SpeechBandHz[lower]) * (densityDb[lower + 1] - densityDb[lower]));
    }

    /// <summary>One octave band's level, in dB, or negative infinity where it holds no bin at all.</summary>
    private static double BandLevelDb(double[] magnitudes, double binHz, int band)
    {
        double centreHz = SpeechBandHz[band];
        int first = Math.Max(1, (int)Math.Ceiling(centreHz / Math.Sqrt(2.0) / binHz));
        int last = Math.Min(magnitudes.Length - 1, (int)Math.Floor(centreHz * Math.Sqrt(2.0) / binHz));

        double power = 0.0;
        for (int k = first; k <= last; k++)
            power += magnitudes[k] * magnitudes[k];

        return 10.0 * Math.Log10(power);
    }

    /// <summary>
    /// The crest factor the sequence is pushed towards, in dB, and how many passes it gets.
    ///
    /// <para><b>Why this exists.</b> ARTA's manual says both things in one paragraph: a random-phase
    /// multisine has a crest factor of 12&#8211;13 dB, <i>and</i> "in ARTA, the periodic noise
    /// generation is implemented with a crest factor lower than 10 dB". So ARTA optimises the phases
    /// and we were matching its definition rather than its implementation &#8212; which costs about
    /// 2.5 dB of drive at the same peak, and the peak is what the output range is set from.</para>
    ///
    /// <para><b>What it buys is headroom rather than a different answer.</b> An impedance is a ratio
    /// of two channels from one record, so the stimulus cancels out of it: measured on a ~1 ohm
    /// fixture the median read 1.023&#8211;1.028 ohm from &#8722;20 dBV all the way to +18, flat
    /// across 38 dB of drive. What more power does is lift every bin further above the analyzer's
    /// own floor, and noise on H does <i>not</i> average out of <c>R*H/(1-H)</c> &#8212; it inflates
    /// it, which is why the same fixture read 1.074 ohm at &#8722;30 dBV. The other half is that
    /// clipping starts at <c>21.01 - crest</c> dBV, so taking the crest from 12.5 dB to under 10
    /// moves the onset from about 8.5 dBV to about 11 and roughly doubles the room above the
    /// 6 dBV default.</para>
    ///
    /// <para><b>The method is iterative clipping with the spectrum restored each pass</b> (Van der
    /// Ouderaa and Schoukens): clip the waveform to the target crest, transform, put the asked-for
    /// magnitudes back while keeping the clipped signal's phases, transform back. The restoration is
    /// what makes this safe here &#8212; the spectrum that comes out is <i>exactly</i> the shape that
    /// went in, to rounding, so a pink sequence is still pink and a speech one still carries its own
    /// octave-band levels. Only the phases move, and they stay disordered: this is a phase-optimised
    /// noise, not a swept signal like a Schroeder-phase multisine, which is the distinction ARTA's
    /// own text draws.</para>
    ///
    /// <para>A pass costs two transforms, which is why the count is modest and the table is cached.
    /// The last pass restores the spectrum and does not clip again, so the result rebounds slightly
    /// above the target &#8212; the figure that matters is the measured <see cref="CrestFactorDb"/>,
    /// not this constant.</para>
    /// </summary>
    private const double TargetCrestFactorDb = 8.5;

    private const int CrestFactorPasses = 16;

    /// <summary>
    /// Pushes the waveform's peak down towards <see cref="TargetCrestFactorDb"/> without moving its
    /// spectrum. In place; <paramref name="imaginary"/> is scratch, and
    /// <paramref name="magnitudes"/> is the shape to restore each pass.
    /// </summary>
    private static void ReduceCrestFactor(double[] real, double[] imaginary, double[] magnitudes)
    {
        int n = real.Length;
        double target = Math.Pow(10.0, TargetCrestFactorDb / 20.0);

        for (int pass = 0; pass < CrestFactorPasses; pass++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
                sum += real[i] * real[i];

            double rms = Math.Sqrt(sum / n);
            if (!(rms > 0.0)) return;

            double limit = target * rms;
            bool clipped = false;
            for (int i = 0; i < n; i++)
            {
                if (real[i] > limit)
                {
                    real[i] = limit;
                    clipped = true;
                }
                else if (real[i] < -limit)
                {
                    real[i] = -limit;
                    clipped = true;
                }
            }

            // Already inside the target: nothing to trade, and another pass would only move phases.
            if (!clipped) return;

            Array.Clear(imaginary);
            ForwardTransform(real, imaginary);

            // Keep the clipped waveform's phases, put the asked-for magnitudes back. Scaling by
            // magnitude/|X| rather than taking an angle: same thing, two multiplies instead of a
            // trig pair, and a bin the clipping emptied keeps whatever phase it had rather than
            // becoming a NaN.
            real[0] = 0.0;
            imaginary[0] = 0.0;

            for (int k = 1; k < n - k; k++)
            {
                double length = Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
                double scale = length > 0.0 ? magnitudes[k] / length : 0.0;

                real[k] *= scale;
                imaginary[k] *= scale;

                // Conjugate symmetry, which is what makes the inverse transform real again.
                real[n - k] = real[k];
                imaginary[n - k] = -imaginary[k];
            }

            // Nyquist carries no component, exactly as it does in the sequence this started from.
            if (n % 2 == 0)
            {
                real[n / 2] = 0.0;
                imaginary[n / 2] = 0.0;
            }

            InverseTransform(real, imaginary);
        }
    }

    /// <summary>
    /// In-place forward DFT, radix-2, of the real signal in <paramref name="real"/> with
    /// <paramref name="imaginary"/> zeroed. Shares <see cref="Transform"/> with the inverse, so the
    /// two cannot disagree about anything but the sign of the twiddle and the 1/N.
    /// </summary>
    private static void ForwardTransform(double[] real, double[] imaginary) =>
        Transform(real, imaginary, inverse: false);

    /// <summary>
    /// In-place inverse DFT of a conjugate-symmetric spectrum, radix-2, leaving the real signal in
    /// <paramref name="real"/>.
    ///
    /// <para>Written out here rather than taken from <c>Tonebench.Dsp</c>, which this project
    /// deliberately does not reference — the same reason the chirp formula exists three times. It
    /// runs once per table rather than per record, so none of the backend work that file documents
    /// is worth importing for it.</para>
    /// </summary>
    private static void InverseTransform(double[] real, double[] imaginary) =>
        Transform(real, imaginary, inverse: true);

    private static void Transform(double[] real, double[] imaginary, bool inverse)
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

        // Cooley-Tukey butterflies. The only thing that separates the two directions is the sign
        // of the twiddle here and the 1/N at the end.
        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = (inverse ? 2.0 : -2.0) * Math.PI / length;
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

        // The 1/N the inverse transform owes. It matters here rather than only being tidy: the
        // crest-factor passes go forward and back repeatedly, so a scaling that did not cancel would
        // compound. The forward direction owes nothing.
        if (!inverse) return;

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
