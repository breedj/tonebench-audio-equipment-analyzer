using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;
using Tonebench.Dsp;

namespace Tonebench.Device.Tests;

/// <summary>
/// The exponential chirp exists in three places — the hardware TX path
/// (<see cref="Qa40xGeneratorWaveform"/>), the simulator (<see cref="SignalSynthesizer"/>) and the
/// canonical <see cref="ExponentialChirp"/> that the frequency-response measurement builds its
/// reference spectrum from. They are separate copies on purpose: <c>Tonebench.Device</c> has no
/// reference to <c>Tonebench.Dsp</c>, and the hardware path is deliberately decoupled from the
/// simulator so no DUT-simulation artefact can leak onto a real DAC.
///
/// <para><b>This is what stops that decision from becoming a bug.</b> The measurement deconvolves
/// what the DAC played against what <see cref="ExponentialChirp"/> says it played. If the two ever
/// disagree, nothing throws and nothing looks wrong — the difference is silently reported as the
/// DUT's frequency response. Any change to one formula must be made to all three.</para>
/// </summary>
public class ChirpFormulaAgreementTests
{
    private const double SampleRateHz = 48_000.0;
    private const int RepeatFrames = 4096;
    private const double StartHz = 20.0;
    private const double EndHz = 20_000.0;
    private const double AmplitudeDbv = -6.0;

    [Fact]
    public void HardwareWaveform_MatchesTheCanonicalChirp_SampleForSample()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = ChirpChannel();

        var expected = CanonicalPeriod();

        for (int frame = 0; frame < RepeatFrames; frame++)
            Assert.Equal(expected[frame], waveform.ComputeSample(channel, frame), precision: 12);
    }

    [Fact]
    public void Simulator_MatchesTheCanonicalChirp_SampleForSample()
    {
        var synthesizer = new SignalSynthesizer(new Random(1), SampleRateHz);
        var profile = new SimulatedSignalProfile
        {
            GeneratorLeft = ChirpChannel(),
            HarmonicsEnabled = false,
            NoiseEnabled = false,
            HumEnabled = false,
        };

        var expected = CanonicalPeriod();

        for (int frame = 0; frame < RepeatFrames; frame++)
            Assert.Equal(expected[frame], synthesizer.ComputeChannelSample(profile, frame, channelIndex: 0), precision: 12);
    }

    [Fact]
    public void HardwareWaveform_RepeatsExactlyOnThePeriod()
    {
        // The property the whole frequency-response measurement rests on. One frame of drift here
        // puts a slow ripple on every measured trace, and nothing else would report it.
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = ChirpChannel();

        for (int frame = 0; frame < RepeatFrames; frame += 37)
        {
            double first = waveform.ComputeSample(channel, frame);
            Assert.Equal(first, waveform.ComputeSample(channel, frame + RepeatFrames), precision: 12);
            Assert.Equal(first, waveform.ComputeSample(channel, frame + (17L * RepeatFrames)), precision: 12);
        }
    }

    [Fact]
    public void HardwareWaveform_StaysPeriodicMillionsOfFramesIn()
    {
        // Integer modulo on the frame index rather than a floating-point remainder of elapsed
        // seconds: after an hour of streaming the latter has accumulated enough rounding to move
        // the wrap off its frame.
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = ChirpChannel();

        const long farOut = 48_000L * 3600L; // one hour of frames
        long aligned = farOut - (farOut % RepeatFrames);

        for (int frame = 0; frame < RepeatFrames; frame += 101)
            Assert.Equal(
                waveform.ComputeSample(channel, frame),
                waveform.ComputeSample(channel, aligned + frame),
                precision: 12);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(8192)]
    [InlineData(65536)]
    public void HardwareWaveform_HonoursWhicheverPeriodTheMeasurementAsksFor(int repeatFrames)
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = new EchoChirpSettings
            {
                AmplitudeDbv = AmplitudeDbv,
                StartFrequencyHz = StartHz,
                EndFrequencyHz = EndHz,
                RepeatFrames = repeatFrames,
            },
        };

        for (int frame = 0; frame < repeatFrames; frame += Math.Max(1, repeatFrames / 20))
            Assert.Equal(
                waveform.ComputeSample(channel, frame),
                waveform.ComputeSample(channel, frame + repeatFrames),
                precision: 12);
    }

    [Fact]
    public void HardwareWaveform_WithADegeneratePeriod_IsSilentRatherThanThrowing()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = new ChannelGeneratorConfig(true, [])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = new EchoChirpSettings { RepeatFrames = 0 },
        };

        Assert.Equal(0.0, waveform.ComputeSample(channel, 123L));
    }

    private static ChannelGeneratorConfig ChirpChannel() =>
        new(true, [])
        {
            Kind = GeneratorKind.EchoChirp,
            Chirp = new EchoChirpSettings
            {
                AmplitudeDbv = AmplitudeDbv,
                StartFrequencyHz = StartHz,
                EndFrequencyHz = EndHz,
                RepeatFrames = RepeatFrames,
            },
        };

    private static double[] CanonicalPeriod()
    {
        var period = new double[RepeatFrames];
        ExponentialChirp.Fill(
            period,
            StartHz,
            EndHz,
            SampleRateHz,
            ExponentialChirp.PeakVoltsFromDbv(AmplitudeDbv));
        return period;
    }
}
