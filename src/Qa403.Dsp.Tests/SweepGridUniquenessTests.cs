namespace Qa403.Dsp.Tests;

/// <summary>
/// The sweep grid must never contain the same frequency twice.
///
/// It is not a cosmetic property. MeasurementSaveService groups a finished sweep's points by
/// frequency to build one capture row per point, and each capture writes one preview row per
/// (channel, kind) -- a pair the schema declares UNIQUE. Two steps sharing a frequency therefore
/// put two same-channel points in one group and the save fails at the database with
/// "UNIQUE constraint failed: capture_preview.capture_id, capture_preview.channel,
/// capture_preview.kind", long after the sweep that caused it has finished.
/// </summary>
public class SweepGridUniquenessTests
{
    public static TheoryData<double, double, int, double, int> GridCases()
    {
        var data = new TheoryData<double, double, int, double, int>();
        double[] starts = [5, 10, 20, 100, 1000, 5000, 19000];
        double[] ends = [50, 1000, 20000, 40000, 90000];
        int[] stepsPerOctave = [1, 2, 3, 5, 10, 24, 48];
        double[] rates = [48000, 96000, 192000];
        int[] sizes = [4096, 16384, 65536, 262144];

        foreach (double start in starts)
            foreach (double end in ends)
                foreach (int steps in stepsPerOctave)
                    foreach (double fs in rates)
                        foreach (int n in sizes)
                            if (end >= start)
                                data.Add(start, end, steps, fs, n);

        return data;
    }

    [Theory]
    [MemberData(nameof(GridCases))]
    public void SnapAndDedupe_never_returns_the_same_frequency_twice(
        double startHz, double endHz, int stepsPerOctave, double sampleRateHz, int fftSize)
    {
        double maxHz = 0.45 * sampleRateHz / 2.0;
        var desired = SweepPoints.LogPerOctave(startHz, endHz, stepsPerOctave);

        var tones = SweepPoints.SnapAndDedupe(desired, sampleRateHz, fftSize, maxHz);

        var frequencies = tones.Select(t => t.FrequencyHz).ToList();
        Assert.Equal(frequencies.Count, frequencies.Distinct().Count());
        Assert.Equal(tones.Length, tones.Select(t => t.Bin).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(GridCases))]
    public void SnapAndDedupeAdaptive_never_returns_the_same_frequency_twice(
        double startHz, double endHz, int stepsPerOctave, double sampleRateHz, int fftSize)
    {
        double maxHz = 0.45 * sampleRateHz / 2.0;
        var desired = SweepPoints.LogPerOctave(startHz, endHz, stepsPerOctave);

        // A size that varies with frequency, which is the whole point of the adaptive path: the
        // fixed-size dedupe keys on bin index, but a bin means nothing across two different N, so
        // this path has to key on the snapped frequency instead.
        var steps = SweepPoints.SnapAndDedupeAdaptive(
            desired, sampleRateHz, hz => hz < 200 ? fftSize * 4 : hz < 2000 ? fftSize : Math.Max(4096, fftSize / 4), maxHz);

        var frequencies = steps.Select(s => s.FrequencyHz).ToList();
        Assert.Equal(frequencies.Count, frequencies.Distinct().Count());
    }
}
