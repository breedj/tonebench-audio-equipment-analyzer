namespace Tonebench.App.Streaming;

/// <summary>
/// The oscilloscope's own view of the stream: every channel, in one object, addressed by
/// <b>absolute</b> sample index, with enough history behind the write head to serve a record that
/// began before its trigger was found.
///
/// <para><b>Why this is not <see cref="SampleRingBuffer"/>.</b> That one is per channel, its
/// capacity <i>is</i> the FFT size, and <c>SnapshotLatest</c> answers one question -- the latest
/// whole window -- into a destination that must be exactly <c>Capacity</c> long. A scope asks a
/// different question: an arbitrary window, chosen after the fact, whose length is a timebase and
/// has nothing to do with any FFT, and which usually <i>ends</i> before the newest sample because
/// most of it is pre-trigger. Reusing the pipeline's buffer would mean searching inside an
/// already-captured window, and then the trigger position is wherever the search happened to land
/// rather than a setting the user chose.</para>
///
/// <para><b>One object, every channel, one lock.</b> This is the rule that "all channels of one
/// tick are captured as a set" exists for, one layer down. A trigger found in the left channel
/// indexes the right one at the same absolute position, so if the two were appended under
/// different locks a record could pair left with a right that is a block older -- which on a scope
/// reads as a relative phase that drifts from capture to capture, and is exactly the fault
/// <c>SpectrumPipeline._sampleSetLock</c> was added for.</para>
///
/// <para><b>Samples are never dropped; the contiguity floor moves instead.</b> Where
/// <see cref="SampleRingBuffer"/> discards stimulus-latency samples so they never land, this
/// writes everything and raises <see cref="ContiguousFrom"/> past it. The absolute index has to
/// stay a true clock -- it is what the trigger, the pre-trigger window and the record bounds are
/// all expressed in -- and dropping samples would break the relationship between an index and the
/// moment it happened. A window that would include unusable samples is refused rather than
/// silently shortened.</para>
///
/// <para><b>The floor only ever moves forward</b>, which gives the <c>keepPendingDiscard</c> rule
/// for free: the device's autorange report arrives a block <i>after</i> the step, by which time a
/// stimulus change may already have booked a longer discard, and taking the later of the two can
/// never cancel it.</para>
/// </summary>
public sealed class ScopeCaptureBuffer
{
    private readonly object _lock = new();
    private double[][] _channels;
    private int _capacityFrames;
    private int _mask;

    private long _writeIndex;
    private long _contiguousFrom;
    private int _epoch;

    /// <param name="channelCount">How many channels the device delivers; two on everything this app drives.</param>
    /// <param name="capacityFrames">Requested depth in frames; rounded up to a power of two.</param>
    public ScopeCaptureBuffer(int channelCount, int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);

