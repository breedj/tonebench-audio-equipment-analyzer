namespace Qa403.Dsp.Tests;

public class SpectrumAveragerTests
{
    [Fact]
    public void Off_leaves_values_untouched()
    {
        var averager = new SpectrumAverager();
        double[] values = [1.0, 2.0, 3.0];

        averager.Update(values, AveragingMode.Off, count: 8);

        Assert.Equal([1.0, 2.0, 3.0], values);
    }

    [Fact]
    public void Linear_average_of_constant_ticks_converges_to_that_constant()
    {
        var averager = new SpectrumAverager();

        for (int tick = 0; tick < 5; tick++)
        {
            double[] values = [4.0, 4.0];
            averager.Update(values, AveragingMode.Linear, count: 4);
            Assert.Equal(4.0, values[0], precision: 9);
        }
    }

    [Fact]
    public void Linear_average_is_exact_boxcar_mean_of_last_count_ticks()
    {
        var averager = new SpectrumAverager();

        averager.Update([2.0], AveragingMode.Linear, count: 3); // mean(2) = 2
        averager.Update([4.0], AveragingMode.Linear, count: 3); // mean(2,4) = 3
        double[] third = [6.0];
        averager.Update(third, AveragingMode.Linear, count: 3); // mean(2,4,6) = 4
        Assert.Equal(4.0, third[0], precision: 9);

        double[] fourth = [8.0];
        averager.Update(fourth, AveragingMode.Linear, count: 3); // window slides: mean(4,6,8) = 6
        Assert.Equal(6.0, fourth[0], precision: 9);
    }

    [Fact]
    public void Exponential_average_moves_toward_new_value_without_jumping_to_it()
    {
        var averager = new SpectrumAverager();

        double[] first = [0.0];
        averager.Update(first, AveragingMode.Exponential, count: 9); // alpha = 0.2, seeds at 0
        Assert.Equal(0.0, first[0]);

        double[] second = [10.0];
        averager.Update(second, AveragingMode.Exponential, count: 9); // 0 + 0.2*(10-0) = 2
        Assert.Equal(2.0, second[0], precision: 9);
    }

    [Fact]
    public void Switching_to_off_and_back_starts_a_fresh_window()
    {
        var averager = new SpectrumAverager();
        averager.Update([100.0], AveragingMode.Linear, count: 4);
        averager.Update([100.0], AveragingMode.Linear, count: 4);

        averager.Update([0.0], AveragingMode.Off, count: 4); // clears state

        double[] afterReset = [5.0];
        averager.Update(afterReset, AveragingMode.Linear, count: 4);
        Assert.Equal(5.0, afterReset[0], precision: 9); // fresh window, not influenced by the 100.0 ticks
    }

    [Fact]
    public void Count_climbs_to_the_target_count_and_holds_there()
    {
        var averager = new SpectrumAverager();
        Assert.Equal(0, averager.Count);

        for (int tick = 1; tick <= 4; tick++)
        {
            averager.Update([1.0], AveragingMode.Linear, count: 4);
            Assert.Equal(tick, averager.Count);
        }

        // Window is full — further ticks keep averaging but the counter holds at the target.
        averager.Update([1.0], AveragingMode.Linear, count: 4);
        Assert.Equal(4, averager.Count);
    }

    [Fact]
    public void Count_climbs_the_same_way_for_Exponential_since_it_has_no_natural_done_state()
    {
        var averager = new SpectrumAverager();

        for (int tick = 1; tick <= 9; tick++)
            averager.Update([1.0], AveragingMode.Exponential, count: 9);

        Assert.Equal(9, averager.Count);

        averager.Update([1.0], AveragingMode.Exponential, count: 9);
        Assert.Equal(9, averager.Count); // holds, doesn't keep climbing past the target
    }

    [Fact]
    public void Count_resets_to_zero_on_Off_and_on_Reset()
    {
        var averager = new SpectrumAverager();
        averager.Update([1.0], AveragingMode.Linear, count: 4);
        averager.Update([1.0], AveragingMode.Linear, count: 4);
        Assert.Equal(2, averager.Count);

        averager.Update([1.0], AveragingMode.Off, count: 4);
        Assert.Equal(0, averager.Count);

        averager.Update([1.0], AveragingMode.Linear, count: 4);
        Assert.Equal(1, averager.Count);

        averager.Reset();
        Assert.Equal(0, averager.Count);
    }

    [Fact]
    public void FromValues_round_trips_through_PsdSpectrum_integration()
    {
        double[] values = [0.0, 1.0, 1.0, 1.0, 0.0]; // 5 bins, N/2+1 form for an 8-point transform
        var sums = new WindowSums(S1: 8.0, S2: 8.0, Length: 8);
        var psd = PsdSpectrum.FromValues(values, sampleRateHz: 8.0, binSizeHz: 1.0, sums);

        Assert.Equal(8.0, psd.SampleRateHz);
        Assert.Equal(1.0, psd.BinSizeHz);
        Assert.Equal(Math.Sqrt(3.0), psd.IntegrateVrms(), precision: 9);
    }
}
