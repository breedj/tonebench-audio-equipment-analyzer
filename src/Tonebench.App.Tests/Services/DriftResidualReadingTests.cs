using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// <see cref="ResponseCaptureSupport.EstimateDriftResidual"/> is three decisions and a call:
/// whether the question can be asked at all, which trace to ask it of, and over which bins. The
/// arithmetic itself is <see cref="ChirpDriftResidual"/>'s and is pinned in the DSP suite; what is
/// here is the decisions, because each of them is a way of reporting a confident wrong figure.
/// </summary>
public class DriftResidualReadingTests
{
    private const double Fs = 48_000.0;
    private const double SpanHz = 24_000.0;
    private const int Fft = 32768;

    private static FrequencyResponseRequest Request(
        bool rightIsReference = true,
        ResponseStimulus stimulus = ResponseStimulus.Chirp,
        double clockDriftPpm = 0.0) =>
        new(SpanHz, 1, ThdSweepLevelMode.GeneratorVoltage, -20.0, ThdSweepLevelUnit.Dbv,
            rightIsReference, 24, 1, false, FrequencyResponseRequest.DefaultChirpStartHz, 8.0,
            BandwidthReferenceMode.BandMedian, BandwidthReferenceSpec.DefaultLowHz,
            BandwidthReferenceSpec.DefaultHighHz, stimulus, 5.0, clockDriftPpm);

    /// <summary>
    /// A result whose phase is exactly the ramp a given drift leaves, and whose magnitude is
    /// <paramref name="shape"/> — the two things the method reads, and nothing else.
    /// </summary>
    private static FrequencyResponseResult Result(double ppm, Func<double, double>? shape = null)
    {
        var request = Request();
        var map = ChirpTimeMap.For(Fs, Fft, request.ChirpStartHz, request.ChirpEndHz)!.Value;

        int bins = (Fft / 2) + 1;
        var magnitude = new double[bins];
        var phase = new double[bins];
        var coherence = new double[bins];
        var confidence = new double[bins];

        for (int k = 0; k < bins; k++)
        {
            double f = k * (Fs / Fft);
            magnitude[k] = shape?.Invoke(f) ?? 1.0;
            phase[k] = ppm * 1e-6 * map.RampRadiansPerEpsilon(f) * 180.0 / Math.PI;
            coherence[k] = 1.0;
            confidence[k] = 1.0;
        }

        return new FrequencyResponseResult(magnitude, phase, coherence, confidence, LatencySamples: 0.0);
    }

    /// <summary>A trace carrying no drift at all, for the "which trace" cases.</summary>
    private static FrequencyResponseResult Clean() => Result(0.0);

    /// <summary>A reference channel that is carrying one, which is the ordinary case.</summary>
    private static readonly ReferenceChannelReading Fed = new(85.0, Usable: true);

    private static DriftResidualReading? Estimate(
        FrequencyResponseRequest request,
        FrequencyResponseResult left,
        FrequencyResponseResult right,
        ReferenceChannelReading? reference = null) =>
        ResponseCaptureSupport.EstimateDriftResidual(request, left, right, reference ?? Fed);

    [Fact]
    public void TheReferenceChannelsOwnRamp_IsWhatIsReported()
    {
        var reading = Estimate(Request(clockDriftPpm: -104.038), Clean(), Result(-8.5));

        Assert.NotNull(reading);
        Assert.Equal(-8.5, reading!.ResidualPpm, 0.2);
        Assert.Equal(-104.038, reading.AppliedPpm);
        Assert.True(reading.Bins > 1000, $"a 24 kHz span should give thousands of bins, not {reading.Bins}");
    }

    /// <summary>
    /// The one that stops a DUT being read as a clock. The measured channel is handed a wild ramp
    /// and the reference a small one; the answer must be the reference's — see
    /// <c>ChirpDriftResidualTests.ADutsOwnPhase_CanManufactureDriftThatIsNotThere</c> for the
    /// measurement that makes this non-negotiable.
    /// </summary>
    [Fact]
    public void TheMeasuredChannelIsNeverRead_EvenWhenItIsTheLouderStory()
    {
        var reading = Estimate(Request(), Result(-900.0), Result(-8.5));

        Assert.Equal(-8.5, reading!.ResidualPpm, 0.2);
    }

    /// <summary>
    /// And with no measured reference there is no clean trace, so there is no reading — rather than
    /// one taken off the DUT's own channel, which is the same thing as a number made up.
    /// </summary>
    [Fact]
    public void WithoutAMeasuredReference_NoReadingIsMade()
    {
        Assert.Null(Estimate(Request(rightIsReference: false), Result(-104.038), Result(-104.038)));
    }

