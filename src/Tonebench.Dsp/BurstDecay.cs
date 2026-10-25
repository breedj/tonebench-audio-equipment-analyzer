namespace Tonebench.Dsp;

/// <summary>
/// Which way to spend the time-frequency trade a constant-Q analysis cannot escape: a filter
/// narrow enough to separate two close resonances rings for longer than either of them.
/// </summary>
public enum BurstDecayResolution
{
    /// <summary>
    /// A wavelet two periods wide (Q ≈ 2.3, about two thirds of an octave). Follows a fast decay
    /// honestly and merges resonances that sit close together.
    /// </summary>
    PreferTime = 0,

    /// <summary>
    /// Four periods wide (Q ≈ 4.5, about a third of an octave — the resolution a filter-bank
    /// measurement would give). Separates close resonances, and smears the first few periods of
    /// every decay because the analysis itself lasts that long.
    /// </summary>
    PreferFrequency = 1,

    /// <summary>
    /// 7.63 periods wide, which is <b>a sixth of an octave</b> exactly — the narrowest rung, and the
    /// one ARTA calls "prefer frequency resolution".
    ///
    /// <para><b>Why it exists.</b> ARTA offers 1/3 and 1/6 octave; this app offered 2/3 and 1/3, so
    /// every setting here was one rung wider than its namesake there and the narrowest analysis ARTA
    /// can do had no counterpart at all. Reported 2026-10-25 as ARTA showing more high-frequency
    /// detail on the same measurement, which is exactly what a filter half as wide resolves. The two
    /// older names predate the ladder and are kept as they are, since they are what stored
    /// measurements carry; the dropdown labels all three by bandwidth, which is the thing the two
    /// programs can actually be matched on.</para>
    ///
    /// <para>The width is set from the bandwidth rather than rounded to a whole number of periods,
    /// unlike the two above — there is no stored measurement to keep looking the same.</para>
    /// </summary>
    SixthOctave = 2,
}

/// <summary>How to read a burst decay off an impulse response.</summary>
/// <param name="MaxPeriods">How deep the surface goes, counted in periods of each frequency.</param>
/// <param name="PointsPerPeriod">Slices per period. Three is enough to see a decay; more is mostly more curves.</param>
/// <param name="FrequencyColumns">Analysis frequencies, log-spaced across the band. Each one is its own convolution.</param>
public sealed record BurstDecaySettings(
    double LowHz,
    double HighHz,
    int MaxPeriods,
    int PointsPerPeriod,
    BurstDecayResolution Resolution,
    int FrequencyColumns)
{
    public const int MaxSupportedPeriods = 100;

    public const int MaxPointsPerPeriod = 8;

    public const int MaxFrequencyColumns = 400;

    public void Validate()
    {
        if (double.IsNaN(LowHz) || LowHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(LowHz), LowHz, "The band's bottom must be above zero.");

        if (double.IsNaN(HighHz) || HighHz <= LowHz)
            throw new ArgumentOutOfRangeException(nameof(HighHz), HighHz, "The band's top must be above its bottom.");

        if (MaxPeriods is < 2 or > MaxSupportedPeriods)
            throw new ArgumentOutOfRangeException(nameof(MaxPeriods), MaxPeriods, $"Between 2 and {MaxSupportedPeriods} periods.");

        if (PointsPerPeriod is < 1 or > MaxPointsPerPeriod)
            throw new ArgumentOutOfRangeException(nameof(PointsPerPeriod), PointsPerPeriod, $"Between 1 and {MaxPointsPerPeriod} points per period.");

        if (FrequencyColumns is < 8 or > MaxFrequencyColumns)
            throw new ArgumentOutOfRangeException(nameof(FrequencyColumns), FrequencyColumns, $"Between 8 and {MaxFrequencyColumns} frequencies.");
    }
}

