using Qa403.Data;
using Qa403.Device.SoundCard;

namespace Qa403.App.Services;

/// <summary>
/// The live copy of <see cref="SoundCardConfig"/>, held in memory for the same reason
/// <see cref="CaptureDelaySetting"/> is: its readers are on paths that must not do database I/O —
/// <see cref="AnalyzerDeviceFactory"/> building a device, and the session applying the effective
/// input gain on a switch.
///
/// <para>It also owns the translation into the device layer's own two records, so exactly one place
/// knows that the document stores millivolts while <see cref="SoundCardCalibration"/> works in
/// peak volts.</para>
/// </summary>
public sealed class SoundCardSetting
{
    private readonly Lock _gate = new();
    private SoundCardConfig _config = SoundCardConfig.Default;

    public SoundCardConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _config = value;
            }
        }
    }

    /// <summary>What the device needs to find and open the card.</summary>
    public SoundCardSettings DeviceSettings
    {
        get
        {
            SoundCardConfig c = Config;
            return new SoundCardSettings
            {
                InputHostApi = c.InputHostApi,
                InputDeviceName = c.InputDeviceName,
                OutputHostApi = c.OutputHostApi,
                OutputDeviceName = c.OutputDeviceName,
                InputChannelOffset = c.InputChannelOffset,
                OutputChannelOffset = c.OutputChannelOffset,
                FrontEndGainLeftDb = c.FrontEndGainLeftDb,
                FrontEndGainRightDb = c.FrontEndGainRightDb,
                GainDisplayUnit = c.GainDisplayUnit,
                MinStimulusFrequencyHz = c.MinStimulusFrequencyHz,
            };
        }
    }

    /// <summary>
    /// Where full scale sits, in peak volts. A null in the document stays null here rather than
    /// becoming a number — <see cref="SoundCardCalibration"/> answers "uncalibrated" from that, and
    /// the difference is what stops a card claiming volts it has never measured.
    /// </summary>
    public SoundCardCalibration Calibration
    {
        get
        {
            SoundCardConfig c = Config;
            return new SoundCardCalibration
            {
                OutputFullScalePeakV = ToVolts(c.OutputFullScalePeakMv),
                InputFullScalePeakVLeft = ToVolts(c.InputFullScalePeakMvLeft),
                InputFullScalePeakVRight = ToVolts(c.InputFullScalePeakMvRight),
            };
        }
    }

    private static double? ToVolts(double? millivolts) => millivolts is { } mv ? mv / 1000.0 : null;
}
