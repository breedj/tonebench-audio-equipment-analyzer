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
public class PeriodicNoiseTests
{
    private const double SampleRateHz = 48_000.0;
    private const int Period = 8192;

    /// <summary>The complex amplitude of one whole period at bin <paramref name="bin"/>, as a magnitude.</summary>
    private static double BinMagnitude(PeriodicNoise noise, int bin, int period, long offset = 0)
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
        var noise = PeriodicNoise.For(Period, SampleRateHz, PeriodicNoise.DefaultCutoffHz);

        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(noise.Sample(i), noise.Sample(i + Period), 12);
            Assert.Equal(noise.Sample(i), noise.Sample(i + (7L * Period)), 12);
        }
    }

    [Fact]
    public void ANegativeOrHugeFrameIndex_StillLandsInThePeriod()
    {
        var noise = PeriodicNoise.For(Period, SampleRateHz, PeriodicNoise.DefaultCutoffHz);

        Assert.Equal(noise.Sample(3), noise.Sample(-Period + 3), 12);
        Assert.Equal(noise.Sample(5), noise.Sample((long)int.MaxValue * Period + 5), 12);
    }

    [Fact]
    public void ItIsUnitRms_SoALevelMeansWhatItSays()
    {
        var noise = PeriodicNoise.For(Period, SampleRateHz, PeriodicNoise.DefaultCutoffHz);

        double sum = 0.0;
        for (int i = 0; i < Period; i++)
            sum += noise.Sample(i) * noise.Sample(i);

        Assert.Equal(1.0, Math.Sqrt(sum / Period), 9);
    }

    [Fact]
    public void EveryBinAboveTheCornerFallsAtThreeDecibelsPerOctave()
    {
        // Pink: amplitude ~ 1/sqrt(f), so an octave up is 1/sqrt(2) -- 3.01 dB in power.
        var noise = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 20.0);
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
        var noise = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 40.0 * binHz);

        double reference = BinMagnitude(noise, 4, Period);
        foreach (int bin in new[] { 1, 2, 8, 16, 32 })
            Assert.Equal(reference, BinMagnitude(noise, bin, Period), 9);
    }

    [Fact]
    public void EveryBinInBandCarriesSomething()
    {
        // "Frequency components on all the FFT frequency bins", which is what makes it a usable
        // stimulus at every column of the result rather than at a comb of them.
        var noise = PeriodicNoise.For(1024, SampleRateHz, PeriodicNoise.DefaultCutoffHz);

        for (int bin = 1; bin < 512; bin++)
            Assert.True(BinMagnitude(noise, bin, 1024) > 0.0, $"bin {bin} is empty");
    }

    [Fact]
    public void ThereIsNoDc()
    {
        var noise = PeriodicNoise.For(Period, SampleRateHz, PeriodicNoise.DefaultCutoffHz);

        double sum = 0.0;
        for (int i = 0; i < Period; i++)
            sum += noise.Sample(i);

        Assert.Equal(0.0, sum / Period, 9);
    }

    [Fact]
    public void TheCrestFactorIsTheOneArtaImplements()
    {
        // ARTA's manual says both things in one paragraph: random phases give 12-13 dB, and "in
        // ARTA, the periodic noise generation is implemented with a crest factor lower than 10 dB".
        // We matched the definition and not the implementation until 2026-10-24, which cost about
        // 4 dB of drive at the same peak. Under 10 is the claim; the optimiser lands on 8.50.
        foreach (int period in new[] { 1024, 8192, 32768 })
        {
            var noise = PeriodicNoise.For(period, SampleRateHz, PeriodicNoise.DefaultCutoffHz);
            Assert.InRange(noise.CrestFactorDb, 8.0, 10.0);
        }
    }

    /// <summary>
    /// The optimisation moves phases and nothing else, which is what makes it safe to apply to a
    /// shape anyone is going to measure against. Stated here as well as implied by the shape tests
    /// above, because it is the invariant that a cheaper crest-reduction scheme would break.
    /// </summary>
    [Fact]
    public void TheCrestOptimisation_LeavesTheSpectrumExactlyWhereItWas()
    {
        var noise = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 20.0);

        // Pink to the third decimal across seven octaves, after sixteen clip-and-restore passes.
        foreach (int bin in new[] { 32, 64, 128, 256, 512, 1024 })
        {
            double perOctaveDb = 20.0 * Math.Log10(BinMagnitude(noise, bin * 2, Period) / BinMagnitude(noise, bin, Period));
            Assert.Equal(-3.0103, perOctaveDb, 3);
        }
    }

    /// <summary>
    /// And it leaves the waveform noise-like. ARTA describes its PN as having a normal distribution
    /// *and* a crest factor under 10 dB, and both halves have to hold: clipping hard enough would
    /// buy the crest factor by flat-topping the signal, which is a different stimulus. Measured
    /// kurtosis runs 2.82 to 3.31 against a Gaussian's 3, with around 1 % of samples within a
    /// hundredth of the peak.
    /// </summary>
    [Fact]
    public void TheCrestOptimisation_LeavesTheAmplitudesNearlyGaussian()
    {
        var noise = PeriodicNoise.For(Period, SampleRateHz, PeriodicNoise.DefaultCutoffHz);

        double fourth = 0.0;
        int atRail = 0;
        double peak = 0.0;
        for (long i = 0; i < Period; i++)
            peak = Math.Max(peak, Math.Abs(noise.Sample(i)));

        for (long i = 0; i < Period; i++)
        {
            double v = noise.Sample(i);
            fourth += v * v * v * v;
            if (Math.Abs(v) > peak * 0.99) atRail++;
        }

        // Unit RMS, so the fourth moment is the kurtosis.
        Assert.InRange(fourth / Period, 2.5, 3.6);
        Assert.InRange(100.0 * atRail / Period, 0.0, 5.0);
    }

    [Fact]
    public void TheSameSettingsGiveTheSameSequence_SoBothChannelsPlayOneStimulus()
    {
        // A stimulus is only a stimulus if the reference channel carries the same one, and a
        // re-primed TX queue has to rebuild the audio it already played.
        var first = PeriodicNoise.For(Period, SampleRateHz, 7.0);
        var again = PeriodicNoise.For(Period, SampleRateHz, 7.0);

        for (int i = 0; i < 200; i++)
            Assert.Equal(first.Sample(i), again.Sample(i), 12);
    }

    [Fact]
    public void RaisingTheCorner_TakesPowerOutOfTheBottomOfTheBand()
    {
        // Which is the whole reason the control exists. Pure 1/f keeps rising all the way down, and
        // that power is spent DAC headroom and DC into a voice coil; holding the magnitude flat
        // below a corner stops the climb while still exciting those frequencies.
        var low = PeriodicNoise.For(Period, SampleRateHz, 5.0);
        var high = PeriodicNoise.For(Period, SampleRateHz, 200.0);

        double lowTilt = BinMagnitude(low, 2, Period) / BinMagnitude(low, 1024, Period);
        double highTilt = BinMagnitude(high, 2, Period) / BinMagnitude(high, 1024, Period);

        Assert.True(highTilt < lowTilt, $"corner 200 Hz tilted {highTilt:0.0} against 5 Hz's {lowTilt:0.0}");
    }

    /// <summary>Octave-band level, in dB: the power of every bin whose frequency falls in the band.</summary>
    private static double BandLevelDb(PeriodicNoise noise, double centreHz, int period, double sampleRateHz)
    {
        double binHz = sampleRateHz / period;
        int first = (int)Math.Ceiling(centreHz / Math.Sqrt(2.0) / binHz);
        int last = (int)Math.Floor(centreHz * Math.Sqrt(2.0) / binHz);

        double power = 0.0;
        for (int k = first; k <= last; k++)
        {
            double a = BinMagnitude(noise, k, period);
            power += a * a;
        }

        return 10.0 * Math.Log10(power);
    }

    private static readonly double[] SpeechBandHz = [125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0];

    [Fact]
    public void ThePinkSequence_IsUnchangedByTheShapeArgumentArriving()
    {
        // The impedance measurement asked for periodic noise before any other shape existed, and
        // plays whatever the default gives: this is what says the default did not move.
        var byDefault = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 5.0);
        var explicitly = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 5.0, PeriodicNoiseShape.Pink);

        for (long i = 0; i < Period; i++)
            Assert.Equal(byDefault.Sample(i), explicitly.Sample(i), 15);
    }

    [Fact]
    public void White_PutsTheSameAmplitudeOnEveryBin()
    {
        var noise = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 20.0, PeriodicNoiseShape.White);

        double reference = BinMagnitude(noise, 64, Period);
        foreach (int bin in new[] { 1, 8, 32, 256, 1024, 2048, 4095 })
            Assert.Equal(reference, BinMagnitude(noise, bin, Period), 9);
    }

    [Fact]
    public void White_IgnoresTheCutoff_HavingNoSlopeForItToStart()
    {
        var low = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 5.0, PeriodicNoiseShape.White);
        var high = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 500.0, PeriodicNoiseShape.White);

        for (long i = 0; i < 64; i++)
            Assert.Equal(low.Sample(i), high.Sample(i), 12);
    }

    /// <summary>
    /// The speech shapes are octave-band levels from IEC 60268-16, so what has to hold is the level
    /// of each band against the others. The table is the thing that cannot be re-derived from the
    /// code, which is the whole reason this test states it a second time.
    /// </summary>
    [Theory]
    [InlineData(PeriodicNoiseShape.SpeechMale2011, new[] { 2.9, 2.9, -0.8, -6.8, -12.8, -18.8, -24.8 })]
    [InlineData(PeriodicNoiseShape.SpeechFemale2011, new[] { -4.4, 5.3, -1.9, -9.1, -15.8, -16.7, -18.0 })]
    [InlineData(PeriodicNoiseShape.SpeechMale2020, new[] { -3.1, -0.1, -0.8, -6.8, -12.8, -18.8, -24.8 })]
    public void TheSpeechShapes_CarryTheirStandardsOwnOctaveBandLevels(PeriodicNoiseShape shape, double[] expectedDb)
    {
        const int period = 32768;
        var noise = PeriodicNoise.For(period, SampleRateHz, cutoffHz: 20.0, shape);

        // Against the 1 kHz band, since every row is read as a shape rather than as a level and the
        // sequence is normalised to unit RMS.
        double measuredRef = BandLevelDb(noise, 1000.0, period, SampleRateHz);
        double expectedRef = expectedDb[3];

        for (int b = 0; b < SpeechBandHz.Length; b++)
        {
            double measured = BandLevelDb(noise, SpeechBandHz[b], period, SampleRateHz) - measuredRef;
            double expected = expectedDb[b] - expectedRef;
            Assert.True(
                Math.Abs(measured - expected) < 0.5,
                $"{SpeechBandHz[b]:0} Hz read {measured:0.00} dB re 1 kHz, expected {expected:0.00}");
        }
    }

    [Fact]
    public void TheTwoMaleShapes_DifferOnlyAtTheBottomTwoBands()
    {
        const int period = 32768;
        var y2011 = PeriodicNoise.For(period, SampleRateHz, cutoffHz: 20.0, PeriodicNoiseShape.SpeechMale2011);
        var y2020 = PeriodicNoise.For(period, SampleRateHz, cutoffHz: 20.0, PeriodicNoiseShape.SpeechMale2020);

        // Both are normalised to unit RMS, so taking less out of the bottom lifts everything else by
        // a common offset: what the 2020 revision changed is 125 Hz and 250 Hz and nothing more.
        double offset = BandLevelDb(y2020, 4000.0, period, SampleRateHz) - BandLevelDb(y2011, 4000.0, period, SampleRateHz);

        foreach (double centreHz in new[] { 500.0, 1000.0, 2000.0, 8000.0 })
        {
            double moved = BandLevelDb(y2020, centreHz, period, SampleRateHz) - BandLevelDb(y2011, centreHz, period, SampleRateHz);
            Assert.Equal(offset, moved, 1);
        }

        double at125 = BandLevelDb(y2020, 125.0, period, SampleRateHz) - BandLevelDb(y2011, 125.0, period, SampleRateHz);
        double at250 = BandLevelDb(y2020, 250.0, period, SampleRateHz) - BandLevelDb(y2011, 250.0, period, SampleRateHz);
        Assert.Equal(offset - 6.0, at125, 1);
        Assert.Equal(offset - 3.0, at250, 1);
    }

    [Fact]
    public void EverySpeechShape_StillRepeatsAndIsUnitRms()
    {
        foreach (var shape in new[] { PeriodicNoiseShape.SpeechMale2011, PeriodicNoiseShape.SpeechFemale2011, PeriodicNoiseShape.SpeechMale2020, PeriodicNoiseShape.White })
        {
            var noise = PeriodicNoise.For(Period, SampleRateHz, cutoffHz: 20.0, shape);

            double sum = 0.0;
            for (long i = 0; i < Period; i++)
            {
                Assert.Equal(noise.Sample(i), noise.Sample(i + Period), 12);
                sum += noise.Sample(i) * noise.Sample(i);
            }

            Assert.Equal(1.0, Math.Sqrt(sum / Period), 9);
            Assert.InRange(noise.CrestFactorDb, 8.0, 10.0);
        }
    }
}
