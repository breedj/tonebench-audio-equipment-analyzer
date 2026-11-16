using Tonebench.App.Streaming;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The oscilloscope's capture buffer. Everything here is about the one thing it exists to get
/// right: an absolute sample index means the same moment to the trigger, to the pre-trigger window
/// and to both channels, and a window that cannot be served honestly is refused rather than
/// half-filled.
/// </summary>
public class ScopeCaptureBufferTests
{
    /// <summary>One block of interleaved frames whose left sample is the frame's absolute index.</summary>
    private static float[] Ramp(int frames, long firstIndex, int channelCount = 2, float rightOffset = 1000f)
    {
        var block = new float[frames * channelCount];
        for (int f = 0; f < frames; f++)
        {
            block[f * channelCount] = firstIndex + f;
            for (int c = 1; c < channelCount; c++)
                block[f * channelCount + c] = firstIndex + f + rightOffset;
        }

        return block;
    }

    private static ScopeCaptureBuffer Filled(int capacityFrames, int totalFrames, int blockFrames = 64)
    {
        var buffer = new ScopeCaptureBuffer(2, capacityFrames);
        for (long i = 0; i < totalFrames; i += blockFrames)
            buffer.Write(Ramp(blockFrames, i), default, startsAfterDiscontinuity: false);

        return buffer;
    }

    [Fact]
    public void TheCapacityIsRoundedUpToAPowerOfTwo()
    {
        // The wrap is a mask, not a modulo, because this lock blocks the thread draining the device.
        Assert.Equal(1024, new ScopeCaptureBuffer(2, 1000).CapacityFrames);
        Assert.Equal(1024, new ScopeCaptureBuffer(2, 1024).CapacityFrames);
        Assert.Equal(2048, new ScopeCaptureBuffer(2, 1025).CapacityFrames);
    }

    [Fact]
    public void AWindowIsAddressedAbsolutely_AndSurvivesTheWrap()
    {
        // 4096 frames written into a 1024-frame ring: everything below 3072 is gone, and the
        // window asked for here straddles the point where the storage wraps.
        var buffer = Filled(capacityFrames: 1024, totalFrames: 4096);

        Assert.Equal(4096, buffer.Newest);
        Assert.Equal(3072, buffer.Oldest);

        var window = new double[500];
        Assert.True(buffer.CopyRange(0, 3300, window));

        for (int i = 0; i < window.Length; i++)
            Assert.Equal(3300 + i, window[i], 6);
    }

    [Fact]
    public void AWindowThatHasBeenOverwritten_IsRefusedRatherThanHalfServed()
    {
        var buffer = Filled(capacityFrames: 1024, totalFrames: 4096);

        // Starts one frame below the oldest held. Half a record drawn as though it were whole is
        // worse than none, because nothing on screen would say which half was real.
        Assert.False(buffer.CopyRange(0, 3071, new double[100]));
        Assert.True(buffer.CopyRange(0, 3072, new double[100]));
    }

    [Fact]
    public void AWindowThatHasNotHappenedYet_IsRefused()
    {
        var buffer = Filled(capacityFrames: 1024, totalFrames: 4096);

        Assert.False(buffer.CopyRange(0, 4000, new double[200]));
        Assert.True(buffer.CopyRange(0, 3896, new double[200]));
    }

    [Fact]
    public void BothChannelsAreReadAtTheSameInstant()
    {
        // The set rule, one layer below SpectrumPipeline._sampleSetLock: a trigger found in the
        // left channel indexes the right at the same absolute position, so the two must have been
        // appended together. The right channel here is the left plus a constant, so any slip shows
        // up as that constant changing.
        var buffer = Filled(capacityFrames: 1024, totalFrames: 2048);

        var left = new double[256];
        var right = new double[256];
        Assert.True(buffer.CopyRange(0, 1500, left));
        Assert.True(buffer.CopyRange(1, 1500, right));

        for (int i = 0; i < left.Length; i++)
            Assert.Equal(1000.0, right[i] - left[i], 6);
    }

    [Fact]
    public void ADiscontinuity_RefusesEveryWindowThatSpansIt()
    {
        // An input-range step punches a hole of order 110 ms. The two samples either side of it
        // were never adjacent, so a record drawn across them shows a step the DUT never made.
        var buffer = new ScopeCaptureBuffer(2, 4096);
        buffer.Write(Ramp(1024, 0), default, startsAfterDiscontinuity: false);
        buffer.Write(Ramp(1024, 1024), default, startsAfterDiscontinuity: true);

        Assert.Equal(1024, buffer.ContiguousFrom);

        Assert.False(buffer.CopyRange(0, 1000, new double[100]));   // spans the break
        Assert.False(buffer.CopyRange(0, 900, new double[100]));    // entirely before it
        Assert.True(buffer.CopyRange(0, 1024, new double[100]));    // entirely after it
    }

