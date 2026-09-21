using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.App.Services;

/// <summary>One planned point of a stepped sweep: the coherent tone to play, the bin it lands on, and the size it is coherent for.</summary>
internal readonly record struct SteppedStepPlan(double FrequencyHz, int Bin, int FftSize);

/// <summary>
/// The mechanics a stepped-sine measurement is made of, shared by <see cref="SteppedResponseRunner"/>
/// and <see cref="CrosstalkRunner"/> the way <see cref="SweepCaptureSupport"/> is shared by the two
/// distortion sweeps: the frequency grid, the tone config, the capture that outruns the stimulus
/// change, the level and input-range probes, and one point's trace. Static helpers rather than a base
/// class, so each runner keeps its own state, its own request record (pinned by its endpoint tests)
/// and its own <c>finally</c> — the control flow is the part of a runner that must stay readable in
/// one place. Only the mechanics live here.
/// </summary>
internal static class SteppedCaptureSupport
{
    /// <summary>Where the level probe starts, and the level below which it gives up. Both from the previous app.</summary>
    public const double ProbeStartDbv = -40.0;

    /// <summary>Most passes the probe will make. Each is a full capture, so this is a real time cost.</summary>
    public const int MaxProbeIterations = 5;

    /// <summary>How close to the requested level the probe has to get before it stops.</summary>
    public const double ProbeToleranceDb = 0.5;

    public const double ProbeFrequencyHz = 1000.0;

    /// <summary>Longest record either probe will use. Both want a level, not resolution, and at the sweep's own sizes each pass would cost seconds.</summary>
    public const int MaxProbeFftSize = 16384;

    /// <summary>
    /// Below this the probe is not measuring a quiet DUT, it is measuring nothing.
    ///
    /// <para>The analyzer's own residual is about −161 dBV per root hertz at its most sensitive
    /// input range, which integrates to far more than this across any band, so a reading under
    /// −120 dBV means the signal path is broken — nothing connected, the DUT unpowered, the wrong
    /// channel — and not that the DUT is very quiet. Worth a hard stop rather than a correction,
    /// because the correction is what makes it dangerous: an output-referred probe that reads
    /// silence computes an enormous shortfall and winds the generator all the way to the DAC's
    /// ceiling, which is full scale into whatever <em>is</em> connected.</para>
    /// </summary>
    public const double ProbeSilenceDbv = -120.0;

    /// <summary>
    /// Fresh signal between two averages, as a fraction of one record — Welch's 50 %, exactly as
    /// <see cref="ThdSweepRunner"/> uses it. Here as there, an average that adds nothing is time
    /// added to every single point.
    /// </summary>
    public const double AverageHopFraction = 0.5;

    /// <summary>Ranges closer than this count as the same one — the ladder is 6 dB apart, so this only absorbs float noise.</summary>
    public const double RangeEpsilonDb = 0.01;

    /// <summary>
    /// The points a stepped run will measure, in order: a log-spaced grid, each frequency snapped
    /// onto a coherent bin of the size it will be analyzed at, de-duplicated where the spacing is
    /// finer than the bins.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grid has fewer than two distinct points.</exception>
    public static SteppedStepPlan[] BuildSteps(
        double startFreqHz, double endFreqHz, int stepsPerOctave, double sampleRateHz, double maxHz,
        bool adaptive, int fixedFftSize, Func<double, int> fftSizeAt)
    {
        var desired = SweepPoints.LogPerOctave(startFreqHz, endFreqHz, stepsPerOctave);

        SteppedStepPlan[] steps = adaptive
            ? [.. SweepPoints.SnapAndDedupeAdaptive(desired, sampleRateHz, fftSizeAt, maxHz)
                .Select(s => new SteppedStepPlan(s.FrequencyHz, BinOf(s.FrequencyHz, s.FftSize, sampleRateHz), s.FftSize))]
            : [.. SweepPoints.SnapAndDedupe(desired, sampleRateHz, fixedFftSize, maxHz)
                .Select(t => new SteppedStepPlan(t.FrequencyHz, t.Bin, fixedFftSize))];

        if (steps.Length < 2)
        {
            throw new ArgumentOutOfRangeException(
                "request",
                $"A response needs at least two distinct points below {maxHz:0} Hz; this grid produced {steps.Length}.");
        }

        return steps;
    }

