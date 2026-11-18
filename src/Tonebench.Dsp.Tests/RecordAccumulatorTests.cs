using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// Combining successive records: the mean of the last N, and the band they have swept.
///
/// <para>The figure that matters most here is the <b>sqrt(N)</b> one, because it is what a reader
/// quotes off an averaged noise floor — so it is asserted against the arithmetic rather than
/// against "smaller than before", which almost any filter would pass.</para>
/// </summary>
public class RecordAccumulatorTests
{
    private static double[][] Record(params double[] samples) => [samples];

    private static double[][] Flat(double value, int frames = 8)
    {
        var channel = new double[frames];
        Array.Fill(channel, value);
        return [channel];
    }

    [Fact]
    public void Normal_HandsTheRecordStraightBack()
    {
        // The ordinary case allocates nothing and copies nothing.
        var accumulator = new RecordAccumulator(RecordAccumulation.Normal, 16);
        var record = Record(1.0, 2.0, 3.0);

        var (channels, lower) = accumulator.Fold(record);

        Assert.Same(record, channels);
        Assert.Null(lower);
        Assert.Equal(1, accumulator.Taken);
    }

    [Fact]
    public void TheCountIsClampedToWhatCanBeAnswered()
    {
        // Fewer than two is not an accumulation of anything, and past 256 the wait is longer than
        // anyone will watch -- four minutes at the one-second record this app allows.
        Assert.Equal(RecordAccumulator.MinCount, new RecordAccumulator(RecordAccumulation.Average, 1).Count);
        Assert.Equal(RecordAccumulator.MinCount, new RecordAccumulator(RecordAccumulation.Average, -5).Count);
        Assert.Equal(RecordAccumulator.MaxCount, new RecordAccumulator(RecordAccumulation.Average, 10_000).Count);
    }

    [Fact]
    public void Average_IsTheMeanPositionByPosition()
    {
        var accumulator = new RecordAccumulator(RecordAccumulation.Average, 2);

        var (first, _) = accumulator.Fold(Record(0.0, 10.0));
        // While it is filling, the mean is of what has arrived -- the trace quietens as it goes
        // rather than appearing all at once after N records.
        Assert.Equal(0.0, first[0][0], 12);
        Assert.Equal(10.0, first[0][1], 12);
        Assert.Equal(1, accumulator.Taken);
        Assert.False(accumulator.Complete);

        var (second, _) = accumulator.Fold(Record(2.0, 20.0));
        Assert.Equal(1.0, second[0][0], 12);
        Assert.Equal(15.0, second[0][1], 12);
        Assert.Equal(2, accumulator.Taken);
        Assert.True(accumulator.Complete);
    }

    [Fact]
    public void Average_IsOfTheLastNRecordsAndNotOfEveryRecordEver()
    {
        // A plain mean of a known number of records, so the figure a reader quotes off it can be
        // stated. An exponential average would never settle and would weight the newest most.
        var accumulator = new RecordAccumulator(RecordAccumulation.Average, 2);

        accumulator.Fold(Flat(0.0, 1));
        accumulator.Fold(Flat(0.0, 1));
        var (third, _) = accumulator.Fold(Flat(6.0, 1));
        // The first record has fallen out of the ring: the mean is of 0 and 6, not of 0, 0 and 6.
        Assert.Equal(3.0, third[0][0], 12);

        var (fourth, _) = accumulator.Fold(Flat(6.0, 1));
        Assert.Equal(6.0, fourth[0][0], 12);
    }

    [Fact]
    public void Average_LeavesTheSignalAloneAndDividesTheNoiseByRootN()
    {
        // The whole claim of the mode, against the arithmetic rather than against "quieter". A
        // 1 kHz tone at the same phase in every record plus independent noise: the tone survives
        // and the noise falls as 1/sqrt(N).
        const int frames = 4096;
        const int count = 64;
        const double noiseV = 0.1;

        var tone = new double[frames];
        for (int i = 0; i < frames; i++)
            tone[i] = Math.Sin(2.0 * Math.PI * i / 64.0);

        var random = new Random(20261118);
        var accumulator = new RecordAccumulator(RecordAccumulation.Average, count);

        double[][] averaged = [];
        for (int r = 0; r < count; r++)
        {
            var record = new double[frames];
            for (int i = 0; i < frames; i++)
                record[i] = tone[i] + (noiseV * Gaussian(random));
            (averaged, _) = accumulator.Fold([record]);
        }

        double residual = 0.0;
        for (int i = 0; i < frames; i++)
        {
            double d = averaged[0][i] - tone[i];
            residual += d * d;
        }

        double rms = Math.Sqrt(residual / frames);
        double expected = noiseV / Math.Sqrt(count);

        // Within 15 %, which is what 4096 samples of a random draw can promise -- and far tighter
        // than the factor of eight between averaged and unaveraged that the claim is about.
        Assert.InRange(rms, expected * 0.85, expected * 1.15);
    }

