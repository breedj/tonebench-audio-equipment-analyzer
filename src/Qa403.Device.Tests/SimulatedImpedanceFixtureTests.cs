using System.Numerics;
using Qa403.Device.Simulation;

namespace Qa403.Device.Tests;

/// <summary>
/// The three <c>Impedance*</c> fixture models, measured the way the impedance screen measures them:
/// stream both channels, take the complex ratio at one coherent frequency, and invert the divider.
///
/// <para>These are end-to-end through <see cref="SimulatedDevice"/> rather than unit tests of the
/// coefficients, so they prove the fixture is actually in the signal path and on the channel the
/// measurement expects to find it on.</para>
///
/// <para><b>Expectations come from the discrete filter, not from the analog circuit.</b> The models
/// are mapped to the z plane by an unprewarped bilinear transform, so the sampled fixture's impedance
/// is exactly what <see cref="SimulatedDut.ResponseAt"/> reports and only approximately the
/// <c>R + sL</c> it was written as -- 6.03 ohms either way at 100 Hz, 73.9 against 63.1 by 10 kHz.
/// Asserting against the analog prototype at the top of the band would be testing the transform's
/// error rather than the measurement.</para>
/// </summary>
public class SimulatedImpedanceFixtureTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>Whole seconds at 48 kHz, so any integer frequency lands exactly on a bin and the single-bin DFT has no leakage.</summary>
    private const int AnalysisFrames = 48_000;

    /// <summary>Discarded before analysis. The sharpest fixture here rings for about 18 ms, so half a second is some 28 time constants.</summary>
    private const int SettleFrames = 24_000;

    /// <summary>Low enough that neither clipper in the simulated signal path is anywhere near being reached.</summary>
    private const double GeneratorDbv = -20.0;

    [Theory]
    [InlineData(50.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public async Task ResistiveFixture_ReadsItsResistorAtEveryFrequency(double freqHz)
    {
        var z = await MeasureAsync(SimulatedDutModel.ImpedanceResistive, freqHz);

        Assert.Equal(SimulatedImpedanceFixture.ResistiveOhms, z.Magnitude, 6);
        Assert.Equal(0.0, z.Phase * 180.0 / Math.PI, 6);
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(955.0)]
    [InlineData(10000.0)]
    public async Task ReactiveFixture_MatchesItsOwnDiscreteImpedance(double freqHz)
    {
        var expected = ExpectedOhms(SimulatedDutModel.ImpedanceReactive, freqHz);
        var z = await MeasureAsync(SimulatedDutModel.ImpedanceReactive, freqHz);

        Assert.Equal(expected.Magnitude, z.Magnitude, 6);
        Assert.Equal(expected.Phase, z.Phase, 6);
    }

    /// <summary>
    /// And at the bottom of the band, where the bilinear transform's error has not yet arrived, the
    /// discrete fixture really is the <c>6 ohm + 1 mH</c> it was written as. This is what says the
    /// model is the circuit it claims to be rather than an arbitrary filter.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(100.0)]
    public async Task ReactiveFixture_WellBelowNyquist_IsTheSeriesCircuitItWasWrittenAs(double freqHz)
    {
        var analog = new Complex(
            SimulatedImpedanceFixture.ReactiveOhms,
            2.0 * Math.PI * freqHz * SimulatedImpedanceFixture.ReactiveHenry);

        var z = await MeasureAsync(SimulatedDutModel.ImpedanceReactive, freqHz);

        Assert.Equal(analog.Magnitude, z.Magnitude, 4);
        Assert.Equal(analog.Phase * 180.0 / Math.PI, z.Phase * 180.0 / Math.PI, 3);
    }

    /// <summary>
    /// The loudspeaker's whole reason for existing: at resonance everything but the motional term
    /// cancels, so the peak is <c>Re + Rres</c> exactly -- 60.000 ohms, purely resistive, at 45 Hz.
    /// </summary>
    [Fact]
    public async Task LoudspeakerFixture_AtResonance_ReadsItsPeakImpedanceAndNoReactance()
    {
        var z = await MeasureAsync(
            SimulatedDutModel.ImpedanceLoudspeaker, SimulatedImpedanceFixture.SpeakerResonanceHz);

        Assert.Equal(SimulatedImpedanceFixture.SpeakerPeakOhms, z.Magnitude, 3);
        Assert.Equal(0.0, z.Phase * 180.0 / Math.PI, 2);
    }

    /// <summary>
    /// Well above resonance only the voice coil is left, and the magnitude settles on it to four
    /// figures. Not quite <i>all</i> of the motional term has gone, though: at 20 kHz -- 444 times
    /// the resonance -- it still leaves <b>-0.15 degrees</b> of reactance behind. That is the model
    /// being a real second-order circuit rather than a resistor with a bump drawn on it, and it is
    /// worth pinning rather than rounding away, since a measurement that reported exactly zero there
    /// would be losing something the fixture genuinely has.
    /// </summary>
    [Fact]
    public async Task LoudspeakerFixture_WellAboveResonance_SettlesOnTheVoiceCoilResistance()
    {
        var z = await MeasureAsync(SimulatedDutModel.ImpedanceLoudspeaker, 20_000.0);

        Assert.Equal(SimulatedImpedanceFixture.SpeakerReOhms, z.Magnitude, 3);
        Assert.Equal(-0.151, z.Phase * 180.0 / Math.PI, 3);
    }

    /// <summary>
    /// The invariant the whole measurement rests on: the fixture is on the left channel only, so the
    /// right one still carries the generator untouched. If a model ever filtered both, the ratio
    /// would divide the fixture out and every impedance would read as an open circuit.
    /// </summary>
    [Theory]
    [InlineData(SimulatedDutModel.ImpedanceResistive)]
    [InlineData(SimulatedDutModel.ImpedanceReactive)]
    [InlineData(SimulatedDutModel.ImpedanceLoudspeaker)]
    public async Task EveryFixture_LeavesTheRightChannelAsAStraightWire(SimulatedDutModel model)
    {
        const double freqHz = 1000.0;

        var (_, fixtureRight) = await CaptureAsync(model, freqHz);
        var (_, wireRight) = await CaptureAsync(SimulatedDutModel.StraightWire, freqHz);

        Assert.Equal(1.0, (fixtureRight / wireRight).Magnitude, 9);
        Assert.Equal(0.0, (fixtureRight / wireRight).Phase, 9);
    }

    /// <summary>Inverting the divider, exactly as the impedance measurement does.</summary>
    private static async Task<Complex> MeasureAsync(SimulatedDutModel model, double freqHz)
    {
        var (left, right) = await CaptureAsync(model, freqHz);
        Complex h = left / right;
        return SimulatedImpedanceFixture.ReferenceOhms * h / (Complex.One - h);
    }

    /// <summary>The impedance the discrete fixture actually presents, from its own transfer function.</summary>
    private static Complex ExpectedOhms(SimulatedDutModel model, double freqHz)
    {
        var dut = SimulatedDut.For(model, SampleRateHz)
            ?? throw new InvalidOperationException($"{model} has no filter cascade.");

        Complex h = dut.ResponseAt(freqHz);
        return SimulatedImpedanceFixture.ReferenceOhms * h / (Complex.One - h);
    }

    /// <summary>
    /// Both channels at one frequency, as complex numbers. A single-bin DFT over a whole number of
    /// seconds rather than an FFT: the ratio is all that is wanted, and every scale factor cancels in
    /// it.
    /// </summary>
    private static async Task<(Complex Left, Complex Right)> CaptureAsync(SimulatedDutModel model, double freqHz)
    {
        var device = new SimulatedDevice(randomSeed: 7);
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(freqHz, GeneratorDbv)]);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(tone, tone) { SnapToBin = false }, CancellationToken.None);

        // After the generator config, which rewrites GeneratorLeft/Right on the profile.
        device.SignalProfile = device.SignalProfile with
        {
            Dut = model,
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
        };

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        int wanted = SettleFrames + AnalysisFrames;

        var left = new List<double>(wanted);
        var right = new List<double>(wanted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            for (int frame = 0; (frame * config.ChannelCount) + 1 < block.SampleCount; frame++)
            {
                left.Add(block.Samples[frame * config.ChannelCount]);
                right.Add(block.Samples[(frame * config.ChannelCount) + 1]);
            }

            block.Dispose();
            if (left.Count >= wanted)
                break;
        }

        return (Bin(left, freqHz), Bin(right, freqHz));
    }

    private static Complex Bin(List<double> samples, double freqHz)
    {
        double w = 2.0 * Math.PI * freqHz / SampleRateHz;

        var accumulator = Complex.Zero;
        for (int n = 0; n < AnalysisFrames; n++)
            accumulator += samples[SettleFrames + n] * Complex.FromPolarCoordinates(1.0, -w * n);

        return accumulator;
    }
}
