using System.Numerics;
using Tonebench.Device.Simulation;

namespace Tonebench.Device.Tests;

/// <summary>
/// The four classic low-pass DUTs (<see cref="FilterPrototype"/>), read off the exact discrete
/// transfer function the simulator runs (<see cref="SimulatedDut.ResponseAt"/> — these are pure
/// cascades, so it is exact for them) and compared with what each alignment is <i>defined</i> by.
///
/// <para>The Butterworth and the Chebyshev have closed forms, so those two are pinned to the
/// formula point for point. The Bessel and the elliptic have none, so they are pinned to the
/// properties that name them: a group delay that does not move across the passband, and a
/// stopband that never comes back above −60 dB. The closed forms are evaluated at the analog
/// frequency each digital one stands for (<see cref="AnalogEquivalentHz"/>), so the comparison
/// is exact rather than "within the warping". One end-to-end check through the streaming device,
/// on the Butterworth, is what proves the sections are in the signal path at all.</para>
/// </summary>
public class FilterDutTests
{
    private const double SampleRateHz = 192_000.0;
    private const double Fc = SimulatedDut.FilterCornerHz;

    private static double GainDb(SimulatedDutModel model, double freqHz, double sampleRateHz = SampleRateHz)
    {
        var dut = SimulatedDut.For(model, sampleRateHz)!;
        return 20.0 * Math.Log10(Complex.Abs(dut.ResponseAt(freqHz)));
    }

    /// <summary>
    /// The analog frequency a digital one stands for under the bilinear transform prewarped at the
    /// corner: <c>H_d(e^(jw)) = H_a(j k tan(w/2))</c> with <c>k = wc / tan(wc / 2fs)</c>. The corner
    /// maps to itself; everything else is a little higher, by <c>x^2/3</c> for <c>x = pi f / fs</c> --
    /// 0.6 % at 4 kHz at 48 kHz, which is 0.8 dB on a 100 dB/decade slope, so the closed forms are
    /// evaluated here rather than at the nominal frequency.
    /// </summary>
    private static double AnalogEquivalentHz(double freqHz, double sampleRateHz = SampleRateHz)
    {
        double wc = 2.0 * Math.PI * Fc;
        double k = wc / Math.Tan(wc / (2.0 * sampleRateHz));
        return k * Math.Tan(Math.PI * freqHz / sampleRateHz) / (2.0 * Math.PI);
    }

    /// <summary>Group delay in seconds by central difference of the unwrapped phase over ±1 Hz.</summary>
    private static double GroupDelayS(SimulatedDutModel model, double freqHz)
    {
        var dut = SimulatedDut.For(model, SampleRateHz)!;
        double phaseLo = dut.ResponseAt(freqHz - 1.0).Phase;
        double phaseHi = dut.ResponseAt(freqHz + 1.0).Phase;
        double dPhase = phaseHi - phaseLo;
        if (dPhase > Math.PI) dPhase -= 2.0 * Math.PI;
        if (dPhase < -Math.PI) dPhase += 2.0 * Math.PI;
        return -dPhase / (2.0 * Math.PI * 2.0);
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(1_000.0)]
    [InlineData(2_000.0)]
    [InlineData(3_000.0)]
    [InlineData(4_000.0)]
    [InlineData(8_000.0)]
    public void Butterworth_FollowsItsClosedForm(double freqHz)
    {
        // |H|^2 = 1 / (1 + (f/fc)^10) for a fifth order.
        double expectedDb = -10.0 * Math.Log10(1.0 + Math.Pow(AnalogEquivalentHz(freqHz) / Fc, 10.0));
        Assert.Equal(expectedDb, GainDb(SimulatedDutModel.ButterworthLowPass, freqHz), 1e-6);
    }

