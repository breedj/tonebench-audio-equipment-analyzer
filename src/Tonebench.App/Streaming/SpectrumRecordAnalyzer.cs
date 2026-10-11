using System.Buffers;
using System.Numerics;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Streaming;

/// <summary>
/// What one analysed record means: the dBV trace, the meters panel's figures and the noise
/// figures. <see cref="SpectrumPipeline.ProcessChannel"/> calls these per tick on live audio, and
/// <see cref="Analyze"/> calls them once on a record that came from somewhere else -- today an
/// imported circuit simulation.
///
/// <para>These moved here out of <see cref="SpectrumPipeline"/> rather than being reimplemented
/// beside it, which is the whole point: an imported simulation's THD has to be the same number the
/// live screen would have shown for the same samples, and two copies of
/// <see cref="ComputeMeters"/> would drift the first time one of them was improved. The pipeline
/// keeps everything that is about <i>streaming</i> -- the cross-tick averagers, the retained
/// snapshot, the flush generation, the broadcast -- and this keeps the arithmetic.</para>
/// </summary>
internal static class SpectrumRecordAnalyzer
{
    /// <summary>
    /// One record, analysed exactly as a live tick would analyse it with averaging off.
    /// </summary>
    /// <param name="samples">
    /// Exactly <c>options.FftSize</c> samples, in volts. Not windowed -- this applies
    /// <see cref="SpectrumPipelineOptions.Window"/> itself, the same way the live path does.
    /// </param>
    /// <param name="sampleRateHz">
    /// The rate those samples are on, which for an imported simulation is derived from the run's
    /// duration rather than from any hardware.
    /// </param>
    /// <param name="powerGain">
    /// A resolved per-bin weighting gain, or empty for none. Empty is what every measurement path
    /// passes: a weighting changes what "noise" means, and a stored measurement has to say which
    /// one produced it rather than inheriting whatever the live screen happened to be showing.
    /// </param>
    /// <remarks>
    /// One method rather than a handful of composable ones because <see cref="PsdSpectrum"/> and
    /// <see cref="AmplitudeSpectrum"/> are <c>ref struct</c>s borrowing the caller's buffers: they
    /// cannot be returned, so everything that reads them has to live inside the frame that owns
    /// them.
    /// </remarks>
    internal static RecordAnalysis Analyze(
        ReadOnlySpan<double> samples,
        double sampleRateHz,
        SpectrumPipelineOptions options,
        IFftBackend fft,
        ReadOnlySpan<double> powerGain = default,
        float externalGainDb = 0f)
    {
        int n = options.FftSize;
        if (samples.Length != n)
            throw new ArgumentException($"Expected exactly {n} samples, got {samples.Length}.", nameof(samples));

        int specLen = (n / 2) + 1;

        double[] windowed = ArrayPool<double>.Shared.Rent(n);
        Complex[] spec = ArrayPool<Complex>.Shared.Rent(specLen);
        double[] ampBuf = ArrayPool<double>.Shared.Rent(specLen);
        double[] psdBuf = ArrayPool<double>.Shared.Rent(specLen);
        double[] psdWeightedBuf = ArrayPool<double>.Shared.Rent(specLen);
        try
        {
            var window = Window.Get(options.Window, n);
            Window.Apply(samples, window, windowed.AsSpan(0, n));

            var specSpan = spec.AsSpan(0, specLen);
            fft.Forward(windowed.AsSpan(0, n), specSpan);

            AmplitudeSpectrum.Compute(specSpan, window.Sums, ampBuf.AsSpan(0, specLen));
            var psd = PsdSpectrum.Compute(specSpan, sampleRateHz, window.Sums, psdBuf.AsSpan(0, specLen));
            double binSizeHz = psd.BinSizeHz;

            // The live path averages in the power domain, so it squares the amplitude trace before
            // handing it on and every reader downstream -- the retained snapshot, the save path --
            // expects power. One record is its own average, so the squaring is all that is needed.
            Span<double> ampPower = ampBuf.AsSpan(0, specLen);
            for (int i = 0; i < specLen; i++)
                ampPower[i] *= ampPower[i];

            var psdWeighted = psd;
            if (!powerGain.IsEmpty)
            {
                for (int i = 0; i < specLen; i++)
                    ampPower[i] *= powerGain[i];
                var weightedValues = psdWeightedBuf.AsSpan(0, specLen);
                for (int i = 0; i < specLen; i++)
                    weightedValues[i] = psd.ValuesV2PerHz[i] * powerGain[i];
                psdWeighted = PsdSpectrum.FromValues(weightedValues, sampleRateHz, binSizeHz, window.Sums);
            }

            (float peakDbv, float totalRmsDbv) = ComputeTimeDomainLevels(samples);

            // Averaging off, so averagesTaken is 1: one record was taken and it is this one. The
            // live path's "a count of 1 is not averaging" rule is about when a single shot may
            // stop, which has no meaning here.
            var meters = ComputeMeters(psd, psdWeighted, peakDbv, totalRmsDbv, options, averagesTaken: 1, externalGainDb);
            var noise = ComputeNoiseFigures(psdWeighted, meters, window.Sums.EnbwHz(sampleRateHz), options);

            var dbv = new double[specLen];
            for (int i = 0; i < specLen; i++)
                dbv[i] = PowerToDbv(ampPower[i]);

            return new RecordAnalysis(dbv, meters, noise, window.Sums, binSizeHz);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(psdWeightedBuf);
            ArrayPool<double>.Shared.Return(psdBuf);
            ArrayPool<double>.Shared.Return(ampBuf);
            ArrayPool<Complex>.Shared.Return(spec);
            ArrayPool<double>.Shared.Return(windowed);
        }
    }

