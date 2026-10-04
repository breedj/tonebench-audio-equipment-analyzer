using Tonebench.Data;

namespace Tonebench.App.Services;

/// <summary>
/// The live copy of <see cref="CaptureDelayConfig"/>: the settle time every stimulus change discards
/// on top of the device's stimulus latency.
///
/// <para>A singleton held in memory rather than read from <see cref="ConfigRepository"/> on demand,
/// because the reader is <see cref="DeviceSessionService.ConfigureGeneratorAsync"/> — called once
/// per sweep point, under the session lock, from a path that has no business doing database I/O.
/// <c>Program</c> loads it once at startup and the <c>/api/config/capture-delay</c> endpoint writes
/// both the document and this. A test builds one bare and sets <see cref="Ms"/> directly.</para>
/// </summary>
public sealed class CaptureDelaySetting
{
    private double _ms;

    /// <summary>The settle time in milliseconds. Written by the endpoint, read on the session's stimulus path.</summary>
    public double Ms
    {
        get => Volatile.Read(ref _ms);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _ms, value);
        }
    }

    /// <summary>The settle time as a whole number of frames at <paramref name="sampleRateHz"/> — what a ring-buffer discard is counted in.</summary>
    public int FramesAt(double sampleRateHz) => (int)Math.Round(Ms * sampleRateHz / 1000.0);
}