    [Fact]
    public void Butterworth_IsThreeDbDownAtItsCorner_AtEverySampleRate()
    {
        // The prewarp is what makes this exact at 48 kHz; without it the corner reads -3.6 dB there.
        foreach (double fs in new[] { 48_000.0, 96_000.0, 192_000.0 })
            Assert.Equal(-3.0103, GainDb(SimulatedDutModel.ButterworthLowPass, Fc, fs), 0.01);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Chebyshev_RipplesBetweenZeroAndMinusOneDb_AtTheChebyshevExtrema(int k)
    {
        // T5(cos t) = cos(5t): |T5| = 1 at f/fc = cos(k pi / 5) -- the ripple troughs, -1 dB, the
        // corner being k = 0 -- and T5 = 0 at f/fc = cos((2k + 1) pi / 10), the crests at 0 dB.
        double troughHz = Fc * Math.Cos(k * Math.PI / 5.0);
        double crestHz = Fc * Math.Cos((2 * k + 1) * Math.PI / 10.0);
        Assert.Equal(-1.0, GainDb(SimulatedDutModel.ChebyshevLowPass, troughHz), 0.002);
        Assert.Equal(0.0, GainDb(SimulatedDutModel.ChebyshevLowPass, Math.Max(crestHz, 1.0)), 0.002);
    }

    [Theory]
    [InlineData(500.0)]
    [InlineData(1_000.0)]
    [InlineData(1_500.0)]
    [InlineData(3_000.0)]
    [InlineData(4_000.0)]
    public void Chebyshev_FollowsItsClosedForm(double freqHz)
    {
        double x = AnalogEquivalentHz(freqHz) / Fc;
        double t5 = (16.0 * Math.Pow(x, 5)) - (20.0 * Math.Pow(x, 3)) + (5.0 * x);
        double epsilonSquared = Math.Pow(10.0, 0.1) - 1.0;
        double expectedDb = -10.0 * Math.Log10(1.0 + (epsilonSquared * t5 * t5));
        Assert.Equal(expectedDb, GainDb(SimulatedDutModel.ChebyshevLowPass, freqHz), 1e-6);
    }

    [Fact]
    public void Bessel_HasAFlatGroupDelayAcrossItsPassband_AndIsThreeDbDownAtItsCorner()
    {
        double expectedS = FilterPrototype.BesselDelayAtDc / (2.0 * Math.PI * Fc);
        Assert.Equal(193e-6, expectedS, 1e-6);

        // Within 1 % from DC to the corner itself: that is the property the alignment is named for,
        // and the one the Butterworth (54 % more delay at its corner than at DC) does not have.
        foreach (double freqHz in new[] { 50.0, 200.0, 500.0, 1_000.0, 1_500.0, 2_000.0 })
            Assert.Equal(expectedS, GroupDelayS(SimulatedDutModel.BesselLowPass, freqHz), expectedS * 0.01);

        Assert.Equal(-3.0103, GainDb(SimulatedDutModel.BesselLowPass, Fc), 0.02);

        // And it pays for it: two octaves up the Butterworth is 60 dB down and the Bessel 28.
        Assert.Equal(-28.3, GainDb(SimulatedDutModel.BesselLowPass, 3.0 * Fc), 0.3);
        Assert.Equal(-47.7, GainDb(SimulatedDutModel.ButterworthLowPass, 3.0 * Fc), 0.3);
    }

    [Fact]
    public void Butterworth_GroupDelayIsNotFlat_WhichIsWhyTheBesselExists()
    {
        // 1.54 for a fifth-order Butterworth: the delay peaks at the corner, and a step through it
        // overshoots.
        double atDc = GroupDelayS(SimulatedDutModel.ButterworthLowPass, 50.0);
        double atCorner = GroupDelayS(SimulatedDutModel.ButterworthLowPass, Fc);
        Assert.InRange(atCorner / atDc, 1.4, 1.7);
    }

    [Fact]
    public void Elliptic_IsOneDbDownAtItsCorner_AndNeverAboveMinusSixtyInItsStopband()
    {
        Assert.Equal(-1.0, GainDb(SimulatedDutModel.EllipticLowPass, Fc), 0.03);

        // Passband ripple stays inside [-1, 0] dB.
        for (double f = 20.0; f < Fc; f *= 1.05)
            Assert.InRange(GainDb(SimulatedDutModel.EllipticLowPass, f), -1.02, 0.02);

        // The stopband from twice the corner to Nyquist: the zeros put two deep notches in it, and
        // between them the response comes back up to the equiripple floor -- and no higher.
        double deepest = 0.0;
        for (double f = 2.0 * Fc; f < SampleRateHz / 2.0; f *= 1.01)
        {
            double db = GainDb(SimulatedDutModel.EllipticLowPass, f);
            Assert.True(db <= -59.9, $"{db:F2} dB at {f:F0} Hz is above the 60 dB stopband");
            deepest = Math.Min(deepest, db);
        }

        Assert.True(deepest < -100.0, $"the deepest stopband point is only {deepest:F1} dB; the zeros are missing");
    }

    [Fact]
    public void Elliptic_ZerosSitWhereThePrototypeSaysTheyDo()
    {
        // The bilinear mapping moves a zero at omega_z to (2 fs) atan(omega_z / k), with k the
        // prewarped constant -- a few hertz at 192 kHz. Search a narrow window around each and expect
        // a null.
        var dut = SimulatedDut.For(SimulatedDutModel.EllipticLowPass, SampleRateHz)!;
        foreach (double zeroOmega in FilterPrototype.Elliptic.ZeroOmegas)
        {
            double nominalHz = zeroOmega * Fc;
            double deepest = 0.0;
            for (double f = nominalHz * 0.99; f < nominalHz * 1.01; f += 0.5)
                deepest = Math.Min(deepest, 20.0 * Math.Log10(Complex.Abs(dut.ResponseAt(f))));

            Assert.True(deepest < -110.0, $"no null near {nominalHz:F0} Hz (deepest {deepest:F1} dB)");
        }
    }

    [Theory]
    [InlineData(SimulatedDutModel.ButterworthLowPass)]
    [InlineData(SimulatedDutModel.ChebyshevLowPass)]
    [InlineData(SimulatedDutModel.BesselLowPass)]
    [InlineData(SimulatedDutModel.EllipticLowPass)]
    public void EveryFilter_IsUnityAtDc_AndIsAClassicFilter(SimulatedDutModel model)
    {
        Assert.True(model.IsClassicFilter());
        Assert.False(model.IsImpedanceFixture());
        Assert.Equal(0, model.MeasuredChannel());
        Assert.Equal(0.0, GainDb(model, 1.0), 0.01);
        Assert.False(SimulatedDut.For(model, SampleRateHz)!.SuppliesOwnDistortion);
    }

    /// <summary>
    /// Through the streaming device, the way <c>SimulatedDutTests</c> measures every model: the
    /// sections are only a DUT if the samples that come out have been through them.
    /// </summary>
    [Fact]
    public async Task Butterworth_IsInTheSignalPath()
    {
        const double fs = 48_000.0;
        double wire = await ToneRmsAsync(SimulatedDutModel.StraightWire, Fc, fs);
        double filtered = await ToneRmsAsync(SimulatedDutModel.ButterworthLowPass, Fc, fs);
        Assert.Equal(-3.01, 20.0 * Math.Log10(filtered / wire), 0.1);

        // An octave up: -30.1 dB on paper, -30.9 through the sampled filter (4 kHz at 48 kHz stands
        // for 4070 Hz -- see AnalogEquivalentHz), and the stream has to agree with the transfer
        // function it was built from, not with the paper.
        double wireOctaveUp = await ToneRmsAsync(SimulatedDutModel.StraightWire, 2.0 * Fc, fs);
        double filteredOctaveUp = await ToneRmsAsync(SimulatedDutModel.ButterworthLowPass, 2.0 * Fc, fs);
        Assert.Equal(GainDb(SimulatedDutModel.ButterworthLowPass, 2.0 * Fc, fs), 20.0 * Math.Log10(filteredOctaveUp / wireOctaveUp), 0.1);
    }

    private static async Task<double> ToneRmsAsync(SimulatedDutModel model, double freqHz, double sampleRateHz)
    {
        var device = new SimulatedDevice(randomSeed: 7);
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(freqHz, 0.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone) { SnapToBin = false }, CancellationToken.None);
        device.SignalProfile = device.SignalProfile with
        {
            Dut = model,
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
        };

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        int wanted = (int)(0.25 * sampleRateHz);
        var samples = new List<double>(wanted);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            for (int frame = 0; frame * 2 < block.SampleCount; frame++)
                samples.Add(block.Samples[frame * 2]);
            block.Dispose();
            if (samples.Count >= wanted)
                break;
        }

        double sum = 0.0;
        int from = samples.Count / 2;
        for (int i = from; i < samples.Count; i++)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (samples.Count - from));
    }
}
