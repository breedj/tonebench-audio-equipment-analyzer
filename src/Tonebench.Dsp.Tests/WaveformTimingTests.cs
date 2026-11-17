using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The horizontal readings and the two levels they are measured between, against waveforms whose
/// answers are arithmetic rather than another implementation's output.
///
/// <para>A scope reading that is plausibly wrong is the failure this whole screen has to avoid, so
/// every expectation here is a figure the waveform was <i>built</i> with: a square of a stated
/// period and duty, a trapezoid of a stated rise time, an overshoot of a stated height.</para>
/// </summary>
public class WaveformTimingTests
{
    private const double SampleRateHz = 192_000.0;

    /// <summary>A square of <paramref name="dutyFraction"/> duty between two levels, with no edge at all.</summary>
    private static double[] Square(double freqHz, double topV, double baseV, double dutyFraction = 0.5, int frames = 19_200)
    {
        var samples = new double[frames];
        double periodSamples = SampleRateHz / freqHz;
        for (int i = 0; i < frames; i++)
            samples[i] = (i % periodSamples) / periodSamples < dutyFraction ? topV : baseV;
        return samples;
    }

    [Fact]
    public void TheTwoLevelsAreTheOnesTheWaveformSettlesAt_NotItsExtremes()
    {
        // A square that overshoots by a fifth of its amplitude on every rising edge. Max and min
        // would say 1.2 and -1; the levels it settles at are 1 and -1, and every edge reading below
        // is measured between those.
        var samples = Square(1_000.0, 1.0, -1.0);
        double periodSamples = SampleRateHz / 1_000.0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (i % periodSamples < 3)
                samples[i] = 1.4;
        }

        var levels = WaveformHistogram.TopBase(samples);
        Assert.NotNull(levels);
        Assert.Equal(1.0, levels!.Value.TopV, 3);
        Assert.Equal(-1.0, levels.Value.BaseV, 3);