    /// <summary>
    /// The one place the amplitude-domain power trace becomes dBV. The <c>/2</c> is peak-squared to
    /// rms-squared, i.e. the same <c>20*log10(A/sqrt2)</c> every level in this app is quoted as;
    /// leaving it out reads 3.01 dB high everywhere. <see cref="double.Epsilon"/> rather than a
    /// zero guard because an exactly-zero bin would otherwise be -Infinity, which no JSON body and
    /// no i16 codec can carry.
    /// </summary>
    internal static double PowerToDbv(double ampPower) =>
        10.0 * Math.Log10(Math.Max(ampPower / 2.0, double.Epsilon));

    /// <summary>
    /// True instantaneous peak and broadband RMS from the raw (unwindowed) time-domain snapshot —
    /// not spectral estimates. Peak is expressed as its RMS-equivalent (peakVpk/sqrt2), matching
    /// every other dBV value in this app's convention (see <c>SignalSynthesizer.DbvToPeakV</c>).
    ///
    /// The DC component is removed first, for the same reason the noise integrations exclude bin 0:
    /// a DC-coupled input's offset is not part of the signal being measured. Leaving it in added
    /// itself straight onto the peak — a 4.6 mV offset read as +0.03 dBV on a 1 V tone, which is
    /// why Pk Vrms sat above 1.0000 for a 1.0000 Vrms generator.
    /// </summary>
    internal static (float PeakDbv, float TotalRmsDbv) ComputeTimeDomainLevels(ReadOnlySpan<double> timeDomainSamples)
    {
        double mean = 0.0;
        foreach (double v in timeDomainSamples)
            mean += v;
        mean /= timeDomainSamples.Length;

        double peakVpk = 0.0;
        double sumSquares = 0.0;
        foreach (double sample in timeDomainSamples)
        {
            double v = sample - mean;
            double a = Math.Abs(v);
            if (a > peakVpk)
                peakVpk = a;
            sumSquares += v * v;
        }

        double peakVrms = peakVpk / Math.Sqrt(2.0);
        double totalRmsVrms = Math.Sqrt(sumSquares / timeDomainSamples.Length);

        float peakDbv = (float)(20.0 * Math.Log10(Math.Max(peakVrms, double.Epsilon)));
        float totalRmsDbv = (float)(20.0 * Math.Log10(Math.Max(totalRmsVrms, double.Epsilon)));
        return (peakDbv, totalRmsDbv);
    }