/// <summary>
/// Burst decay: how long each frequency rings, measured in <b>periods of that frequency</b> rather
/// than in milliseconds.
///
/// <para><b>Why the axis is periods, and why that is not a presentation choice.</b> A 40 Hz
/// resonance and a 4 kHz one that both ring for ten cycles are the same defect, and on a
/// millisecond axis one of them is 250 ms long and the other 2.5 ms. A cumulative spectral decay
/// draws exactly that picture: a common time axis, on which every low-frequency ridge looks
/// alarming and every high-frequency one looks clean, whatever the DUT is doing. Rescaling the axis
/// per frequency is what makes the two comparable, and it is the entire difference between the two
/// measurements.</para>
///
/// <para><b>Constant-Q, by complex wavelet.</b> Each analysis frequency gets a Gaussian-windowed
/// complex exponential whose length is a fixed number of <i>periods</i> — so the filter's bandwidth
/// is proportional to its centre frequency, exactly as the axis is. Convolving it with the impulse
/// response gives the analytic signal in that band, and its magnitude is the envelope whose decay
/// is the answer. <see cref="BurstDecayResolution"/> is the wavelet's width, and there is no
/// setting that avoids the trade.</para>
///
/// <para><b>Each frequency is normalised to its own maximum</b>, which deletes the DUT's frequency
/// response from the surface on purpose: a burst decay answers "how long", and leaving "how loud"
/// in would let a bass hump bury the ringing three octaves up. That is what makes it unreadable as
/// a frequency response and readable as a decay — and it is the opposite of what
/// <see cref="CumulativeSpectralDecay"/> does.</para>
///
/// <para><b>Reading before the impulse is fine; reading past the record is not.</b> The wavelet is
/// symmetric, so even the first slice reaches back several periods — into the stretch the sweep gate
/// zeroed. That is the correct thing to read there: a causal DUT has nothing before its impulse,
/// and what the gate deleted is harmonic-distortion packets rather than response. Past the gate's
/// far edge is the opposite case — the zeros there are the gate, and reading them would draw a
/// perfect decay at exactly the frequencies (the lowest, which need the most milliseconds per
/// period) least likely to have one. Those slices are reported as absent instead, through
/// <see cref="DecaySlice.FirstValidColumn"/>.</para>
/// </summary>
public static class BurstDecay
{
    /// <summary>Levels below this are floored, so an all-zero band does not serialise as -Infinity.</summary>
    public const double FloorDb = -300.0;

    /// <summary>
    /// The wavelet's width, as the full width at half maximum of its Gaussian envelope, in periods.
    ///
    /// <para>Its bandwidth follows exactly: a Gaussian envelope whose time FWHM is <c>p</c> periods
    /// transforms to one whose frequency FWHM is <c>0.8825 f / p</c> — which is ARTA's own
    /// <c>Δf/f₀ = 2.3548/(ω₀σ)</c> written out, the same wavelet by the same definition. So the
    /// ladder in octaves is <c>p = 0.8825 / (2^(n/2) − 2^(−n/2))</c>: 1.89 periods for two thirds of
    /// an octave, 3.81 for a third, 7.63 for a sixth. The first two are rounded to 2 and 4, which is
    /// within 5 % and is what every stored measurement was taken with.</para>
    /// </summary>
    public static double WaveletPeriods(BurstDecayResolution resolution) => resolution switch
    {
        BurstDecayResolution.SixthOctave => SixthOctavePeriods,
        BurstDecayResolution.PreferFrequency => 4.0,
        _ => 2.0,
    };

    /// <summary>
    /// 0.8825 / (2^(1/12) − 2^(−1/12)) — the envelope width whose bandwidth is a sixth of an octave.
    /// </summary>
    private const double SixthOctavePeriods = 7.6334;

    /// <summary>Gaussian FWHM in standard deviations — <c>2 sqrt(2 ln 2)</c>.</summary>
    private const double FwhmSigmas = 2.3548200450309493;

    /// <summary>
    /// Where the wavelet is truncated, in standard deviations. Three leaves the envelope at 1.1%,
    /// and the pedestal subtraction below takes that last 1.1% to zero rather than to a step — a
    /// Gaussian cut off while it is still non-zero is a Gaussian plus a rectangle, and it is the
    /// rectangle's edges, not the Gaussian's own skirts, that would set the filter's sidelobes.
    ///
    /// <para>Widening this is not the way to separate a quiet resonance from a loud one nearby, and
    /// four sigma was tried: a 4 kHz ring 40 dB under a 1 kHz one two octaves away is not lost to
    /// leakage but to the louder ring's <i>own</i> content in the 4 kHz band, which a second-order
    /// resonance really has. Nothing about the window changes that; a lower-Q neighbour or a wider
    /// spacing does.</para>
    /// </summary>
    private const double SupportSigmas = 3.0;

    /// <summary>
    /// Computes the surface. <paramref name="peakIndex"/> is the linear impulse and becomes period
    /// zero; <paramref name="ir"/> is read circularly.
    /// </summary>
    /// <param name="availablePostSamples">
    /// How far past the impulse the record is real rather than gate. Frequencies whose wavelet would
    /// reach past it are reported absent for the slices concerned rather than answered with zeros.
    /// </param>
    public static DecaySurface Compute(
        ReadOnlySpan<double> ir,
        int peakIndex,
        double sampleRateHz,
        int availablePostSamples,
        BurstDecaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ir.Length, nameof(ir));
        settings.Validate();