    /// <summary>
    /// The bin a snapped tone sits on. Exact by construction — <see cref="SweepPoints"/> already
    /// rounded the frequency onto a bin centre for this size — and it is deliberately this rather
    /// than the peak the analyzer located: a peak search can lock onto mains hum or a DUT's own
    /// oscillation, and reading the level off the wrong bin is invisible in the result.
    /// </summary>
    public static int BinOf(double freqHz, int fftSize, double sampleRateHz) =>
        (int)Math.Round(freqHz * fftSize / sampleRateHz);

    /// <summary>
    /// The pipeline settings for a stepped capture. Two differ from
    /// <c>ThdSweepRunner.BuildSweepOptions</c> and both are load-bearing:
    /// <see cref="SpectrumPipelineOptions.RetainComplexSpectrum"/>, because the phase is read off the
    /// complex bin, and both channels always processed, because a gain or crosstalk trace is a ratio
    /// between them and the two have to come from the same tick.
    ///
    /// <para><c>Averaging</c> follows the same rule the sweep uses: a single average is "no
    /// averaging", not "Linear with count 1". <see cref="SpectrumAverager"/> treats count ≤ 1 as
    /// <see cref="AveragingMode.Off"/> and leaves its Count at 0, so asking for Linear would leave
    /// the single-shot completion check false forever and every point would time out waiting for a
    /// capture that can never complete.</para>
    /// </summary>
    public static SpectrumPipelineOptions BuildSteppedOptions(
        SpectrumPipelineOptions current, int fftSize, WindowType window, int averageCount, double startFreqHz, double sampleRateHz) =>
        current with
        {
            FftSize = fftSize,
            Window = window,
            Averaging = averageCount > 1 ? AveragingMode.Linear : AveragingMode.Off,
            AverageCount = averageCount,
            ProcessLeft = true,
            ProcessRight = true,
            SingleShot = true,
            RetainComplexSpectrum = true,
            // A runner measures unweighted whatever the FFT screen has selected: a sweep's noise
            // and THD+N figures are defined over the plain band, and the weighting is restored
            // with the rest of the saved options afterwards.
            Weighting = WeightingSpec.None,

            // Nothing here reads a preview, and at these FFT sizes it is not free.
            CapturePreview = false,

            AverageHopFraction = AverageHopFraction,

            // The meters are only read for their peak (the ranging predictor), but the peak comes
            // with a located fundamental, and the default 200 Hz search floor exists to keep mains
            // hum from being mistaken for one. A run starting at 20 Hz would otherwise fail to
            // locate its own tone for the whole bottom decade and lose its range predictor with it.
            FundamentalMinSearchHz = Math.Min(current.FundamentalMinSearchHz, startFreqHz / 2.0),
            HarmonicMaxFreqHz = sampleRateHz / 2.0,
        };

    /// <summary>One coherent tone on the chosen output(s), the other silent.</summary>
    public static GeneratorConfig ToneConfig(double freqHz, double ampDbv, bool left, bool right)
    {
        var tone = new ChannelGeneratorConfig(true, [new GeneratorTone(freqHz, ampDbv)]) { Kind = GeneratorKind.Tones };
        return new GeneratorConfig(
            left ? tone : ChannelGeneratorConfig.Off,
            right ? tone : ChannelGeneratorConfig.Off)
        {
            // The frequency is already coherent; snapping again is idempotent and keeps the device's
            // reported tone identical to the one this run recorded.
            SnapToBin = true,
        };
    }

