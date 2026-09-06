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
        double peak = double.NaN;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Meters is not { } meters)
                continue;

            peak = double.IsNaN(peak) ? meters.PeakDbv : Math.Max(peak, meters.PeakDbv);
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
    /// How long one record may take: the time to refill the window after the flush, plus the
    /// in-flight audio the flush discards, times three for slack. Loose on purpose — this exists to
    /// notice a wedged device, not to police timing.
    /// </summary>
    public static TimeSpan CaptureTimeout(double sampleRateHz, int fftSize)
    {
        const double StimulusLatencySeconds = 0.1;

        double recordSeconds = fftSize / sampleRateHz;
        return TimeSpan.FromSeconds(((recordSeconds + StimulusLatencySeconds) * 3.0) + 2.0);
    }

    /// <summary>
    /// The chirp on both channels. Both, always: with the right input as the reference it needs the
    /// same stimulus fed straight back to it, and without it the right channel is a trace of its own.
    /// </summary>
    public static GeneratorConfig BuildGenerator(
        FrequencyResponseRequest request, double amplitudeDbv, int? repeatFrames = null)
    {
        var chirp = new EchoChirpSettings
        {
            AmplitudeDbv = amplitudeDbv,
            StartFrequencyHz = request.ChirpStartHz,
            EndFrequencyHz = request.ChirpEndHz,
            Smoothing = SmoothingOctaves.None,

            // The whole measurement rests on this matching the analysis size exactly.
            RepeatFrames = repeatFrames ?? request.FftSize,
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
    /// that is wrong by the shape of the DUT's own response. The median is the level the response
    /// actually sits at, which is what "output voltage" means.</para>
    /// </summary>
    public static double MedianGainDb(
        ReadOnlySpan<Complex> reference,
        ReadOnlySpan<Complex> response,
        int fftSize,
        FrequencyResponseRequest request)
    {
        double binHz = request.SampleRateHz / fftSize;
        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(reference.Length - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));
        if (lastBin <= firstBin)
            return double.NaN;

        double maxPower = 0.0;
        for (int k = firstBin; k <= lastBin; k++)
            maxPower = Math.Max(maxPower, (reference[k] * Complex.Conjugate(reference[k])).Real);

        if (maxPower <= 0.0)
            return double.NaN;

        double lambda = FrequencyResponseEstimator.RegularisationFraction * maxPower;

        var gains = new List<double>(lastBin - firstBin + 1);
        for (int k = firstBin; k <= lastBin; k++)
        {
            double xx = (reference[k] * Complex.Conjugate(reference[k])).Real;

            // Only where the sweep actually has energy. Elsewhere the ratio is noise over nothing,
            // and including it would drag the median toward the noise floor.
            if (xx < 1e-6 * maxPower)
                continue;

            gains.Add(FrequencyResponseEstimator.ToDb((response[k] * Complex.Conjugate(reference[k]) / (xx + lambda)).Magnitude));
        }

        if (gains.Count == 0)
            return double.NaN;

        gains.Sort();
        return gains[gains.Count / 2];
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
            BuildTrace(request, displayColumns, FrequencyResponseTraceKind.Left, leftResult, generatorAmpDbv),
            BuildTrace(request, displayColumns, FrequencyResponseTraceKind.Right, rightResult, generatorAmpDbv),
        ];

        // With the right input as the reference, gain is the true L/R transfer function. Without it,
        // there is nothing to compare against but the stimulus, so gain is the left channel's own
        // response as a ratio -- the same curve as the dBV one, shifted by the drive level.
        traces.Add(BuildTrace(request, displayColumns, FrequencyResponseTraceKind.Gain, gainResult ?? leftResult, offsetDb: 0.0));

        return traces;
    }

    public static FrequencyResponseTrace BuildTrace(
        FrequencyResponseRequest request,
        int displayColumns,
        FrequencyResponseTraceKind kind,
        FrequencyResponseResult result,
        double offsetDb)
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
        var oneDb = BandwidthAnalyzer.Find(traceDb, binHz, dropDb: 1.0, firstBin, lastBin);
        var threeDb = BandwidthAnalyzer.Find(traceDb, binHz, dropDb: 3.0, firstBin, lastBin);

        int columns = Math.Min(displayColumns, lastBin - firstBin + 1);
        var minMax = new MinMax[columns];
        var centerBins = new double[columns];
        SpectrumDecimator.DecimateLogWithCenters(traceDb.AsSpan(0, lastBin + 1), minMax, centerBins, firstBin);

        var freqHz = new float[columns];
        var magMin = new float[columns];
        var magMax = new float[columns];
        var phaseDeg = new float[columns];
        var coherence = new float[columns];

        for (int c = 0; c < columns; c++)
        {
            freqHz[c] = (float)(centerBins[c] * binHz);
            magMin[c] = (float)minMax[c].Min;
            magMax[c] = (float)minMax[c].Max;

            // Phase and coherence are sampled at the column's centre rather than min/max'd: the
            // min/max rule exists so a single-bin spike survives decimation, and neither of these
            // has spikes worth preserving -- a min/max band on an unwrapped phase would just be the
            // column's slope drawn as noise.
            int centre = Math.Clamp((int)Math.Round(centerBins[c]), 0, result.PhaseDeg.Length - 1);
            phaseDeg[c] = (float)result.PhaseDeg[centre];
            coherence[c] = (float)result.Coherence[centre];
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
            BandwidthReading.From(oneDb),
            BandwidthReading.From(threeDb),
            peak.LevelDb,
            peak.FreqHz);
    }
}