    /// <summary>
    /// Everything the meters panel shows, computed once per channel per tick and shared by both
    /// the live broadcast (<see cref="BroadcastMetersFrame"/>) and <see cref="LatestSnapshots"/> --
    /// so a saved measurement's metrics are exactly what was on screen, never re-derived
    /// separately. Returns <see langword="null"/> when no fundamental (and so no harmonics) could
    /// be found, matching the old inline logic's "nothing to broadcast this tick" behaviour.
    /// </summary>
    internal static SpectrumMeters? ComputeMeters(
        PsdSpectrum psd, PsdSpectrum psdWeighted, float peakDbv, float totalRmsDbv, SpectrumPipelineOptions options, int averagesTaken,
        float externalGainDb = 0f)
    {
        int minSearchBin = Math.Max(1, (int)Math.Round(options.FundamentalMinSearchHz / psd.BinSizeHz));
        int fundamentalBin = FindGlobalPeakBin(psd.ValuesV2PerHz, minSearchBin);
        if (fundamentalBin <= 0)
            return null;

        // The measurement bandwidth for every noise-integrated figure below, and the upper bound
        // on which harmonics count toward THD. See SpectrumPipelineOptions.MeasurementLoFreqHz —
        // passing this rather than the full spectrum is what keeps DC and out-of-band noise out
        // of THD+N/SNR.
        var band = psd.BandOf(options.MeasurementLoFreqHz, options.MeasurementHiFreqHz);

        // Harmonics may be counted past the measurement band -- see
        // SpectrumPipelineOptions.HarmonicMaxFreqHz. Everything else below stays on `band`.
        int harmonicMaxBin = options.HarmonicMaxFreqHz is { } harmonicMaxHz
            ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(harmonicMaxHz))
            : band.HiBinInclusive;

        int harmonicCount = Math.Max(1, options.HarmonicCount);
        var harmonicsBuf = new Harmonic[harmonicCount];
        int written = HarmonicAnalyzer.Analyze(
            psd, fundamentalBin, harmonicCount, harmonicsBuf, maxBinInclusive: harmonicMaxBin);
        if (written == 0)
            return null;

        var harmonics = written == harmonicsBuf.Length ? harmonicsBuf : harmonicsBuf[..written];
        var fundamental = harmonics[0];

        float fundamentalFreqHz = (float)(fundamentalBin * psd.BinSizeHz);
        float thdPercent = (float)(SpectrumMetrics.Thd(harmonics) * 100.0);
        // The two noise-integrated figures read the *weighted* PSD; their reference, the fundamental,
        // was measured above on the unweighted one -- every standard curve is 0 dB at 1 kHz, but a
        // tone elsewhere must still be reported at its true level.
        float thdNPercent = (float)(SpectrumMetrics.ThdPlusN(psdWeighted, fundamental, band, options.NotchOctaves) * 100.0);
        float snrDb = (float)SpectrumMetrics.SnrDb(psdWeighted, harmonics, band, options.NotchOctaves);
        float fundamentalDbv = (float)fundamental.AmplitudeDbv;
        float noiseFloorDbv = fundamentalDbv - snrDb;

        var otherPeaks = FindOtherPeaks(psd, harmonics, fundamentalDbv, options);

