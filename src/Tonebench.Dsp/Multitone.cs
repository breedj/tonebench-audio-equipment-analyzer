namespace Tonebench.Dsp;

/// <summary>One tone of a multitone comb as it was measured: the bin it sits on and its noise-subtracted level.</summary>
public readonly record struct MultitoneTone(int Bin, double FreqHz, double AmplitudeVrms, double AmplitudeDbv);

/// <summary>
/// The gap between two adjacent tones of the comb -- everything in it is distortion or noise. The
/// figures are referenced to the comb's total RMS (dBr), so bands are comparable with each other
/// and the overall TD+N is the power sum of the same numbers.
/// </summary>
/// <param name="ToneIndex">Index into <see cref="MultitoneResult.Tones"/> of the tone at the band's lower edge.</param>
/// <param name="LoBin">First bin of the gap, clear of the lower tone's skirt.</param>
/// <param name="HiBin">Last bin of the gap, clear of the upper tone's skirt.</param>
/// <param name="Resolved">
/// False when the two tones sit closer than <see cref="MultitoneAnalyzer.MinToneSpacingBins"/>, so
/// no gap exists to read: every other figure is then zero and the band is left out of the total.
/// </param>
/// <param name="HighestBin">The bin of the highest line in the gap.</param>
/// <param name="HighestFreqHz">Its frequency.</param>
/// <param name="HighestDbv">Its level, skirt-summed and floor-subtracted like a harmonic.</param>
/// <param name="HighestDbr">Its level relative to the comb's RMS.</param>
/// <param name="DnVrms">RMS of everything in the gap -- distortion plus noise.</param>
/// <param name="DnDbr">That, relative to the comb's RMS.</param>
public readonly record struct MultitoneBand(
    int ToneIndex, int LoBin, int HiBin, bool Resolved,
    int HighestBin, double HighestFreqHz, double HighestDbv, double HighestDbr,
    double DnVrms, double DnDbr);

/// <summary>
/// What <see cref="MultitoneAnalyzer.Analyze"/> found: every tone, every gap between adjacent tones,
/// and the total distortion-plus-noise over the resolved gaps relative to the comb.
/// </summary>
/// <param name="Tones">The measured tones, ascending; comb tones that landed on one bin are one tone here.</param>
/// <param name="Bands">One per adjacent pair of tones, in order.</param>
/// <param name="CombVrms">√Σ V² over the tones -- the reference every dBr figure is against.</param>
/// <param name="Tdn">√(Σ resolved gap power) / <see cref="CombVrms"/>, as a ratio.</param>
/// <param name="ResolvedBands">How many bands went into <see cref="Tdn"/>.</param>
public sealed record MultitoneResult(
    IReadOnlyList<MultitoneTone> Tones,
    IReadOnlyList<MultitoneBand> Bands,
    double CombVrms,
    double Tdn,
    int ResolvedBands)
{
    public double TdnPercent => Tdn * 100.0;

    public double TdnDb => 20.0 * Math.Log10(Math.Max(Tdn, double.Epsilon));

    public double CombDbv => 20.0 * Math.Log10(Math.Max(CombVrms, double.Epsilon));

    public int UnresolvedBands => Bands.Count - ResolvedBands;
}