        // What the extremes would have said, so the test states what it is protecting against.
        var extremes = WaveformMeasure.Measure(samples);
        Assert.Equal(1.4, extremes.MaxV, 6);
    }

    [Fact]
    public void ARecordWithOneLevel_HasNoTopAndNoBase()
    {
        // DC, and silence: a "top" and a "base" a hundredth of a division apart are two names for
        // one number, and a rise time measured between them is a reading of the noise.
        Assert.Null(WaveformHistogram.TopBase(new double[4_096]));

        var dc = new double[4_096];
        Array.Fill(dc, 0.25);
        Assert.Null(WaveformHistogram.TopBase(dc));
    }

    [Fact]
    public void ASquaresPeriodDutyAndWidths_AreTheOnesItWasBuiltWith()
    {
        var samples = Square(1_000.0, 1.0, -1.0, dutyFraction: 0.25);
        var levels = WaveformHistogram.TopBase(samples)!.Value;
        var timing = WaveformTimingMeasure.Measure(samples, SampleRateHz, levels.TopV, levels.BaseV);

        Assert.NotNull(timing.PeriodS);
        Assert.Equal(1e-3, timing.PeriodS!.Value, 6);
        Assert.Equal(1_000.0, timing.FrequencyHz!.Value, 1);
        Assert.Equal(0.25e-3, timing.PositiveWidthS!.Value, 6);
        Assert.Equal(0.75e-3, timing.NegativeWidthS!.Value, 6);
        Assert.Equal(25.0, timing.DutyPercent!.Value, 1);
    }

    [Fact]
    public void ARiseTimeIsThe10To90OfTheEdge_InterpolatedBetweenSamples()
    {
        // A single trapezoid: flat at -1, a straight ramp of exactly 100 samples to +1, flat again.
        // 10 % to 90 % of that ramp is 80 samples, which is 416.67 us at this rate -- and the ramp
        // is deliberately not a whole number of samples long at its thresholds, so an integer-index
        // reading would be out by up to one sample and this would fail.
        const int rampSamples = 100;
        var samples = new double[4_096];
        for (int i = 0; i < samples.Length; i++)
        {
            if (i < 1_000)
                samples[i] = -1.0;
            else if (i < 1_000 + rampSamples)
                samples[i] = -1.0 + (2.0 * (i - 1_000) / rampSamples);
            else
                samples[i] = 1.0;
        }

        // Measured against the levels *exactly*, so this is a test of the edge timing alone. The
        // histogram's own pair is a hair off +/-1 here -- the ramp's own samples land in the end
        // bins and pull their means in -- which moves the thresholds by about a thousandth of a
        // sample, and that is the histogram's business rather than this reading's.
        var levels = WaveformHistogram.TopBase(samples)!.Value;
        Assert.Equal(1.0, levels.TopV, 2);
        Assert.Equal(-1.0, levels.BaseV, 2);
        var timing = WaveformTimingMeasure.Measure(samples, SampleRateHz, 1.0, -1.0);

        Assert.NotNull(timing.RiseTimeS);
        Assert.Equal(0.8 * rampSamples / SampleRateHz, timing.RiseTimeS!.Value, 8);
        // One edge, one direction: there is no falling edge in this record to measure.
        Assert.Null(timing.FallTimeS);
    }

    [Fact]
    public void OvershootIsQuotedAgainstTheAmplitude_AndIsZeroWhenThereIsNone()
    {
        var clean = Square(1_000.0, 1.0, -1.0);
        var levels = WaveformHistogram.TopBase(clean)!.Value;
        var flat = WaveformTimingMeasure.Measure(clean, SampleRateHz, levels.TopV, levels.BaseV);
        Assert.Equal(0.0, flat.OvershootPercent!.Value, 6);
        Assert.Equal(0.0, flat.PreshootPercent!.Value, 6);

        // A single sample 0.3 V above a 2 V amplitude is 15 % of overshoot.
        var ringing = Square(1_000.0, 1.0, -1.0);
        ringing[500] = 1.3;
        var rung = WaveformTimingMeasure.Measure(ringing, SampleRateHz, levels.TopV, levels.BaseV);
        Assert.Equal(15.0, rung.OvershootPercent!.Value, 3);
    }

    [Fact]
    public void ASinesPeriodIsItsOwn_AndItsDutyIsAHalf()
    {
        // A sine has no settled levels in the sense a square does -- its histogram peaks at the
        // rails because that is where it spends its time -- so the pair comes out at the peaks, as
        // every bench instrument reports it. The period and the duty are still exactly right, which
        // is what a reader asks of a sine.
        var samples = new double[19_200];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = Math.Sin(2 * Math.PI * 1_000.0 * i / SampleRateHz);

        var levels = WaveformHistogram.TopBase(samples);
        Assert.NotNull(levels);
        var timing = WaveformTimingMeasure.Measure(samples, SampleRateHz, levels!.Value.TopV, levels.Value.BaseV);

        Assert.Equal(1e-3, timing.PeriodS!.Value, 6);
        Assert.Equal(50.0, timing.DutyPercent!.Value, 0);
    }

    [Fact]
    public void ARecordShorterThanOneCycle_ReportsNoPeriodRatherThanAWrongOne()
    {
        // Half a cycle: one rising crossing and no second one. Everything that needs two is null,
        // and nothing invents a period from the record's own length.
        var samples = new double[96];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = Math.Sin(2 * Math.PI * 1_000.0 * i / SampleRateHz);

        var timing = WaveformTimingMeasure.Measure(samples, SampleRateHz, 1.0, -1.0);
        Assert.Null(timing.PeriodS);
        Assert.Null(timing.FrequencyHz);
        Assert.Null(timing.DutyPercent);
    }

    [Fact]
    public void NothingHereIsEverANaN()
    {
        // The rule a scope's status record lives by: System.Text.Json refuses a NaN part way
        // through a response, which the browser reads as the run having done nothing. Absent is
        // null, never NaN -- see ScopeStatusJsonTests.
        foreach (var samples in new[] { new double[8], new[] { 1.0, 1.0 }, Square(1_000.0, 1.0, -1.0) })
        {
            var timing = WaveformTimingMeasure.Measure(samples, SampleRateHz, 1.0, -1.0);
            foreach (double? value in new[]
                     {
                         timing.PeriodS, timing.FrequencyHz, timing.PositiveWidthS, timing.NegativeWidthS,
                         timing.DutyPercent, timing.RiseTimeS, timing.FallTimeS, timing.OvershootPercent,
                         timing.PreshootPercent,
                     })
            {
                Assert.True(value is null || double.IsFinite(value.Value), $"{value} is not a number a status may carry");
            }
        }
    }
}
