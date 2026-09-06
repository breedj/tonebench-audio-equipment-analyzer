using System.Numerics;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Tests;

public class HarmonicPacketExtractorTests
{
    private const int FftSize = 16384;
    private const double SampleRateHz = 48000.0;

    /// <summary>The third harmonic of the top of this sweep still fits below Nyquist, so nothing folds back.</summary>
    private const double StartHz = 20.0;

    private const double EndHz = 4000.0;

    private const double Distortion = 0.05;

    /// <summary>
    /// The k-th packet's position is what the whole extractor indexes from, and it is the same
    /// formula the anti-distortion gate uses to decide what to discard. If the two ever disagree the
    /// extractor reads a stretch of record the gate did not consider a packet.
    /// </summary>
    [Fact]
    public void PacketOffsetSamples_AgreesWithTheGateItSharesTheGeometryWith()
    {
        var extractor = Extractor();

        double secondHarmonic = FftSize * Math.Log(2.0) / Math.Log(EndHz / StartHz);
        Assert.Equal(secondHarmonic, extractor.PacketOffsetSamples(2), 9);
        Assert.Equal(0.0, extractor.PacketOffsetSamples(1), 12);

        // ImpulseGate keeps half the way back to that packet, so the packet is outside what it keeps.
        var gate = ImpulseGate.ForSweep(FftSize, StartHz, EndHz);
        Assert.True(gate.PreSamples < extractor.PacketOffsetSamples(2));
    }

    /// <summary>
    /// Each order's curve has to stop at its own frequency, because the stimulus has no energy above
    /// the sweep's end: the k-th harmonic can only be seen for fundamentals up to f2/k. A UI drawing
    /// every curve to the same right-hand edge would be inventing the last part of most of them.
    /// </summary>
    [Fact]
    public void UpperValidHz_FallsWithOrder()
    {
        var extractor = Extractor();

        Assert.Equal(EndHz / 2.0, extractor.UpperValidHz(2), 9);
        Assert.Equal(EndHz / 3.0, extractor.UpperValidHz(3), 9);
        Assert.Equal(EndHz / 5.0, extractor.UpperValidHz(5), 9);

        var packets = Extract(CubicDut(Chirp(), Distortion));
        foreach (var packet in packets)
            Assert.True(packet.FundamentalHz[^1] <= packet.UpperValidHz + 1e-9);
    }

    /// <summary>
    /// The closed-form check that pins the amplitude convention. For <c>y = x + d·x^3</c> driven at
    /// peak amplitude A, the identity <c>sin^3 = (3·sin - sin3)/4</c> gives a fundamental of
    /// <c>A(1 + 0.75·d·A^2)</c> and a third harmonic of <c>d·A^3/4</c>, so the dBc figure is known
    /// exactly and does not depend on any choice made inside the extractor. It is what rules out the
    /// 6 dB window-gain error: correcting the packet transform for its window's coherent gain would
    /// move this number and nothing else would notice.
    /// </summary>
    [Fact]
    public void Extract_OfACubicDut_ReadsTheAmplitudeTheAlgebraPredicts()
    {
        var packets = Extract(CubicDut(Chirp(), Distortion));
        var third = packets.Single(p => p.Order == 3);

        double expectedDbc = 20.0 * Math.Log10((Distortion / 4.0) / (1.0 + (0.75 * Distortion)));

        foreach (double level in Band(third, 100.0, 1000.0))
            Assert.Equal(expectedDbc, level, 0.25);
    }

    /// <summary>
    /// A memoryless cubic makes a third harmonic and nothing else, so every other order must sit far
    /// below it. Catches a packet window sized so wide that it drags in its neighbours, and an
    /// off-by-one in the packet position that would read H3's skirt as H2 or H4.
    /// </summary>
    [Fact]
    public void Extract_OfACubicDut_FindsNothingAtTheOrdersItDoesNotProduce()
    {
        var packets = Extract(CubicDut(Chirp(), Distortion));

        double third = Band(packets.Single(p => p.Order == 3), 100.0, 1000.0).Average();

        foreach (var packet in packets.Where(p => p.Order != 3))
        {
            double level = Band(packet, 100.0, packet.UpperValidHz * 0.75).Average();
            Assert.True(level < third - 30.0,
                $"H{packet.Order} at {level:0.0} dBc against H3 at {third:0.0} dBc");
        }
    }

