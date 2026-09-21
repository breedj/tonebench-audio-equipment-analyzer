namespace Qa403.App.Streaming;

/// <summary>
/// Fixed-capacity circular buffer of the most recent samples for one channel, in volts. The
/// spectrum pipeline's FFT worker always analyzes the latest full window via
/// <see cref="SnapshotLatest"/> — a sliding window, not a strict one-shot-per-N-samples capture —
/// so the display can tick at a steady rate independent of how <c>Capacity</c> compares to the
/// rate new samples arrive.
/// </summary>
internal sealed class SampleRingBuffer(int capacity)
{
    private readonly double[] _buffer = new double[capacity];
    private readonly object _lock = new();
    private long _writeIndex;
    private int _discardRemaining;

    public int Capacity { get; } = capacity;

    /// <summary>True once at least <see cref="Capacity"/> samples have ever been appended.</summary>
    public bool IsFull => Interlocked.Read(ref _writeIndex) >= Capacity;

    /// <summary>
    /// Samples appended since the last <see cref="Reset"/>, counting only those that actually
    /// landed (the ones dropped to drain the stimulus latency do not advance this).
    ///
    /// <para>Monotonic within a reset epoch, which is what makes it usable as the clock for
    /// overlap-controlled averaging: two analyses whose <c>SamplesWritten</c> differ by at least
    /// <see cref="Capacity"/> share no samples at all, and by <c>Capacity/2</c> share exactly half.
    /// See <c>SpectrumPipelineOptions.AverageHopFraction</c> — without a counter like this the
    /// pipeline can only average on its display tick, whose relationship to the record length is
    /// accidental.</para>
    /// </summary>
    public long SamplesWritten => Interlocked.Read(ref _writeIndex);

    /// <summary>
    /// Stimulus-latency samples still to be dropped before anything lands in the buffer — see
    /// <see cref="Reset"/>'s <c>discardSamples</c>.
    ///
    /// <para>Exposed so a ring-buffer <i>resize</i> can carry the pending discard onto the
    /// replacement buffer. An FFT-size change allocates fresh buffers on the acquisition thread
    /// (<c>SpectrumPipeline.EnsureRingBuffers</c>), and a fresh buffer's discard counter is zero —
    /// so a sweep that changes FFT size and stimulus together would otherwise lose the discard
    /// booked by the flush and refill its first record with pre-change audio, which is the exact
    /// failure the discard exists to prevent.</para>
    /// </summary>
    public int DiscardRemaining
    {
        get
        {
            lock (_lock)
                return _discardRemaining;
        }
    }

    /// <summary>
    /// Discards everything buffered, so <see cref="IsFull"/> goes false until a whole new window
    /// of samples has arrived.
    ///
    /// Called whenever the signal reaching this buffer changes discontinuously — acquisition
    /// restart, a new sample rate, a generator or input-range change. Without it the next FFT
    /// analyzes a record spliced from before and after the change, and a step part-way through a
    /// record smears the tone's energy into a broad skirt either side of it: the spectrum shows a
    /// noise floor tens of dB too high, decaying over exactly one buffer-fill as the stale samples
    /// slide out. The fill time is Capacity/sampleRate — 0.68 s for a 128k FFT at 192 kHz, but
    /// 21.8 s for a 1M FFT at 48 kHz.
    ///
    /// <para><paramref name="discardSamples"/> is what makes this actually work on real hardware.
    /// Emptying the buffer is only half the job: the change has not reached the wire yet when the
    /// caller returns, so the samples arriving immediately afterwards still carry the *old* stimulus
    /// and refill this buffer with exactly the discontinuity the reset was meant to exclude. Pass
    /// <see cref="Qa403.Device.IAnalyzerDevice.StimulusLatencyFrames"/> so those are dropped too.
    /// Measured on a QA403, 1 kHz at -18 -> -17 dBV with no range change of any kind, first record
    /// after the reset versus steady state:</para>
    /// <code>
    ///   FFT      reset only     reset + discard
    ///   4096     +16.4 dB           +0.3 dB
    ///   16384    +25.4 dB           -0.1 dB
    ///   32768    +41.3 dB           +0.1 dB
    ///   65536     +1.9 dB           +0.0 dB
    /// </code>
    /// <para>A 1 dB step is enough to do that because the damage scales with where the step lands
    /// under the window, not with its size. It is also why the artifact never appears if acquisition
    /// is stopped, the generator changed, and acquisition restarted: by then the stale audio is long
    /// gone and the buffer fills entirely from post-change samples.</para>
    /// </summary>
    public void Reset(int discardSamples = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(discardSamples);

        lock (_lock)
        {
            Array.Clear(_buffer);
            Interlocked.Exchange(ref _writeIndex, 0);
            _discardRemaining = discardSamples;
        }
    }

    /// <summary>
    /// Appends every <paramref name="channelIndex"/>-th sample (stride <paramref name="channelCount"/>)
    /// from an interleaved block — i.e. one channel's worth of samples out of a multi-channel
    /// <c>PcmBlock</c>. Each sample is multiplied by <paramref name="scale"/> on the way in: the
    /// external input gain undone, so the buffer holds the DUT's volts rather than the analyzer's.
    /// Done here, where the float becomes a double, because it is the one place every sample passes.
    /// </summary>
    public void AppendInterleaved(ReadOnlySpan<float> interleaved, int channelIndex, int channelCount, double scale = 1.0)
    {
        lock (_lock)
        {
            int cap = _buffer.Length;
            for (int i = channelIndex; i < interleaved.Length; i += channelCount)
            {
                // Still draining the stimulus latency booked by Reset — these samples predate the
                // change that caused it, so they must not land here at all.
                if (_discardRemaining > 0)
                {
                    _discardRemaining--;
                    continue;
                }

                _buffer[_writeIndex % cap] = interleaved[i] * scale;
                _writeIndex++;
            }
        }
    }

    /// <summary>
    /// Copies the most recent <see cref="Capacity"/> samples into <paramref name="destination"/>
    /// (must be exactly <see cref="Capacity"/> long), oldest first. Only meaningful once
    /// <see cref="IsFull"/> is true.
    /// </summary>
    public void SnapshotLatest(Span<double> destination)
    {
        if (destination.Length != Capacity)
            throw new ArgumentException("Destination must match Capacity.", nameof(destination));

        lock (_lock)
        {
            // Two block copies rather than a modulo per element: the window is contiguous in at most
            // two runs (oldest sample to the end of the storage, then wrap to the write head). This
            // is not just tidier -- SpectrumPipeline holds one lock across *every* channel's snapshot
            // so the channels describe the same instant, and that lock blocks the acquisition drain
            // loop, so the per-element version's cost at a 1M-point FFT was a real risk of dropped
            // RX chunks (which invalidate a noise measurement outright).
            int cap = _buffer.Length;
            long start = Math.Max(0, _writeIndex - cap);
            int head = (int)(start % cap);

            int firstRun = cap - head;
            _buffer.AsSpan(head, firstRun).CopyTo(destination);
            if (firstRun < cap)
                _buffer.AsSpan(0, cap - firstRun).CopyTo(destination[firstRun..]);
        }
    }
}
