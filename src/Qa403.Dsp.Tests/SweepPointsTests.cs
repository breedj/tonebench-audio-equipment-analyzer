namespace Qa403.Dsp.Tests;

public class SweepPointsTests
{
    [Fact]
    public void LogPerOctave_FiveStepsPerOctaveOverTwoOctaves_ReturnsElevenAscendingFrequencies()
    {
        // 20 Hz -> 80 Hz is exactly 2 octaves, so 5 steps/octave gives 10 steps and 11 endpoints.
        var values = SweepPoints.LogPerOctave(20.0, 80.0, 5);

        Assert.Equal(11, values.Length);
        Assert.Equal(20.0, values[0], 9);
        Assert.Equal(80.0, values[^1], 9);

        for (int i = 1; i < values.Length; i++)
            Assert.True(values[i] > values[i - 1], $"values[{i}] = {values[i]} is not above values[{i - 1}] = {values[i - 1]}");
    }

    [Fact]
    public void LogPerOctave_EquallySpacedInLogSpace_HasAConstantRatioBetweenNeighbours()
    {
        var values = SweepPoints.LogPerOctave(20.0, 20000.0, 6);

        double firstRatio = values[1] / values[0];
        for (int i = 2; i < values.Length - 1; i++)
            Assert.Equal(firstRatio, values[i] / values[i - 1], 9);
    }

    [Fact]
    public void LogPerOctave_SpanNarrowerThanOneOctave_StillReturnsAtLeastThreePoints()
    {
        // The max(1, octaves) / max(2, steps) clamps: a sub-octave span must not collapse to a
        // single point, or the sweep has nothing to plot.
        var values = SweepPoints.LogPerOctave(1000.0, 1200.0, 5);

        Assert.True(values.Length >= 3);
        Assert.Equal(1000.0, values[0], 9);
        Assert.Equal(1200.0, values[^1], 9);
    }

    [Fact]
    public void LogPerOctave_EndBelowStart_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepPoints.LogPerOctave(1000.0, 100.0, 5));

    [Fact]
    public void LogPerOctave_ZeroStartFrequency_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepPoints.LogPerOctave(0.0, 1000.0, 5));

    [Fact]
    public void SnapAndDedupe_LowFrequenciesCollidingOnOneBin_ReturnsThatBinOnce()
    {
        // At 48 kHz / 65536 the bins are 0.73 Hz apart, so 20 steps/octave from 20 Hz asks for
        // several frequencies that all round into the same couple of bins.
        const double fs = 48000.0;
        const int n = 65536;

        var desired = SweepPoints.LogPerOctave(20.0, 25.0, 20);
        var tones = SweepPoints.SnapAndDedupe(desired, fs, n, maxHz: fs / 2);

        Assert.True(tones.Length < desired.Length, "expected at least one collision to be removed");
        Assert.Equal(tones.Select(t => t.Bin).Distinct().Count(), tones.Length);
    }

    [Fact]
    public void SnapAndDedupe_ReturnsTonesAscendingByFrequency()
    {
        var desired = SweepPoints.LogPerOctave(20.0, 20000.0, 5);
        var tones = SweepPoints.SnapAndDedupe(desired, 48000.0, 65536, maxHz: 24000.0);

        for (int i = 1; i < tones.Length; i++)
            Assert.True(tones[i].FrequencyHz > tones[i - 1].FrequencyHz);
    }

    [Fact]
    public void SnapAndDedupe_EveryToneLandsOnACoherentBin()
    {
        const double fs = 192000.0;
        const int n = 32768;

        var desired = SweepPoints.LogPerOctave(20.0, 90000.0, 4);
        var tones = SweepPoints.SnapAndDedupe(desired, fs, n, maxHz: 0.98 * fs / 2);

        Assert.NotEmpty(tones);
        foreach (var tone in tones)
        {
            Assert.Equal(1, Gcd(tone.Bin, n));
            Assert.Equal(tone.Bin * fs / n, tone.FrequencyHz, 9);
        }
    }

    [Fact]
    public void SnapAndDedupe_FrequenciesAboveTheLimit_AreDropped()
    {
        const double fs = 48000.0;
        const double maxHz = 20000.0;

        var desired = SweepPoints.LogPerOctave(1000.0, 23000.0, 6);
        var tones = SweepPoints.SnapAndDedupe(desired, fs, 65536, maxHz);

        Assert.NotEmpty(tones);
        Assert.All(tones, t => Assert.True(t.FrequencyHz <= maxHz, $"{t.FrequencyHz} Hz is above the {maxHz} Hz limit"));
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a;
    }
}
