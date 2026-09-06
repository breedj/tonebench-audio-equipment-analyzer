namespace Qa403.Dsp;

/// <summary>
/// One gated impulse response, ready to display or to transform.
/// </summary>
/// <param name="Samples">
/// The windowed impulse, <c>PreSamples + PostSamples + 1</c> long, with the linear impulse at index
/// <see cref="PreSamples"/>.
/// </param>
/// <param name="FirstSampleTimeS">Time of index 0 relative to the impulse. Negative.</param>
/// <param name="ResolutionLimitHz">
/// <c>1 / (gate length in seconds)</c>. Below this the gated magnitude describes the window rather
/// than the DUT — a 5 ms gate says nothing about 100 Hz. The single most important number on the
/// panel, which is why it is computed here rather than left to the UI.
/// </param>
public sealed record GatedImpulse(
    double[] Samples,
    double FirstSampleTimeS,
    double SampleRateHz,
    int PreSamples,
    int PostSamples,
    double ResolutionLimitHz);

/// <summary>
/// The <b>user-facing</b> gate: how much of the impulse response to keep either side of the peak,
/// which is a question about reflections and frequency resolution.
///
/// <para><b>Not to be confused with <see cref="ImpulseGate"/>, and they are wired to different
/// things.</b> <see cref="ImpulseGate.ForSweep"/> is fixed by the sweep geometry, is not exposed to
/// the user, and exists for one purpose: to delete the harmonic distortion packets that sweep
/// deconvolution leaves ahead of the linear impulse. This gate is chosen by the user and is always a
/// <i>subset</i> of that one — <see cref="Bounds"/> is what enforces it. A UI wired to the wrong one
/// either keeps the distortion the whole scheme exists to remove, or offers a control that silently
/// does nothing.</para>
/// </summary>
public static class ImpulseWindow
{
    /// <summary>
    /// Windows <paramref name="ir"/> about <paramref name="peakIndex"/>, keeping
    /// <paramref name="preMs"/> before the impulse and <paramref name="postMs"/> after it.
    ///
    /// <para>Reads <paramref name="ir"/> <b>circularly</b>: the impulse response of a periodic sweep
    /// is a circular record, and a peak near index 0 has its pre-samples at the far end of the array.
    /// </para>
    ///
    /// <para>Applies <i>half</i> of <paramref name="windowType"/> on each side — rise up to the
    /// impulse, fall away from it — rather than one whole window across the span, because the
    /// impulse is not at the centre of the gate and a whole window would attenuate it by however far
    /// off-centre it sits. The halves are cut from the same cached DFT-even coefficient arrays the
    /// rest of the app uses, so every window type works without re-deriving cosine sums; a
    /// consequence of that convention is that the outermost sample is small rather than exactly
    /// zero. <see cref="WindowType.Rectangular"/> is a legitimate choice here and yields a bare cut:
    /// it is what you want when the point is to isolate a reflection exactly.</para>
    /// </summary>
    public static GatedImpulse WindowIr(
        ReadOnlySpan<double> ir,
        int peakIndex,
        double preMs,
        double postMs,
        double sampleRateHz,
        WindowType windowType = WindowType.Hann)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ir.Length, nameof(ir));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(postMs);

        int pre = Math.Max(1, (int)Math.Round(preMs * sampleRateHz / 1000.0));
        int post = Math.Max(1, (int)Math.Round(postMs * sampleRateHz / 1000.0));

        if (pre + post + 1 > ir.Length)
            throw new ArgumentOutOfRangeException(nameof(postMs), "A gate cannot be longer than the record it gates.");

        var rise = Window.Get(windowType, (2 * pre) + 2).Values;
        var fall = Window.Get(windowType, (2 * post) + 2).Values;

        var samples = new double[pre + post + 1];
        for (int j = 0; j < samples.Length; j++)
        {
            int offset = j - pre;
            int source = (((peakIndex + offset) % ir.Length) + ir.Length) % ir.Length;

            // Both halves peak at 1.0 where they meet, so index `pre` reads the impulse untouched.
            double taper = offset <= 0 ? rise[j + 1] : fall[post + 1 + offset];
            samples[j] = ir[source] * taper;
        }

        double lengthSeconds = samples.Length / sampleRateHz;

        return new GatedImpulse(
            samples,
            -pre / sampleRateHz,
            sampleRateHz,
            pre,
            post,
            1.0 / lengthSeconds);
    }

    /// <summary>
    /// Circularly rotates <paramref name="ir"/> so that <paramref name="peakIndex"/> lands at index
    /// 0. Lossless, and for the wire and for storage only — never for analysis, where the peak's
    /// position is the thing that distinguishes a genuine DUT delay from the periodic sweep's
    /// arbitrary start point.
    /// </summary>
    public static void RotateToOrigin(ReadOnlySpan<double> ir, int peakIndex, Span<double> destination)
    {
        if (destination.Length != ir.Length)
            throw new ArgumentException("Destination must match the impulse response's length.", nameof(destination));

        for (int i = 0; i < ir.Length; i++)
            destination[i] = ir[(((peakIndex + i) % ir.Length) + ir.Length) % ir.Length];
    }

    /// <summary>
    /// The widest gate the sweep geometry allows, in milliseconds — what the UI's fields clamp to.
    /// Derived from <see cref="ImpulseGate.ForSweep"/> rather than re-deriving the harmonic spacing,
    /// so the user-facing gate cannot drift outside the anti-distortion one it must stay inside.
    /// </summary>
    public static (double MaxPreMs, double MaxPostMs) Bounds(
        int fftSize, double sampleRateHz, double startHz, double endHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var gate = ImpulseGate.ForSweep(fftSize, startHz, endHz);
        return (gate.PreSamples * 1000.0 / sampleRateHz, gate.PostSamples * 1000.0 / sampleRateHz);
    }
}
