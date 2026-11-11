using System.Numerics;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Services;

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
    /// The stimulus on both channels. Both, always: with the right input as the reference it needs
    /// the same stimulus fed straight back to it, and without it the right channel is a trace of its
    /// own.
    /// </summary>
    public static GeneratorConfig BuildGenerator(
        FrequencyResponseRequest request, double amplitudeDbv, int? repeatFrames = null)
    {
        int period = repeatFrames ?? request.FftSize;

        return request.UsesNoise
            ? BuildNoiseGenerator(amplitudeDbv, period, request.NoiseCutoffHz, request.NoiseShape)
            : BuildChirpGenerator(amplitudeDbv, request.ChirpStartHz, request.ChirpEndHz, period);
    }

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
    /// Periodic pink noise at a broadband RMS, on both channels — the impedance measurement's third
    /// stimulus, and ARTA's "PN Pink".
    ///
    /// <para><b>One whole period per record</b>, which is the chirp's arrangement applied to a noise:
    /// <paramref name="periodFrames"/> is the analysis length, so every record is excited by the same
    /// deterministic amplitude at every bin and the analysis window is <b>Rectangular</b> — see
    /// <c>ImpedanceRunner.BuildOptions</c>. It used to be a continuous random stream, on the
    /// reasoning that a measurement against a <i>measured</i> reference channel only needs energy in
    /// every bin and not a whole period of anything. True, and not enough: a random stream's bins are
    /// Rayleigh distributed about their mean, so one in ten lands 8.8 dB down in any given record and
    /// one in a hundred 19 dB down, and on a divider — where the DUT channel sits 40 dB below the
    /// reference — those are the bins that read wrong. Measured on a real fixture 2026-10-21, the
    /// bins that lost coherence were the ones carrying external interference, and the curve spiked
    /// wherever a momentarily weak stimulus bin met one.</para>
    /// </summary>
    /// <param name="periodFrames">The analysis record length, so the record holds exactly one period.</param>
    /// <param name="cutoffHz">Where the sequence stops being pink: flat below, −3 dB per octave above.</param>
    public static GeneratorConfig BuildPinkNoiseGenerator(double levelDbv, int periodFrames, double cutoffHz) =>
        BuildNoiseGenerator(levelDbv, periodFrames, cutoffHz, PeriodicNoiseShape.Pink);

    /// <summary>
    /// The same sequence at any of its shapes — ARTA's PN list. See
    /// <see cref="BuildPinkNoiseGenerator"/> for why it is periodic over the record, which is the
    /// half that matters and is true of every shape alike.
    /// </summary>
    public static GeneratorConfig BuildNoiseGenerator(
        double levelDbv, int periodFrames, double cutoffHz, PeriodicNoiseShape shape)
    {
        var channel = new ChannelGeneratorConfig(Enabled: true, Tones: [])
        {
            Kind = GeneratorKind.PinkNoise,
            Noise = new WhiteNoiseSettings
            {
                LevelDbv = levelDbv,
                PeriodFrames = periodFrames,
                CutoffHz = cutoffHz,
                Shape = shape,
            },
        };

        // Nothing to snap: bin snapping moves a tone onto a coherent bin, and noise has no tone.
        return new GeneratorConfig(channel, channel) { SnapToBin = false };
    }

    /// <summary>
    /// The stimulus spectrum the response is divided by: one period of whatever the DAC is playing,
    /// transformed the same way the capture is.
    ///
    /// <para>Every stimulus here is a pure function of the frame index with a period of exactly one
    /// record, which is what makes this possible at all — the sweep comes from
    /// <see cref="ExponentialChirp"/> and the noises from <see cref="PeriodicNoise"/>, and in both
    /// cases <c>Tonebench.Device</c> builds the played audio from the same code. A drift between the
    /// two would be reported as DUT response and nothing would complain, which is why
    /// <c>ChirpFormulaAgreementTests</c> pins the sweep sample for sample and why the noise is read
    /// from <see cref="PeriodicNoise"/> here rather than rebuilt.</para>
    ///
    /// <para>Note the two amplitude conventions, which are the generator's own: the sweep's level is
    /// a <i>peak</i> and a noise's is a broadband <i>RMS</i>. Both arrive here as the dBV figure the
    /// generator was configured with, so the reference carries the same scale as the stimulus either
    /// way — and the level probe measures the DUT rather than trusting either number.</para>
    /// </summary>
    public static Complex[] BuildReferenceSpectrum(
        IFftBackend fft, FrequencyResponseRequest request, double amplitudeDbv, int? fftSize = null)
    {
        int size = fftSize ?? request.FftSize;

        var period = new double[size];
        if (request.UsesNoise)
        {
            var noise = PeriodicNoise.For(size, request.SampleRateHz, request.NoiseCutoffHz, request.NoiseShape);
            double rms = Math.Pow(10.0, amplitudeDbv / 20.0);
            for (int i = 0; i < size; i++)
                period[i] = rms * noise.Sample(i);
        }
        else
        {
            ExponentialChirp.Fill(
                period,
                request.ChirpStartHz,
                request.ChirpEndHz,
                request.SampleRateHz,
                ExponentialChirp.PeakVoltsFromDbv(amplitudeDbv));
        }

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

    /// <summary>
    /// The value at <paramref name="fraction"/> of the way up the sorted values, nearest-rank, or
    /// NaN for an empty set — <see cref="Median"/> with the rank chosen by the caller.
    /// </summary>
    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.ToList();
        if (sorted.Count == 0)
            return double.NaN;

        sorted.Sort();
        return sorted[Math.Clamp((int)(fraction * (sorted.Count - 1)), 0, sorted.Count - 1)];
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
    /// How much clock drift the finished estimates still carry, or null where the question cannot
    /// honestly be asked — see <see cref="DriftResidualReading"/> for why it is worth asking.
    ///
    /// <para><b>It reads the reference channel and nothing else, and that restriction is the whole
    /// design.</b> A run with the right input as the reference — the frequency-response panel's own
    /// default — feeds that input the stimulus rather than the DUT's output, so its response against
    /// the <i>synthetic</i> chirp holds the clock ramp and no DUT at all. Without a measured
    /// reference there is only the DUT's own channel, and <b>that is not good enough</b>: measured
    /// against synthetic phase curves, a bare one-pole low-pass at 2 kHz reads as <b>133 ppm</b> of
    /// drift and a 200 Hz pole as 51 ppm, against the 104 ppm this project's bench actually has. The
    /// reason is not numerical: over three decades <c>f</c> and <c>f·ln f</c> are similar enough that
    /// a saturating phase curve really is well described by a large ramp plus a large offsetting
    /// delay. So a reading off a DUT-bearing trace could not be told from a stale figure, and the
    /// honest answer is to make none. A realistic analyzer path — linear-phase converter filters, a
    /// gentle analog reconstruction pole, DC coupling — biases this by about <b>2.5 ppm</b>, which is
    /// what makes the reference case worth having.</para>
    ///
    /// <para><b>Never the gain trace</b>, which is the same rule the correction itself follows and
    /// for the same reason: L/R is two captured channels on one clock, so the ramp has already
    /// cancelled out of it and it carries no information about ε in either direction.</para>
    ///
    /// <para><b>The band is gated on flatness, not on a noise floor</b>, and
    /// <see cref="ResidualFlatnessDb"/>'s effect is measured rather than assumed. A path's phase
    /// curvature travels with its magnitude roll-off, so dropping the bins where the magnitude has
    /// left the band's own middle drops exactly the region that would contaminate the fit — on a
    /// 2 kHz pole that takes the reading from 133 ppm to 47, and on a 200 Hz pole from 51 to 5. It
    /// cannot rescue a <i>brickwall</i>, whose phase curves far below a magnitude that is still flat,
    /// which is the residual risk this method carries and the reason the 2.5 ppm above is a
    /// measurement rather than a bound. The gate also subsumes a noise floor: a bin the stimulus
    /// never reached is nowhere near the middle of the band.</para>
    ///
    /// <para><b>The weighting is uniform over what survives</b>, deliberately unlike
    /// <see cref="FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg"/>'s magnitude weighting. What is
    /// being fitted is a shape <i>across</i> the band, so leverage should come from spanning it
    /// rather than from the loudest part of it. Weighting by 1/f, by 1/f² and by magnitude were each
    /// measured against the same phase curves, and every one of them was worse.</para>
    /// </summary>
    public static DriftResidualReading? EstimateDriftResidual(
        FrequencyResponseRequest request,
        FrequencyResponseResult leftResult,
        FrequencyResponseResult rightResult)
    {
        // Chirp only: the ramp lives on the sweep's own frequency-to-time map, and a periodic-noise
        // stimulus has none -- the same restriction the correction itself carries.
        if (request.Stimulus != ResponseStimulus.Chirp) return null;

        // The reference channel or nothing. See the remarks: a DUT in the path can manufacture more
        // fake drift than this bench has real drift.
        if (!request.RightChannelIsReference) return null;

        if (ChirpTimeMap.For(request.SampleRateHz, request.FftSize, request.ChirpStartHz, request.ChirpEndHz)
            is not { } map) return null;

        double binHz = request.BinHz;
        int binCount = rightResult.Magnitude.Length;
        if (binCount < 4 || binHz <= 0) return null;

        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(binCount - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));
        if (lastBin - firstBin + 1 < ChirpDriftResidual.MinBins) return null;

        var (weight, fitFrom, fitTo) = FlatBand(rightResult.Magnitude, firstBin, lastBin);
        if (weight is null) return null;

        if (ChirpDriftResidual.Fit(rightResult.PhaseDeg, weight, binHz, map, fitFrom, fitTo)
            is not { } fit) return null;

        // The NaN-truncates-the-whole-response trap: System.Text.Json refuses to write one part way
        // through serializing, and the client discards the entire status rather than one field. A
        // reading that cannot be reported is reported as absent.
        if (!double.IsFinite(fit.ResidualPpm) || !double.IsFinite(fit.SigmaPpm) || !double.IsFinite(fit.FitRmsDeg))
            return null;

        double applied = request.ClockDrift is not null ? request.ClockDriftPpm : 0.0;
        if (!double.IsFinite(applied)) applied = 0.0;

        // The slip in samples is exactly |ppm| * 1e-6 * FftSize -- the sample rate cancels -- which
        // is why the record length is the only other figure the wording needs.
        return new DriftResidualReading(
            fit.ResidualPpm, applied, fit.SigmaPpm, fit.FitRmsDeg, fit.Bins,
            fitFrom * binHz, fitTo * binHz,
            Math.Abs(fit.ResidualPpm) * 1e-6 * request.FftSize,
            request.FftSize / request.SampleRateHz);
    }

    /// <summary>
    /// How far from the band's strong part a bin's magnitude may sit and still be fitted. Loose
    /// enough that a healthy reference path keeps its whole band — the figure is not about accuracy
    /// of level, it is about staying out of a roll-off's phase.
    /// </summary>
    private const double ResidualFlatnessDb = 3.0;

    /// <summary>
    /// Which magnitude the gate is measured from. <b>Not the median</b>, which was the first attempt
    /// and is wrong in exactly the case the gate exists for: bins are uniform in frequency, so on a
    /// path that rolls off the median bin is part way <i>down the slope</i> and a window around it
    /// selects the slope rather than the passband — measured, a one-pole at 2.4 kHz kept 8 to
    /// 16 kHz, which is all roll-off and no flat band at all. The ninetieth percentile sits in the
    /// strong part of any response whose passband is most of a decade wide, and on a flat path it is
    /// indistinguishable from the median. Not the <i>maximum</i> either: one noisy bin would then
    /// decide the whole band.
    /// </summary>
    private const double ResidualFlatnessPercentile = 0.90;

    /// <summary>
    /// The flat part of the band: 1 where a bin is worth fitting and 0 where it is not, plus the
    /// first and last surviving bin. The weights are null when too few survive.
    /// </summary>
    private static (double[]? Weight, int First, int Last) FlatBand(
        double[] magnitude, int firstBin, int lastBin)
    {
        double strong = Percentile(
            Enumerable.Range(firstBin, lastBin - firstBin + 1).Select(k => magnitude[k]),
            ResidualFlatnessPercentile);
        if (!double.IsFinite(strong) || strong <= 0) return (null, 0, 0);

        double lo = strong * Math.Pow(10.0, -ResidualFlatnessDb / 20.0);
        double hi = strong * Math.Pow(10.0, ResidualFlatnessDb / 20.0);

        var weight = new double[magnitude.Length];
        int usable = 0, first = int.MaxValue, last = -1;

        for (int k = firstBin; k <= lastBin; k++)
        {
            double m = magnitude[k];
            if (!double.IsFinite(m) || m < lo || m > hi) continue;

            weight[k] = 1.0;
            usable++;
            first = Math.Min(first, k);
            last = k;
        }

        return usable >= ChirpDriftResidual.MinBins ? (weight, first, last) : (null, 0, 0);
    }

    /// <summary>
    /// Puts the reading in the log, which is the half of it that survives the process.
    ///
    /// <para>A figure that only ever reaches a panel cannot answer for a measurement sent in
    /// afterwards, and "the phase looks wrong" is exactly how a stale drift figure is reported. One
    /// line per pass, at Information: it names what the run corrected by, what is left, and the two
    /// figures that say how much the second one is worth.</para>
    /// </summary>
    public static void LogDriftResidual(ILogger logger, DriftResidualReading? reading)
    {
        if (reading is null) return;

        logger.LogInformation(
            "Clock drift residual: {Residual:+0.00;-0.00;0.00} ppm left after correcting " +
            "{Applied:+0.00;-0.00;0.00} ppm ({Slip:0.00} samples over a {Record:0.00} s record), " +
            "from {Bins} reference-channel bins over {From:0}-{To:0} Hz, fit missed by " +
            "{Rms:0.00} deg rms (+/-{Sigma:0.000} ppm)",
            reading.ResidualPpm, reading.AppliedPpm, reading.SlipSamples, reading.RecordSeconds,
            reading.Bins, reading.FromHz, reading.ToHz, reading.FitRmsDeg, reading.SigmaPpm);
    }

    /// <summary>
    /// How far under the band's own middle a bin may sit and still be fitted. Generous of the
    /// signal and strict about the noise: the estimator holds the phase below 120 dB under the
    /// trace's peak, so this cannot admit a held bin, while 40 dB of signal-to-noise is well under
    /// a degree of phase error and so costs the fit nothing.
    /// </summary>
    private const double ResidualWeightFloorDb = 40.0;

    /// <summary>1 where the bin is worth fitting and 0 where it is not, or null when too few are.</summary>
    private static double[]? BuildResidualWeights(double[] magnitude, int firstBin, int lastBin)
    {
        double median = Median(Enumerable.Range(firstBin, lastBin - firstBin + 1).Select(k => magnitude[k]));
        if (!double.IsFinite(median) || median <= 0) return null;

        double floor = median * Math.Pow(10.0, -ResidualWeightFloorDb / 20.0);
        var weight = new double[magnitude.Length];
        int usable = 0;

        for (int k = firstBin; k <= lastBin; k++)
        {
            if (!double.IsFinite(magnitude[k]) || magnitude[k] < floor) continue;
            weight[k] = 1.0;
            usable++;
        }

        return usable >= ChirpDriftResidual.MinBins ? weight : null;
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

        // Corners are searched only where the stimulus actually put energy. Outside that the
        // estimate is a ratio of two near-zero numbers and would end every search on its first step
        // — and a smoothing band reaching past the top of it lifts the last columns toward that same
        // nothing, which is why this is computed before the smoothing rather than after.
        int firstBin = Math.Max(1, (int)Math.Ceiling(request.AnalysisLowHz / binHz));
        int lastBin = Math.Min(binCount - 1, (int)Math.Floor(request.AnalysisHighHz / binHz));
        if (lastBin <= firstBin)
            lastBin = Math.Min(binCount - 1, firstBin + 1);

        var band = new FractionalOctaveSmoother.Band(firstBin, lastBin);
        var smoothed = FractionalOctaveSmoother.Smooth(result.Magnitude, binHz, request.SmoothingDenominator, band);

        var traceDb = new double[binCount];
        for (int k = 0; k < binCount; k++)
            traceDb[k] = FrequencyResponseEstimator.ToDb(smoothed[k]) + offsetDb;

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

        // The phase column over the same bands the magnitude is smoothed over, weighted by magnitude
        // -- see FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg, and note it is a weighted mean
        // rather than the vector mean the impedance chart's phase takes: this one is unwrapped, and
        // the turns it carries are what the anchor below and the client's restore-delay checkbox act
        // on. Both delay curves are read off the *unsmoothed* phase above, which is deliberate: the
        // group delay runs its own least-squares regression over this very window, and smoothing its
        // input first would apply that window twice.

        // The phase curve is anchored at the same bin, for the reason the phase delay is: the
        // unwrapper starts at bin 0 and walks the sub-band junk, so what it hands out carries an
        // arbitrary whole number of turns -- a constant that says nothing about the DUT and that
        // the ±180° axis cannot show. Measured on a real amplifier (QA403 1077527945, 1 W into
        // 8 Ohm, chirp from 2 Hz): a response flat to a decibel read −467° at 10 Hz and −562° at
        // 90 kHz, i.e. a curve with 95° of real shape in it, sitting a turn and a half off the
        // bottom of the plot with nothing drawn anywhere. Anchored, the same capture reads −107°
        // to −202° about an inverting amplifier's own −180°.
        //
        // Before the add-back, never after: the delay is restored on the client, and a DUT with a
        // real delay runs through many turns in its passband, which is exactly what anchoring must
        // not delete. Group delay is a derivative and so is immune either way.
        var smoothedPhaseDeg = FractionalOctaveSmoother.SmoothUnwrappedPhaseDeg(
            result.PhaseDeg, result.Magnitude, binHz, request.SmoothingDenominator, band);

        // Taken from the smoothed curve, because that is the curve the anchor has to land in
        // (-180, 180]; whole turns, so the two agree except within a degree of a turn boundary.
        double anchorTurnsDeg = GroupDelay.WholeTurnsDeg(smoothedPhaseDeg[reference.Index]);
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
            phaseDeg[c] = (float)(smoothedPhaseDeg[centre] - anchorTurnsDeg);
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
