namespace Tonebench.Dsp;

/// <summary>
/// Reads a record at a fractional sample offset: the band-limited value <i>between</i> two samples.
///
/// <para><b>What this is for, and it is not what it sounds like.</b> An oscilloscope's trigger is
/// interpolated — <see cref="EdgeTrigger"/> reports where the crossing really was, to a fraction of
/// a sample — but the record it anchors has to start on a whole sample, so every capture ends up
/// with the trigger a different fraction of a sample from its own first frame. One record does not
/// care. <b>Averaging records does</b>: the whole premise is that sample i of every record is the
/// same point on the waveform, and a residual misalignment uniform over ±½ a sample attenuates a
/// sinusoid by <c>sinc(f·Δt)</c> — <b>0.9 dB at a quarter of the sample rate and 3.6 dB at 0.95 of
/// Nyquist</b>. That is a real loss, it is worst exactly where a scope is being pushed, and it reads
/// as a smaller waveform rather than as a misaligned one. Shifting each record onto the first one's
/// fractional grid removes it.</para>
///
/// <para><b>Windowed sinc rather than linear, for the reason the chart's own drawing already
/// documents.</b> A sampled waveform is band-limited by the ADC's decimation filter, so between any
/// two samples there is exactly one signal it could have been — and it is not the straight line.
/// Linear interpolation is 0.05 % of amplitude at 48 samples a period and 13 % at two and a half,
/// which would trade an alignment error for an amplitude error at the same end of the band. The
/// kernel is Lanczos-windowed at <see cref="Taps"/> taps, the same choice and the same count as
/// <c>scope/interpolation.ts</c> uses to draw, so the aligned record and the drawn trace do not
/// disagree about what sits between two samples.</para>
///
/// <para><b>What this is <i>not</i>: equivalent-time sampling.</b> A bench scope interleaves records
/// taken at different fractional offsets to synthesise a sample rate above its own, which works
/// because its sampler is slower than its front end. That premise does not hold here — the band
/// limit is the ADC's own digital decimation filter, applied <i>before</i> the samples exist, so
/// there is nothing between them that the samples do not already determine and nothing above
/// Nyquist left to recover. Interleaving would therefore add no information at all, only noise
/// averaging that plain averaging already gives. See
/// <c>FractionalDelayTests.ASincReconstructionFromOneRecord_AlreadyHasEverythingInterleavingCouldAdd</c>,
/// which measures it rather than asserting it.</para>
/// </summary>
public static class FractionalDelay
{
    /// <summary>
    /// How many samples either side the kernel reaches.
    ///
    /// <para>16, matching the chart's own reconstruction. A bare sinc is infinitely long and
    /// truncating one leaves ripples either side of every edge, so it is Lanczos-windowed: the
    /// window reaches zero at the kernel's ends and the ripple goes with it.</para>
    /// </summary>
    public const int Taps = 16;

    /// <summary>
    /// Below this, in samples, a shift is skipped and the record is used as it is.
    ///
    /// <para>A thousandth of a sample is 5 ns at 192 kS/s and 0.007° of phase at 20 kHz — far below
    /// anything this analyzer can resolve — so resampling for it would spend the kernel's own
    /// residue to correct nothing.</para>
    /// </summary>
    public const double NegligibleShift = 0.001;

    /// <summary>
    /// Whether <paramref name="shiftSamples"/> is worth resampling for.
    ///
    /// <para>Its own function because the caller has to make the same decision the shift does: a
    /// record left alone must not be <i>counted</i> as aligned, or the next one is shifted onto a
    /// grid nothing established.</para>
    /// </summary>
    public static bool Matters(double shiftSamples) =>
        double.IsFinite(shiftSamples) && Math.Abs(shiftSamples) >= NegligibleShift;

    /// <summary>
    /// Writes <paramref name="source"/> read at <c>i + shiftSamples</c> into
    /// <paramref name="destination"/>.
    ///
    /// <para>A positive shift reads <i>later</i> in the source, which moves a feature <i>earlier</i>
    /// in the result — the sign convention a caller gets wrong once: to move the trigger from
    /// fraction <c>f</c> to fraction <c>g</c>, the shift is <c>f − g</c>.</para>
    ///
    /// <para><b>The ends are held rather than wrapped or zeroed.</b> A record has no outside: zeros
    /// would be a step at each edge, and wrapping would put the end of the capture before its
    /// start. Holding the first and last sample is the one choice that invents no feature — and the
    /// error it leaves is confined to <see cref="Taps"/> samples at each end, which is why a caller
    /// that measures an edge should keep it away from them — the same "a record has no signal
    /// outside itself" fact the scope's removed in-path filter had to pad around.</para>
    /// </summary>
    public static void Shift(ReadOnlySpan<double> source, Span<double> destination, double shiftSamples)
    {
        if (destination.IsEmpty)
            return;
        if (source.IsEmpty)
        {
            destination.Clear();
            return;
        }

        if (!Matters(shiftSamples))
        {
            int copy = Math.Min(source.Length, destination.Length);
            source[..copy].CopyTo(destination[..copy]);
            for (int i = copy; i < destination.Length; i++)
                destination[i] = source[^1];
            return;
        }

        for (int i = 0; i < destination.Length; i++)
            destination[i] = At(source, i + shiftSamples);
    }

    /// <summary>
    /// The band-limited value at <paramref name="position"/>, a fractional sample index.
    ///
    /// <para>A position that lands exactly on a sample returns that sample, rather than a sum that
    /// should come to it — the same rule the chart's reconstruction follows, so a shift of zero and
    /// no shift at all cannot differ in the last bits.</para>
    /// </summary>
    public static double At(ReadOnlySpan<double> source, double position)
    {
        if (source.IsEmpty)
            return 0.0;
        if (!double.IsFinite(position))
            return source[0];

        int nearest = (int)Math.Round(position);
        if (Math.Abs(position - nearest) < 1e-12)
            return source[Math.Clamp(nearest, 0, source.Length - 1)];

        int first = (int)Math.Floor(position) - Taps + 1;
        double sum = 0.0;
        double weight = 0.0;

        for (int k = 0; k < Taps * 2; k++)
        {
            int index = first + k;
            double distance = position - index;
            double w = Lanczos(distance);
            if (w == 0.0)
                continue;

            // Held at the ends: a record has no outside, and zeros there would be a step.
            sum += w * source[Math.Clamp(index, 0, source.Length - 1)];
            weight += w;
        }

        // The kernel's own weights sum to 1 only in the limit, so normalising is what keeps a
        // constant stretch of signal at exactly its own level -- which is the one thing a reader
        // would notice at a glance.
        return weight != 0.0 ? sum / weight : source[Math.Clamp(nearest, 0, source.Length - 1)];
    }

    /// <summary>sinc(x)·sinc(x/Taps) — a sinc tapered to nothing at the kernel's own ends.</summary>
    private static double Lanczos(double x)
    {
        double a = Math.Abs(x);
        if (a >= Taps)
            return 0.0;
        if (a < 1e-12)
            return 1.0;

        double pix = Math.PI * x;
        return Taps * Math.Sin(pix) * Math.Sin(pix / Taps) / (pix * pix);
    }
}
