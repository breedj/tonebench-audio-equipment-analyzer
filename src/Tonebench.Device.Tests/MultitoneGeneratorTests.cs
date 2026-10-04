namespace Tonebench.Device.Tests;

public class MultitoneGeneratorTests
{
    [Fact]
    public void Build_SpansTheFixedBandAtTheRequestedDensity()
    {
        var tones = MultitoneGenerator.Build(new MultitoneSettings { TonesPerOctave = 5 });

        // 5 Hz -> 20 kHz is log2(4000) ~= 11.97 octaves, so 5 tones/octave gives floor(11.97*5)+1 = 60.
        Assert.Equal(60, tones.Count);
        Assert.Equal(MultitoneGenerator.BandStartHz, tones[0].FrequencyHz, precision: 9);
        Assert.True(tones[^1].FrequencyHz <= MultitoneGenerator.BandEndHz);

        // Frequencies are strictly increasing and spaced one fifth of an octave apart.
        for (int i = 1; i < tones.Count; i++)
            Assert.Equal(Math.Pow(2.0, 1.0 / 5.0), tones[i].FrequencyHz / tones[i - 1].FrequencyHz, precision: 9);
    }

    [Fact]
    public void Build_WithZeroRolloff_CombinedRmsMatchesTheRequestedTotal()
    {
        // The load-bearing assertion behind the "(w/ 0 dB rolloff)" wording in the spec: uncorrelated
        // tones add in power, so sqrt(sum of squares) must land exactly on the requested level.
        const double totalRmsDbv = -50.0;
        var tones = MultitoneGenerator.Build(new MultitoneSettings { TotalRmsDbv = totalRmsDbv });

        double sumSquares = tones.Sum(t => Math.Pow(10.0, t.AmplitudeDbv / 20.0) * Math.Pow(10.0, t.AmplitudeDbv / 20.0));
        double combinedDbv = 20.0 * Math.Log10(Math.Sqrt(sumSquares));

        Assert.Equal(totalRmsDbv, combinedDbv, precision: 9);
    }

    [Fact]
    public void Build_WithZeroRolloff_AllTonesShareOneLevel()
    {
        var tones = MultitoneGenerator.Build(new MultitoneSettings { RolloffDbPerOctave = 0.0 });

        foreach (var tone in tones)
            Assert.Equal(tones[0].AmplitudeDbv, tone.AmplitudeDbv, precision: 9);
    }

    [Fact]
    public void Build_WithRolloff_DropsByTheRequestedDbPerOctave()
    {
        const int tonesPerOctave = 5;
        var tones = MultitoneGenerator.Build(new MultitoneSettings
        {
            TonesPerOctave = tonesPerOctave,
            RolloffDbPerOctave = 6.0,
        });

        // Index i and i + tonesPerOctave are exactly one octave apart.
        Assert.Equal(tones[0].AmplitudeDbv - 6.0, tones[tonesPerOctave].AmplitudeDbv, precision: 9);
        Assert.Equal(tones[0].AmplitudeDbv - 12.0, tones[2 * tonesPerOctave].AmplitudeDbv, precision: 9);
    }

    [Fact]
    public void Build_WithRolloff_KeepsTheBandStartLevelUnchanged()
    {
        // Rolloff tilts the comb around the band start; it must not also renormalise back up to the
        // requested total, or "total RMS with 0 dB rolloff" would stop meaning anything.
        var flat = MultitoneGenerator.Build(new MultitoneSettings { RolloffDbPerOctave = 0.0 });
        var tilted = MultitoneGenerator.Build(new MultitoneSettings { RolloffDbPerOctave = 6.0 });

        Assert.Equal(flat[0].AmplitudeDbv, tilted[0].AmplitudeDbv, precision: 9);
        Assert.True(tilted[^1].AmplitudeDbv < flat[^1].AmplitudeDbv);
    }

    [Fact]
    public void Build_RejectsATonesPerOctaveBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MultitoneGenerator.Build(new MultitoneSettings { TonesPerOctave = 0 }));
    }
}
