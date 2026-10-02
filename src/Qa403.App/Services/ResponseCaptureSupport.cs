using System.Numerics;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;
using Qa403.Dsp.Fft;

namespace Qa403.App.Services;

/// <summary>
/// The pure parts of a chirp measurement, shared by <see cref="FrequencyResponseRunner"/> and
/// <see cref="ImpulseResponseRunner"/>.
///
/// <para><b>What is here and what is deliberately not.</b> Everything in this file is a pure
/// function of its arguments: build the stimulus, build the reference spectrum it is divided by,
/// read a peak, turn a finished estimate into chart columns. Those genuinely must not diverge
/// between the two measurements — the impulse-response run stores its gated magnitude and phase in
/// the same <c>response_trace</c> rows, and the browser draws them with the same chart, so two
/// copies of the decimation and corner-finding would be two answers to one question.</para>
///
/// <para><b>The runners' control flow is not here, and that is on purpose.</b> Each runner keeps its
/// own <c>RunAsync</c>, its own phase sequence and — above all — its own <c>finally</c>. That block
/// silences the generator, restores autorange step-down, stops the acquisition it started and puts
/// the pipeline options back, in an order this project has documented at length; sharing it would
/// mean one measurement's edit could silence a different measurement's generator. Each also keeps
/// its own <c>BuildOptions</c>, which looks identical today and must be free to stop being so
/// without acquiring a boolean that reads as a mode switch.</para>
/// </summary>
internal static class ResponseCaptureSupport
{
    /// <summary>What the level probe should do with one iteration's correction.</summary>
    public enum ProbeCorrection
    {
        /// <summary>In range. Keep probing at the corrected amplitude.</summary>
        Continue,

        /// <summary>
        /// The correction asks for more than the generator has, for the first time. Clamp to the
        /// ceiling and <b>measure again</b> before believing it.
        /// </summary>
        ConfirmCeiling,

        /// <summary>The ceiling has been asked for twice running. Accept it and stop.</summary>
        AcceptCeiling,

        /// <summary>The correction asks for less than the generator's floor — the DUT's gain is too high to measure this way.</summary>
        BelowFloor,
    }

    /// <summary>
    /// One step of the output-referred level probe: where to move the generator, and whether the
    /// answer can be trusted yet.
    ///
    /// <para><b>Why a demand for the ceiling has to be confirmed.</b> The probe models the DUT as
    /// <c>measured = amplitude + gain</c> and corrects by the shortfall, so a capture that reads as
    /// silence produces an enormous shortfall and asks for far more level than the generator has.
    /// Acting on that reading once used to end the probe: it clamped to the maximum and broke out,
    /// so a <i>single</i> bad record decided the drive level for the whole measurement and the run
    /// went ahead playing full output into the DUT. That is the same class of fault as the 10.7 dB
    /// overshoot this probe's ordering already exists to prevent, and worse — it is the generator's
    /// ceiling rather than a few dB.</para>
    ///
    /// <para>Requiring the ceiling twice running costs one extra probe capture (~85 ms) and settles
    /// it with a measurement rather than a heuristic: a DUT that really is that quiet asks again and
    /// is believed, while a spurious reading is corrected back down by the very next iteration. The
    /// confirmation capture does play full output for its own duration, which is the price — but
    /// against an entire measurement at full output, which is what happened before.</para>
    ///
    /// <para>Step <i>size</i> is deliberately not what this checks. The probe starts at −40 dBV and
    /// a legitimate first correction toward an 18 dBV target is 58 dB, so a large jump is normal and
    /// a size limit would reject good readings. What is never legitimate is asking for more than the
    /// hardware has.</para>
    ///
    /// <para>The floor is not confirmed the same way, and does not need to be: it aborts the run
    /// with a message rather than driving the DUT, so a spurious reading there fails safe.</para>
    /// </summary>
    /// <param name="ceilingAlreadyAsked">Whether the previous iteration already demanded the ceiling.</param>
    public static (double Amplitude, ProbeCorrection Outcome) CorrectProbeAmplitude(
        double amplitude, double shortfall, bool ceilingAlreadyAsked, double maxDbv, double minDbv)
    {
        double corrected = amplitude + shortfall;

        if (corrected > maxDbv)
            return (maxDbv, ceilingAlreadyAsked ? ProbeCorrection.AcceptCeiling : ProbeCorrection.ConfirmCeiling);

        if (corrected < minDbv)
            return (corrected, ProbeCorrection.BelowFloor);

        return (corrected, ProbeCorrection.Continue);
    }

