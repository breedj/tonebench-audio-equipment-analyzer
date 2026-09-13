namespace Qa403.Dsp;

/// <summary>
/// The frequency grid for a stepped sweep (THD vs frequency and, later, frequency response):
/// log-spaced desired frequencies, then snapped onto coherent FFT bins.
///
/// Snapping is not cosmetic. A tone that does not land on an exact bin leaks across the whole
/// spectrum under any window, and that leakage lands on the harmonic bins the sweep is about to
/// measure — it reads as distortion that isn't there. Every point therefore goes through
/// <see cref="CoherentFrequency.Nearest"/>, which is also what the live generator does
/// (<c>GeneratorConfig.SnapToBin</c>), so the requested and the played frequency agree.
///
/// The spacing itself is per *octave* rather than per decade, matching the previous WinForms app
/// (<c>QaLibrary.GetLineairSpacedLogarithmicValuesPerOctave</c>) and how audio measurements are
/// conventionally quoted.
/// </summary>
public static class SweepPoints
{
    /// <summary>
    /// Log-spaced frequencies across [<paramref name="startHz"/>, <paramref name="endHz"/>],
    /// <paramref name="stepsPerOctave"/> per octave, endpoints included.
    ///
    /// The step count is <c>max(2, floor(stepsPerOctave * octaves))</c> over
    /// <c>max(1, log2(end/start))</c> octaves — the two clamps come from the reference
    /// implementation and keep a degenerate span (start == end, or a sub-octave range) from
    /// producing a single point or none at all.
    /// </summary>
    public static double[] LogPerOctave(double startHz, double endHz, int stepsPerOctave)
    {
        if (startHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(startHz), startHz, "Start frequency must be greater than 0.");
        if (endHz < startHz)
            throw new ArgumentOutOfRangeException(nameof(endHz), endHz, "End frequency must not be below the start frequency.");
        if (stepsPerOctave < 1)
            throw new ArgumentOutOfRangeException(nameof(stepsPerOctave), stepsPerOctave, "Steps per octave must be at least 1.");

        double octaves = Math.Max(1.0, Math.Log2(endHz / startHz));
        int totalSteps = Math.Max(2, (int)(stepsPerOctave * octaves));
        double logStep = octaves / totalSteps;

        var values = new double[totalSteps + 1];
        for (int i = 0; i <= totalSteps; i++)
        {
            double hz = startHz * Math.Pow(2.0, i * logStep);
            values[i] = Math.Min(hz, endHz);
        }

        return values;
    }

    /// <summary>
    /// Continues a grid past its end: <c>fromHz·2^(k/stepsPerOctave)</c> for k = 1, 2, … up to and
    /// including the first value at or above <paramref name="toHz"/>. Empty when there is nothing
    /// to add.
    ///
    /// Exists for the frequency-normalized THD sweep, which has to measure the fundamental at the
    /// harmonics' own frequencies — up to several times the requested end. It is deliberately
    /// <em>not</em> <see cref="LogPerOctave"/> over the widened span: that spreads
    /// <c>floor(steps·octaves)</c> steps evenly, so widening the span re-spaces every requested
    /// point and the requested end stops being a grid point at all. This keeps the requested grid
    /// exactly as it was and hangs the extension off its last point at the nominal per-octave
    /// step, and it overshoots the cap by up to one step rather than stopping short of it, so
    /// bin-snapping cannot leave the highest harmonic a hair outside the measured response.
    /// </summary>
    public static double[] ExtendPerOctave(double fromHz, double toHz, int stepsPerOctave)
    {
        if (fromHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(fromHz), fromHz, "Start frequency must be greater than 0.");
        if (stepsPerOctave < 1)
            throw new ArgumentOutOfRangeException(nameof(stepsPerOctave), stepsPerOctave, "Steps per octave must be at least 1.");

        if (toHz <= fromHz)
            return [];

        var values = new List<double>();
        for (int k = 1; ; k++)
        {
            double hz = fromHz * Math.Pow(2.0, (double)k / stepsPerOctave);
            values.Add(hz);
            if (hz >= toHz)
                break;
        }

        return [.. values];
    }

