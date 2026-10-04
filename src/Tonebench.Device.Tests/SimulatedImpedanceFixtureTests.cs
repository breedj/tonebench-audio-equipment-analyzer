using System.Numerics;
using Tonebench.Device.Simulation;

namespace Tonebench.Device.Tests;

/// <summary>
/// The three <c>Impedance*</c> fixture models, measured the way the impedance screen measures them:
/// stream both channels, take the complex ratio at one coherent frequency, and invert the divider.
///
/// <para>These are end-to-end through <see cref="SimulatedDevice"/> rather than unit tests of the
/// coefficients, so they prove the fixture is actually in the signal path and on the channel the
/// measurement expects to find it on: the <b>right</b>, with the left carrying the generator side —
/// bench wiring, so the impedance screen's default reference channel (Left) is the correct one.</para>
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

    /// <summary>
    /// A fifth of a second at 48 kHz, so the bin spacing is 5 Hz and every frequency this class measures
    /// at -- 20, 45, 50, 100, 955, 1000, 10000, 20000 -- is a multiple of it and lands exactly on a bin,
    /// which is what keeps the single-bin DFT free of leakage. It was a whole second until 2026-09-30;
    /// the simulator streams at real-time cadence on purpose, so a record here costs its own duration in
    /// wall clock, and this class was 57 s of a 9 m 28 s suite. Shortening it is only safe while every
    /// frequency above stays a multiple of 48000/AnalysisFrames.
    /// </summary>
    private const int AnalysisFrames = 9_600;

    /// <summary>Discarded before analysis. The sharpest fixture here rings for about 18 ms, so a quarter of a second is some 14 time constants -- and the assertions here run to six decimal places, so this is the constant that fails first if it is cut too far.</summary>
    private const int SettleFrames = 12_000;

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

    [Theory]
    [InlineData(50.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public async Task CapacitiveFixture_MatchesItsOwnDiscreteImpedance(double freqHz)
    {
        var expected = ExpectedOhms(SimulatedDutModel.ImpedanceCapacitive, freqHz);
        var z = await MeasureAsync(SimulatedDutModel.ImpedanceCapacitive, freqHz);

        Assert.Equal(expected.Magnitude, z.Magnitude, expected.Magnitude * 1e-6);
        Assert.Equal(expected.Phase, z.Phase, 6);
    }

    /// <summary>
    /// The capacitor is the mirror of the inductor: the same 6 dB per octave on a log plot, in the
    /// other direction, and the phase is what tells them apart. Well below Nyquist it is the
    /// <c>ESR - j/(wC)</c> it was written as — 169 ohms at -89.9 degrees at 20 Hz.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(100.0)]
    public async Task CapacitiveFixture_WellBelowNyquist_IsTheSeriesCircuitItWasWrittenAs(double freqHz)
    {
        var analog = new Complex(
            SimulatedImpedanceFixture.CapacitiveEsrOhms,
            -1.0 / (2.0 * Math.PI * freqHz * SimulatedImpedanceFixture.CapacitiveFarad));

        var z = await MeasureAsync(SimulatedDutModel.ImpedanceCapacitive, freqHz);

        Assert.Equal(analog.Magnitude, z.Magnitude, analog.Magnitude * 1e-4);
        Assert.Equal(analog.Phase * 180.0 / Math.PI, z.Phase * 180.0 / Math.PI, 2);
        Assert.True(z.Phase < -1.5, $"a capacitor's phase must be near -90 degrees, not {z.Phase * 180.0 / Math.PI:F1}");
    }

    /// <summary>
    /// The short reads what a real lead set read on the bench: 0.12 ohms, with 0.6 uH lifting it to
    /// 17 degrees by 10 kHz. That is a divider ratio of about -58 dB, so this is also the fixture
    /// that says the measurement still resolves a load sixty decibels under the reference. Below a
    /// kilohertz it is the circuit it was written as; at 10 kHz the inductance has warped like the
    /// reactive fixture's (see the class remarks) and the discrete filter is the reference.
    /// </summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    public async Task ShortFixture_ReadsItsLeads(double freqHz)
    {
        var analog = new Complex(
            SimulatedImpedanceFixture.ShortOhms,
            2.0 * Math.PI * freqHz * SimulatedImpedanceFixture.ShortHenry);

        var z = await MeasureAsync(SimulatedDutModel.ImpedanceShort, freqHz);

        Assert.Equal(analog.Magnitude, z.Magnitude, analog.Magnitude * 1e-3);
        Assert.Equal(analog.Phase * 180.0 / Math.PI, z.Phase * 180.0 / Math.PI, 1);
    }

    [Fact]
    public async Task ShortFixture_AtTheTopOfTheBand_MatchesItsOwnDiscreteImpedance()
    {
        var expected = ExpectedOhms(SimulatedDutModel.ImpedanceShort, 10_000.0);
        var z = await MeasureAsync(SimulatedDutModel.ImpedanceShort, 10_000.0);

        Assert.Equal(expected.Magnitude, z.Magnitude, expected.Magnitude * 1e-4);
        Assert.Equal(expected.Phase * 180.0 / Math.PI, z.Phase * 180.0 / Math.PI, 2);
        Assert.InRange(z.Phase * 180.0 / Math.PI, 15.0, 25.0);
    }

    /// <summary>
    /// The open is open — 1.6 megohms at 1 kHz, from a 100 pF stray — and, unlike a straight wire,
    /// its divider ratio is not exactly one, so inverting it is a large finite number rather than a
    /// division by zero.
    /// </summary>
    [Fact]
    public async Task OpenFixture_ReadsAsAnOpenWithoutDividingByZero()
    {
        var (left, right) = await CaptureAsync(SimulatedDutModel.ImpedanceOpen, 1000.0);
        Complex h = left / right;
        Assert.NotEqual(Complex.One, h);

        var z = await MeasureAsync(SimulatedDutModel.ImpedanceOpen, 1000.0);
        double expectedOhms = 1.0 / (2.0 * Math.PI * 1000.0 * SimulatedImpedanceFixture.OpenStrayFarad);

        Assert.True(double.IsFinite(z.Magnitude));
        Assert.Equal(expectedOhms, z.Magnitude, expectedOhms * 0.05);
    }

    /// <summary>
    /// The invariant the whole measurement rests on: the fixture is on the right channel only, so the
    /// left one still carries the generator untouched. If a model ever filtered both, the ratio
    /// would divide the fixture out and every impedance would read as an open circuit.
    /// </summary>
    [Theory]
    [InlineData(SimulatedDutModel.ImpedanceResistive)]
    [InlineData(SimulatedDutModel.ImpedanceReactive)]
    [InlineData(SimulatedDutModel.ImpedanceLoudspeaker)]
    [InlineData(SimulatedDutModel.ImpedanceCapacitive)]
    [InlineData(SimulatedDutModel.ImpedanceShort)]
    [InlineData(SimulatedDutModel.ImpedanceOpen)]
    public async Task EveryFixture_LeavesTheLeftChannelAsAStraightWire(SimulatedDutModel model)
    {
        const double freqHz = 1000.0;

        var (fixtureLeft, _) = await CaptureAsync(model, freqHz);
        var (wireLeft, _) = await CaptureAsync(SimulatedDutModel.StraightWire, freqHz);

        Assert.Equal(1.0, (fixtureLeft / wireLeft).Magnitude, 9);
        Assert.Equal(0.0, (fixtureLeft / wireLeft).Phase, 9);
    }

    /// <summary>Inverting the divider, exactly as the impedance measurement does with the reference on the left.</summary>
    private static async Task<Complex> MeasureAsync(SimulatedDutModel model, double freqHz)
    {
        var (left, right) = await CaptureAsync(model, freqHz);
        Complex h = right / left;
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
    /// Both channels at one frequency, as complex numbers. A single-bin DFT over a whole number of periods of
    /// the tone rather than an FFT: the ratio is all that is wanted, and every scale factor cancels in
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
