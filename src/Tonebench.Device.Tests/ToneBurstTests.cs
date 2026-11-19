using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests;

/// <summary>
/// The tone-burst stimulus. Its assertions are about the <b>envelope</b> rather than about a
/// spectrum, which is the shape of the measurement: what a burst shows is the first and last cycle,
/// and a THD figure taken over one says nothing at all.
/// </summary>
public class ToneBurstTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>750 Hz at 48 kHz is 64 frames a cycle, exactly.</summary>
    private const double FreqHz = 750.0;

    private const int CycleFrames = 64;

    private static ToneBurstSettings Settings(int on = 5, int off = 20, double levelDbv = 0.0) =>
        new() { FrequencyHz = FreqHz, LevelDbv = levelDbv, CyclesOn = on, CyclesOff = off };

    private static ChannelGeneratorConfig Channel(ToneBurstSettings settings) =>
        new(Enabled: true, Tones: []) { Kind = GeneratorKind.ToneBurst, Burst = settings };

    private static double[] Record(ToneBurstSettings settings, int frames)
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = Channel(settings);
        return [.. Enumerable.Range(0, frames).Select(i => waveform.ComputeSample(channel, i))];
    }

    [Fact]
    public void TheBurstRepeatsOnAWholeNumberOfFrames()
    {
        var settings = Settings(on: 5, off: 20);

        Assert.Equal(CycleFrames, ToneBurst.CycleFrames(settings, SampleRateHz));
        Assert.Equal(25L * CycleFrames, ToneBurst.BurstPeriodFrames(settings, SampleRateHz));
        Assert.Equal(SampleRateHz / (25.0 * CycleFrames), ToneBurst.RepetitionHz(settings, SampleRateHz), precision: 9);
        Assert.Equal(0.2, ToneBurst.DutyFraction(settings), precision: 12);
    }

    /// <summary>
    /// A burst is periodic over its own repeat, exactly — the same property every other periodic
    /// kind here has, and for the same reason: a repeat that landed between samples would walk the
    /// waveform off its grid and smear every line.
    /// </summary>
    [Fact]
    public void ItIsPeriodicOverThatRepeat()
    {
        var settings = Settings(on: 3, off: 7);
        long period = ToneBurst.BurstPeriodFrames(settings, SampleRateHz);

        for (long i = 0; i < 200; i++)
        {
            Assert.Equal(
                ToneBurst.Sample(settings, i, SampleRateHz),
                ToneBurst.Sample(settings, i + period, SampleRateHz),
                precision: 15);
        }
    }

    /// <summary>
    /// The gate opens and closes on whole carrier cycles, so both edges fall on a zero crossing and
    /// there is no step to taper. Stated as a bound on the largest sample-to-sample jump in the
    /// whole record: it is no bigger than the carrier's own steepest step, which is what "the gate
    /// adds no discontinuity" means in a sampled signal.
    /// </summary>
    [Fact]
    public void TheGateAddsNoStepTheCarrierDoesNotAlreadyHave()
    {
        var settings = Settings(on: 4, off: 4);
        var record = Record(settings, CycleFrames * 8 * 3);

        double carrierStep = ToneBurst.PeakVolts(settings.LevelDbv) * Math.Sin(2.0 * Math.PI / CycleFrames);

        double worst = 0.0;
        for (int i = 1; i < record.Length; i++)
            worst = Math.Max(worst, Math.Abs(record[i] - record[i - 1]));

        Assert.True(worst <= carrierStep * 1.000001, $"worst step {worst:0.######} against {carrierStep:0.######}");
    }

    /// <summary>
    /// The first sample of every burst is the carrier's own zero, and the frames between bursts are
    /// exactly zero rather than nearly so.
    /// </summary>
    [Fact]
    public void TheQuietPartIsSilenceAndTheBurstStartsAtZero()
    {
        var settings = Settings(on: 2, off: 3);
        var record = Record(settings, CycleFrames * 5 * 2);

        Assert.Equal(0.0, record[0], precision: 15);

        for (int i = CycleFrames * 2; i < CycleFrames * 5; i++)
            Assert.Equal(0.0, record[i], precision: 15);

        // And the next burst begins at the same place in the carrier as the first did.
        Assert.Equal(record[0], record[CycleFrames * 5], precision: 15);
        Assert.Equal(record[1], record[CycleFrames * 5 + 1], precision: 15);
    }

    /// <summary>
    /// Zero cycles off is a continuous tone, deliberately: it is the control case a reader compares
    /// the burst against, and refusing it would mean leaving the screen to get it.
    /// </summary>
    [Fact]
    public void WithNothingOff_ItIsAPlainSine()
    {
        var record = Record(Settings(on: 1, off: 0), CycleFrames * 4);
        double peak = ToneBurst.PeakVolts(0.0);

        for (int i = 0; i < record.Length; i++)
            Assert.Equal(peak * Math.Sin(2.0 * Math.PI * i / CycleFrames), record[i], precision: 12);
    }

    /// <summary>
    /// <b>The level names the carrier, not the stimulus.</b> So the peak does not move as the gate
    /// narrows — which is what keeps the output range where it is — while the overall rms falls as
    /// the square root of the duty, and the carrier <i>line</i> a spectrum reads falls as the duty
    /// itself. All three are different numbers and the app quotes each where it means it.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0.0, 0.0)]
    [InlineData(1, 1, -3.0103, -6.0206)]
    [InlineData(1, 3, -6.0206, -12.0412)]
    [InlineData(5, 20, -6.9897, -13.9794)]
    public void TheLevelIsTheCarriers_AndTheOtherTwoFollowTheDuty(
        int on, int off, double expectedOverallDb, double expectedLineDb)
    {
        var settings = Settings(on, off, levelDbv: -10.0);
        var record = Record(settings, (int)ToneBurst.BurstPeriodFrames(settings, SampleRateHz) * 4);

        // The peak is the carrier's own, at any duty.
        Assert.Equal(ToneBurst.PeakVolts(-10.0), record.Max(Math.Abs), precision: 9);

        // And the measured broadband rms is the overall figure, not the level that was typed.
        double measured = 20.0 * Math.Log10(Math.Sqrt(record.Sum(v => v * v) / record.Length));
        Assert.Equal(-10.0 + expectedOverallDb, measured, precision: 6);
        Assert.Equal(-10.0 + expectedOverallDb, ToneBurst.OverallRmsDbv(settings), precision: 4);
        Assert.Equal(-10.0 + expectedLineDb, ToneBurst.CarrierLineDbv(settings), precision: 4);
    }

    /// <summary>
    /// The range selector is given the carrier's peak, so narrowing the burst never steps the range
    /// down — a range derived from the overall rms would, and would then clip every burst.
    /// </summary>
    [Fact]
    public void TheRangeSelectorIsGivenTheCarriersPeak()
    {
        double wide = OutputRangeSelector.RequiredPeakVolts(Channel(Settings(on: 100, off: 1)));
        double narrow = OutputRangeSelector.RequiredPeakVolts(Channel(Settings(on: 1, off: 100)));

        Assert.Equal(Math.Sqrt(2.0), wide, precision: 9);
        Assert.Equal(wide, narrow, precision: 12);
    }

    /// <summary>
    /// The carrier goes through the shape family's own quantiser, so a burst and a square asked for
    /// the same frequency play the same frequency — which is what makes "the same tone, gated" a
    /// true description of the comparison.
    /// </summary>
    [Theory]
    [InlineData(1000.0)]
    [InlineData(10_000.0)]
    [InlineData(700.0)]
    public void TheCarrierIsQuantisedLikeTheRestOfTheFamily(double freqHz)
    {
        var settings = Settings() with { FrequencyHz = freqHz };

        Assert.Equal(
            PeriodicWave.EffectiveFrequencyHz(freqHz, SampleRateHz),
            ToneBurst.EffectiveFrequencyHz(settings, SampleRateHz),
            precision: 12);
    }

    /// <summary>
    /// Counts outside what this class will play are clamped rather than left to overflow the frame
    /// arithmetic — the endpoint refuses them first, so this is the backstop for a caller that holds
    /// the settings directly.
    /// </summary>
    [Fact]
    public void TheCycleCountsAreClamped()
    {
        Assert.Equal(ToneBurst.MinCyclesOn, ToneBurst.CyclesOn(Settings(on: 0)));
        Assert.Equal(ToneBurst.MaxCycles, ToneBurst.CyclesOn(Settings(on: int.MaxValue)));
        Assert.Equal(ToneBurst.MinCyclesOff, ToneBurst.CyclesOff(Settings(off: -5)));
        Assert.Equal(ToneBurst.MaxCycles, ToneBurst.CyclesOff(Settings(off: int.MaxValue)));

        // And the period it produces still fits, which is the whole point of the ceiling.
        var biggest = Settings(on: ToneBurst.MaxCycles, off: ToneBurst.MaxCycles) with { FrequencyHz = 20.0 };
        Assert.True(ToneBurst.BurstPeriodFrames(biggest, 192_000.0) > 0);
    }

    /// <summary>
    /// The simulator plays the same samples the hardware does, like the shapes do — a gate that
    /// opened at a different place under <c>--simulate</c> would be a different measurement.
    /// </summary>
    [Fact]
    public void TheSimulatorPlaysTheSameBurst()
    {
        var settings = Settings(on: 3, off: 5, levelDbv: -12.0);
        var channel = Channel(settings);

        var hardware = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var simulator = new Simulation.SignalSynthesizer(new Random(2), SampleRateHz);
        var profile = new Simulation.SimulatedSignalProfile
        {
            GeneratorLeft = channel,
            HarmonicsEnabled = false,
            HumEnabled = false,
            NoiseEnabled = false,
        };

        for (long frame = 0; frame < CycleFrames * 8 * 2; frame++)
        {
            Assert.Equal(
                hardware.ComputeSample(channel, frame),
                simulator.ComputeChannelSample(profile, frame, channelIndex: 0),
                precision: 15);
        }
    }
}
