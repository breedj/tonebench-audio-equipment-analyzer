namespace Tonebench.Dsp;

/// <summary>What a display-path filter does to the trace.</summary>
public enum ScopeFilterKind
{
    /// <summary>Nothing. The trace is the record.</summary>
    None,

    /// <summary>Keeps what is below the corner — a bandwidth limit, the scope's "20 MHz" button.</summary>
    LowPass,

    /// <summary>Keeps what is above it. For a drift or a rumble the coupling's mean removal cannot reach.</summary>
    HighPass,

    /// <summary>Keeps a band around the centre, at the given Q.</summary>
    BandPass,

    /// <summary>Removes one. What mains hum is taken out with.</summary>
    Notch,
}

/// <summary>
/// One display-path filter: what it does, where, and how sharply.
/// </summary>
/// <param name="Kind">What it does.</param>
/// <param name="FrequencyHz">The corner, or the centre for a band-pass and a notch.</param>
/// <param name="Q">
/// How narrow the band is, for <see cref="ScopeFilterKind.BandPass"/> and
/// <see cref="ScopeFilterKind.Notch"/>. Ignored by the other two, whose shape is
/// <paramref name="Order"/>'s.
/// </param>
/// <param name="Order">
/// The Butterworth order of a low- or high-pass, before the zero-phase pass doubles it. Even, 2 to
/// 8; ignored by the other two.
/// </param>
public readonly record struct ScopeFilterSpec(
    ScopeFilterKind Kind,
    double FrequencyHz,
    double Q = 0.707,
    int Order = 2)
{
    public static ScopeFilterSpec Off { get; } = new(ScopeFilterKind.None, 0.0);

    /// <summary>Whether this spec would change the trace at all.</summary>
    public bool Filters => Kind != ScopeFilterKind.None && FrequencyHz > 0.0;
}

/// <summary>
/// The oscilloscope's in-path filters: a low-pass, a high-pass, a band-pass and a notch, applied to
/// the record before anything is drawn or measured.
///
/// <para><b>Zero-phase, and that is the decision the rest follows from.</b> An ordinary causal
/// filter delays what it passes, and on this screen the x axis is time <em>relative to the
/// trigger</em> — so a low-pass would slide the waveform off the very edge it fired on, and a rise
/// time measured from there would be of an edge that is no longer where it happened. Running the
/// filter forward and then backward over the record cancels the delay exactly. Two things follow
/// and are not hidden: the magnitude is applied <b>twice</b>, so the response is |H|² and a
/// second-order corner behaves like a fourth-order one; and it is only possible at all because the
/// record is finite and already captured, which a live filter on a stream could not be.</para>
///
/// <para><b>Reflected at both ends before it is run.</b> A record is a window cut out of a
/// continuous stream: the signal has no edges, only the array does. Filtering the bare array makes
/// the filter start from rest and ring at both ends, which draws as a transient the DUT never
/// produced — at the very start of the record, which is where a pre-trigger view is read.</para>
/// </summary>
public static class WaveformFilters
{
    /// <summary>The lowest and highest order offered, and the step between them. Even, because a section is second-order.</summary>
    public const int MinOrder = 2;
    public const int MaxOrder = 8;

    /// <summary>The bounds on a band-pass or notch Q. Below 0.1 it is barely a band; above 50 it rings for longer than most records.</summary>
    public const double MinQ = 0.1;
    public const double MaxQ = 50.0;

    /// <summary>
    /// How much of the record is reflected onto each end before filtering, as a multiple of the
    /// filter's own settling length. Four, which puts the array's own edge far enough outside the
    /// window that whatever the filter does there has decayed before it reaches a real sample.
    /// </summary>
    public const int PadPeriods = 4;

    /// <summary>At most this many samples of padding, so a very low corner cannot double the work on a long record.</summary>
    public const int MaxPadSamples = 1 << 16;

    /// <summary>
    /// How far up the band a response curve is drawn, as a fraction of the sample rate.
    ///
    /// <para>0.45, which is where this analyzer's own analysis band stops: past it the filter's
    /// shape is a property of the sampling rather than of the setting, and drawing it would invite
    /// reading a corner off the part of the curve the bilinear mapping compressed.</para>
    /// </summary>
    public const double ResponseTopFraction = 0.45;

    /// <summary>
    /// Filters one channel into <paramref name="destination"/>, which may be the same span as
    /// <paramref name="source"/>.
    ///
    /// <para>A spec that does nothing copies, rather than returning early with a different array:
    /// every caller wants one array to go on with either way.</para>
    /// </summary>
    public static void Apply(
        ScopeFilterSpec spec,
        ReadOnlySpan<double> source,
        double sampleRateHz,
        Span<double> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, source.Length);

        if (!spec.Filters || source.Length < 4 || !(sampleRateHz > 0.0))
        {
            source.CopyTo(destination);
            return;
        }

