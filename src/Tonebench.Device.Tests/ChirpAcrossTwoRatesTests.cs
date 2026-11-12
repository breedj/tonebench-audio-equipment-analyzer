using System.Numerics;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.Device.Tests;

/// <summary>
/// <b>Whether the chirp survives a generator and a capture running at different rates</b> — stage
/// 2b's gate, and verification item 6 of <c>docs/separate in out interfaces plan.md</c>.
///
/// <para>The frequency-response measurement rests on one equality: the sweep's period is exactly
/// the analysis record. Today both sides share one <c>AcquisitionConfig.SampleRateHz</c>, so the
/// period is the FFT size and that is the end of it. Stage 2b would let the output run at its own
/// rate — a 48 kHz DAC against a 192 kHz ADC, say — and the question this file settles is whether
/// the equality still holds when the two grids differ, with the generator's period set to
/// <c>FftSize / 4</c> of its own frames.</para>
///
/// <para>It is worth settling before any of stage 2b is built, because if the answer were no the
/// whole stage would be dead: there would be no period that is whole on both sides, every record
/// would hold a fraction of a sweep, and the rectangular window the measurement uses would smear
/// that error across the entire spectrum rather than degrading gracefully.</para>
///
/// <para><b>The answer is yes, and the reason is a design decision already taken.</b> Both the
/// canonical <see cref="ExponentialChirp"/> and the two device-side re-derivations express the
/// sweep as a function of <i>seconds into the period</i>, deriving the period in seconds from
/// their own frame count and their own rate — <c>periodSeconds = RepeatFrames / sampleRateHz</c>.
/// A quarter of the frames at a quarter of the rate is the same number of seconds, so the two
/// grids are two samplings of one continuous waveform. Nothing has to change for that; what these
/// tests do is pin it, so the property cannot be lost by a later edit that looks harmless.</para>
/// </summary>
public class ChirpAcrossTwoRatesTests
{
    private const double CaptureRateHz = 192_000.0;
    private const int CaptureFrames = 65_536;

    private const int Ratio = 4;
    private const double GeneratorRateHz = CaptureRateHz / Ratio;
    private const int GeneratorFrames = CaptureFrames / Ratio;

    private const double StartHz = 20.0;

    /// <summary>
    /// Deliberately well inside the <i>generator's</i> Nyquist (24 kHz), not up against it. A sweep
    /// to 24 kHz at 48 kHz aliases at its own top end, and the reconstruction test below would then
    /// be measuring that rather than the periodicity.
    /// </summary>
    private const double EndHz = 20_000.0;

    private const double AmplitudeDbv = -6.0;

    private static double[] Period(int frames, double sampleRateHz, int? overrideFrames = null)
    {
        var period = new double[overrideFrames ?? frames];
        ExponentialChirp.Fill(
            period, StartHz, EndHz, sampleRateHz, ExponentialChirp.PeakVoltsFromDbv(AmplitudeDbv));
        return period;
    }

    /// <summary>
    /// The claim in its simplest form: the quarter-rate period lies exactly on the capture-rate
    /// period, every fourth frame. Not nearly — the two are the same function evaluated at the same
    /// instants, so this is an equality and the tolerance is floating point's.
    /// </summary>
    [Fact]
    public void AQuarterRateChirp_IsTheCaptureRateChirpOnEveryFourthFrame()
    {
        var generator = Period(GeneratorFrames, GeneratorRateHz);
        var capture = Period(CaptureFrames, CaptureRateHz);

        for (int n = 0; n < GeneratorFrames; n++)
            Assert.Equal(capture[n * Ratio], generator[n], precision: 12);
    }

    /// <summary>
    /// And the two device-side copies follow their own rate rather than the analysis one, which is
    /// what makes stage 2b a matter of plumbing a second rate rather than of rewriting the sweep.
    /// <c>EchoChirpSettings.RepeatFrames</c> counts the <i>generator's</i> frames, and the period in
    /// seconds is derived from the rate the synthesizer was built with.
    /// </summary>
    [Fact]
    public void TheHardwareAndSimulatorWaveforms_FollowTheirOwnRate_NotTheAnalysisRate()
    {
        var capture = Period(CaptureFrames, CaptureRateHz);

        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = new EchoChirpSettings
            {
                AmplitudeDbv = AmplitudeDbv,
                StartFrequencyHz = StartHz,
                EndFrequencyHz = EndHz,
                // A quarter of the analysis record, counted in the generator's own frames.
                RepeatFrames = GeneratorFrames,
            },
        };

        var waveform = new Qa40xGeneratorWaveform(new Random(1), GeneratorRateHz);
        var synthesizer = new SignalSynthesizer(new Random(1), GeneratorRateHz);
        var profile = new SimulatedSignalProfile
        {
            GeneratorLeft = channel,
            HarmonicsEnabled = false,
            NoiseEnabled = false,
            HumEnabled = false,
        };