        double lowHz = settings.LowHz;
        double highHz = Math.Min(settings.HighHz, sampleRateHz / 2.0);
        if (highHz <= lowHz)
            throw new ArgumentOutOfRangeException(nameof(settings), "The band's top must be above its bottom, and below Nyquist.");

        var freqHz = LogFrequencyGrid.Columns(lowHz, highHz, settings.FrequencyColumns);
        int sliceCount = (settings.MaxPeriods * settings.PointsPerPeriod) + 1;
        double waveletPeriods = WaveletPeriods(settings.Resolution);

        // How deep each frequency can answer for, filled in below. The wavelet's half-support is the
        // *same number of periods* at every frequency (SupportSigmas x waveletPeriods / FwhmSigmas),
        // so that depth is monotonically increasing in frequency — which is what lets a slice be
        // described by one FirstValidColumn rather than a mask.
        //
        // Column-major while it is computed (one convolution fills one frequency's whole decay),
        // transposed into slices at the end.
        var columns = new float[settings.FrequencyColumns][];
        var availablePeriods = new double[settings.FrequencyColumns];

        // Each frequency is an independent convolution over the same read-only record, and the low
        // ones are expensive — a two-period wavelet at 40 Hz and 192 kHz is 24 000 taps. Nothing
        // here is shared or ordered, so it parallelises exactly.
        var source = ir.ToArray();
        Parallel.For(0, settings.FrequencyColumns, c =>
        {
            double f = freqHz[c];
            double samplesPerPeriod = sampleRateHz / f;
            double sigmaSamples = waveletPeriods * samplesPerPeriod / FwhmSigmas;
            int half = Math.Max(1, (int)Math.Ceiling(SupportSigmas * sigmaSamples));

            availablePeriods[c] = (availablePostSamples - half) / samplesPerPeriod;

            int taps = (2 * half) + 1;
            var cosine = new double[taps];
            var sine = new double[taps];
            // The pedestal, subtracted so the wavelet reaches its own ends at zero rather than at a
            // step. See SupportSigmas.
            double pedestal = Math.Exp(-0.5 * half * half / (sigmaSamples * sigmaSamples));

            for (int i = -half; i <= half; i++)
            {
                double t = i / sigmaSamples;
                double w = Math.Exp(-0.5 * t * t) - pedestal;
                double phase = 2.0 * Math.PI * f * i / sampleRateHz;
                cosine[i + half] = w * Math.Cos(phase);
                sine[i + half] = w * Math.Sin(phase);
            }

            var magnitudes = new float[sliceCount];
            for (int s = 0; s < sliceCount; s++)
            {
                double periods = (double)s / settings.PointsPerPeriod;
                int centre = peakIndex + (int)Math.Round(periods * samplesPerPeriod);

                double re = 0.0;
                double im = 0.0;
                for (int i = 0; i < taps; i++)
                {
                    int index = centre - half + i;
                    double x = source[(((index % source.Length) + source.Length) % source.Length)];
                    re += x * cosine[i];
                    im -= x * sine[i];
                }

                magnitudes[s] = (float)Math.Sqrt((re * re) + (im * im));
            }

            columns[c] = magnitudes;
        });

        // Per frequency, and over the whole depth rather than against the first slice: a resonance
        // can peak a period or two in, and a reference taken at period zero would then draw it
        // rising above 0 dB.
        var reference = new double[settings.FrequencyColumns];
        for (int c = 0; c < settings.FrequencyColumns; c++)
        {
            double peak = 0.0;
            foreach (float m in columns[c])
                peak = Math.Max(peak, m);
            reference[c] = Math.Max(peak, 1e-30);
        }

        var slices = new List<DecaySlice>(sliceCount);
        for (int s = 0; s < sliceCount; s++)
        {
            double periods = (double)s / settings.PointsPerPeriod;

            int first = 0;
            while (first < settings.FrequencyColumns && availablePeriods[first] < periods)
                first++;

            var levels = new float[settings.FrequencyColumns - first];
            for (int c = first; c < settings.FrequencyColumns; c++)
            {
                levels[c - first] = (float)Math.Max(
                    20.0 * Math.Log10(Math.Max(columns[c][s], 1e-30) / reference[c]), FloorDb);
            }

            slices.Add(new DecaySlice(periods, first, levels));
        }

        // No single number: the analysis resolution is proportional to frequency by construction,
        // which is the point of it.
        return new DecaySurface(DecayAxis.Periods, freqHz, slices, 0.0);
    }
}