    /// <summary>
    /// <b>The test that matters, and the only one that catches an <c>f</c> against <c>f/k</c>
    /// inversion.</b> On a DUT that distorts uniformly the two conventions produce curves differing
    /// only by a shift no reader would question. So this DUT distorts over one narrow stretch of the
    /// sweep only — legitimate, because the sweep's instantaneous frequency is a known function of
    /// time, so "distort while the input is near 280 Hz" is just a time envelope on the coefficient.
    ///
    /// <para>The recovered H3 curve must then peak at the <b>fundamental</b> that was being driven,
    /// 280 Hz. Reporting the output frequency instead would put the peak at 840 Hz — a factor of
    /// three, and unmissable once you know to look.</para>
    /// </summary>
    [Fact]
    public void Extract_OfADutThatDistortsOverPartOfTheBand_PutsTheRiseAtTheFundamental()
    {
        const double centreHz = 280.0;

        var stimulus = Chirp();
        var response = BandLimitedCubicDut(stimulus, 0.25, centreHz, octaves: 0.6);

        var third = Extract(response).Single(p => p.Order == 3);

        int peak = 0;
        for (int i = 1; i < third.LevelDbc.Length; i++)
            if (third.LevelDbc[i] > third.LevelDbc[peak])
                peak = i;

        Assert.InRange(third.FundamentalHz[peak], centreHz / 1.3, centreHz * 1.3);

        // And it really is a rise, not a tilt: a decade away it is far down.
        double away = Band(third, 1000.0, 1300.0).Average();
        Assert.True(third.LevelDbc[peak] > away + 20.0,
            $"peak {third.LevelDbc[peak]:0.0} dBc against {away:0.0} dBc at 1 kHz");
    }

    private static HarmonicPacketExtractor Extractor() =>
        new(FftSize, SampleRateHz, StartHz, EndHz, new MathNetBackend());

    /// <summary>
    /// Runs the whole path the runner will: deconvolve gated for the linear magnitude, ungated for
    /// the packets, then extract. <c>Complete</c> is called first deliberately — it gates its
    /// working spectrum in place, and <c>CompleteImpulse</c> has to be immune to that.
    /// </summary>
    private static IReadOnlyList<HarmonicPacket> Extract(double[] response)
    {
        var stimulus = Chirp();
        var estimator = new FrequencyResponseEstimator(
            FftSize, new MathNetBackend(), ImpulseGate.ForSweep(FftSize, StartHz, EndHz));
        estimator.Add(Spectrum(stimulus), Spectrum(response));

        var linear = estimator.Complete();
        var impulse = estimator.CompleteImpulse();

        return Extractor().Extract(impulse.Ungated, impulse.PeakIndex, linear.Magnitude);
    }

    private static IEnumerable<double> Band(HarmonicPacket packet, double lowHz, double highHz)
    {
        for (int i = 0; i < packet.FundamentalHz.Length; i++)
            if (packet.FundamentalHz[i] >= lowHz && packet.FundamentalHz[i] <= highHz)
                yield return packet.LevelDbc[i];
    }

    private static double[] Chirp()
    {
        var period = new double[FftSize];
        ExponentialChirp.Fill(period, StartHz, EndHz, SampleRateHz, 1.0);
        return period;
    }

    private static double[] CubicDut(double[] stimulus, double distortion)
    {
        var response = new double[stimulus.Length];
        for (int i = 0; i < stimulus.Length; i++)
            response[i] = stimulus[i] + (distortion * stimulus[i] * stimulus[i] * stimulus[i]);
        return response;
    }

    /// <summary>
    /// A cubic whose coefficient is a Gaussian in log-frequency centred on
    /// <paramref name="centreHz"/>. Applied as an envelope on the sample index, since the sweep's
    /// instantaneous frequency is <c>f1·e^{Lt/T}</c> and therefore invertible: sample n is being
    /// driven at a frequency this function knows exactly.
    /// </summary>
    private static double[] BandLimitedCubicDut(
        double[] stimulus, double distortion, double centreHz, double octaves)
    {
        double sweepRate = Math.Log(EndHz / StartHz);
        double sigma = octaves * Math.Log(2.0);

        var response = new double[stimulus.Length];
        for (int i = 0; i < stimulus.Length; i++)
        {
            double freqHz = StartHz * Math.Exp(sweepRate * i / stimulus.Length);
            double fromCentre = Math.Log(freqHz / centreHz);
            double weight = Math.Exp(-fromCentre * fromCentre / (2.0 * sigma * sigma));

            response[i] = stimulus[i]
                + (distortion * weight * stimulus[i] * stimulus[i] * stimulus[i]);
        }

        return response;
    }

    private static Complex[] Spectrum(double[] samples)
    {
        var spectrum = new Complex[(FftSize / 2) + 1];
        new MathNetBackend().Forward(samples, spectrum);
        return spectrum;
    }
}