        var sections = Sections(spec, sampleRateHz);
        if (sections.Length == 0)
        {
            source.CopyTo(destination);
            return;
        }

        int pad = PadLength(spec, source.Length, sampleRateHz);
        int total = source.Length + (2 * pad);
        var work = new double[total];

        // Mirrored, and MIRRORED rather than reflected through the end samples -- which is the
        // one choice here that is easy to get wrong, and both alternatives were measured.
        //
        // The textbook padding for a forward-backward filter is the odd reflection,
        // 2*x[0] - x[k], because it continues the signal's own slope and so joins the record with
        // no kink. That is exact where the end sample is a zero crossing and wrong everywhere
        // else: it centres the padding on 2*x[end], so a record ending away from its mean has an
        // offset of twice that difference injected just outside it, which a low-pass then
        // faithfully tracks. Measured on an 8 kHz tone ending at -0.866 through a 1 kHz
        // fourth-order low-pass: the middle of the record read 2.4e-8 as it should and the last
        // sample read -0.866 -- the whole of the input, through a filter 151 dB down at that
        // frequency.
        //
        // A mirror is continuous in value and injects no offset at all, because the padding is
        // the record's own samples reversed: the same amplitude, the same spectrum, the same mean.
        // What it leaves is a cusp rather than a kink, and the residue that puts on the record's
        // own edge measured 0.075 and 0.037 on that same tone -- an order of magnitude less, and
        // bounded by the signal rather than by twice it.
        //
        // The trade is real and goes the other way on content the filter PASSES, where the odd
        // reflection's slope extrapolation is excellent and the mirror's cusp is not: a 200 Hz
        // tone through the same low-pass came back within 0.0008 of its analytic value under odd
        // padding and within 0.133 under a mirror. So neither wins outright, and the mirror is
        // chosen on the worst case rather than the best -- 0.13 against 0.87 of full scale, which
        // is the right way round for an instrument.
        //
        // No padding is right in general, because the filtered trace near the record's ends
        // depends on an assumption about what lies outside it, and the record is a window of a
        // stream that has since moved on. What would remove it rather than bound it is filtering
        // in context -- keeping a margin of real samples either side of the record when it is
        // taken -- which is a change to the capture path rather than to this. The manual says the
        // first and last few cycles of the corner are the filter's own assumption.
        for (int i = 0; i < pad; i++)
        {
            work[pad - 1 - i] = source[Math.Min(i + 1, source.Length - 1)];
            work[pad + source.Length + i] = source[Math.Max(source.Length - 2 - i, 0)];
        }

        source.CopyTo(work.AsSpan(pad));

        foreach (var section in sections)
        {
            Forward(section, work);
            Backward(section, work);
        }