    /// <summary>
    /// Applies a stimulus and returns the analysis of a record that holds <em>only</em> that
    /// stimulus.
    ///
    /// <para><b>The record armed by the stimulus change is thrown away, not measured</b> — the same
    /// rule, for the same reason, as <see cref="FrequencyResponseRunner.MeasureOnceAsync"/>.
    /// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/> discards the audio already queued
    /// for the DAC, but that is the <i>play</i> latency, not the round trip back through the DUT and
    /// the ADC, so some pre-change audio still reaches the front of the next record. A record that is
    /// part old tone and part new one reads the new tone's bin low by whatever fraction of the record
    /// the old tone occupied.</para>
    ///
    /// <para><see cref="ThdSweepRunner"/> tolerates exactly this, and is right to: THD is a ratio of
    /// harmonics to the fundamental <i>within one record</i>, so a level error common to both cancels.
    /// A stepped level measurement reads an absolute level, so nothing cancels.</para>
    ///
    /// <para>The residue is a fixed number of frames, so the damage scales as 1/<paramref name="fftSize"/>
    /// — which is why it showed up as "spikes only with adaptive resolution on". A fixed 192 kHz /
    /// 131072 run gives every point a 683 ms record and the residue is a per-cent of it; adaptive
    /// sizing takes the top of the band down to 4096 points (21 ms at 192 kHz), where the same
    /// residue is most of the record. Measured on the simulator before this: 6.0 dB low at 996 Hz /
    /// 8192, and a total miss (−205 dBV) at 2836 Hz / 4096, on a plain loopback.</para>
    ///
    /// <para>Waiting one whole record is the only bound available that does not depend on a latency
    /// figure the app never measures. It costs one extra record per point.</para>
    /// </summary>
    public static async Task<SpectrumSnapshot[]> CaptureWithStimulusAsync(
        DeviceSessionService session, SpectrumPipeline pipeline, GeneratorConfig generator, int fftSize, TimeSpan timeout, CancellationToken ct)
    {
        // Strictly before ConfigureGeneratorAsync, which is what flushes. Changing FftSize makes the
        // acquisition thread allocate replacement ring buffers, and doing that after the flush would
        // discard the flush's own state — the buffers it emptied are not the buffers the next record
        // fills.
        if (pipeline.Options.FftSize != fftSize)
            pipeline.Options = pipeline.Options with { FftSize = fftSize };

        // The throwaway is one *record*, not one averaged capture: all it has to do is carry the ring
        // past the samples that still hold the previous tone, and averaging it would pay the whole
        // average count twice per point. Averaging off makes SingleShotComplete fire on the first
        // completed record instead.
        var measurementOptions = pipeline.Options;
        pipeline.Options = measurementOptions with { Averaging = AveragingMode.Off, AverageCount = 1 };

        // Arms the throwaway. Do NOT add a pipeline.Flush(0) straight after this call:
        // SampleRingBuffer.Reset(0) would clear the pending in-flight discard counter, and the step
        // would land back inside the very record the flush existed to protect.
        await session.ConfigureGeneratorAsync(generator, ct);
        await WaitForCaptureAsync(pipeline, timeout, ct);

        // Restored before the flush, which is what resets the averagers — the measured capture has
        // to start its averaging from empty, and from the settings the run asked for.
        pipeline.Options = measurementOptions;

        // Buffers emptied, nothing discarded: the stimulus has not changed since the throwaway
        // above, so there is no pre-change audio left in flight and a non-zero discard would throw
        // away good samples for nothing.
        pipeline.Flush(0);
        await WaitForCaptureAsync(pipeline, timeout, ct);

        return pipeline.LatestSnapshots();
    }