    [Fact]
    public void ADiscontinuity_BumpsTheEpoch()
    {
        var buffer = new ScopeCaptureBuffer(2, 4096);
        int before = buffer.Epoch;

        buffer.Write(Ramp(64, 0), default, startsAfterDiscontinuity: false);
        Assert.Equal(before, buffer.Epoch);

        buffer.Write(Ramp(64, 64), default, startsAfterDiscontinuity: true);
        Assert.Equal(before + 1, buffer.Epoch);

        buffer.NoteDiscontinuity();
        Assert.Equal(before + 2, buffer.Epoch);
    }

    [Fact]
    public void ABookedDiscard_RefusesTheFramesItCovers()
    {
        // The stimulus already committed to the DAC, plus the DUT's own settle.
        var buffer = new ScopeCaptureBuffer(2, 4096);
        buffer.Write(Ramp(512, 0), default, startsAfterDiscontinuity: false);
        buffer.BookDiscard(256);

        Assert.Equal(768, buffer.ContiguousFrom);

        buffer.Write(Ramp(1024, 512), default, startsAfterDiscontinuity: false);

        Assert.False(buffer.CopyRange(0, 700, new double[100]));
        Assert.True(buffer.CopyRange(0, 768, new double[100]));
    }

    [Fact]
    public void ALaterShorterDiscard_CannotCancelOneAlreadyOwed()
    {
        // The rule FlushAfterRangeStep's keepPendingDiscard exists for, and here it is structural
        // rather than a flag: the device's autorange report lands a block AFTER the step, by which
        // time the stimulus path may have booked a longer discard of its own. Overwriting it would
        // delete a discard nothing re-books, and the point's averages would begin at the tone step.
        var buffer = new ScopeCaptureBuffer(2, 8192);
        buffer.Write(Ramp(512, 0), default, startsAfterDiscontinuity: false);

        buffer.BookDiscard(4096);
        Assert.Equal(4608, buffer.ContiguousFrom);

        buffer.BookDiscard(16);
        Assert.Equal(4608, buffer.ContiguousFrom);

        buffer.NoteDiscontinuity();
        Assert.Equal(4608, buffer.ContiguousFrom);
    }

    [Fact]
    public void TheExternalGainIsUndoneOnTheWayIn()
    {
        // The scope draws the DUT's volts, not the analyzer's -- the same multiply
        // SpectrumPipeline.Enqueue applies at its own door, so a 10:1 divider reads as the DUT.
        var buffer = new ScopeCaptureBuffer(2, 1024);
        double[] scale = [10.0, 0.5];
        buffer.Write(Ramp(64, 0), scale, startsAfterDiscontinuity: false);

        var left = new double[8];
        var right = new double[8];
        Assert.True(buffer.CopyRange(0, 0, left));
        Assert.True(buffer.CopyRange(1, 0, right));

        for (int i = 0; i < left.Length; i++)
        {
            Assert.Equal(i * 10.0, left[i], 6);
            Assert.Equal((i + 1000.0) * 0.5, right[i], 6);
        }
    }

    [Fact]
    public void AMismatchedScale_IsRefused()
    {
        var buffer = new ScopeCaptureBuffer(2, 1024);
        double[] tooFew = [1.0];

        Assert.Throws<ArgumentException>(() => buffer.Write(Ramp(8, 0), tooFew, startsAfterDiscontinuity: false));
    }

    [Fact]
    public void Resizing_KeepsTheClockRunningButServesNothingFromBefore()
    {
        // The index has to stay a true clock across a record-length change: it is what the trigger
        // and every record bound are expressed in.
        var buffer = Filled(capacityFrames: 1024, totalFrames: 2048);
        long before = buffer.Newest;

        buffer.EnsureCapacity(4096);

        Assert.Equal(before, buffer.Newest);
        Assert.Equal(before, buffer.ContiguousFrom);
        Assert.Equal(4096, buffer.CapacityFrames);
        Assert.False(buffer.CopyRange(0, before - 100, new double[50]));
    }

    [Fact]
    public void ResizingToTheSameDepth_ChangesNothing()
    {
        var buffer = Filled(capacityFrames: 1024, totalFrames: 2048);

        buffer.EnsureCapacity(1024);

        Assert.Equal(0, buffer.ContiguousFrom);
        Assert.True(buffer.CopyRange(0, 1500, new double[100]));
    }

    [Fact]
    public void AvailableFramesIsWhatCanActuallyBeServed()
    {
        var buffer = new ScopeCaptureBuffer(2, 1024);
        Assert.Equal(0, buffer.AvailableFrames);

        buffer.Write(Ramp(512, 0), default, startsAfterDiscontinuity: false);
        Assert.Equal(512, buffer.AvailableFrames);

        // Past capacity it is the ring's depth, not everything ever written.
        for (long i = 512; i < 4096; i += 512)
            buffer.Write(Ramp(512, i), default, startsAfterDiscontinuity: false);
        Assert.Equal(1024, buffer.AvailableFrames);

        // And a break shortens it to whatever has arrived since.
        buffer.Write(Ramp(256, 4096), default, startsAfterDiscontinuity: true);
        Assert.Equal(256, buffer.AvailableFrames);
    }
}
