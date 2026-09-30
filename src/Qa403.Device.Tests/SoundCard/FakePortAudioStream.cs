using Qa403.Device.SoundCard;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// A stream that behaves like a card without being one: reads hand back whatever
/// <see cref="InputSample"/> says, writes are captured, and both can be told to report the driver's
/// two non-fatal overflow conditions.
///
/// <para>Reads do <b>not</b> block for real time. A pump paced by a real card's clock would make
/// every test take as long as the audio it moves; here the read returns at once and the test
/// decides how many transfers to let through.</para>
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

    public bool Read(float[] buffer, int frames)
    {
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
