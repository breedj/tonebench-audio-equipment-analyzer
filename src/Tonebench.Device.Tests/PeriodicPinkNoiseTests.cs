using Tonebench.Device;

namespace Tonebench.Device.Tests;

/// <summary>
/// The four things a periodic pink stimulus has to be: periodic, pink above its corner, flat below
/// it, and present on every bin at an amplitude that does not depend on which record you look at.
///
/// <para>Measured with a Goertzel at the bins themselves rather than with a transform, which is both
/// exact for a signal whose period <i>is</i> the record and avoids giving this project an FFT it
/// otherwise has no use for.</para>
/// </summary>
public class PeriodicPinkNoiseTests
{
    private const double SampleRateHz = 48_000.0;
    private const int Period = 8192;

    /// <summary>The complex amplitude of one whole period at bin <paramref name="bin"/>, as a magnitude.</summary>
    private static double BinMagnitude(PeriodicPinkNoise noise, int bin, int period, long offset = 0)
    {
        double re = 0.0;
        double im = 0.0;
        for (int i = 0; i < period; i++)
        {
            double sample = noise.Sample(offset + i);
            double angle = -2.0 * Math.PI * bin * i / period;
            re += sample * Math.Cos(angle);
            im += sample * Math.Sin(angle);
        }

        return 2.0 * Math.Sqrt((re * re) + (im * im)) / period;
    }

    [Fact]
    public void TheSequenceRepeatsExactlyAfterOnePeriod()
    {
        // The whole point. Every analysis record holds the same waveform, so every bin is excited by
        // the same deterministic amplitude every time.
        var noise = PeriodicPinkNoise.For(Period, SampleRateHz, PeriodicPinkNoise.DefaultCutoffHz);

        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(noise.Sample(i), noise.Sample(i + Period), 12);
            Assert.Equal(noise.Sample(i), noise.Sample(i + (7L * Period)), 12);
        }
    }

    [Fact]
    public void ANegativeOrHugeFrameIndex_StillLandsInThePeriod()
    {
        var noise = PeriodicPinkNoise.For(Period, SampleRateHz, PeriodicPinkNoise.DefaultCutoffHz);

        Assert.Equal(noise.Sample(3), noise.Sample(-Period + 3), 12);
        Assert.Equal(noise.Sample(5), noise.Sample((long)int.MaxValue * Period + 5), 12);
    }

    [Fact]
    public void ItIsUnitRms_SoALevelMeansWhatItSays()
    {
        var noise = PeriodicPinkNoise.For(Period, SampleRateHz, PeriodicPinkNoise.DefaultCutoffHz);

        double sum = 0.0;
        for (int i = 0; i < Period; i++)
            sum += noise.Sample(i) * noise.Sample(i);

        Assert.Equal(1.0, Math.Sqrt(sum / Period), 9);
    }

    [Fact]
    public void EveryBinAboveTheCornerFallsAtThreeDecibelsPerOctave()
    {
        // Pink: amplitude ~ 1/sqrt(f), so an octave up is 1/sqrt(2) -- 3.01 dB in power.
        var noise = PeriodicPinkNoise.For(Period, SampleRateHz, cutoffHz: 20.0);
        double binHz = SampleRateHz / Period;

        // Stopping short of Nyquist, which carries no component: it has no conjugate partner to
        // hold a phase, so it is left out of the spectrum entirely.
        foreach (int bin in new[] { 32, 64, 128, 256, 512, 1024 })
        {
            double lower = BinMagnitude(noise, bin, Period);
            double upper = BinMagnitude(noise, bin * 2, Period);
            double perOctaveDb = 20.0 * Math.Log10(upper / lower);

            Assert.True(
                Math.Abs(perOctaveDb + 3.0103) < 0.01,
                $"{bin * binHz:0.#} Hz to {bin * 2 * binHz:0.#} Hz fell {perOctaveDb:0.00} dB");
        }
    }

    [Fact]
    public void BelowTheCornerItIsFlat_RatherThanPilingPowerIntoTheBottomOfTheBand()
    {
        double binHz = SampleRateHz / Period;
        var noise = PeriodicPinkNoise.For(Period, SampleRateHz, cutoffHz: 40.0 * binHz);

        double reference = BinMagnitude(noise, 4, Period);
        foreach (int bin in new[] { 1, 2, 8, 16, 32 })
            Assert.Equal(reference, BinMagnitude(noise, bin, Period), 9);
    }

    [Fact]
    public void EveryBinInBandCarriesSomething()
    {
        // "Frequency components on all the FFT frequency bins", which is what makes it a usable
        // stimulus at every column of the result rather than at a comb of them.
        var noise = PeriodicPinkNoise.For(1024, SampleRateHz, PeriodicPinkNoise.DefaultCutoffHz);

        for (int bin = 1; bin < 512; bin++)
            Assert.True(BinMagnitude(noise, bin, 1024) > 0.0, $"bin {bin} is empty");
    }

    [Fact]
    public void ThereIsNoDc()
    {
        var noise = PeriodicPinkNoise.For(Period, SampleRateHz, PeriodicPinkNoise.DefaultCutoffHz);

        double sum = 0.0;
        for (int i = 0; i < Period; i++)
            sum += noise.Sample(i);

        Assert.Equal(0.0, sum / Period, 9);
    }

    [Fact]
    public void TheCrestFactorIsTheOneArtaDescribes()
    {
        // ARTA's manual puts pink PN "slightly above 12 dB", which is what random phases give. This
        // is the number OutputRangeSelector.NoiseCrestFactorDb has to cover, so it is pinned here
        // rather than left to vary with the phase hash.
        foreach (int period in new[] { 1024, 8192, 32768 })
        {
            var noise = PeriodicPinkNoise.For(period, SampleRateHz, PeriodicPinkNoise.DefaultCutoffHz);
            Assert.InRange(noise.CrestFactorDb, 9.0, 14.5);
        }
    }

    [Fact]
    public void TheSameSettingsGiveTheSameSequence_SoBothChannelsPlayOneStimulus()
    {
        // A stimulus is only a stimulus if the reference channel carries the same one, and a
        // re-primed TX queue has to rebuild the audio it already played.
        var first = PeriodicPinkNoise.For(Period, SampleRateHz, 7.0);
        var again = PeriodicPinkNoise.For(Period, SampleRateHz, 7.0);

        for (int i = 0; i < 200; i++)
            Assert.Equal(first.Sample(i), again.Sample(i), 12);
    }

    [Fact]
    public void RaisingTheCorner_TakesPowerOutOfTheBottomOfTheBand()
    {
        // Which is the whole reason the control exists. Pure 1/f keeps rising all the way down, and
        // that power is spent DAC headroom and DC into a voice coil; holding the magnitude flat
        // below a corner stops the climb while still exciting those frequencies.
        var low = PeriodicPinkNoise.For(Period, SampleRateHz, 5.0);
        var high = PeriodicPinkNoise.For(Period, SampleRateHz, 200.0);

        double lowTilt = BinMagnitude(low, 2, Period) / BinMagnitude(low, 1024, Period);
        double highTilt = BinMagnitude(high, 2, Period) / BinMagnitude(high, 1024, Period);

        Assert.True(highTilt < lowTilt, $"corner 200 Hz tilted {highTilt:0.0} against 5 Hz's {lowTilt:0.0}");
    }
}
