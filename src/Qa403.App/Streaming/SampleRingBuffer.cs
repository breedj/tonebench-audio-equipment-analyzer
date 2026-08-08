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

    public int Capacity { get; } = capacity;

    /// <summary>True once at least <see cref="Capacity"/> samples have ever been appended.</summary>
    public bool IsFull => Interlocked.Read(ref _writeIndex) >= Capacity;

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
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            Interlocked.Exchange(ref _writeIndex, 0);
        }
    }

    /// <summary>
    /// Appends every <paramref name="channelIndex"/>-th sample (stride <paramref name="channelCount"/>)
    /// from an interleaved block — i.e. one channel's worth of samples out of a multi-channel
    /// <c>PcmBlock</c>.
    /// </summary>
    public void AppendInterleaved(ReadOnlySpan<float> interleaved, int channelIndex, int channelCount)
    {
        lock (_lock)
        {
            int cap = _buffer.Length;
            for (int i = channelIndex; i < interleaved.Length; i += channelCount)
            {
                _buffer[_writeIndex % cap] = interleaved[i];
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
            int cap = _buffer.Length;
            long start = Math.Max(0, _writeIndex - cap);
            for (int i = 0; i < cap; i++)
                destination[i] = _buffer[(start + i) % cap];
        }
    }
}