        work.AsSpan(pad, source.Length).CopyTo(destination);
    }

    /// <summary>
    /// The filter's magnitude at one frequency, in dB — <b>the response the trace really gets</b>,
    /// which is the squared one because every section runs twice.
    ///
    /// <para>This is what the display rail's preview draws, and it is asked of the analyzer rather
    /// than recomputed on the client for the reason every reading on this screen is: two
    /// implementations of one filter drift, and a preview that is not the filter is worse than no
    /// preview.</para>
    /// </summary>
    public static double ResponseDb(ScopeFilterSpec spec, double freqHz, double sampleRateHz)
    {
        if (!spec.Filters || !(sampleRateHz > 0.0))
            return 0.0;

        var sections = Sections(spec, sampleRateHz);
        if (sections.Length == 0)
            return 0.0;

        double omega = 2.0 * Math.PI * freqHz / sampleRateHz;
        var (sin1, cos1) = Math.SinCos(omega);
        var (sin2, cos2) = Math.SinCos(2.0 * omega);

        double magnitudeSquared = 1.0;
        foreach (var s in sections)
        {
            double numReal = s.B0 + (s.B1 * cos1) + (s.B2 * cos2);
            double numImag = -((s.B1 * sin1) + (s.B2 * sin2));
            double denReal = 1.0 + (s.A1 * cos1) + (s.A2 * cos2);
            double denImag = -((s.A1 * sin1) + (s.A2 * sin2));

            double den = (denReal * denReal) + (denImag * denImag);
            if (den <= 0.0)
                return double.NegativeInfinity;

            magnitudeSquared *= ((numReal * numReal) + (numImag * numImag)) / den;
        }

        // Twice, because forward and backward each apply it. 10*log10 of the squared magnitude is
        // 20*log10 of the magnitude, so a second pass is another 10*log10 of the same quantity.
        double linearSquared = magnitudeSquared * magnitudeSquared;
        return linearSquared <= 0.0 ? -300.0 : Math.Max(-300.0, 10.0 * Math.Log10(linearSquared));
    }

    /// <summary>
    /// How long the padding is: four settling lengths of the filter, bounded.
    ///
    /// <para>A low corner settles slowly, so the padding is derived from the <i>corner</i> rather
    /// than fixed — a 10 Hz high-pass on a 48 kS/s record settles over thousands of samples and a
    /// few hundred of padding would leave the ring inside the window.</para>
    /// </summary>
    private static int PadLength(ScopeFilterSpec spec, int length, double sampleRateHz)
    {
        double periods = PadPeriods;
        if (spec.Kind is ScopeFilterKind.BandPass or ScopeFilterKind.Notch)
            periods *= Math.Clamp(spec.Q, MinQ, MaxQ);

        double samples = periods * sampleRateHz / Math.Max(spec.FrequencyHz, 1e-6);
        return (int)Math.Clamp(Math.Ceiling(samples), 1, Math.Min(MaxPadSamples, Math.Max(1, length)));
    }

    private readonly record struct Section(double B0, double B1, double B2, double A1, double A2);

    private static void Forward(Section s, Span<double> x)
    {
        double z1 = 0.0, z2 = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double input = x[i];
            double output = (s.B0 * input) + z1;
            z1 = (s.B1 * input) - (s.A1 * output) + z2;
            z2 = (s.B2 * input) - (s.A2 * output);
            x[i] = output;
        }
    }

    private static void Backward(Section s, Span<double> x)
    {
        double z1 = 0.0, z2 = 0.0;
        for (int i = x.Length - 1; i >= 0; i--)
        {
            double input = x[i];
            double output = (s.B0 * input) + z1;
            z1 = (s.B1 * input) - (s.A1 * output) + z2;
            z2 = (s.B2 * input) - (s.A2 * output);
            x[i] = output;
        }
    }

    /// <summary>
    /// The biquad sections a spec comes to — the Audio EQ Cookbook forms, which is what every
    /// other second-order section in this project is built from.
    ///
    /// <para>A low- or high-pass of order <c>n</c> is <c>n/2</c> sections whose Q values are the
    /// Butterworth pole Qs, <c>1 / (2 cos((2k+1)π / 2n))</c> — <b>not</b> <c>n/2</c> identical
    /// sections at 0.707, which is a different filter: cascading two of those gives −6 dB at the
    /// corner and a rounded knee where a fourth-order Butterworth gives −3 dB and a flat one.</para>
    /// </summary>
    private static Section[] Sections(ScopeFilterSpec spec, double sampleRateHz)
    {
        double nyquist = 0.5 * sampleRateHz;
        double frequency = spec.FrequencyHz;
        if (!(frequency > 0.0) || frequency >= nyquist)
            return [];

        double w0 = 2.0 * Math.PI * frequency / sampleRateHz;
        var (sinW, cosW) = Math.SinCos(w0);

        switch (spec.Kind)
        {
            case ScopeFilterKind.BandPass:
            {
                double alpha = sinW / (2.0 * Math.Clamp(spec.Q, MinQ, MaxQ));
                double a0 = 1.0 + alpha;
                return [new Section(alpha / a0, 0.0, -alpha / a0, (-2.0 * cosW) / a0, (1.0 - alpha) / a0)];
            }

            case ScopeFilterKind.Notch:
            {
                double alpha = sinW / (2.0 * Math.Clamp(spec.Q, MinQ, MaxQ));
                double a0 = 1.0 + alpha;
                return
                [
                    new Section(1.0 / a0, (-2.0 * cosW) / a0, 1.0 / a0, (-2.0 * cosW) / a0, (1.0 - alpha) / a0),
                ];
            }

            case ScopeFilterKind.LowPass:
            case ScopeFilterKind.HighPass:
            {
                int order = NormalisedOrder(spec.Order);
                int count = order / 2;
                var sections = new Section[count];
                for (int k = 0; k < count; k++)
                {
                    double q = 1.0 / (2.0 * Math.Cos(((2.0 * k) + 1.0) * Math.PI / (2.0 * order)));
                    double alpha = sinW / (2.0 * q);
                    double a0 = 1.0 + alpha;
                    sections[k] = spec.Kind == ScopeFilterKind.LowPass
                        ? new Section(
                            (1.0 - cosW) / 2.0 / a0,
                            (1.0 - cosW) / a0,
                            (1.0 - cosW) / 2.0 / a0,
                            (-2.0 * cosW) / a0,
                            (1.0 - alpha) / a0)
                        : new Section(
                            (1.0 + cosW) / 2.0 / a0,
                            -(1.0 + cosW) / a0,
                            (1.0 + cosW) / 2.0 / a0,
                            (-2.0 * cosW) / a0,
                            (1.0 - alpha) / a0);
                }

                return sections;
            }

            default:
                return [];
        }
    }

    /// <summary>The order a spec really runs at: even, and inside the offered range.</summary>
    public static int NormalisedOrder(int order)
    {
        int clamped = Math.Clamp(order, MinOrder, MaxOrder);
        return clamped % 2 == 0 ? clamped : clamped + 1;
    }
}
