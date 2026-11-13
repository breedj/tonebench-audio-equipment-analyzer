using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Device.Split;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// A stimulus played at one rate and analysed at another — stage 2b of
/// <c>docs/separate in out interfaces plan.md</c>.
///
/// <para>Two things carry the whole of it. The <b>period</b> is a count of the generator's frames,
/// so it scales with the rate ratio and stays the same number of seconds; and the <b>reference
/// spectrum</b> is built at the generator's rate and expressed on the analysis grid, which is a
/// zero-pad rather than a resample because the bin widths come out identical.</para>
///
/// <para>The one-rate cases here matter as much as the two-rate ones: nothing about an ordinary
/// bench may move, and the quiet way for that to break is a cap that applies when it should not.</para>
/// </summary>
public class TwoRateStimulusTests
{
    private static readonly IFftBackend Fft = new FftFlatBackend();

    private static FrequencyResponseRequest Request(double generatorRateHz = 0.0, ResponseStimulus stimulus = ResponseStimulus.Chirp) =>
        new(FrequencySpanHz: 24_000.0,
            ResolutionMultiplier: 1,
            LevelMode: ThdSweepLevelMode.GeneratorVoltage,
            LevelValue: -20.0,
            LevelUnit: ThdSweepLevelUnit.Dbv,
            RightChannelIsReference: false,
            SmoothingDenominator: 0,
            AverageCount: 1,
            Stimulus: stimulus,
            GeneratorSampleRateHz: generatorRateHz);

    // ---- the period ----

    [Fact]
    public void ThePeriod_IsTheAnalysisSizeOnABenchWithOneClock()
    {
        var request = Request();

        Assert.Equal(request.FftSize, request.GeneratorPeriodFrames);
        Assert.Equal(request.SampleRateHz, request.GeneratorRateHz);
        Assert.False(request.GeneratorIsSlower);
    }

    [Fact]
    public void ThePeriod_IsInGeneratorFrames_AndSoIsTheSameNumberOfSeconds()
    {
        // 48 kHz against a 48 kHz span, i.e. a 96 kHz capture: a quarter of the frames at a quarter
        // of the rate. EchoChirpSettings.RepeatFrames already counts the generator's frames, which
        // is what makes this a scale factor rather than a rewrite of the sweep.
        var request = Request(generatorRateHz: 48_000.0) with { FrequencySpanHz = 96_000.0 };

        Assert.Equal(192_000.0, request.SampleRateHz);
        Assert.Equal(request.FftSize / 4, request.GeneratorPeriodFrames);
        Assert.Equal(
            request.FftSize / request.SampleRateHz,
            request.GeneratorPeriodFrames / request.GeneratorRateHz,
            precision: 12);
    }

    // ---- the band ----

    [Fact]
    public void TheAnalysedBand_OnABenchWithOneClock_IsUnchanged()
    {
        // The whole point of the cap being conditional. A one-rate bench has to measure exactly what
        // it measured before, and the quiet way to break that is a ceiling that always applies.
        var request = Request();

        Assert.Equal(double.PositiveInfinity, request.StimulusCeilingHz);
        Assert.Equal(FrequencyResponseRequest.ChirpEndNyquistFraction * request.SampleRateHz / 2.0, request.ChirpEndHz);
        Assert.Equal(FrequencyResponseRequest.AnalysisHighNyquistFraction * request.SampleRateHz / 2.0, request.AnalysisHighHz);
    }

    [Fact]
    public void ASweepOnASlowerGenerator_StopsInsideItsOwnNyquist()
    {
        // A 48 kHz generator cannot sweep to its own Nyquist without aliasing at the top; item 6
        // measured 20 kHz against a 24 kHz Nyquist as the figure that works.
        var request = Request(generatorRateHz: 48_000.0) with { FrequencySpanHz = 96_000.0 };

        Assert.True(request.GeneratorIsSlower);
        Assert.Equal(19_200.0, request.ChirpEndHz, precision: 6);

        // And the reported band stops with the stimulus rather than trailing into a dead band the
        // regulariser is holding down.
        Assert.Equal(request.ChirpEndHz, request.AnalysisHighHz, precision: 6);
    }

