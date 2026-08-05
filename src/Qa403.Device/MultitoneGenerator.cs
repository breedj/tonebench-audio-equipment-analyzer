namespace Qa403.Device;

/// <summary>
/// Derives the tone comb for <see cref="GeneratorKind.Multitone"/>: log-spaced tones across a
/// fixed 5 Hz - 20 kHz band, optionally tilted by a per-octave rolloff, normalised so the combined
/// RMS matches the requested total.
/// </summary>
/// <remarks>
/// The tones are *not* snapped to coherent FFT bins. <c>Qa403.Dsp.CoherentFrequency.Nearest</c>
/// needs both the sample rate and the FFT length N, and neither is known here — the generator is
/// configured before acquisition starts, and N belongs to the spectrum pipeline, not the device.
/// The analysis window (Blackman-Harris by default) keeps the resulting leakage acceptable for a
/// live display. Revisit when the frequency-response measurement lands and stimulus and analysis
/// record lengths are chosen together.
/// </remarks>
public static class MultitoneGenerator
{
    /// <summary>Lowest tone in the comb, Hz. Fixed by design — the multitone generator exposes no band controls.</summary>
    public const double BandStartHz = 5.0;

    /// <summary>Highest tone the comb may reach, Hz. Tones are emitted while f &lt;= this.</summary>
    public const double BandEndHz = 20_000.0;

    /// <summary>
    /// Builds the comb: f_i = 5 Hz * 2^(i / tonesPerOctave) while f_i &lt;= 20 kHz, each tone
    /// attenuated by <c>RolloffDbPerOctave</c> per octave above the band start, then scaled so the
    /// combined RMS equals <c>TotalRmsDbv</c> when the rolloff is 0 dB/octave (a non-zero rolloff
    /// only ever removes energy, so the actual total lands below the requested one — that is what
    /// "total RMS level (w/ 0 dB rolloff)" means).
    /// </summary>
    public static IReadOnlyList<GeneratorTone> Build(MultitoneSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.TonesPerOctave < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings), settings.TonesPerOctave, "TonesPerOctave must be at least 1.");
        }

        // Two passes: frequencies and rolloff weights first, because the per-tone scale factor
        // depends on the tone count, which isn't known until the band has been walked.
        var frequenciesHz = new List<double>();
        var weights = new List<double>();

        for (int i = 0; ; i++)
        {
            double frequencyHz = BandStartHz * Math.Pow(2.0, (double)i / settings.TonesPerOctave);
            if (frequencyHz > BandEndHz)
                break;

            double octavesAboveStart = Math.Log2(frequencyHz / BandStartHz);
            frequenciesHz.Add(frequencyHz);
            weights.Add(Math.Pow(10.0, -settings.RolloffDbPerOctave * octavesAboveStart / 20.0));
        }

        // Uncorrelated tones add in power, so N equal-level tones each at (total / sqrt(N)) sum to
        // exactly the requested total RMS.
        double totalRmsV = Math.Pow(10.0, settings.TotalRmsDbv / 20.0);
        double perToneRmsV = totalRmsV / Math.Sqrt(frequenciesHz.Count);

        var tones = new GeneratorTone[frequenciesHz.Count];
        for (int i = 0; i < tones.Length; i++)
        {
            double toneRmsV = perToneRmsV * weights[i];
            tones[i] = new GeneratorTone(frequenciesHz[i], 20.0 * Math.Log10(toneRmsV));
        }

        return tones;
    }
}
