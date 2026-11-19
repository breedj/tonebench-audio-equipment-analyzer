using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// The per-cycle levels, and the bias over a part-cycle window they exist to remove.
/// </summary>
public class WaveformCycleTests
{
    private const double SampleRateHz = 192_000.0;

    /// <param name="cycles">How many periods the window holds — fractional on purpose.</param>
    private static double[] Sine(double cycles, double freqHz = 1000.0, double amplitude = 1.0, double offset = 0.0)
    {
        int count = (int)Math.Round(cycles * SampleRateHz / freqHz);
        var x = new double[count];
        for (int i = 0; i < count; i++)
            x[i] = offset + (amplitude * Math.Sin(2 * Math.PI * freqHz * i / SampleRateHz));
        return x;
    }

    [Fact]
    public void OverOneWholeCycleOfASine_TheMeanIsZeroAndTheRmsIsTheAmplitudeOverRootTwo()
    {
        var x = Sine(cycles: 4);
        var timing = WaveformTimingMeasure.Measure(x, SampleRateHz, 1.0, -1.0);

        var cycle = WaveformCycleMeasure.Measure(
            x, timing.CycleFirstSample!.Value, timing.CycleSampleCount!.Value, SampleRateHz);

        Assert.NotNull(cycle);
        Assert.Equal(0.0, cycle!.Value.MeanV, 3);
        Assert.Equal(1.0 / Math.Sqrt(2.0), cycle.Value.RmsV, 3);
        // No offset, so the deviation about the mean is the RMS itself.
        Assert.Equal(cycle.Value.RmsV, cycle.Value.StdevV, 3);
    }

    [Fact]
    public void OverAPartCycleWindow_TheWholeWindowRmsIsBiasedAndTheCycleOneIsNot()
    {
        // THE reason this measurement exists. A scope's window is set by the timebase and almost
        // never holds a whole number of cycles; what the part-cycle contributes biases the
        // whole-window RMS of a periodic signal, and the figure that comes out is a perfectly
        // ordinary number with nothing on screen to say it is the wrong one.
        //
        // 10.125 cycles rather than 10.25: the bias goes as sin(4*pi*n)/(4*pi*n), which is worst at
        // n + 1/8 and EXACTLY zero at a whole number and at n + 1/4. A test written at 10.25 passes
        // against a cycle reading that does nothing at all, which is how this comment came to be
        // here.
        var x = Sine(cycles: 10.125);
        var window = WaveformMeasure.Measure(x);
        var timing = WaveformTimingMeasure.Measure(x, SampleRateHz, 1.0, -1.0);
        var cycle = WaveformCycleMeasure.Measure(
            x, timing.CycleFirstSample!.Value, timing.CycleSampleCount!.Value, SampleRateHz);

        double truth = 1.0 / Math.Sqrt(2.0);
        double windowError = Math.Abs(window.RmsV - truth) / truth;
        double cycleError = Math.Abs(cycle!.Value.RmsV - truth) / truth;

        // 0.39 % by the formula above; the cycle reading has to be an order better.
        Assert.True(windowError > 0.003, $"the window's RMS was not biased enough to be worth fixing: {windowError:P3}");
        Assert.True(cycleError < 0.001, $"the cycle's RMS is off by {cycleError:P3}");
        Assert.True(cycleError < windowError / 5, "the cycle reading is not better than the window's.");
    }

    [Fact]
    public void TheCycleMean_IsZeroWhereTheWindowsIsNot()
    {
        // Starker than the RMS: the mean of a sine over a whole cycle is exactly zero, and over
        // 10.25 of them it is whatever the quarter contributes.
        var x = Sine(cycles: 10.25);
        var timing = WaveformTimingMeasure.Measure(x, SampleRateHz, 1.0, -1.0);
        var cycle = WaveformCycleMeasure.Measure(
            x, timing.CycleFirstSample!.Value, timing.CycleSampleCount!.Value, SampleRateHz);

        Assert.True(Math.Abs(WaveformMeasure.Measure(x).MeanV) > 0.005);
        Assert.Equal(0.0, cycle!.Value.MeanV, 3);
    }

    [Fact]
    public void OnASignalRidingOnAnOffset_TheMeanIsTheOffsetAndTheDeviationIsTheSignal()
    {
        var x = Sine(cycles: 4, offset: 5.0);
        var timing = WaveformTimingMeasure.Measure(x, SampleRateHz, 6.0, 4.0);
        var cycle = WaveformCycleMeasure.Measure(
            x, timing.CycleFirstSample!.Value, timing.CycleSampleCount!.Value, SampleRateHz);

        Assert.Equal(5.0, cycle!.Value.MeanV, 3);
        Assert.Equal(1.0 / Math.Sqrt(2.0), cycle.Value.StdevV, 3);
        // And the true RMS carries the offset, which is the whole difference between the two.
        Assert.Equal(Math.Sqrt(25.0 + 0.5), cycle.Value.RmsV, 3);
    }

    [Fact]
    public void TheDeviation_SurvivesAnOffsetFarLargerThanTheSignal()
    {
        // The second-pass rule, stated as a test: sumSquares/n - mean^2 is catastrophic
        // cancellation here, which is a supply rail or a tube's plate with a few millivolts of
        // ripple on it.
        var x = Sine(cycles: 4, amplitude: 1e-3, offset: 300.0);
        var timing = WaveformTimingMeasure.Measure(x, SampleRateHz, 300.001, 299.999);
        var cycle = WaveformCycleMeasure.Measure(
            x, timing.CycleFirstSample!.Value, timing.CycleSampleCount!.Value, SampleRateHz);

        Assert.Equal(1e-3 / Math.Sqrt(2.0), cycle!.Value.StdevV, 8);
    }

    [Fact]
    public void TheArea_IsTheMeanTimesTheCyclesOwnLength()
    {
        var x = Sine(cycles: 4, offset: 2.0);
        var timing = WaveformTimingMeasure.Measure(x, SampleRateHz, 3.0, 1.0);
        var cycle = WaveformCycleMeasure.Measure(
            x, timing.CycleFirstSample!.Value, timing.CycleSampleCount!.Value, SampleRateHz);

        // One millisecond of a 2 V offset is 2 mV·s; the sine integrates to nothing over the cycle.
        Assert.Equal(2.0 * 1e-3, cycle!.Value.AreaVs, 6);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 1)]
    [InlineData(100, 50)]
    public void ARangeThatIsNotInsideTheRecord_IsRefusedRatherThanClamped(int first, int count)
    {
        // Absent is this screen's answer to a reading it has not got; a clamped range would measure
        // some other stretch and report it as a cycle.
        Assert.Null(WaveformCycleMeasure.Measure(new double[64], first, count, SampleRateHz));
    }

    [Fact]
    public void ANonPositiveSampleRate_IsRefused()
    {
        Assert.Null(WaveformCycleMeasure.Measure(new double[64], 0, 32, 0.0));
    }
}