    [Fact]
    public void AFasterGenerator_NarrowsNothing()
    {
        // Nothing here can help with a generator that outruns the capture: what it does above the
        // capture's Nyquist is invisible, and aliases in. The band stays the analysis band.
        var request = Request(generatorRateHz: 192_000.0) with { FrequencySpanHz = 24_000.0 };

        Assert.False(request.GeneratorIsSlower);
        Assert.Equal(double.PositiveInfinity, request.StimulusCeilingHz);
    }

    // ---- the reference ----

    [Fact]
    public void TheReference_BuiltAtAQuarterRate_LandsOnTheAnalysisBinsExactly()
    {
        // The load-bearing one. The two periods are the same number of seconds, so the bin widths
        // are identical -- fsOut/sizeOut == fsIn/size -- and bin k is bin k. What is left is the
        // scale: an unscaled DFT bin sums its samples, so without the correction a 1:4 pairing would
        // read 12 dB out and the output-referred probe would drive the DUT by that much.
        var atAQuarter = Request(generatorRateHz: 48_000.0) with { FrequencySpanHz = 96_000.0 };
        Complex[] quarter = ResponseCaptureSupport.BuildReferenceSpectrum(Fft, atAQuarter, -20.0);

        // The same sweep sampled at the *capture* rate: what a perfect DAC running at 192 kHz would
        // have played. That is the comparison item 6 makes -- a quarter-rate period lies on the
        // capture-rate one every fourth frame -- and it is the only honest one, since a one-rate
        // request sweeps to its own far higher end frequency and is simply a different chirp.
        var reconstruction = new double[atAQuarter.FftSize];
        ExponentialChirp.Fill(
            reconstruction,
            atAQuarter.ChirpStartHz,
            atAQuarter.ChirpEndHz,
            atAQuarter.SampleRateHz,
            ExponentialChirp.PeakVoltsFromDbv(-20.0));

        var full = new Complex[(atAQuarter.FftSize / 2) + 1];
        Fft.Forward(reconstruction, full);

        Assert.Equal(full.Length, quarter.Length);

        // Compared against the reference's own peak rather than bin by bin, because a chirp's bins
        // vary over orders of magnitude and a relative tolerance on a near-null bin means nothing.
        double peak = full.Max(c => c.Magnitude);
        int lastPlayed = (int)(atAQuarter.ChirpEndHz / atAQuarter.BinHz);

        double worst = 0.0;
        for (int bin = 1; bin < lastPlayed; bin++)
            worst = Math.Max(worst, (full[bin] - quarter[bin]).Magnitude / peak);

        Assert.True(worst < 0.02, $"worst bin differed by {worst:P2} of the reference's peak");

        // And above the generator's Nyquist there is nothing, because nothing was played -- which is
        // what the estimator's regularisation already bounds for the chirp's out-of-band decades.
        int firstUnplayed = (int)(atAQuarter.GeneratorRateHz / 2.0 / atAQuarter.BinHz) + 2;
        for (int bin = firstUnplayed; bin < quarter.Length; bin++)
            Assert.Equal(0.0, quarter[bin].Magnitude);
    }

    [Fact]
    public void TheReference_OnABenchWithOneClock_IsByteForByteWhatItAlwaysWas()
    {
        // Zero carried through as "no second rate" rather than as a rate of zero: the one-rate path
        // must not even take the scaling branch.
        var request = Request();

        Complex[] implicitly = ResponseCaptureSupport.BuildReferenceSpectrum(Fft, request, -20.0);
        Complex[] explicitly = ResponseCaptureSupport.BuildReferenceSpectrum(
            Fft, request with { GeneratorSampleRateHz = request.SampleRateHz }, -20.0);

        for (int bin = 0; bin < implicitly.Length; bin++)
            Assert.Equal(implicitly[bin], explicitly[bin]);
    }