    /// <summary>Polls for the single-shot capture; <see cref="SweepCaptureSupport.WaitForSingleShotAsync"/> with this measurement's message.</summary>
    public static async Task WaitForCaptureAsync(SpectrumPipeline pipeline, TimeSpan timeout, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (!pipeline.SingleShotComplete)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"A stepped response point did not complete within {timeout.TotalSeconds:0.0} s. The device may have stopped streaming.");
            }

            await Task.Delay(SweepCaptureSupport.CapturePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long one point may take: the time to refill the FFT window after the flush (including the
    /// discarded in-flight audio), plus the averages, times three for slack. Generous on purpose —
    /// this exists to notice a wedged device, not to police timing.
    /// </summary>
    public static TimeSpan CaptureTimeout(int fftSize, double sampleRateHz, int averageCount, double settleSeconds) =>
        TimeSpan.FromSeconds((PointSeconds(fftSize, sampleRateHz, averageCount, settleSeconds) * 3.0) + 2.0);

    /// <summary>
    /// How long one point is expected to take: the in-flight audio the flush discards, the throwaway
    /// record that follows the stimulus change (see <see cref="CaptureWithStimulusAsync"/>), one
    /// record to refill the window after its flush, then <c>averageCount</c> averages at
    /// <see cref="AverageHopFraction"/> — <c>1 + (n-1)·hop</c> records rather than <c>n</c>.
    /// </summary>
    public static double PointSeconds(int fftSize, double sampleRateHz, int averageCount, double settleSeconds)
    {
        // ~65 ms at every sample rate (Qa40xUsbDevice.StimulusLatencyFrames); 100 ms is a
        // deliberately loose stand-in rather than widening DeviceSessionService's surface for it.
        // The settle time is the user's own number (DeviceSessionService.CaptureDelayMs), discarded
        // on top of it at every stimulus change.
        const double StimulusLatencySeconds = 0.1;

        double recordSeconds = fftSize / sampleRateHz;
        double averageSeconds = (1.0 + ((Math.Max(1, averageCount) - 1) * AverageHopFraction)) * recordSeconds;
        return (2.0 * recordSeconds) + StimulusLatencySeconds + settleSeconds + averageSeconds;
    }

    /// <summary>What the probe and the sweep report while they run: the phase and the generator level to broadcast.</summary>
    public delegate void ProbeReport(FrequencyResponsePhase phase, int done, int total, double generatorAmpDbv);

    /// <summary>
    /// The generator amplitude an output-referred level asks for, solved by probing at 1 kHz on the
    /// chosen output(s): measure what comes back on <paramref name="measuredChannel"/>, correct the
    /// generator by the shortfall, repeat.
    ///
    /// <para>Reads <b>one</b> channel, named by the caller, unlike <c>ThdSweepRunner</c>'s equivalent
    /// which takes the loudest of the two. On the stepped response the right input is (usually) the
    /// reference, carrying the stimulus at the generator's own level — taking the maximum would lock
    /// the probe onto that reference and solve for the generator level rather than for the DUT's
    /// output, silently turning "1 W into 8 Ω" into "the generator at 2.83 V". A crosstalk run reads
    /// its aggressor channel, whichever that is; its victim is 60–90 dB down and would fail the
    /// silence guard.</para>
    ///
    /// <para>The input range is set from the <b>target</b> before the first probe pass, not after
    /// it. That ordering is load-bearing: the probe measures the DUT's gain from its own captures,
    /// so a leftover range too sensitive for the target clips them, the probe reads the loss as
    /// missing gain and pushes the generator up, which clips harder still — measured on the chirp
    /// path as a 10.7 dB overshoot against a plain loopback.</para>
    /// </summary>
    /// <returns>The solved generator level, the peak the last probe measured (the range predictor's seed) and the generator level that peak was measured at.</returns>
    public static async Task<(double GenDbv, double PeakDbv, double PeakAtGenDbv)> ProbeGeneratorLevelAsync(
        DeviceSessionService session,
        SpectrumPipeline pipeline,
        StreamChannel measuredChannel,
        double wantedOutDbv,
        double sampleRateHz,
        int probeFftSize,
        bool generateLeft,
        bool generateRight,
        TimeSpan timeout,
        ProbeReport report,
        CancellationToken ct)
    {
        report(FrequencyResponsePhase.DeterminingLevel, 0, MaxProbeIterations, ProbeStartDbv);

        double maxGenDbv = session.Capabilities.OutputRangesDbv.Count > 0
            ? session.Capabilities.OutputRangesDbv.Max()
            : 18.0;

        // The target is the DUT's level on the measured channel; the ladder is the analyzer's.
        if (session.AutoRangeEnabled != false)
            await SetInputRangeForAsync(session, session.ToAnalyzerDbv(wantedOutDbv, measuredChannel), ct);

        var probeTone = CoherentFrequency.Nearest(ProbeFrequencyHz, sampleRateHz, probeFftSize);
        double genDbv = ProbeStartDbv;
        string channelName = measuredChannel == StreamChannel.Left ? "left" : "right";

        // Tracked as a pair: the peak, and the generator level it was measured at. The loop can exit
        // having just applied an unmeasured correction, and a peak paired with the wrong generator
        // level is a constant offset on every prediction the run then makes.
        double peakDbv = double.NegativeInfinity;
        double peakAtGenDbv = genDbv;

        for (int i = 0; i < MaxProbeIterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            report(FrequencyResponsePhase.DeterminingLevel, i, MaxProbeIterations, genDbv);

            var snapshots = await CaptureWithStimulusAsync(
                session, pipeline, ToneConfig(probeTone.FrequencyHz, genDbv, generateLeft, generateRight), probeFftSize, timeout, ct);

            peakDbv = SweepCaptureSupport.MeasuredPeakDbv(snapshots);
            peakAtGenDbv = genDbv;

            double measuredDbv = ResponseCaptureSupport.LevelAt(snapshots, measuredChannel, probeTone.Bin);
            if (double.IsNaN(measuredDbv) || measuredDbv < ProbeSilenceDbv)
            {
                throw new InvalidOperationException(
                    $"No signal came back on the {channelName} input at {genDbv:0.##} dBV generator while determining the drive level "
                        + $"— check that the DUT is connected and powered, and that its output reaches the {channelName} input.");
            }

            double errorDb = wantedOutDbv - measuredDbv;

            // Measured at the level asked for: genDbv is right and has been verified, not just
            // calculated. Stop here rather than applying one more unmeasured correction.
            if (Math.Abs(errorDb) <= ProbeToleranceDb)
                break;

            // Already at the DAC's ceiling and still short — another pass would only re-clamp.
            if (genDbv >= maxGenDbv && errorDb > 0.0)
                break;

            genDbv += errorDb;

            if (genDbv <= ProbeStartDbv)
            {
                throw new InvalidOperationException(
                    $"Could not reach the requested output level — the generator would need to go below {ProbeStartDbv:0} dBV. "
                        + "Check that the DUT is connected and powered.");
            }

            genDbv = Math.Min(genDbv, maxGenDbv);
        }

        return (genDbv, peakDbv, peakAtGenDbv);
    }

    /// <summary>
    /// Measures the input range this DUT needs before the sweep starts, for the directly-specified
    /// generator level where nothing else has measured it: park on the least sensitive range, play
    /// the probe tone at the real generator level, capture, and pick the range from the peak. Returns
    /// that peak, so it also seeds the per-point predictor; negative infinity when nothing came back
    /// (the range is then left at the widest, which is where a sweep of an unknown DUT should start).
    ///
    /// <para>This is <see cref="FrequencyResponseRunner"/>'s own input-range probe, ported for the
    /// same reason it exists there: without it the run starts on whatever range the previous
    /// measurement left behind and lets the device's own ranger walk to the right one. That works --
    /// an autorange step announces itself in-band and the point re-captures -- but every step costs a
    /// ~110 ms hole and a re-captured record, and with the predictor unseeded the first point cannot
    /// suppress step-down either, so the ranger hunts freely through the start of the sweep. Arriving
    /// on the right range costs one short capture instead.</para>
    ///
    /// <para>Least sensitive range first, and for the same reason as there: an unknown DUT can be
    /// doing anything, and a probe that clips reads a peak that is a property of the range rather
    /// than of the signal -- which would then choose that same range again and stay wrong.</para>
    /// </summary>
    public static async Task<double> ProbeInputRangeAsync(
        DeviceSessionService session,
        SpectrumPipeline pipeline,
        GeneratorConfig probeTone,
        double probeFreqHz,
        double generatorAmpDbv,
        int probeFftSize,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;
        if (ranges.Count == 0)
            return double.NegativeInfinity;

        await ApplyInputRangeAsync(session, ranges.Max(), ct);

        var snapshots = await CaptureWithStimulusAsync(session, pipeline, probeTone, probeFftSize, timeout, ct);

        double peakDbv = SweepCaptureSupport.MeasuredPeakDbv(snapshots);
        if (double.IsNegativeInfinity(peakDbv))
        {
            // Nothing came back. Leave the range where the probe parked it -- the widest, which is
            // the safe place to start a sweep of a DUT we know nothing about -- and let the first
            // point seed the predictor the way it used to.
            logger.LogWarning(
                "The input-range probe measured nothing at {FreqHz:0.##} Hz; leaving the range at {RangeDbv:0.#} dBV "
                    + "and letting the first sweep point find it",
                probeFreqHz, session.InputRangeDbv);
            return double.NegativeInfinity;
        }

        double selected = InputRangeSelector.For(ranges, peakDbv);
        logger.LogInformation(
            "Input-range probe measured a peak of {PeakDbv:0.##} dBV at {FreqHz:0.##} Hz / {GenDbv:0.##} dBV generator; "
                + "input range {RangeDbv:0.#} dBV",
            peakDbv, probeFreqHz, generatorAmpDbv, selected);

        await ApplyInputRangeAsync(session, selected, ct);
        return peakDbv;
    }

    /// <summary>
    /// Programs the smallest input range that still leaves <see cref="InputRangeSelector.HeadroomDb"/>
    /// above <paramref name="expectedLevelDbv"/>, out of whatever this device actually offers. One
    /// ladder decides every input range in this app — see <see cref="InputRangeSelector"/>.
    /// </summary>
    public static async Task SetInputRangeForAsync(DeviceSessionService session, double expectedLevelDbv, CancellationToken ct)
    {
        var ranges = session.Capabilities.InputRangesDbv;
        if (ranges.Count == 0)
            return;

        await ApplyInputRangeAsync(session, InputRangeSelector.For(ranges, expectedLevelDbv), ct);
    }

    /// <summary>
    /// Goes through <see cref="DeviceSessionService.SetInputRangeAsync"/> so a mid-stream change is
    /// applied on the transfer pump rather than written to EP1 with EP2 transfers in flight.
    /// </summary>
    public static async Task ApplyInputRangeAsync(DeviceSessionService session, double rangeDbv, CancellationToken ct)
    {
        if (Math.Abs(rangeDbv - session.InputRangeDbv) <= RangeEpsilonDb)
            return;

        await session.SetInputRangeAsync(rangeDbv, ct);
    }

    /// <summary>
    /// One curve of a stepped measurement. Nothing is decimated and nothing is smoothed: every sample
    /// here <i>is</i> a measurement, so the min and the max of a "column" are the same number, and
    /// there is no fractional-octave smoothing to apply to a grid already coarser than the narrowest
    /// smoothing the chirp offers. Coherence is 1.0 for the same reason — one deterministic capture
    /// per point has no cross-record variance to report, and reporting a fabricated figure would be
    /// worse than reporting none.
    ///
    /// <para>With a <paramref name="reference"/> the ±1 dB and −3 dB corners are found against it
    /// (see <see cref="ResponseCaptureSupport.BuildTrace"/> for the two questions they answer). Without
    /// one — a crosstalk trace, where a "bandwidth" means nothing — the corner blocks are zero and the
    /// reference reads as the peak, the same convention the impedance rows use; the peak itself is
    /// still found, since for a crosstalk trace it is the worst case.</para>
    /// </summary>
    public static FrequencyResponseTrace BuildPointTrace(
        FrequencyResponseTraceKind kind,
        double[] freqHz,
        double[] magnitudeDb,
        double[] phaseDeg,
        BandwidthReferenceSpec? reference)
    {
        var peak = BandwidthAnalyzer.FindPeak(magnitudeDb, freqHz);

        BandwidthReading oneDb;
        BandwidthReading threeDb;
        double referenceDb;
        BandwidthReferenceMode referenceMode;
        bool fellBackToPeak;
        if (reference is { } spec)
        {
            var found = BandwidthAnalyzer.FindReference(magnitudeDb, freqHz, spec);
            oneDb = BandwidthReading.From(BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 1.0, found, symmetric: true));
            threeDb = BandwidthReading.From(BandwidthAnalyzer.Find(magnitudeDb, freqHz, dropDb: 3.0, found, symmetric: false));
            referenceDb = found.LevelDb;
            referenceMode = found.Mode;
            fellBackToPeak = found.FellBackToPeak;
        }
        else
        {
            oneDb = new BandwidthReading(1.0, true, 0.0, 0.0, 0.0, false, false);
            threeDb = new BandwidthReading(3.0, false, 0.0, 0.0, 0.0, false, false);
            referenceDb = peak.LevelDb;
            referenceMode = BandwidthReferenceMode.Peak;
            fellBackToPeak = false;
        }

        // A stepped sweep removes no delay and its unwrapper starts at the first tone, so the
        // phase already carries the DUT's physical turns: the delays are a plain finite difference
        // on the sweep's own grid and the phase delay takes no anchor -- anchoring it at a passband
        // tone would delete a whole turn's worth of real delay from any DUT slower than half a
        // period there. Valid while the tone spacing keeps each phase step under half a turn.
        var groupDelayS = GroupDelay.FromPhaseOnGrid(phaseDeg, freqHz);
        var phaseDelayS = GroupDelay.PhaseDelayOnGrid(phaseDeg, freqHz, anchorIndex: null);

        int n = freqHz.Length;
        var freq = new float[n];
        var mag = new float[n];
        var phase = new float[n];
        var coherence = new float[n];
        var groupDelay = new float[n];
        var phaseDelay = new float[n];

        for (int i = 0; i < n; i++)
        {
            freq[i] = (float)freqHz[i];
            mag[i] = (float)magnitudeDb[i];
            phase[i] = (float)phaseDeg[i];
            coherence[i] = 1.0f;
            groupDelay[i] = (float)groupDelayS[i];
            phaseDelay[i] = (float)phaseDelayS[i];
        }

        // The same array for min and max: the chart draws the max as the line and the min as a band
        // beneath it, and here there is nothing between them to draw.
        return new FrequencyResponseTrace(
            kind,
            freqHz[0],
            freqHz[^1],
            freq,
            mag,
            (float[])mag.Clone(),
            phase,
            coherence,
            groupDelay,
            phaseDelay,
            oneDb,
            threeDb,
            peak.LevelDb,
            peak.FreqHz,
            referenceDb,
            referenceMode,
            fellBackToPeak);
    }
}