        for (int n = 0; n < GeneratorFrames; n += 37)
        {
            Assert.Equal(capture[n * Ratio], waveform.ComputeSample(channel, n), precision: 12);
            Assert.Equal(
                capture[n * Ratio],
                synthesizer.ComputeChannelSample(profile, n, channelIndex: 0),
                precision: 12);
        }
    }

    /// <summary>
    /// <b>The negative control, without which the three tests above prove nothing.</b> The plan
    /// document predicts that a period off by a single frame "puts a drifting ripple on every
    /// trace"; here it is at the source. One extra generator frame stretches the period by
    /// 1/16384 of itself, which is inaudible as a number and enormous as a phase error by the end
    /// of the sweep — so the two waveforms start together and separate.
    /// </summary>
    [Fact]
    public void AQuarterRatePeriodOffByOneFrame_DriftsAwayFromTheCaptureRateChirp()
    {
        var capture = Period(CaptureFrames, CaptureRateHz);
        var stretched = Period(GeneratorFrames, GeneratorRateHz, overrideFrames: GeneratorFrames + 1);

        // It begins in agreement, which is exactly why a spot check near t = 0 would miss it.
        Assert.Equal(capture[0], stretched[0], precision: 12);

        double worst = 0.0;
        for (int n = 0; n < GeneratorFrames; n++)
            worst = Math.Max(worst, Math.Abs(capture[n * Ratio] - stretched[n]));

        // Full scale here is about 0.71 V peak, so this is the waveforms having nothing to do with
        // one another by the end -- which is the signature to look for, and what the test above
        // would have shown if the period did not carry across the rates.
        Assert.True(worst > 0.5, $"one frame of stretch moved the waveform by only {worst:0.000} V");
    }

    /// <summary>
    /// End to end: what a 48 kHz DAC would actually hand a 192 kHz ADC.
    ///
    /// <para>The tests above compare two sample grids of one formula, which leaves the obvious
    /// objection — that the capture would see the DAC's <i>reconstruction</i> of the quarter-rate
    /// sequence, not the formula re-evaluated. For a periodic sequence that reconstruction is
    /// exact and computable: transform the period, zero-pad the spectrum to the capture length,
    /// transform back. That is ideal band-limited interpolation, with no window and no filter
    /// design to argue about, and it is only valid because the sweep stops well inside the
    /// generator's own Nyquist.</para>
    ///
    /// <para>What is left over is the sweep's own energy above 24 kHz — the skirts of the sweep's
    /// top end and the fade's corners — which a real DAC would also remove. Measured: <b>30 µV
    /// against a 0.71 V peak, 87 dB down</b>. So the reconstruction is the capture-rate chirp for
    /// every purpose this measurement has, and everything downstream of this point is already
    /// covered by the existing deconvolution tests.</para>
    /// </summary>
    [Fact]
    public void AQuarterRateChirpReconstructed_IsTheCaptureRateChirp()
    {
        var generator = Period(GeneratorFrames, GeneratorRateHz);
        var capture = Period(CaptureFrames, CaptureRateHz);

        var fft = new FftFlatBackend();

        var narrow = new Complex[(GeneratorFrames / 2) + 1];
        fft.Forward(generator, narrow);

        // Zero-padding in frequency is interpolation in time. The scale is the ratio of the two
        // lengths because Forward returns raw sums while Inverse divides by its own N.
        var wide = new Complex[(CaptureFrames / 2) + 1];
        for (int k = 0; k < narrow.Length; k++)
            wide[k] = narrow[k] * Ratio;

        var reconstructed = new double[CaptureFrames];
        fft.Inverse(wide, reconstructed);

        double worst = 0.0;
        for (int n = 0; n < CaptureFrames; n++)
            worst = Math.Max(worst, Math.Abs(capture[n] - reconstructed[n]));

        // 1e-4 against a measured 3e-5: close enough to notice a real regression, loose enough not
        // to fail on an FFT backend whose rounding differs by a few counts in the last place.
        Assert.True(worst < 1e-4, $"the reconstruction missed the capture-rate chirp by {worst:0.0000000} V");

        // And it passes through the generator's own samples, which is the defining property of an
        // interpolation as opposed to a filtering. Asserted as an absolute difference rather than
        // with Assert.Equal's decimal places, because inside the fade the samples are near zero and
        // a places comparison there is really asking for relative precision on a quantity that has
        // none -- the first attempt failed on 9.7e-10 against a sample of 0.
        double worstOnGrid = 0.0;
        for (int n = 0; n < GeneratorFrames; n++)
            worstOnGrid = Math.Max(worstOnGrid, Math.Abs(generator[n] - reconstructed[n * Ratio]));

        Assert.True(worstOnGrid < 1e-8, $"the reconstruction moved its own samples by {worstOnGrid:0.0e+00} V");
    }
}