    [Theory]
    [InlineData(ResponseStimulus.PinkNoise)]
    [InlineData(ResponseStimulus.WhiteNoise)]
    public void ANoiseReference_IsAlsoBuiltAtTheRateItIsPlayedAt(ResponseStimulus stimulus)
    {
        // Noise is why the reference has to be rebuilt at all rather than the sweep simply being
        // stopped early: a periodic sequence fills *every* bin up to its own Nyquist, so a reference
        // built at the analysis rate would claim energy at bins a slower DAC never played.
        var request = Request(generatorRateHz: 48_000.0, stimulus: stimulus) with { FrequencySpanHz = 96_000.0 };

        Complex[] reference = ResponseCaptureSupport.BuildReferenceSpectrum(Fft, request, -20.0);

        int firstUnplayed = (int)(request.GeneratorRateHz / 2.0 / request.BinHz) + 2;
        for (int bin = firstUnplayed; bin < reference.Length; bin++)
            Assert.Equal(0.0, reference[bin].Magnitude);

        // And it is not empty where the generator does reach.
        Assert.Contains(reference.Take(firstUnplayed - 4), c => c.Magnitude > 0.0);
    }

    // ---- end to end ----

    /// <summary>
    /// The whole thing, through a runner: a simulated bench generating at 48 kHz and capturing at
    /// 192 kHz still deconvolves to a straight wire.
    ///
    /// <para>This is the test that would catch the reference and the generator disagreeing, which is
    /// the failure this measurement has no other defence against — a stimulus divided by the wrong
    /// reference is reported as DUT response and nothing complains.</para>
    /// </summary>
    [Fact]
    public async Task AMeasurementAcrossTwoRates_StillReadsUnityGain()
    {
        var hub = new SpectrumBroadcastHub();
        var pipeline = new SpectrumPipeline(hub, NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 4096, TargetFrameRateHz = 100, DisplayColumns = 200 },
        };

        var device = new SplitAnalyzerDevice(
            Clean(), Clean(),
            new SplitDeviceSettings { GeneratorSampleRateHz = 48_000.0 },
            NullLogger<SplitAnalyzerDevice>.Instance);

        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var runner = new FrequencyResponseRunner(session, pipeline, hub, NullLogger<FrequencyResponseRunner>.Instance);

        await runner.StartAsync(
            Request(generatorRateHz: 48_000.0) with { FrequencySpanHz = 96_000.0, ResolutionMultiplier = 1 },
            CancellationToken.None);

        FrequencyResponseStatus status = await AwaitCompletionAsync(runner);

        Assert.Equal(FrequencyResponsePhase.Finished, status.Phase);
        Assert.Null(status.Error);

        FrequencyResponseTrace gain = status.Traces.Single(t => t.Kind == FrequencyResponseTraceKind.Gain);

        // Mid-band only: the bottom is the chirp's own fade-in and the top is where the slower
        // generator stops.
        int low = gain.MagnitudeMaxDb.Length / 8;
        int high = gain.MagnitudeMaxDb.Length / 2;
        for (int i = low; i < high; i++)
            Assert.InRange(gain.MagnitudeMaxDb[i], -0.5, 0.5);
    }

    private static SimulatedDevice Clean()
    {
        var device = new SimulatedDevice(randomSeed: 1);
        device.SignalProfile = device.SignalProfile with
        {
            HarmonicsEnabled = false,
            NoiseEnabled = false,
            HumEnabled = false,
        };

        return device;
    }

    private static async Task<FrequencyResponseStatus> AwaitCompletionAsync(FrequencyResponseRunner runner)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (runner.Running)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the two-rate measurement did not finish");
            await Task.Delay(25);
        }

        return runner.Status;
    }
}
