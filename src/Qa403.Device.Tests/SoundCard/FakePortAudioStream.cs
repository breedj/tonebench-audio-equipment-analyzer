using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// A stream that behaves like a card without being one: reads hand back whatever
/// <see cref="InputSample"/> says, writes are captured, and both can be told to report the driver's
/// two non-fatal overflow conditions.
///
/// <para><b>Reads pace themselves</b>, because that is the property the pump is built on: a real
/// card's read blocks until it has a transfer's worth, which is what stops the loop outrunning the
/// consumer. Returning at once instead let the producer race ahead until the drop-oldest channel
/// began discarding, which showed up as non-contiguous sequence numbers and a discontinuity flag on
/// the wrong block — intermittently, and only under load. <see cref="PaceReads"/> turns it off for
/// a test that wants the race.</para>
/// </summary>
internal sealed class FakePortAudioStream : IPortAudioStream
{
    private readonly object _gate = new();
    private readonly List<float[]> _written = [];

    public double InputLatencyS { get; set; } = 0.01;

    public double OutputLatencyS { get; set; } = 0.02;

    public double SampleRateHz { get; set; } = 48_000.0;

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Order of native calls, so a test can assert that teardown really happened and in which order.</summary>
    public List<string> Calls { get; } = [];

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    /// <summary>What every input sample reads as, given the frame index and channel.</summary>
    public Func<long, int, float> InputSample { get; set; } = (_, _) => 0f;

    /// <summary>Reads on which the driver reports an input overflow — samples lost before the transfer.</summary>
    public HashSet<int> OverflowOnRead { get; } = [];

    /// <summary>Writes on which the driver reports an output underflow — the DAC ran dry.</summary>
    public HashSet<int> UnderflowOnWrite { get; } = [];

    private long _frameIndex;

    /// <summary>Copies of every buffer written, in order.</summary>
    public IReadOnlyList<float[]> Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    public void Start()
    {
        Calls.Add("start");
        Started = true;
    }

    public void Stop()
    {
        Calls.Add("stop");
        Stopped = true;
    }

    /// <summary>Whether a read takes as long as the audio it returns, as a real card's does.</summary>
    public bool PaceReads { get; set; } = true;

    /// <summary>
    /// Reads that throw <c>paTimedOut</c> instead of returning data, by read index. A real ASIO
    /// stream does this for the first few hundred milliseconds, before the driver delivers its
    /// first buffer.
    /// </summary>
    public HashSet<int> TimeOutOnRead { get; } = [];

    /// <summary>
    /// Writes made before the first read — i.e. the priming, exactly. Observed here rather than
    /// inferred from <see cref="WriteCount"/> afterwards, which races: the consumer is handed a
    /// block before the pump makes that iteration's write, so counting afterwards gives one more or
    /// one fewer depending on scheduling.
    /// </summary>
    public int WritesBeforeFirstRead { get; private set; } = -1;

    public bool Read(float[] buffer, int frames)
    {
        if (WritesBeforeFirstRead < 0)
            WritesBeforeFirstRead = WriteCount;

        if (PaceReads)
            Thread.Sleep(TimeSpan.FromSeconds(frames / Math.Max(1.0, SampleRateHz)));

        if (TimeOutOnRead.Contains(ReadCount))
        {
            ReadCount++;
            throw new PortAudioException("timed out", PaError.TimedOut);
        }

        int index = ReadCount++;
        int channels = buffer.Length / Math.Max(1, frames);

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
                buffer[(f * channels) + c] = InputSample(_frameIndex + f, c);
        }

        _frameIndex += frames;
        return OverflowOnRead.Contains(index);
    }

    public bool Write(float[] buffer, int frames)
    {
        int index = WriteCount++;
        lock (_gate)
        {
            _written.Add([.. buffer.AsSpan(0, Math.Min(buffer.Length, frames * 4))]);
        }

        return UnderflowOnWrite.Contains(index);
    }

    public void Dispose()
    {
        Calls.Add("dispose");
        Disposed = true;
    }
}