    /// <summary>
    /// Noise has no sweep time map, so there is no ramp to look for — the same restriction
    /// <see cref="FrequencyResponseRequest.ClockDrift"/> itself carries, and for the same reason.
    /// </summary>
    [Theory]
    [InlineData(ResponseStimulus.PinkNoise)]
    [InlineData(ResponseStimulus.WhiteNoise)]
    [InlineData(ResponseStimulus.SpeechMale2011)]
    public void ANoiseStimulus_HasNoRampToRead(ResponseStimulus stimulus)
    {
        Assert.Null(Estimate(Request(stimulus: stimulus), Clean(), Result(-104.038)));
    }

    /// <summary>
    /// An uncorrected run still reports, and reports 0 as what it was corrected by — which is the
    /// case the panel words as "this measurement is carrying N ppm you could remove" rather than as
    /// a stale figure.
    /// </summary>
    [Fact]
    public void AnUncorrectedRun_ReportsTheWholeDrift()
    {
        var reading = Estimate(Request(clockDriftPpm: 0.0), Clean(), Result(-104.038));

        Assert.Equal(-104.038, reading!.ResidualPpm, 0.2);
        Assert.Equal(0.0, reading.AppliedPpm);
    }

    /// <summary>
    /// A drift figure too small for <see cref="ChirpClockDrift.For"/> to act on was never applied,
    /// so it must not be reported as applied either — otherwise the panel reads "corrected by
    /// 0.02 ppm" over a run that was not corrected at all.
    /// </summary>
    [Fact]
    public void ADriftFigureTooSmallToHaveBeenApplied_IsNotReportedAsApplied()
    {
        var reading = Estimate(Request(clockDriftPpm: 0.02), Clean(), Result(-104.038));

        Assert.Equal(0.0, reading!.AppliedPpm);
    }

    /// <summary>
    /// The flatness gate: a path that rolls off at the top of the band has its roll-off dropped,
    /// because a roll-off's phase is what contaminates the fit. The ramp is unchanged, so the answer
    /// must be too — what moves is the band that was fitted, which the reading reports so a log line
    /// can say where the figure came from.
    /// </summary>
    [Fact]
    public void ARollOffAtTheTopOfTheBand_IsNotFitted()
    {
        // One pole at a tenth of Nyquist: 3 dB down by 2.4 kHz, so most of the band is excluded.
        var reading = Estimate(Request(), Clean(), Result(-8.5, f => 1.0 / Math.Sqrt(1.0 + Math.Pow(f / 2400.0, 2))));

        Assert.NotNull(reading);
        Assert.Equal(-8.5, reading!.ResidualPpm, 0.2);
        Assert.True(reading.ToHz < 5_000.0, $"the fit should stop near the corner, and reached {reading.ToHz:0} Hz");
        Assert.True(reading.FromHz < 20.0, $"and should still start at the bottom, not {reading.FromHz:0.0} Hz");
    }

    [Fact]
    public void AFlatPath_KeepsTheWholeAnalysedBand()
    {
        var request = Request();
        var reading = Estimate(request, Clean(), Result(-8.5));

        Assert.True(reading!.ToHz > request.AnalysisHighHz - 2.0 * request.BinHz,
            $"a flat path should reach {request.AnalysisHighHz:0} Hz and reached {reading.ToHz:0}");
    }

    /// <summary>
    /// <b>The guard the bench asked for.</b> A reference channel the server has judged not to be
    /// carrying one makes no reading at all — rather than a reading of whatever leaks into an
    /// unconnected input, which is how this measurement first went wrong: it claimed a drift figure
    /// was stale that three independent measurements agreed on to 0.3 ppm.
    /// </summary>
    [Fact]
    public void AReferenceChannelCarryingNothing_MakesNoReading()
    {
        var unusable = new ReferenceChannelReading(SnrDb: 2.0, Usable: false);

        Assert.Null(Estimate(Request(clockDriftPpm: -104.038), Clean(), Result(-8.5), unusable));

        // And a run the check could not make an answer about at all -- too few measured bins to
        // judge -- is the same refusal, not the benefit of the doubt.
        Assert.Null(ResponseCaptureSupport.EstimateDriftResidual(
            Request(clockDriftPpm: -104.038), Clean(), Result(-8.5), reference: null));
    }

    /// <summary>
    /// And what that channel was carrying rides on the reading, because the figure is only ever
    /// worth what the channel it was read off was.
    /// </summary>
    [Fact]
    public void TheReadingSaysWhatTheReferenceWasCarrying()
    {
        Assert.Equal(85.0, Estimate(Request(), Clean(), Result(-8.5))!.ReferenceSnrDb);
    }

    /// <summary>
    /// A trace with nothing in it anywhere — a reference channel with no cable on it — leaves too
    /// few bins to fit, and that is reported as no reading rather than as a fit of the noise.
    /// </summary>
    [Fact]
    public void AReferenceChannelWithNothingOnIt_IsNoReading()
    {
        Assert.Null(Estimate(Request(), Clean(), Result(-8.5, _ => 0.0)));
    }
}
