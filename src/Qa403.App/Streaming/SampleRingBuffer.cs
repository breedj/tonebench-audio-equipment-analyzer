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
