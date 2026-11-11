using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// <see cref="ResponseCaptureSupport.CheckReferenceChannel"/> answers one question — is the
/// reference input carrying a transfer function — and it exists because nothing in this app asked
/// it. <b>Right channel is reference</b> is ticked by default, and a bench with one channel wired
/// produced a complete, plausible measurement built on dividing by noise: the gain trace, its phase,
/// its group delay and the latency reading, with nothing on screen saying so. Reported from the
/// bench 2026-11-11.
///
/// <para>The estimator's own accuracy is <c>TransferFunctionSnrTests</c>' business. What is here is
/// the wiring: which runs are checked, which bins are counted, and where the line is drawn.</para>
/// </summary>
public class ReferenceChannelCheckTests
{
    private const double Fs = 48_000.0;
    private const double SpanHz = 24_000.0;
    private const int Fft = 32768;
    private const int Bins = (Fft / 2) + 1;

    private static FrequencyResponseRequest Request(bool rightIsReference = true) =>
        new(SpanHz, 1, ThdSweepLevelMode.GeneratorVoltage, -20.0, ThdSweepLevelUnit.Dbv,
            rightIsReference, 24, 1, false, FrequencyResponseRequest.DefaultChirpStartHz, 8.0,
            BandwidthReferenceMode.BandMedian, BandwidthReferenceSpec.DefaultLowHz,
            BandwidthReferenceSpec.DefaultHighHz, ResponseStimulus.Chirp, 5.0, 0.0);

    /// <summary>
    /// A result whose phase is <paramref name="snrDb"/> of a delayed signal in complex noise — the
    /// one thing this check reads — with a flat magnitude unless <paramref name="shape"/> says
    /// otherwise.
    /// </summary>
    private static FrequencyResponseResult Result(double snrDb, Func<double, double>? shape = null, int seed = 7)
    {
        var rng = new Random(seed);
        double a = Math.Pow(10.0, snrDb / 20.0);

        var magnitude = new double[Bins];
        var phase = new double[Bins];
        var unwrapper = new PhaseUnwrapper();

        for (int k = 0; k < Bins; k++)
        {
            double f = k * (Fs / Fft);
            magnitude[k] = shape?.Invoke(f) ?? 1.0;

            double re = a * Math.Cos(-2 * Math.PI * f * 1e-3);
            double im = a * Math.Sin(-2 * Math.PI * f * 1e-3);

            double u1 = Math.Max(rng.NextDouble(), 1e-12);
            double u2 = rng.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1)) / Math.Sqrt(2.0);
            re += r * Math.Cos(2 * Math.PI * u2);
            im += r * Math.Sin(2 * Math.PI * u2);

            phase[k] = unwrapper.NextFromRadians(Math.Atan2(im, re));
        }

        var ones = new double[Bins];
        Array.Fill(ones, 1.0);
        return new FrequencyResponseResult(magnitude, phase, ones, ones, LatencySamples: 0.0);
    }

    [Fact]
    public void AFedReferenceReadsAsFedAndSaysByHowMuch()
    {
        var reading = ResponseCaptureSupport.CheckReferenceChannel(Request(), Result(60.0));

        Assert.NotNull(reading);
        Assert.True(reading!.Usable);
        Assert.Equal(60.0, reading.SnrDb, 3.0);
    }

    /// <summary>The case this exists for: the box ticked and nothing on the input.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-20.0)]
    public void AnInputWithNothingOnIt_IsNotUsable(double snrDb)
    {
        var reading = ResponseCaptureSupport.CheckReferenceChannel(Request(), Result(snrDb));

        Assert.NotNull(reading);
        Assert.False(reading!.Usable);
    }

    /// <summary>
    /// The line is drawn where it is because of what lies on either side of it, not because 20 is a
    /// round number: the estimate saturates near 0 dB, and a real reference tap reads 60 and better.
    /// </summary>
    [Fact]
    public void TheLineSitsClearOfBothEndsOfWhatTheEstimatorCanSay()
    {
        Assert.True(ResponseCaptureSupport.MinReferenceSnrDb > TransferFunctionSnr.MinSnrDb + 10.0);
        Assert.True(ResponseCaptureSupport.MinReferenceSnrDb < 40.0);

        Assert.False(ResponseCaptureSupport.CheckReferenceChannel(Request(), Result(10.0))!.Usable);
        Assert.True(ResponseCaptureSupport.CheckReferenceChannel(Request(), Result(35.0))!.Usable);
    }

    /// <summary>
    /// A run measured against the synthetic stimulus has no reference, so there is nothing to check
    /// and no reading — rather than a reading of the right input, which on such a run may be
    /// anything at all and is not being divided by.
    /// </summary>
    [Fact]
    public void ARunWithoutAReference_IsNotChecked()
    {
        Assert.Null(ResponseCaptureSupport.CheckReferenceChannel(Request(rightIsReference: false), Result(0.0)));
    }

    /// <summary>
    /// <b>The one way this estimate can be made to lie.</b> The estimator <i>holds</i> the phase
    /// constant below its own magnitude floor, and a constant has a second difference of exactly
    /// zero — so a stretch of held bins would read as a perfect channel. Here the whole band is
    /// 200 dB under the trace's own peak, which is well under
    /// <see cref="FrequencyResponseEstimator.PhaseFloorRelative"/>: every bin in the analysed band
    /// is held, none is counted, and the answer is no reading rather than a flawless one.
    /// </summary>
    [Fact]
    public void BinsWhosePhaseWasHeldRatherThanMeasured_AreNotCountedAsPerfect()
    {
        // Everything above 100 Hz sits 200 dB under the peak, so the estimator held its phase
        // there: a constant, whose second difference is exactly zero over thousands of bins.
        var result = Result(0.0, shape: f => f < 100.0 ? 1.0 : 1e-10);

        var reading = ResponseCaptureSupport.CheckReferenceChannel(Request(), result);

        // The sixty-odd bins that *were* measured carry nothing, and that is what is reported --
        // the held ninety-nine per cent must not vote. Counted, they would put this far above the
        // line and hand a disconnected input a clean bill of health.
        Assert.NotNull(reading);
        Assert.False(reading!.Usable, $"read {reading.SnrDb:0.0} dB, which is the held bins voting");
        Assert.True(reading.SnrDb < ResponseCaptureSupport.MinReferenceSnrDb);
    }

    /// <summary>
    /// And the counterpart: a reference whose level rolls off is still checked over the band where
    /// its phase <em>was</em> measured. A roll-off is not a disconnected input, and refusing to
    /// check one would quietly disable the guard on every bench with a filter in the reference path.
    /// </summary>
    [Fact]
    public void AReferenceThatRollsOff_IsStillChecked()
    {
        var result = Result(60.0, shape: f => 1.0 / Math.Sqrt(1.0 + Math.Pow(f / 2000.0, 2)));

        var reading = ResponseCaptureSupport.CheckReferenceChannel(Request(), result);

        Assert.NotNull(reading);
        Assert.True(reading!.Usable);
    }
}
