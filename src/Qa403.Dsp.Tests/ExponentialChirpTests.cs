namespace Qa403.Dsp.Tests;

public class ExponentialChirpTests
{
    private const double SampleRateHz = 48000.0;

    [Fact]
    public void Fill_StartsAndEndsAtZero_SoThePeriodWrapsWithoutAStep()
    {
        var period = new double[4096];
        ExponentialChirp.Fill(period, 20.0, 20000.0, SampleRateHz, 1.0);

        // The fade exists to remove the wrap discontinuity; if it ever stops doing that, the step
        // splatters broadband energy across every bin of the measurement.
        Assert.Equal(0.0, period[0], 12);
        Assert.Equal(0.0, period[^1], 3);
    }

    [Fact]
    public void Fill_SweepsUpward_ReachingHigherFrequenciesLater()
    {
        const int n = 8192;
        var period = new double[n];
        ExponentialChirp.Fill(period, 100.0, 10000.0, SampleRateHz, 1.0);

        // Zero crossings per unit time is a cheap proxy for instantaneous frequency.
        int early = ZeroCrossings(period.AsSpan(n / 8, n / 8));
        int late = ZeroCrossings(period.AsSpan(n - (n / 4), n / 8));

        Assert.True(late > early * 4, $"expected a rising sweep, got {early} then {late} crossings");
    }

    [Fact]
    public void Fill_AmplitudeIsPeakNotRms()
    {
        var period = new double[16384];
        ExponentialChirp.Fill(period, 20.0, 20000.0, SampleRateHz, 2.5);

        double peak = 0.0;
        foreach (double sample in period)
            peak = Math.Max(peak, Math.Abs(sample));

        Assert.Equal(2.5, peak, 2);
    }

    [Fact]
    public void PhaseAt_SpendsEqualTimePerOctave()
    {
        // The defining property of an exponential sweep: the instantaneous frequency doubles in a
        // fixed slice of the period, no matter which octave. Differentiating phase numerically.
        const double periodSeconds = 1.0;
        double FreqAt(double tau)
        {
            const double h = 1e-6;
            return (ExponentialChirp.PhaseAt(tau + h, 20.0, 20480.0, periodSeconds)
                - ExponentialChirp.PhaseAt(tau, 20.0, 20480.0, periodSeconds)) / h / (2.0 * Math.PI);
        }

        // 20 Hz to 20480 Hz is exactly ten octaves, so each tenth of the period is one octave.
        Assert.Equal(20.0, FreqAt(0.0), 1);
        Assert.Equal(40.0, FreqAt(0.1), 1);
        Assert.Equal(320.0, FreqAt(0.4), 0);
        Assert.InRange(FreqAt(0.9), 10239.0, 10241.0);
    }

    [Fact]
    public void Fade_IsOneInTheMiddleAndZeroAtBothEdges()
    {
        Assert.Equal(0.0, ExponentialChirp.Fade(0.0, 1.0, 0.005), 12);
        Assert.Equal(1.0, ExponentialChirp.Fade(0.5, 1.0, 0.005), 12);
        Assert.Equal(0.0, ExponentialChirp.Fade(1.0, 1.0, 0.005), 12);
        Assert.Equal(0.5, ExponentialChirp.Fade(0.0025, 1.0, 0.005), 12);
    }

    [Fact]
    public void Fade_ZeroLengthIsUnity_RatherThanSilence()
    {
        Assert.Equal(1.0, ExponentialChirp.Fade(0.0, 1.0, 0.0));
    }

    [Fact]
    public void PeakVoltsFromDbv_MatchesTheGeneratorsConvention()
    {
        // 0 dBV is 1 Vrms, which is sqrt(2) V peak.
        Assert.Equal(Math.Sqrt(2.0), ExponentialChirp.PeakVoltsFromDbv(0.0), 12);
        Assert.Equal(Math.Sqrt(2.0) * 0.1, ExponentialChirp.PeakVoltsFromDbv(-20.0), 12);
    }

    [Theory]
    [InlineData(0.0, 20000.0)]
    [InlineData(20.0, 20.0)]
    [InlineData(20000.0, 20.0)]
    public void Fill_RejectsADegenerateBand(double startHz, double endHz)
    {
        var period = new double[256];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExponentialChirp.Fill(period, startHz, endHz, SampleRateHz, 1.0));
    }

    private static int ZeroCrossings(ReadOnlySpan<double> samples)
    {
        int count = 0;
        for (int i = 1; i < samples.Length; i++)
            if ((samples[i - 1] < 0.0) != (samples[i] < 0.0))
                count++;
        return count;
    }
}
