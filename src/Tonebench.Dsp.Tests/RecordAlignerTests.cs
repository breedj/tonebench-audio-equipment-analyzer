using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// Putting every record of an accumulation on one fractional grid: the three rules, and the figure
/// that says why it is worth doing.
///
/// <para>The arithmetic of reading between samples is <c>FractionalDelayTests</c>'. What is tested
/// here is the <b>bookkeeping</b> — which record sets the grid, which direction a record is moved,
/// and that a shift too small to act on does not quietly become the new grid.</para>
/// </summary>
public class RecordAlignerTests
{
    private static double[][] Tone(int frames, double cyclesPerSample, double delaySamples) =>
        [Enumerable.Range(0, frames)
            .Select(i => Math.Sin(2.0 * Math.PI * cyclesPerSample * (i - delaySamples)))
            .ToArray()];

    [Fact]
    public void TheFirstRecordSetsTheGridAndIsUsedAsItIs()
    {
        // So a single capture is never interpolated: the figures on screen for one are readings of
        // the samples themselves.
        var aligner = new RecordAligner();
        var channels = Tone(64, 0.1, 0.0);

        Assert.Null(aligner.Grid);
        var first = aligner.Align(channels, triggerOffsetSamples: 64.7);

        Assert.Same(channels, first);
        // The grid is the offset it was given -- the trigger's place inside the record, pre-trigger
        // length included -- rather than a fractional part, which is the whole of what this class
        // got wrong the first time.
        Assert.Equal(64.7, aligner.Grid!.Value, 12);
    }

    [Fact]
    public void ARecordOnTheSameFractionIsUsedAsItIs()
    {
        var aligner = new RecordAligner();
        aligner.Align(Tone(64, 0.1, 0.0), triggerOffsetSamples: 64.7);

        var same = Tone(64, 0.1, 0.0);
        Assert.Same(same, aligner.Align(same, triggerOffsetSamples: 64.7));
    }

    [Fact]
    public void ARecordTriggeredEarlierWithinItsSamples_IsMovedLaterOntoTheGrid()
    {
        // The sign, which is the thing to get right: the grid's trigger sits at .7 and this
        // record's at .2, so its waveform has to move 0.5 samples LATER to line up.
        const double cycles = 0.1;
        var aligner = new RecordAligner();

        // The grid record: its waveform sits where a trigger at .7 put it.
        aligner.Align(Tone(256, cycles, delaySamples: 0.7), triggerOffsetSamples: 64.7);

        // This one was triggered at .2, so its waveform is 0.5 samples earlier.
        var later = aligner.Align(Tone(256, cycles, delaySamples: 0.2), triggerOffsetSamples: 64.2);

        // After aligning it should read as though it had been delayed by 0.7 too.
        for (int i = FractionalDelay.Taps; i < 256 - FractionalDelay.Taps; i++)
        {
            double expected = Math.Sin(2.0 * Math.PI * cycles * (i - 0.7));
            Assert.Equal(expected, later[0][i], 3);
        }
    }

    [Fact]
    public void AShiftTooSmallToActOn_DoesNotBecomeTheNewGrid()
    {
        // Taking the grid from whichever record happened to be close enough would let it walk a
        // thousandth of a sample at a time, which over 256 records is a quarter of a sample.
        var aligner = new RecordAligner();
        aligner.Align(Tone(64, 0.1, 0.0), triggerOffsetSamples: 10.5);

        aligner.Align(Tone(64, 0.1, 0.0), triggerOffsetSamples: 10.5004);

        Assert.Equal(10.5, aligner.Grid!.Value, 12);
    }

    [Fact]
    public void Reset_LetsTheNextRecordSetTheGrid()
    {
        var aligner = new RecordAligner();
        aligner.Align(Tone(64, 0.1, 0.0), triggerOffsetSamples: 10.25);
        Assert.Equal(10.25, aligner.Grid!.Value, 12);

        aligner.Reset();
        Assert.Null(aligner.Grid);

        aligner.Align(Tone(64, 0.1, 0.0), triggerOffsetSamples: 10.8);
        Assert.Equal(10.8, aligner.Grid!.Value, 12);
    }

    [Fact]
    public void ATriggerPositionThatIsNotANumber_LeavesTheRecordAndTheGridAlone()
    {
        var aligner = new RecordAligner();
        var channels = Tone(64, 0.1, 0.0);

        Assert.Same(channels, aligner.Align(channels, double.NaN));
        Assert.Null(aligner.Grid);
    }

    [Fact]
    public void AnEmptyRecordIsHandedBack()
    {
        var aligner = new RecordAligner();
        double[][] empty = [];

        Assert.Same(empty, aligner.Align(empty, 10.5));
        Assert.Null(aligner.Grid);
    }

