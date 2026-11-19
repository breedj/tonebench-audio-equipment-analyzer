using Tonebench.App.Streaming;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The readings added so the chosen-measurement readout could offer what a bench instrument does:
/// where the extremes fell, the other half of the duty cycle, and the per-cycle levels.
/// </summary>
public class ScopeExtraMeasurementTests
{
    private const double SampleRateHz = 192_000.0;

    private static double[] Square(int frames, int halfPeriod, double high = 1.0, double low = -1.0)
    {
        var x = new double[frames];
        for (int i = 0; i < frames; i++)
            x[i] = (i / halfPeriod) % 2 == 0 ? low : high;
        return x;
    }

    [Fact]
    public void TheExtremesCarryWhenTheyFell_OnTheChartsOwnAxis()
    {
        // The reading a transient is looked at for: a record that holds one glitch has nowhere else
        // to say where it is without hunting with a cursor.
        var x = new double[1000];
        x[250] = 1.0;
        x[750] = -1.0;

        // windowStartS is what the chart draws the window's first sample at, so a peak in the
        // pre-trigger reads negative -- the same axis the cursors and the annotations are on.
        var m = ScopeSliceMeasurements.Of(x, SampleRateHz, windowStartS: -0.001);

        Assert.Equal(-0.001 + (250 / SampleRateHz), m.TimeAtMaxS!.Value, 9);
        Assert.Equal(-0.001 + (750 / SampleRateHz), m.TimeAtMinS!.Value, 9);
    }

    [Fact]
    public void AClippedFlatTop_ReportsWhereTheClippingBeganRatherThanWhereItEnded()
    {
        // The first such sample, not the last: what a reader wants off a railed waveform is when it
        // hit the rail.
        var x = new double[100];
        for (int i = 40; i < 60; i++)
            x[i] = 1.0;

        var m = ScopeSliceMeasurements.Of(x, SampleRateHz);
        Assert.Equal(40 / SampleRateHz, m.TimeAtMaxS!.Value, 9);
    }

    [Fact]
    public void TheTwoDutyCycles_AreMeasuredRatherThanOneSubtractedFromTheOther()
    {
        // A 25 % duty: 2048 low, then 512 high in each 2048-frame... built as a ramp of widths
        // instead, so the two halves genuinely differ.
        var x = new double[4096];
        for (int i = 0; i < x.Length; i++)
        {
            int into = i % 1024;
            x[i] = into < 256 ? 1.0 : -1.0;
        }

        var m = ScopeSliceMeasurements.Of(x, SampleRateHz);

        Assert.NotNull(m.DutyPercent);
        Assert.NotNull(m.NegativeDutyPercent);
        Assert.Equal(25.0, m.DutyPercent!.Value, 1);
        Assert.Equal(75.0, m.NegativeDutyPercent!.Value, 1);
        // They add to the period by construction, which is exactly why the second one is measured:
        // agreeing because they were both read is worth something, agreeing because one was
        // subtracted from the other is worth nothing.
        Assert.Equal(100.0, m.DutyPercent.Value + m.NegativeDutyPercent.Value, 6);
    }

    [Fact]
    public void ASquareWithNoCycle_HasNoDutyAtAll()
    {
        // Absent rather than a plausible number: a record with one level has no cycle to divide.
        var m = ScopeSliceMeasurements.Of(new double[1000], SampleRateHz);
        Assert.Null(m.DutyPercent);
        Assert.Null(m.NegativeDutyPercent);
        Assert.Null(m.CycleRmsV);
    }

    [Fact]
    public void TheCycleLevels_AreOfOnePeriodAndTheWindowsAreOfTheWindow()
    {
        // A square whose window holds two and a half periods: the whole window is 60 % high and one
        // period is 50 %, so the two means differ and both are right about their own stretch.
        var x = Square(frames: 2560, halfPeriod: 512);

        var m = ScopeSliceMeasurements.Of(x, SampleRateHz);

        Assert.NotNull(m.CycleMeanV);
        Assert.Equal(0.0, m.CycleMeanV!.Value, 6);
        Assert.Equal(1.0, m.CycleRmsV!.Value, 6);
        Assert.Equal(1.0, m.CycleStdevV!.Value, 6);
        // The window's own mean is not zero, which is the whole point of having both.
        Assert.NotEqual(0.0, m.MeanV, 3);
    }

    [Fact]
    public void TheArea_IsTheMeanTimesTheWindowsLength()
    {
        var x = new double[960];
        Array.Fill(x, 2.0);

        var m = ScopeSliceMeasurements.Of(x, SampleRateHz);

        // 960 samples at 192 kS/s is 5 ms; 2 V across it is 10 mV*s.
        Assert.Equal(0.010, m.AreaVs!.Value, 9);
    }

    [Fact]
    public void AnEnvelope_HasNoneOfThem()
    {
        // An envelope is a band rather than a waveform, so only its extremes have readings -- and
        // this is the place with the most opportunities to break that rule.
        var lower = new double[100];
        var upper = new double[100];
        Array.Fill(lower, -1.0);
        Array.Fill(upper, 1.0);

        var m = ScopeSliceMeasurements.OfEnvelope(lower, upper);

        Assert.Null(m.CycleMeanV);
        Assert.Null(m.CycleRmsV);
        Assert.Null(m.AreaVs);
        Assert.Null(m.TimeAtMaxS);
        Assert.Null(m.NegativeDutyPercent);
        Assert.Equal(2.0, m.PeakToPeakV);
    }
}
