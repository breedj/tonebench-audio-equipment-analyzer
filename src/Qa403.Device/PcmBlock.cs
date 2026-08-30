using System.Buffers;

namespace Qa403.Device;

/// <summary>
/// One block of calibrated PCM, interleaved by channel, in volts.
///
/// Ownership contract: <see cref="Samples"/> wraps a buffer rented from
/// <see cref="ArrayPool{T}.Shared"/>. Producing an <see cref="IAnalyzerDevice"/> hands the
/// block, and ownership of that buffer, to whoever consumes <see cref="IAnalyzerDevice.StreamAsync"/>.
/// The consumer MUST call <see cref="Dispose"/> exactly once per block — typically via
/// <c>using</c>/<c>await using</c> inside the <c>await foreach</c> loop — which returns the
/// buffer to the pool. Do not read <see cref="Samples"/>, or retain a reference to it, after
/// disposal: the array may already be rented out to an unrelated caller. <see cref="Dispose"/>
/// itself is idempotent (a second call is a no-op) so double-dispose is safe, but disposing
/// from two different owners of the same block is not — there is exactly one owner at a time.
/// </summary>
public sealed class PcmBlock : IDisposable
{
    private float[]? _buffer;

    public PcmBlock(
        float[] buffer, int sampleCount, double sampleRateHz, int channelCount, long sequenceNumber,
        long timestampTicks, bool startsAfterDiscontinuity = false)
    {
        if (sampleCount < 0 || sampleCount > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (channelCount < 1)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        _buffer = buffer;
        SampleCount = sampleCount;
        SampleRateHz = sampleRateHz;
        ChannelCount = channelCount;
        SequenceNumber = sequenceNumber;
        TimestampTicks = timestampTicks;
        StartsAfterDiscontinuity = startsAfterDiscontinuity;
    }

    public double SampleRateHz { get; }

    public int ChannelCount { get; }

    /// <summary>Monotonically increasing per-stream block index, starting at 0.</summary>
    public long SequenceNumber { get; }

    /// <summary>Monotonic capture timestamp: a <see cref="System.Diagnostics.Stopwatch"/> tick count (see <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>), not wall-clock time.</summary>
    public long TimestampTicks { get; }

    /// <summary>
    /// True when the samples immediately preceding this block are missing — the stream is not
    /// continuous across its leading edge.
    ///
    /// <para>Set for the first block after an input-range step. That step quiesces EP2, and the
    /// quiesce's drain discards every already-completed RX transfer (about one queue depth, ~64 ms
    /// at 192 kHz) on top of the relay settle frames the assembler drops — a hole of order 110 ms.
    /// It is not counted by <see cref="IAnalyzerDevice.DroppedRxChunks"/>, which counts only chunks
    /// the consumer was too slow to take, and it is invisible in <see cref="SequenceNumber"/>,
    /// which is assigned locally and stays contiguous across it.</para>
    ///
    /// <para>A consumer that concatenates blocks into an analysis window must break the window
    /// here. <c>SampleRingBuffer</c> concatenates unconditionally, so a record spanning this splice
    /// shows the tone smeared into a broad skirt and the whole broadband floor lifted for exactly
    /// one record — the visible "glitch". <c>DeviceSessionService.PumpAsync</c> answers it with
    /// <c>SpectrumPipeline.Flush()</c>, which trades that record for a brief pause in updates.</para>
    ///
    /// <para>Flushed bare, with no stimulus-latency discard: unlike a generator or output-range
    /// change this is not a change on the way *out*, so there is no pre-change audio still queued
    /// for the DAC to outrun. Everything from this block on is already post-step.</para>
    /// </summary>
    public bool StartsAfterDiscontinuity { get; }

    /// <summary>Number of valid interleaved samples in <see cref="Samples"/> (may be less than the rented buffer's length).</summary>
    public int SampleCount { get; }

    public int FrameCount => SampleCount / ChannelCount;

    /// <summary>Interleaved calibrated samples in volts. Valid only until <see cref="Dispose"/> is called — see the type's ownership contract.</summary>
    public Span<float> Samples => (_buffer ?? throw new ObjectDisposedException(nameof(PcmBlock))).AsSpan(0, SampleCount);

    public void Dispose()
    {
        float[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
            ArrayPool<float>.Shared.Return(buffer);
    }
}