    [Fact]
    public void Average_OfRecordsThatDoNotLineUp_LowPassesTheSignalItself()
    {
        // The one assumption this cannot check, stated as a test so it is not mistaken for a bug.
        // The same tone with a different phase in every record averages toward nothing: a jittery
        // trigger buys a smaller waveform and calls it quieter.
        const int frames = 1024;
        const int count = 32;

        var accumulator = new RecordAccumulator(RecordAccumulation.Average, count);
        double[][] averaged = [];
        for (int r = 0; r < count; r++)
        {
            var record = new double[frames];
            double phase = 2.0 * Math.PI * r / count;
            for (int i = 0; i < frames; i++)
                record[i] = Math.Sin((2.0 * Math.PI * i / 64.0) + phase);
            (averaged, _) = accumulator.Fold([record]);
        }

        double peak = 0.0;
        for (int i = 0; i < frames; i++)
            peak = Math.Max(peak, Math.Abs(averaged[0][i]));

        // A tenth of the amplitude it went in with, rather than the 1.0 a stable trigger keeps.
        Assert.True(peak < 0.1, $"peak {peak:F4}");
    }

    [Fact]
    public void Envelope_IsTheBandEveryPositionHasSwept()
    {
        var accumulator = new RecordAccumulator(RecordAccumulation.Envelope, 8);

        var (high1, low1) = accumulator.Fold(Record(0.0, 1.0));
        // One record is a band of no width: the outlines are the record itself.
        Assert.Equal(new[] { 0.0, 1.0 }, high1[0]);
        Assert.Equal(new[] { 0.0, 1.0 }, low1![0]);

        var (high2, low2) = accumulator.Fold(Record(-1.0, 2.0));
        Assert.Equal(new[] { 0.0, 2.0 }, high2[0]);
        Assert.Equal(new[] { -1.0, 1.0 }, low2![0]);

        // A record inside the band leaves it where it was.
        var (high3, low3) = accumulator.Fold(Record(-0.5, 1.5));
        Assert.Equal(new[] { 0.0, 2.0 }, high3[0]);
        Assert.Equal(new[] { -1.0, 1.0 }, low3![0]);
    }

    [Fact]
    public void Envelope_RemembersTheWorstCaseRatherThanTheLastN()
    {
        // An envelope answers "how far does this ever move", so a bound that quietly forgot the
        // worst case a few records ago would answer a different question each time it was read.
        var accumulator = new RecordAccumulator(RecordAccumulation.Envelope, 2);

        accumulator.Fold(Record(5.0));
        accumulator.Fold(Record(0.0));
        var (high, low) = accumulator.Fold(Record(0.0));

        Assert.Equal(5.0, high[0][0], 12);
        Assert.Equal(0.0, low![0][0], 12);
    }

    [Fact]
    public void Envelope_HandsOutCopies_SoAFoldCannotRewriteARecordAlreadyDrawn()
    {
        var accumulator = new RecordAccumulator(RecordAccumulation.Envelope, 8);

        var (high, _) = accumulator.Fold(Record(1.0));
        accumulator.Fold(Record(9.0));

        Assert.Equal(1.0, high[0][0], 12);
    }

    [Fact]
    public void ARecordOfADifferentShape_StartsAgainRatherThanBeingCombined()
    {
        // A different length or channel count means position i is no longer the same point on the
        // waveform, so combining them would average two different measurements together.
        var accumulator = new RecordAccumulator(RecordAccumulation.Average, 8);

        accumulator.Fold(Flat(10.0, frames: 4));
        var (after, _) = accumulator.Fold(Flat(2.0, frames: 6));

        Assert.Equal(6, after[0].Length);
        Assert.Equal(2.0, after[0][0], 12);
        Assert.Equal(1, accumulator.Taken);
    }

    [Fact]
    public void AChannelCountThatChanges_AlsoStartsAgain()
    {
        var accumulator = new RecordAccumulator(RecordAccumulation.Envelope, 8);

        accumulator.Fold([[1.0], [1.0]]);
        var (high, low) = accumulator.Fold([[5.0]]);

        Assert.Single(high);
        Assert.Equal(5.0, high[0][0], 12);
        Assert.Equal(5.0, low![0][0], 12);
        Assert.Equal(1, accumulator.Taken);
    }

    [Fact]
    public void Reset_StartsFromTheNextRecord()
    {
        var accumulator = new RecordAccumulator(RecordAccumulation.Average, 4);
        accumulator.Fold(Flat(8.0, 1));
        accumulator.Fold(Flat(8.0, 1));
        Assert.Equal(2, accumulator.Taken);

        accumulator.Reset();
        Assert.Equal(0, accumulator.Taken);

        var (after, _) = accumulator.Fold(Flat(2.0, 1));
        Assert.Equal(2.0, after[0][0], 12);
        Assert.Equal(1, accumulator.Taken);
    }

    [Fact]
    public void AnEmptyRecord_IsHandedBackRatherThanAccumulated()
    {
        var accumulator = new RecordAccumulator(RecordAccumulation.Average, 4);

        var (channels, lower) = accumulator.Fold([]);

        Assert.Empty(channels);
        Assert.Null(lower);
    }

    /// <summary>Box-Muller, so the noise above is Gaussian rather than uniform — which is what the sqrt(N) figure assumes.</summary>
    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