    /// <summary>
    /// The highest time-domain peak either channel saw, in dBV, or NaN when neither reported one.
    ///
    /// <para>A peak, not an RMS and not a spectral bin: an input range has to accommodate the
    /// largest instantaneous sample, and a chirp's crest factor is nothing like a sine's.
    /// <see cref="SpectrumMeters.PeakDbv"/> is already RMS-referenced (<c>peak/√2</c>), which is the
    /// convention <see cref="InputRangeSelector"/> and the device's own ranger both expect — see
    /// that type's remarks on why the reference app's raw-peak convention is not used here.</para>
    /// </summary>
    public static double PeakDbv(SpectrumSnapshot[] snapshots)
    {
        // The analyzer's peak, with any external input gain put back -- this only ever feeds
        // InputRangeSelector, whose ladder is in the analyzer's dBV. See SweepCaptureSupport.MeasuredPeakDbv.
        double peak = double.NaN;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is not { } meters)
                continue;

            peak = double.IsNaN(peak) ? meters.AnalyzerPeakDbv : Math.Max(peak, meters.AnalyzerPeakDbv);
        }

        return peak;
    }

    /// <summary>
    /// Both channels' complex spectra from one capture.
    ///
    /// <para>They must come from the same tick, which <see cref="SpectrumPipeline"/> guarantees by
    /// snapshotting every channel under one lock — the gain trace is a ratio between them, so a
    /// left record taken even one block before the right would report that offset as DUT phase.</para>
    /// </summary>
    public static (Complex[] Left, Complex[] Right) RequireBothChannels(
        SpectrumSnapshot[] snapshots, int fftSize, string measurementName)
    {
        int expected = (fftSize / 2) + 1;

        Complex[]? left = null;
        Complex[]? right = null;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Complex is null || snapshot.Complex.Length != expected)
                continue;

            if (snapshot.Channel == StreamChannel.Left)
                left = snapshot.Complex;
            else if (snapshot.Channel == StreamChannel.Right)
                right = snapshot.Complex;
        }

        if (left is null || right is null)
        {
            throw new InvalidOperationException(
                $"A {measurementName} capture needs both channels' spectra. The pipeline returned "
                    + $"{(left is null ? "no left" : "left")} and {(right is null ? "no right" : "right")}.");
        }

        return (left, right);
    }

    /// <summary>
    /// One channel's averaged level at one bin, in dBV, or NaN when that channel produced no
    /// spectrum. <c>Dbv</c> is the amplitude spectrum in the peak/root-two convention every dBV in
    /// this app uses, so for a tone that is coherent in the record the centre bin carries the whole
    /// amplitude and reading it is exact rather than approximate.
    /// </summary>
    public static double LevelAt(SpectrumSnapshot[] snapshots, StreamChannel channel, int bin)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Channel != channel)
                continue;

            return bin >= 0 && bin < snapshot.Dbv.Length ? snapshot.Dbv[bin] : double.NaN;
        }

        return double.NaN;
    }

    /// <summary>
    /// One channel's raw complex coefficient at one bin, or null when the spectrum was not retained
    /// (<see cref="SpectrumPipelineOptions.RetainComplexSpectrum"/>).
    ///
    /// <para>The last record's rather than an average: the pipeline's averager works on magnitudes
    /// and would discard the phase entirely. Both channels of one call come from the same tick, which
    /// <see cref="SpectrumPipeline"/> guarantees by snapshotting every channel under one lock, so a
    /// ratio between them is a real relationship and the analysis window cancels.</para>
    /// </summary>
    public static Complex? ComplexAt(SpectrumSnapshot[] snapshots, StreamChannel channel, int bin)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Channel != channel || snapshot.Complex is not { } complex)
                continue;

            return bin >= 0 && bin < complex.Length ? complex[bin] : null;
        }

        return null;
    }

    /// <summary>
    /// How long one record may take: the time to refill the window after the flush, plus the
    /// in-flight audio the flush discards and the settle time the session discards after it
    /// (<see cref="DeviceSessionService.CaptureDelayMs"/>), times three for slack. Loose on purpose —
    /// this exists to notice a wedged device, not to police timing.
    /// </summary>
        /// <param name="streamSettleMs">
    /// Samples the device will withhold at the start of this acquisition while its hardware
    /// settles (DeviceSessionService.StreamSettleMs). Added to the budget rather than ignored:
    /// those frames are genuinely not coming, and at 8192 points with one average the budget is
    /// 3.3 s against a 3 s settle, so leaving it out turns a settling card into a TimeoutException.
    /// </param>
    public static TimeSpan CaptureTimeout(double sampleRateHz, int fftSize, double captureDelayMs, double streamSettleMs = 0.0)
    {
        const double StimulusLatencySeconds = 0.1;

        double recordSeconds = fftSize / sampleRateHz;
        return TimeSpan.FromSeconds(((recordSeconds + StimulusLatencySeconds + (captureDelayMs / 1000.0)) * 3.0) + 2.0 + (streamSettleMs / 1000.0));
    }

    /// <summary>
    /// The chirp on both channels. Both, always: with the right input as the reference it needs the
    /// same stimulus fed straight back to it, and without it the right channel is a trace of its own.
    /// </summary>
    public static GeneratorConfig BuildGenerator(
        FrequencyResponseRequest request, double amplitudeDbv, int? repeatFrames = null) =>
        BuildChirpGenerator(
            amplitudeDbv, request.ChirpStartHz, request.ChirpEndHz, repeatFrames ?? request.FftSize);

    /// <summary>
    /// The same chirp stated in its own terms rather than a frequency-response request's, so the
    /// impedance measurement can build the identical stimulus without borrowing that request type.
    /// </summary>
    public static GeneratorConfig BuildChirpGenerator(
        double amplitudeDbv, double startFreqHz, double endFreqHz, int repeatFrames)
    {
        var chirp = new EchoChirpSettings
        {
            AmplitudeDbv = amplitudeDbv,
            StartFrequencyHz = startFreqHz,
            EndFrequencyHz = endFreqHz,
            Smoothing = SmoothingOctaves.None,

            // The whole measurement rests on this matching the analysis size exactly.
            RepeatFrames = repeatFrames,
        };

        var channel = new ChannelGeneratorConfig(Enabled: true, Tones: [])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = chirp,
        };

        // Bin snapping is for tones; a chirp has no single frequency to snap and the period is what
        // makes it coherent instead.
        return new GeneratorConfig(channel, channel) { SnapToBin = false };
    }

    /// <summary>
    /// Pink noise at a broadband RMS, on both channels — the impedance measurement's third stimulus.
    ///
    /// <para>Nothing here is periodic in the record and nothing needs to be: this stimulus is only
    /// ever analysed against a <i>measured</i> reference channel, so what matters is that every bin
    /// gets energy, not that the record holds a whole period of anything. That is also why the
    /// analysis window is Hann rather than the chirp's Rectangular — see
    /// <c>ImpedanceRunner.BuildOptions</c>.</para>
    /// </summary>
    public static GeneratorConfig BuildPinkNoiseGenerator(double levelDbv)
    {
        var channel = new ChannelGeneratorConfig(Enabled: true, Tones: [])
        {
            Kind = GeneratorKind.PinkNoise,
            Noise = new WhiteNoiseSettings { LevelDbv = levelDbv },
        };

        // Nothing to snap: bin snapping moves a tone onto a coherent bin, and noise has no tone.
        return new GeneratorConfig(channel, channel) { SnapToBin = false };
    }

    /// <summary>
    /// The stimulus spectrum the response is divided by: one period of the same sweep the DAC is
    /// playing, transformed the same way the capture is.
    ///
    /// <para>Generated from <see cref="ExponentialChirp"/>, which
    /// <c>Qa403.Device.Tests.ChirpFormulaAgreementTests</c> pins to the generator's own copy sample
    /// for sample. A drift between the two would be reported as DUT response and nothing would
    /// complain.</para>
    /// </summary>
    public static Complex[] BuildReferenceSpectrum(
        IFftBackend fft, FrequencyResponseRequest request, double amplitudeDbv, int? fftSize = null)
    {
        int size = fftSize ?? request.FftSize;

        var period = new double[size];
        ExponentialChirp.Fill(
            period,
            request.ChirpStartHz,
            request.ChirpEndHz,
            request.SampleRateHz,
            ExponentialChirp.PeakVoltsFromDbv(amplitudeDbv));

        var spectrum = new Complex[(size / 2) + 1];
        fft.Forward(period, spectrum);
        return spectrum;
    }

    /// <summary>
    /// The DUT's gain, in dB, as the <b>median</b> of the deconvolved response across the analysed
    /// band.
    ///
    /// <para>Median rather than mean: a mean in dB is pulled down hard by any null, and a mean of
    /// magnitudes is pulled up by any resonance, so either would make the probe converge to a level
    /// that is wrong by the shape of the DUT's own response.</para>
    ///
    /// <para>This is the whole-band figure; the level probe reads <see cref="ProbeGainDb"/>, which
    /// takes the same median over the bandwidth reference band instead. Over the whole band the
    /// median is weighted by linear bins toward the top of the span, so for a shaped DUT it lands
    /// wherever the span happens to end — for a phono stage at a 96 kHz span, near 48 kHz, some
    /// 27 dB below 1 kHz — and "0 dBV out" then names a level no one can point to on the curve.</para>
    /// </summary>
    public static double MedianGainDb(
        ReadOnlySpan<Complex> reference,
        ReadOnlySpan<Complex> response,
        int fftSize,
        FrequencyResponseRequest request)
    {
        var gains = GainsPerBin(reference, response, fftSize, request);
        return Median(gains.Select(g => g.GainDb));
    }

    /// <summary>
    /// The gain the output-referred level probe corrects against: the DUT's level <b>at the
    /// bandwidth reference</b>, so that "0 dBV out" means 0 dBV where the corners are measured
    /// from — the passband by default, which is where every datasheet quotes a level, or the peak
    /// under <see cref="BandwidthReferenceMode.Peak"/>. Before this the probe took the median over
    /// the whole analysed band (<see cref="MedianGainDb"/>), and against the simulator's RIAA stage
    /// two runs with reference bands of 50-100 Hz and 5-10 kHz drove the DUT identically — both to
    /// the generator's 18 dBV ceiling, since the whole-band median sat 27 dB below 1 kHz.
    ///
    /// <para>The probe's record is short (a 16384-point chirp is 11.7 Hz per bin at 192 kHz), so a
    /// narrow reference band may hold only a handful of bins; a median of a few bins is still the
    /// right figure. A band with <b>no</b> bins inside the analysed range falls back to the
    /// whole-band median and says so through <paramref name="fellBackToWholeBand"/>, the same way
    /// the corner search falls back to the peak — rather than quoting a level against a band it
    /// never measured.</para>
    /// </summary>
    public static double ProbeGainDb(
        ReadOnlySpan<Complex> reference,
        ReadOnlySpan<Complex> response,
        int fftSize,
        FrequencyResponseRequest request,
        out bool fellBackToWholeBand)
    {
        fellBackToWholeBand = false;
        var gains = GainsPerBin(reference, response, fftSize, request);
        if (gains.Count == 0)
            return double.NaN;

        var spec = request.BandwidthReferenceSpec;
        if (spec.Mode == BandwidthReferenceMode.Peak)
            return gains.Max(g => g.GainDb);

        double binHz = request.SampleRateHz / fftSize;
        var inBand = gains.Where(g => g.Bin * binHz >= spec.LowHz && g.Bin * binHz <= spec.HighHz).Select(g => g.GainDb).ToList();
        if (inBand.Count > 0)
            return Median(inBand);

        fellBackToWholeBand = true;
        return Median(gains.Select(g => g.GainDb));
    }

    /// <summary>
    /// The deconvolved gain at every analysed bin the sweep put energy into — the one H1 estimate
    /// both probe figures are read from.
    /// </summary>
    private static List<(int Bin, double GainDb)> GainsPerBin(
        ReadOnlySpan<Complex> reference,
        ReadOnlySpan<Complex> response,
        int fftSize,
        FrequencyResponseRequest request)
    {
        var gains = new List<(int Bin, double GainDb)>();

        double binHz = request.SampleRateHz / fftSize;
        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(reference.Length - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));
        if (lastBin <= firstBin)
            return gains;

        double maxPower = 0.0;
        for (int k = firstBin; k <= lastBin; k++)
            maxPower = Math.Max(maxPower, (reference[k] * Complex.Conjugate(reference[k])).Real);

        if (maxPower <= 0.0)
            return gains;

        double lambda = FrequencyResponseEstimator.RegularisationFraction * maxPower;

        gains.Capacity = lastBin - firstBin + 1;
        for (int k = firstBin; k <= lastBin; k++)
        {
            double xx = (reference[k] * Complex.Conjugate(reference[k])).Real;

            // Only where the sweep actually has energy. Elsewhere the ratio is noise over nothing,
            // and including it would drag the median toward the noise floor.
            if (xx < 1e-6 * maxPower)
                continue;

            gains.Add((k, FrequencyResponseEstimator.ToDb((response[k] * Complex.Conjugate(reference[k]) / (xx + lambda)).Magnitude)));
        }

        return gains;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.ToList();
        if (sorted.Count == 0)
            return double.NaN;

        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Turns the accumulated estimates into the curves the chart draws. Order matters: smooth the
    /// linear magnitudes, convert to dB, then find corners on the full-resolution smoothed trace,
    /// and only then decimate for display.
    /// </summary>
    /// <remarks>
    /// Takes finished results rather than the estimators that produced them, and that is not a
    /// stylistic choice: <see cref="FrequencyResponseEstimator.Complete"/> gates its working
    /// spectrum <b>in place</b> and is therefore not idempotent, so a caller that also wants the
    /// numbers behind the curves must call it exactly once and pass the result around. Taking
    /// estimators here made it easy to call it a second time and gate twice.
    /// </remarks>
    public static List<FrequencyResponseTrace> BuildTraces(
        FrequencyResponseRequest request,
        double generatorAmpDbv,
        int displayColumns,
        FrequencyResponseResult leftResult,
        FrequencyResponseResult rightResult,
        FrequencyResponseResult? gainResult)
    {
        List<FrequencyResponseTrace> traces =
        [
            // A channel's own response is reported as the level it would produce, in dBV, if driven
            // by a sine at the generator's amplitude -- which is what makes the "FR (dBV)" view read
            // in the same units as the FFT screen rather than as a bare ratio.
            //
            // No delay is put back on either: against the synthesised chirp the rotation the
            // estimator removed was the record's arbitrary start point in the periodic stimulus,
            // not a delay, so these carry excess delay only.
            BuildTrace(request, displayColumns, FrequencyResponseTraceKind.Left, leftResult, generatorAmpDbv, addBackDelayS: 0.0),
            BuildTrace(request, displayColumns, FrequencyResponseTraceKind.Right, rightResult, generatorAmpDbv, addBackDelayS: 0.0),
        ];

        // With the right input as the reference, gain is the true L/R transfer function. Without it,
        // there is nothing to compare against but the stimulus, so gain is the left channel's own
        // response as a ratio -- the same curve as the dBV one, shifted by the drive level.
        //
        // Only the gain estimator's LatencySamples is a real delay -- both of its spectra come from
        // one capture, so the rotation cancels and what it removed is the DUT's -- and that is the
        // one case where the delay curves get it back and read as physical delay.
        double gainDelayS = gainResult is null ? 0.0 : gainResult.LatencySamples / request.SampleRateHz;
        traces.Add(BuildTrace(request, displayColumns, FrequencyResponseTraceKind.Gain, gainResult ?? leftResult, offsetDb: 0.0, gainDelayS));

        return traces;
    }

    /// <param name="addBackDelayS">
    /// Added to every group- and phase-delay sample inside the analysed band: the delay the
    /// estimator removed from <paramref name="result"/>'s phase, when — and only when — that
    /// removal was of a real delay rather than of the record's arbitrary rotation.
    /// </param>
    public static FrequencyResponseTrace BuildTrace(
        FrequencyResponseRequest request,
        int displayColumns,
        FrequencyResponseTraceKind kind,
        FrequencyResponseResult result,
        double offsetDb,
        double addBackDelayS)
    {
        double binHz = request.BinHz;
        int binCount = result.Magnitude.Length;

        var smoothed = FractionalOctaveSmoother.Smooth(result.Magnitude, binHz, request.SmoothingDenominator);

        var traceDb = new double[binCount];
        for (int k = 0; k < binCount; k++)
            traceDb[k] = FrequencyResponseEstimator.ToDb(smoothed[k]) + offsetDb;

        // Corners are searched only where the stimulus actually put energy. Outside that the
        // estimate is a ratio of two near-zero numbers and would end every search on its first step.
        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(binCount - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));
        if (lastBin <= firstBin)
            lastBin = Math.Min(binCount - 1, firstBin + 1);

        var peak = BandwidthAnalyzer.FindPeak(traceDb, binHz, firstBin, lastBin);

        // One reference for both readings — see BandwidthAnalyzer.FindReference. The ±1 dB row is a
        // flatness figure and the −3 dB row a roll-off one, which is the whole reason they are not
        // the same search at two thresholds: against a passband reference a downward-only 1 dB
        // search walks straight up through an output transformer's leakage resonance without
        // stopping.
        var reference = BandwidthAnalyzer.FindReference(traceDb, binHz, request.BandwidthReferenceSpec, firstBin, lastBin);
        var oneDb = BandwidthAnalyzer.Find(traceDb, binHz, dropDb: 1.0, reference, symmetric: true, firstBin, lastBin);
        var threeDb = BandwidthAnalyzer.Find(traceDb, binHz, dropDb: 3.0, reference, symmetric: false, firstBin, lastBin);

        // Both delay curves at full resolution, before decimation, for the same reason the corners
        // are: a derivative of the column-sampled phase is aliased wherever columns are finer than
        // bins. The group delay's regression window is the smoothing setting's, clamped to the
        // analysed band; the phase delay's arbitrary whole turns (the unwrapper starts at DC and
        // walks through the sub-band junk) are anchored at the bandwidth reference *before* the
        // measured delay is put back -- anchoring after would be ambiguous by construction, since
        // a delayed DUT's physical phase runs through many turns in the passband.
        var groupDelayS = GroupDelay.FromUniformPhase(result.PhaseDeg, binHz, request.SmoothingDenominator, firstBin, lastBin);
        var phaseDelayS = GroupDelay.PhaseDelayUniform(result.PhaseDeg, binHz, firstBin, lastBin, anchorBin: reference.Index);
        for (int k = firstBin; k <= lastBin; k++)
        {
            groupDelayS[k] += addBackDelayS;
            phaseDelayS[k] += addBackDelayS;
        }

        int columns = Math.Min(displayColumns, lastBin - firstBin + 1);
        var minMax = new MinMax[columns];
        var centerBins = new double[columns];
        SpectrumDecimator.DecimateLogWithCenters(traceDb.AsSpan(0, lastBin + 1), minMax, centerBins, firstBin);

        var freqHz = new float[columns];
        var magMin = new float[columns];
        var magMax = new float[columns];
        var phaseDeg = new float[columns];
        var coherence = new float[columns];
        var groupDelay = new float[columns];
        var phaseDelay = new float[columns];

        for (int c = 0; c < columns; c++)
        {
            freqHz[c] = (float)(centerBins[c] * binHz);
            magMin[c] = (float)minMax[c].Min;
            magMax[c] = (float)minMax[c].Max;

            // Phase, coherence and the delays are sampled at the column's centre rather than
            // min/max'd: the min/max rule exists so a single-bin spike survives decimation, and none
            // of these has spikes worth preserving -- a min/max band on an unwrapped phase would just
            // be the column's slope drawn as noise, and the group delay has already been integrated
            // over its window.
            int centre = Math.Clamp((int)Math.Round(centerBins[c]), 0, result.PhaseDeg.Length - 1);
            phaseDeg[c] = (float)result.PhaseDeg[centre];
            coherence[c] = (float)result.Coherence[centre];
            groupDelay[c] = (float)groupDelayS[centre];
            phaseDelay[c] = (float)phaseDelayS[centre];
        }

        return new FrequencyResponseTrace(
            kind,
            firstBin * binHz,
            lastBin * binHz,
            freqHz,
            magMin,
            magMax,
            phaseDeg,
            coherence,
            groupDelay,
            phaseDelay,
            BandwidthReading.From(oneDb),
            BandwidthReading.From(threeDb),
            peak.LevelDb,
            peak.FreqHz,
            reference.LevelDb,
            reference.Mode,
            reference.FellBackToPeak);
    }
}