/// <summary>
/// Distortion measured against a multitone comb: with every tone's own bins excluded, whatever is
/// left between adjacent tones is the DUT's distortion products plus the noise floor. Reported per
/// gap (the highest line in it, and the gap's D+N power) and in total, the way THD+N is: everything
/// that is not the stimulus, over the stimulus.
///
/// <para>Conventions, and why. <b>The reference is the comb's total RMS</b> (√Σ V²), for the
/// per-band figures and the total alike -- a per-band figure against its own lower tone would read
/// differently from the total on a rolled-off comb, and the two would not add up. <b>Tone skirts
/// are ±<see cref="HarmonicAnalyzer.DefaultSkirtHalfWidthBins"/> bins</b> and the tones are
/// measured with <see cref="HarmonicAnalyzer.MeasureTone"/>, the same peak search, skirt sum and
/// noise-floor subtraction THD and IMD use; the highest line in a gap is skirt-summed and
/// floor-subtracted the same way, with the skirt <em>clamped to the gap</em> -- a line next to a tone
/// would otherwise sum the tone's own first sidelobe bin (−12 dB under Hann) and read as a product
/// 1 dB below the tone; measured against the simulator's clean comb before the clamp, every empty
/// gap's "highest" sat at −18.8 dBr for exactly that reason. The floor it subtracts is the gap's
/// own median density, since the flanking-window estimate a harmonic uses would sit on the tones.
/// <b>A gap's D+N is the raw integral</b> over its bins,
/// nothing subtracted -- it is distortion <em>plus noise</em> by definition. <b>A gap narrower than
/// <see cref="MinToneSpacingBins"/> is unresolved</b> rather than read against the tones' own
/// window lobes (the IMD rule): a 5-tones-per-octave comb has its tones under 1 Hz apart at the
/// bottom, which no practical FFT separates, so the low bands merge and are reported as such.
/// Nothing below the first tone or above the last is counted -- those are not between tones.</para>
/// </summary>
public static class MultitoneAnalyzer
{
    /// <summary>Fewest bins between two tones for the gap between them to be readable; see <see cref="IntermodulationAnalyzer.MinToneSpacingBins"/>.</summary>
    public const int MinToneSpacingBins = IntermodulationAnalyzer.MinToneSpacingBins;

    /// <summary>
    /// Measures the comb whose tones are expected at <paramref name="toneBins"/> (any order,
    /// duplicates and out-of-range bins tolerated) and every gap between adjacent tones up to
    /// <paramref name="maxBinInclusive"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when fewer than two tones lie inside the spectrum, or when the comb
    /// has no measurable level at all.
    /// </returns>
    public static MultitoneResult? Analyze(
        PsdSpectrum psd,
        ReadOnlySpan<int> toneBins,
        int maxBinInclusive,
        int peakSearchSpanBins = HarmonicAnalyzer.DefaultPeakSearchSpanBins,
        int skirtHalfWidthBins = HarmonicAnalyzer.DefaultSkirtHalfWidthBins,
        bool subtractNoiseFloor = true)
    {
        int length = psd.ValuesV2PerHz.Length;
        int highestBin = Math.Min(length - 1, maxBinInclusive);

        var candidates = new List<int>(toneBins.Length);
        foreach (int bin in toneBins)
        {
            if (bin > 0 && bin < length)
                candidates.Add(bin);
        }
        candidates.Sort();

        // Located bins, deduplicated: at a small FFT several comb tones round to one bin, and
        // measuring that bin three times would count one tone's power three times over.
        var tones = new List<MultitoneTone>();
        double combPower = 0.0;
        foreach (int bin in candidates)
        {
            var tone = HarmonicAnalyzer.MeasureTone(psd, bin, peakSearchSpanBins, skirtHalfWidthBins, subtractNoiseFloor);
            if (tones.Count > 0 && tone.Bin <= tones[^1].Bin)
                continue;

            double vrms = tone.AmplitudeVrms;
            combPower += vrms * vrms;
            tones.Add(new MultitoneTone(tone.Bin, tone.Bin * psd.BinSizeHz, vrms, 20.0 * Math.Log10(Math.Max(vrms, double.Epsilon))));
        }

        if (tones.Count < 2 || combPower <= 0.0)
            return null;

        double combVrms = Math.Sqrt(combPower);
        var bands = new List<MultitoneBand>(tones.Count - 1);
        double totalPower = 0.0;
        int resolved = 0;

        for (int i = 0; i + 1 < tones.Count; i++)
        {
            int lowerBin = tones[i].Bin;
            int upperBin = tones[i + 1].Bin;
            int lo = lowerBin + skirtHalfWidthBins + 1;
            int hi = Math.Min(upperBin - skirtHalfWidthBins - 1, highestBin);

            if (upperBin - lowerBin < MinToneSpacingBins || hi < lo)
            {
                bands.Add(new MultitoneBand(i, lo, hi, false, 0, 0.0, double.NegativeInfinity, double.NegativeInfinity, 0.0, double.NegativeInfinity));
                continue;
            }

            int peakBin = lo;
            double peakValue = psd.ValuesV2PerHz[lo];
            for (int k = lo + 1; k <= hi; k++)
            {
                if (psd.ValuesV2PerHz[k] > peakValue)
                {
                    peakValue = psd.ValuesV2PerHz[k];
                    peakBin = k;
                }
            }

            double gapPower = psd.Integrate(new BinRange(lo, hi)).PowerV2;
            double highestVrms = LineInGapVrms(psd, peakBin, lo, hi, skirtHalfWidthBins, subtractNoiseFloor);
            double dnVrms = Math.Sqrt(Math.Max(0.0, gapPower));
            totalPower += gapPower;
            resolved++;

            bands.Add(new MultitoneBand(
                i, lo, hi, true,
                peakBin, peakBin * psd.BinSizeHz,
                20.0 * Math.Log10(Math.Max(highestVrms, double.Epsilon)),
                20.0 * Math.Log10(Math.Max(highestVrms, double.Epsilon) / combVrms),
                dnVrms,
                20.0 * Math.Log10(Math.Max(dnVrms, double.Epsilon) / combVrms)));
        }

        return new MultitoneResult(tones, bands, combVrms, Math.Sqrt(totalPower) / combVrms, resolved);
    }