    [Fact]
    public void EveryChannelIsMovedByTheSameShift()
    {
        // Or a stereo pair comes out with a relative phase that drifts from capture to capture,
        // which is the fault ScopeCaptureBuffer's single lock exists to prevent one layer down.
        const double cycles = 0.2;
        var aligner = new RecordAligner();
        aligner.Align([Tone(128, cycles, 0.5)[0], Tone(128, cycles, 0.5)[0]], triggerOffsetSamples: 10.5);

        var moved = aligner.Align(
            [Tone(128, cycles, 0.1)[0], Tone(128, cycles, 0.1)[0]], triggerOffsetSamples: 10.1);

        Assert.Equal(2, moved.Length);
        for (int i = FractionalDelay.Taps; i < 128 - FractionalDelay.Taps; i++)
            Assert.Equal(moved[0][i], moved[1][i], 12);
    }

    [Fact]
    public void RecordsEitherSideOfTheHalfSampleMark_AreAlignedToWithinTheKernelsResidue()
    {
        // THE regression. A record starts at round(triggerPosition) - preTrigger, so the trigger's
        // offset INSIDE it is preTrigger + (tp - round(tp)) -- a value in [pre-1/2, pre+1/2). The
        // fractional part of the absolute position is a different number: the two agree for a
        // crossing in the first half of a sample and differ by a WHOLE sample for one in the
        // second half. Aligning on the fractional part therefore shifted half the records a sample
        // too far and added a sample of spread to the half-sample it was there to remove --
        // measured on a 60 kHz tone at 192 kS/s as a 4.2 dB loss where doing nothing costs 1.4.
        //
        // The two records below are what that looks like: triggers at 1024.4 and 1025.6, whose
        // in-record offsets are pre+0.4 and pre-0.4, 0.8 of a sample apart -- where their absolute
        // fractional parts are 0.4 and 0.6, only 0.2 apart.
        const double cycles = 0.3;
        const int pre = 64;
        var aligner = new RecordAligner();

        // Record A: trigger at 1024.4, so its waveform sits 0.4 of a sample into the grid.
        aligner.Align(Tone(256, cycles, delaySamples: 0.4), triggerOffsetSamples: pre + 0.4);

        // Record B: trigger at 1025.6 -- rounded UP, so its offset inside the record is pre - 0.4.
        var aligned = aligner.Align(Tone(256, cycles, delaySamples: -0.4), triggerOffsetSamples: pre - 0.4);

        for (int i = FractionalDelay.Taps; i < 256 - FractionalDelay.Taps; i++)
        {
            double expected = Math.Sin(2.0 * Math.PI * cycles * (i - 0.4));
            Assert.Equal(expected, aligned[0][i], 2);
        }
    }

    [Fact]
    public void AnOffsetMoreThanASampleFromTheGrid_LeavesTheRecordAlone()
    {
        // Records of one geometry cannot differ by more than a sample, so a larger difference means
        // the record length or the pre-trigger moved under the accumulation -- and a shift for a
        // record this grid never described would be a correction for the wrong thing. The
        // accumulator's own shape check deals with it.
        var aligner = new RecordAligner();
        aligner.Align(Tone(64, 0.1, 0.0), triggerOffsetSamples: 16.3);

        var untouched = Tone(64, 0.1, 0.0);
        Assert.Same(untouched, aligner.Align(untouched, triggerOffsetSamples: 48.3));
    }

    [Fact]
    public void AlignedAccumulation_KeepsTheAmplitudeAnUnalignedOneLoses()
    {
        // The figure the whole thing exists for, measured through the two classes together rather
        // than through the primitive on its own. At 0.4 cycles a sample a uniform half-sample
        // misalignment costs sinc(0.4) = 0.757, i.e. 2.4 dB -- and it reads as a smaller waveform.
        const double cycles = 0.4;
        const int frames = 512;
        const int records = 64;

        var aligner = new RecordAligner();
        var aligned = new RecordAccumulator(RecordAccumulation.Average, records);
        var unaligned = new RecordAccumulator(RecordAccumulation.Average, records);

        double[][] alignedOut = [];
        double[][] unalignedOut = [];
        for (int r = 0; r < records; r++)
        {
            double fraction = (double)r / records;
            var record = Tone(frames, cycles, delaySamples: fraction);

            (alignedOut, _) = aligned.Fold(aligner.Align(record, triggerOffsetSamples: 64 + fraction));
            (unalignedOut, _) = unaligned.Fold(record);
        }

        double alignedRms = Rms(alignedOut[0]);
        double unalignedRms = Rms(unalignedOut[0]);

        Assert.Equal(1.0 / Math.Sqrt(2.0), alignedRms, 2);
        Assert.Equal(Sinc(cycles) / Math.Sqrt(2.0), unalignedRms, 2);
    }

    private static double Rms(double[] samples)
    {
        double total = 0.0;
        int from = FractionalDelay.Taps;
        int to = samples.Length - FractionalDelay.Taps;
        for (int i = from; i < to; i++)
            total += samples[i] * samples[i];
        return Math.Sqrt(total / (to - from));
    }

    private static double Sinc(double cyclesPerSample)
    {
        double x = Math.PI * cyclesPerSample;
        return Math.Sin(x) / x;
    }
}