        ChannelCount = channelCount;
        _capacityFrames = RoundUpPowerOfTwo(capacityFrames);
        _mask = _capacityFrames - 1;
        _channels = Allocate(channelCount, _capacityFrames);
    }

    public int ChannelCount { get; }

    /// <summary>Depth in frames. Always a power of two, so the wrap is a mask rather than a modulo.</summary>
    public int CapacityFrames
    {
        get
        {
            lock (_lock)
                return _capacityFrames;
        }
    }

    /// <summary>One past the newest frame written, in absolute frame indices. Monotonic for the life of the buffer.</summary>
    public long Newest
    {
        get
        {
            lock (_lock)
                return _writeIndex;
        }
    }

    /// <summary>The oldest frame still held -- anything below it has been overwritten.</summary>
    public long Oldest
    {
        get
        {
            lock (_lock)
                return Math.Max(0, _writeIndex - _capacityFrames);
        }
    }

    /// <summary>
    /// The first frame of the current unbroken stretch. A window starting below this spans either a
    /// hole in the stream or audio that predates a change nobody wants measured, and is refused.
    /// </summary>
    public long ContiguousFrom
    {
        get
        {
            lock (_lock)
                return _contiguousFrom;
        }
    }

    /// <summary>
    /// Bumped every time the stream breaks. A record carries the epoch it was taken in, so a
    /// consumer holding one can tell that the world has moved on underneath it.
    /// </summary>
    public int Epoch
    {
        get
        {
            lock (_lock)
                return _epoch;
        }
    }

    /// <summary>The longest window that can currently be served, in frames.</summary>
    public long AvailableFrames
    {
        get
        {
            lock (_lock)
            {
                long floor = Math.Max(_contiguousFrom, Math.Max(0, _writeIndex - _capacityFrames));
                return Math.Max(0, _writeIndex - floor);
            }
        }
    }

    /// <summary>
    /// Resizes to hold at least <paramref name="capacityFrames"/>, discarding what is held. Called
    /// when the record length or the sample rate changes, off the acquisition thread.
    /// </summary>
    public void EnsureCapacity(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        int wanted = RoundUpPowerOfTwo(capacityFrames);

        lock (_lock)
        {
            if (wanted == _capacityFrames)
                return;

            _capacityFrames = wanted;
            _mask = wanted - 1;
            _channels = Allocate(ChannelCount, wanted);

            // Everything held described a different depth; nothing before here may be read, but the
            // clock keeps running so an index still means the moment it always meant.
            _contiguousFrom = _writeIndex;
            _epoch++;
        }
    }

    /// <summary>
    /// Marks the stream broken as of the next frame written: an input-range step, which punches a
    /// hole of order 110 ms that no record may span.
    /// </summary>
    public void NoteDiscontinuity()
    {
        lock (_lock)
        {
            _contiguousFrom = Math.Max(_contiguousFrom, _writeIndex);
            _epoch++;
        }
    }

    /// <summary>
    /// Refuses the next <paramref name="frames"/> frames: the stimulus already committed to the DAC
    /// plus the DUT's own settle, booked at the one seam every stimulus change passes through.
    /// Takes the later of this and whatever is already owed, so a late report cannot cancel it.
    /// </summary>
    public void BookDiscard(long frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);

        lock (_lock)
            _contiguousFrom = Math.Max(_contiguousFrom, _writeIndex + frames);
    }

    /// <summary>
    /// Appends one block of interleaved samples, applying <paramref name="scale"/> per channel --
    /// the external input gain undone, so the scope draws the DUT's volts and not the analyzer's,
    /// the same multiply <c>SpectrumPipeline.Enqueue</c> applies at its own door.
    /// </summary>
    /// <param name="interleaved">Frames of <see cref="ChannelCount"/> samples each, in logical channel order.</param>
    /// <param name="scale">One multiplier per channel, or empty for unity.</param>
    /// <param name="startsAfterDiscontinuity">The block's own flag; breaks contiguity before anything lands.</param>
    public void Write(ReadOnlySpan<float> interleaved, ReadOnlySpan<double> scale, bool startsAfterDiscontinuity)
    {
        if (!scale.IsEmpty && scale.Length < ChannelCount)
            throw new ArgumentException("A scale must be given for every channel, or none at all.", nameof(scale));

        lock (_lock)
        {
            if (startsAfterDiscontinuity)
            {
                _contiguousFrom = Math.Max(_contiguousFrom, _writeIndex);
                _epoch++;
            }

            int frames = interleaved.Length / ChannelCount;
            for (int f = 0; f < frames; f++)
            {
                int slot = (int)(_writeIndex & _mask);
                int baseIndex = f * ChannelCount;
                for (int c = 0; c < ChannelCount; c++)
                {
                    double v = interleaved[baseIndex + c];
                    _channels[c][slot] = scale.IsEmpty ? v : v * scale[c];
                }

                _writeIndex++;
            }
        }
    }

    /// <summary>
    /// Copies <c>destination.Length</c> frames of one channel, starting at absolute frame
    /// <paramref name="fromAbsolute"/>.
    ///
    /// <para>Answers <b>false</b> rather than serving part of a window: below <see cref="Oldest"/>
    /// the samples have been overwritten, below <see cref="ContiguousFrom"/> they span a break, and
    /// past <see cref="Newest"/> they have not happened yet. Half a record drawn as though it were
    /// whole is worse than no record, because nothing on screen would say which half was real.</para>
    /// </summary>
    public bool CopyRange(int channel, long fromAbsolute, Span<double> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);

        if (destination.IsEmpty)
            return true;

        lock (_lock)
        {
            long end = fromAbsolute + destination.Length;
            if (fromAbsolute < _contiguousFrom
                || fromAbsolute < _writeIndex - _capacityFrames
                || end > _writeIndex)
            {
                return false;
            }

            // Two block copies rather than a modulo per element, for SampleRingBuffer's own reason:
            // this lock blocks the thread that is draining the device, and a block not taken is a
            // dropped block.
            var source = _channels[channel];
            int head = (int)(fromAbsolute & _mask);
            int firstRun = Math.Min(destination.Length, _capacityFrames - head);
            source.AsSpan(head, firstRun).CopyTo(destination);
            if (firstRun < destination.Length)
                source.AsSpan(0, destination.Length - firstRun).CopyTo(destination[firstRun..]);

            return true;
        }
    }

    private static double[][] Allocate(int channelCount, int capacityFrames)
    {
        var channels = new double[channelCount][];
        for (int c = 0; c < channelCount; c++)
            channels[c] = new double[capacityFrames];

        return channels;
    }

    private static int RoundUpPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
        {
            result <<= 1;
            if (result <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The capacity is too large to hold.");
        }

        return result;
    }
}