    /// <summary>
    /// A line at <paramref name="peakBin"/> measured like a harmonic -- skirt-summed, with the local
    /// noise floor subtracted and a below-floor result reported well under it -- but with the skirt
    /// clamped to [<paramref name="lo"/>, <paramref name="hi"/>] so a tone's own sidelobe bins never
    /// enter the sum, and the floor taken as the median density of the gap itself.
    /// </summary>
    private static double LineInGapVrms(PsdSpectrum psd, int peakBin, int lo, int hi, int skirtHalfWidthBins, bool subtractNoiseFloor)
    {
        int from = Math.Max(lo, peakBin - skirtHalfWidthBins);
        int to = Math.Min(hi, peakBin + skirtHalfWidthBins);
        double totalVrms = psd.IntegrateVrms(from, to);
        if (!subtractNoiseFloor)
            return totalVrms;

        double noiseDensity = GapMedianDensity(psd, lo, hi);
        if (noiseDensity <= 0.0)
            return totalVrms;

        double noisePower = noiseDensity * (to - from + 1) * psd.BinSizeHz;
        double signalPower = (totalVrms * totalVrms) - noisePower;
        double floorVrms = Math.Sqrt(noisePower) * HarmonicAnalyzer.BelowFloorAmplitudeFactor;
        return signalPower <= 0.0 ? floorVrms : Math.Max(Math.Sqrt(signalPower), floorVrms);
    }

    /// <summary>Median PSD density over the gap, scaled to the mean of an exponential distribution like <c>HarmonicAnalyzer.LocalNoiseDensity</c>.</summary>
    private static double GapMedianDensity(PsdSpectrum psd, int lo, int hi)
    {
        int n = hi - lo + 1;
        if (n <= 0)
            return 0.0;

        double[] values = new double[n];
        psd.ValuesV2PerHz.Slice(lo, n).CopyTo(values);
        Array.Sort(values);
        double median = n % 2 == 1 ? values[n / 2] : 0.5 * (values[(n / 2) - 1] + values[n / 2]);
        return median * HarmonicAnalyzer.MedianToMeanExponential;
    }
}
