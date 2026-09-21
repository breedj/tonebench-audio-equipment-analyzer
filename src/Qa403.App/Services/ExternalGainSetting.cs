using Qa403.Data;
using Qa403.App.Streaming;

namespace Qa403.App.Services;

/// <summary>
/// The live copy of <see cref="ExternalGainConfig"/>: the gain, in dB, of whatever sits between
/// the DUT and each analyzer input (a 10:1 divider is −20 dB).
///
/// <para>Held in memory for the same reason <see cref="CaptureDelaySetting"/> is: the reader is
/// <see cref="Streaming.SpectrumPipeline.Enqueue"/>, once per block on the acquisition path, which
/// has no business doing database I/O. <c>Program</c> loads it at startup and the
/// <c>/api/config/external-gain</c> endpoint writes both the document and this. A test builds one
/// bare and sets the two channels directly.</para>
///
/// <para>The pipeline <em>divides</em> samples by the gain so every level downstream is the DUT's
/// output; the linear factor is cached per write because it is read per block, and the dB value is
/// what everything else quotes — the status, the saved parameters, and the one conversion back the
/// other way, <see cref="DeviceSessionService.ToAnalyzerDbv"/>, which input ranging needs.</para>
/// </summary>
public sealed class ExternalGainSetting
{
    private double _leftDb;
    private double _rightDb;
    private double _leftScale = 1.0;
    private double _rightScale = 1.0;

    /// <summary>Gain in front of the left input, in dB. Negative for a divider.</summary>
    public double LeftDb
    {
        get => Volatile.Read(ref _leftDb);
        set
        {
            Require(value);
            Volatile.Write(ref _leftScale, ScaleOf(value));
            Volatile.Write(ref _leftDb, value);
        }
    }

    /// <summary>Gain in front of the right input, in dB. Negative for a divider.</summary>
    public double RightDb
    {
        get => Volatile.Read(ref _rightDb);
        set
        {
            Require(value);
            Volatile.Write(ref _rightScale, ScaleOf(value));
            Volatile.Write(ref _rightDb, value);
        }
    }

    /// <summary>The gain of the given input channel, in dB.</summary>
    public double GainDb(StreamChannel channel) => channel == StreamChannel.Right ? RightDb : LeftDb;

    /// <summary>The gain of channel index <paramref name="channel"/> (0 = left), in dB.</summary>
    public double GainDb(int channel) => channel == 1 ? RightDb : LeftDb;

    /// <summary>
    /// What a sample of channel index <paramref name="channel"/> is multiplied by on its way into
    /// the pipeline: <c>10^(−gain/20)</c>, i.e. the divider undone. 1 when nothing is in the path.
    /// </summary>
    public double LinearScale(int channel) => Volatile.Read(ref channel == 1 ? ref _rightScale : ref _leftScale);

    /// <summary>True when either input has something in front of it.</summary>
    public bool Any => LeftDb != 0.0 || RightDb != 0.0;

    private static double ScaleOf(double gainDb) => Math.Pow(10.0, -gainDb / 20.0);

    private static void Require(double gainDb)
    {
        if (!double.IsFinite(gainDb) || Math.Abs(gainDb) > ExternalGainConfig.MaxAbsDb)
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, $"An external gain must be within ±{ExternalGainConfig.MaxAbsDb:0} dB.");
    }
}