    /// <summary>
    /// Snaps each desired frequency onto the nearest coherent bin and returns the distinct tones,
    /// ascending, dropping anything above <paramref name="maxHz"/>.
    ///
    /// De-duplication is load-bearing rather than tidiness: at the bottom of a sweep the log
    /// spacing is far finer than the bin spacing (at 48 kHz / 64k the bins are 0.73 Hz apart, so
    /// 5 steps/octave from 20 Hz asks for several frequencies inside one bin), and without this the
    /// sweep would re-measure the same tone several times and plot a vertical stack of points.
    /// </summary>
    /// <param name="maxHz">
    /// Upper bound, typically a little under Nyquist. A tone at Nyquist itself has no usable
    /// harmonics and its own bin is the one <see cref="PsdSpectrum"/> gives half weight, so callers
    /// should stay clear of it.
    /// </param>
    public static CoherentTone[] SnapAndDedupe(
        ReadOnlySpan<double> desiredHz, double sampleRateHz, int fftSize, double maxHz)
    {
        var seenBins = new HashSet<int>();
        var tones = new List<CoherentTone>(desiredHz.Length);

        foreach (double hz in desiredHz)
        {
            if (hz <= 0.0 || hz > maxHz)
                continue;

            var tone = CoherentFrequency.Nearest(hz, sampleRateHz, fftSize);

            // Nearest() can round *up* onto a coprime bin past maxHz — re-check after snapping.
            if (tone.FrequencyHz > maxHz)
                continue;

            if (seenBins.Add(tone.Bin))
                tones.Add(tone);
        }

        tones.Sort(static (a, b) => a.Bin.CompareTo(b.Bin));
        return [.. tones];
    }

    /// <summary>
    /// The adaptive-resolution counterpart of <see cref="SnapAndDedupe"/>: every point is snapped
    /// against <i>its own</i> FFT size, chosen by <paramref name="fftSizeFor"/>, and carries that
    /// size along for the runner to program before it plays the tone.
    ///
    /// <para>Two things differ from the fixed-size version, both forced by the per-point size.
    /// Snapping has to happen against the size that point will actually be analyzed at, because a
    /// coherent bin is only coherent for one N — a frequency snapped for 131072 points generally is
    /// not coherent at 16384 and would leak straight into the harmonic bins the sweep is measuring.
    /// And de-duplication can no longer key on the bin index, which means nothing across different
    /// N (bin 40 is 58.6 Hz at 131072 and 469 Hz at 16384); it keys on the snapped frequency
    /// instead, which is the thing that was actually being de-duplicated all along.</para>
    /// </summary>
    /// <param name="fftSizeFor">
    /// The FFT size to analyze a given desired frequency at. Called with the pre-snap frequency:
    /// snapping moves a tone by at most half a bin, far too little to change which size band it
    /// falls in, and this way the size is settled before the snap that depends on it.
    /// </param>
    public static SweepStep[] SnapAndDedupeAdaptive(
        ReadOnlySpan<double> desiredHz, double sampleRateHz, Func<double, int> fftSizeFor, double maxHz)
    {
        ArgumentNullException.ThrowIfNull(fftSizeFor);

        var seenHz = new HashSet<double>();
        var steps = new List<SweepStep>(desiredHz.Length);

        foreach (double hz in desiredHz)
        {
            if (hz <= 0.0 || hz > maxHz)
                continue;

            int fftSize = fftSizeFor(hz);
            var tone = CoherentFrequency.Nearest(hz, sampleRateHz, fftSize);

            // Nearest() can round *up* past maxHz — re-check after snapping, same as SnapAndDedupe.
            if (tone.FrequencyHz > maxHz)
                continue;

            if (seenHz.Add(tone.FrequencyHz))
                steps.Add(new SweepStep(tone.FrequencyHz, fftSize));
        }

        steps.Sort(static (a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));
        return [.. steps];
    }
}

/// <summary>One sweep point: the coherent frequency to play, and the FFT size it is coherent for.</summary>
public readonly record struct SweepStep(double FrequencyHz, int FftSize);