        IntermodulationResult? intermodulation = null;
        if (options.Intermodulation is { } imdSpec)
        {
            // Products above the band limit are omitted, not zero — see IntermodulationSpec.
            int maxProductBin = imdSpec.MaxProductFreqHz is { } maxProductHz
                ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(maxProductHz))
                : band.HiBinInclusive;
            intermodulation = IntermodulationAnalyzer.Analyze(
                psd, psd.BinOfFrequency(imdSpec.ToneLowHz), psd.BinOfFrequency(imdSpec.ToneHighHz),
                imdSpec.Method, imdSpec.MaxOrder, maxProductBin);
        }

        // Same band-limit convention as IMD: gaps above MeasurementHiFreqHz (or the spec's own
        // limit) do not count, so the figure is in-band distortion the way THD+N is.
        MultitoneResult? multitone = null;
        if (options.Multitone is { } multitoneSpec && multitoneSpec.ToneFreqsHz.Count >= 2)
        {
            int maxGapBin = multitoneSpec.MaxProductFreqHz is { } maxGapHz
                ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(maxGapHz))
                : band.HiBinInclusive;
            int[] toneBins = new int[multitoneSpec.ToneFreqsHz.Count];
            for (int i = 0; i < toneBins.Length; i++)
                toneBins[i] = psd.BinOfFrequency(multitoneSpec.ToneFreqsHz[i]);
            multitone = MultitoneAnalyzer.Analyze(psd, toneBins, maxGapBin);
        }

        return new SpectrumMeters(
            fundamentalFreqHz, (float)psd.BinSizeHz, thdPercent, thdNPercent, snrDb,
            fundamentalDbv, noiseFloorDbv, peakDbv, totalRmsDbv, harmonics, otherPeaks, averagesTaken,
            intermodulation, multitone, externalGainDb);
    }

    /// <summary>
    /// The band RMS and the noise density, on the weighted PSD, whether or not a fundamental was
    /// found -- <see cref="ComputeMeters"/> returns null without one and the meters frame is then not
    /// sent at all, which is fine for THD and useless for a noise measurement with the generator
    /// off. With meters, the density excludes what SNR excludes (the fundamental's notch and the
    /// harmonics' lobes); without, it is the mean over the whole band. Never NaN: an empty band reads
    /// as the floor of the float scale rather than poisoning a JSON body.
    /// </summary>
    internal static NoiseFigures ComputeNoiseFigures(PsdSpectrum psdWeighted, SpectrumMeters? meters, double enbwHz, SpectrumPipelineOptions options)
    {
        var band = psdWeighted.BandOf(options.MeasurementLoFreqHz, options.MeasurementHiFreqHz);
        double bandRmsVrms = SpectrumMetrics.BandRmsVrms(psdWeighted, band);
        double densityV2PerHz = meters is { Harmonics.Length: > 0 }
            ? SpectrumMetrics.NoiseDensityV2PerHz(psdWeighted, meters.Harmonics, band, options.NotchOctaves)
            : SpectrumMetrics.NoiseDensityV2PerHz(psdWeighted, band);

        return new NoiseFigures(
            (float)(20.0 * Math.Log10(Math.Max(bandRmsVrms, double.Epsilon))),
            (float)(10.0 * Math.Log10(Math.Max(densityV2PerHz, double.Epsilon))),
            (float)enbwHz,
            (float)options.MeasurementLoFreqHz,
            (float)options.MeasurementHiFreqHz,
            options.Weighting);
    }

    /// <summary>
    /// Everything else worth calling out in the spectrum: mains hum, its own harmonics, and real
    /// intermodulation/mixing products between multiple simultaneous generator tones (the shared
    /// soft-clip in SimulatedDevice is a genuine nonlinearity, so these are real products, not
    /// synthesized ones). Scanned from <see cref="SpectrumPipelineOptions.DisplayLoFreqHz"/>, not
    /// <see cref="SpectrumPipelineOptions.FundamentalMinSearchHz"/> — hum at 50/100 Hz is exactly
    /// what this is for.
    /// </summary>
    internal static (float FreqHz, float Dbc)[] FindOtherPeaks(
        PsdSpectrum psd, ReadOnlySpan<Harmonic> harmonics, float fundamentalDbv, SpectrumPipelineOptions options)
    {
        int otherPeaksCount = Math.Max(0, options.OtherPeaksCount);
        if (otherPeaksCount == 0)
            return [];

        Span<BinRange> excluded = harmonics.Length <= 32 ? stackalloc BinRange[harmonics.Length] : new BinRange[harmonics.Length];
        for (int i = 0; i < harmonics.Length; i++)
        {
            excluded[i] = new BinRange(
                harmonics[i].Bin - HarmonicAnalyzer.DefaultSkirtHalfWidthBins, harmonics[i].Bin + HarmonicAnalyzer.DefaultSkirtHalfWidthBins);
        }

        int minBin = Math.Max(1, (int)Math.Round(options.DisplayLoFreqHz / psd.BinSizeHz));
        Span<SpectralPeak> peaks = otherPeaksCount <= 32 ? stackalloc SpectralPeak[otherPeaksCount] : new SpectralPeak[otherPeaksCount];
        int written = PeakFinder.FindPeaks(psd, excluded, otherPeaksCount, peaks, minBin);

        var result = new (float FreqHz, float Dbc)[written];
        for (int i = 0; i < written; i++)
            result[i] = ((float)(peaks[i].Bin * psd.BinSizeHz), (float)(peaks[i].AmplitudeDbv - fundamentalDbv));

        return result;
    }

    internal static int FindGlobalPeakBin(ReadOnlySpan<double> psdValues, int loBinInclusive)
    {
        if (loBinInclusive >= psdValues.Length)
            return -1;

        int peak = loBinInclusive;
        double peakValue = psdValues[loBinInclusive];
        for (int k = loBinInclusive + 1; k < psdValues.Length; k++)
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

/// <summary>
/// One record's worth of analysis: the full-resolution dBV trace, the meters (null when no
/// fundamental could be found), the noise figures, and the window sums and bin size the trace was
/// produced with -- which a stored spectrum row needs in order to say what it means.
/// </summary>
internal sealed record RecordAnalysis(
    double[] Dbv,
    SpectrumMeters? Meters,
    NoiseFigures Noise,
    WindowSums Sums,
    double BinSizeHz);
