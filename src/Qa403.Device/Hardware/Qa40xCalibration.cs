using System.Buffers.Binary;

namespace Qa403.Device.Hardware;

/// <summary>
/// The QA403's on-device factory calibration page, and the single documented point where raw
/// ADC/DAC counts become volts and volts become raw counts (CLAUDE.md: "Calibration is applied
/// exactly once, in Qa403.Device" / "converting raw ADC counts to volts"). <c>Qa403.Dsp</c> never
/// sees a raw count or a gain constant — everything downstream of <see cref="RawToVolts"/> is
/// already in volts.
///
/// Why an on-device read instead of qa40x-baremetal's hard-coded constants
/// (Acquisition.cs:103,197-198): those constants are valid for exactly one input range and one
/// output range each (the range the app happened to have selected when they were measured —
/// confirmed by the project's own README, quoted in docs/protocol.md §5). Autoranging requires
/// correct calibration at all 8 input ranges, which a single hard-coded constant cannot provide.
/// docs/protocol.md §2.4 documents the alternative this class implements: an on-device factory
/// calibration page, readable over the register channel and covering every range.
///
/// Load sequence (reference/PyQa40x-main/PyQa40x-main/src/PyQa40x/control.py:51-67
/// <c>load_calibration</c>): write <see cref="Qa40xRegisters.CalibrationPageSelectFactoryPage"/>
/// to <see cref="Qa40xRegisters.CalibrationPageSelect"/>, then read
/// <see cref="Qa40xRegisters.CalibrationPageStream"/> 128 times, each read yielding the next 4
/// bytes (little-endian) of the 512-byte page.
///
/// Record layout (PyQa40x control.py:69-121 <c>get_adc_cal</c>/<c>get_dac_cal</c>, corroborated
/// by reference/qa40x-rs-main's <c>adc_offset</c>/<c>dac_offset</c>, "per PyQa40x"): each range
/// has a 6-byte record — an unused int16 level field, then a float32 dB correction, both
/// little-endian (<c>struct.unpack_from('&lt;hf', ...)</c>). Right-channel record is
/// <c>left_offset + 6</c>. dB -> linear via <c>10^(db/20)</c>.
/// </summary>
internal sealed class Qa40xCalibration
{
    private const int RecordBytes = 6;
    private const int RightChannelOffset = 6;

    /// <summary>Byte offset of the left-channel ADC record for each input range dBV (PyQa40x control.py:80).</summary>
    private static readonly IReadOnlyDictionary<double, int> AdcOffsets = new Dictionary<double, int>
    {
        [0] = 24, [6] = 36, [12] = 48, [18] = 60, [24] = 72, [30] = 84, [36] = 96, [42] = 108,
    };

    /// <summary>Byte offset of the left-channel DAC record for each output range dBV (PyQa40x control.py:107).</summary>
    private static readonly IReadOnlyDictionary<double, int> DacOffsets = new Dictionary<double, int>
    {
        [-12] = 120, [-2] = 132, [8] = 144, [18] = 156,
    };

    private readonly byte[] _page;

    private Qa40xCalibration(byte[] page) => _page = page;

    /// <summary>Reads the 512-byte factory calibration page from the device (PyQa40x control.py:51-67).</summary>
    public static async Task<Qa40xCalibration> LoadAsync(Qa40xTransport transport, CancellationToken ct)
    {
        await transport.WriteRegisterAsync(
            Qa40xRegisters.CalibrationPageSelect, Qa40xRegisters.CalibrationPageSelectFactoryPage, ct).ConfigureAwait(false);

        byte[] page = new byte[Qa40xRegisters.CalibrationPageBytes];
        for (int offset = 0; offset < page.Length; offset += 4)
        {
            uint word = await transport.ReadRegisterAsync(Qa40xRegisters.CalibrationPageStream, ct).ConfigureAwait(false);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset, 4), word);
        }

        return new Qa40xCalibration(page);
    }

    /// <summary>Builds an instance directly from an in-memory page, bypassing the device read — for unit tests only (no hardware, no transport).</summary>
    internal static Qa40xCalibration FromPage(byte[] page)
    {
        if (page.Length != Qa40xRegisters.CalibrationPageBytes)
            throw new ArgumentException($"Calibration page must be {Qa40xRegisters.CalibrationPageBytes} bytes.", nameof(page));

        return new Qa40xCalibration(page);
    }

    /// <summary>Linear ADC correction factor for <paramref name="inputRangeDbv"/>/<paramref name="channel"/> (0=left, 1=right).</summary>
    public double GetAdcLinear(double inputRangeDbv, int channel) => ReadLinear(AdcOffsets, inputRangeDbv, channel);

    /// <summary>Linear DAC correction factor for <paramref name="outputRangeDbv"/>/<paramref name="channel"/> (0=left, 1=right).</summary>
    public double GetDacLinear(double outputRangeDbv, int channel) => ReadLinear(DacOffsets, outputRangeDbv, channel);

    /// <summary>
    /// Raw ADC counts -> volts. Derived from PyQa40x <c>analyzer.py:179-190</c>:
    /// <c>volts = (raw / int32.MaxValue) * calAdcLinear * 10^((inputRangeDbv - 6) / 20)</c> — the
    /// "-6 dB" term is PyQa40x's own documented correction for the ADC being differential.
    /// </summary>
    public double RawToVolts(int raw, double inputRangeDbv, int channel)
    {
        double calLinear = GetAdcLinear(inputRangeDbv, channel);
        double dbfsCorrection = Math.Pow(10.0, (inputRangeDbv - 6.0) / 20.0);
        return (raw / (double)int.MaxValue) * calLinear * dbfsCorrection;
    }

    /// <summary>
    /// Volts -> raw DAC counts. Derived from PyQa40x <c>analyzer.py:121-141</c>:
    /// <c>raw = round(volts * 10^-((outputRangeDbv + 3) / 20) * calDacLinear * int32.MaxValue)</c>.
    /// The <c>10^-((level+3)/20)</c> term is algebraically <c>1 / PeakVolts(level)</c> (the "+3 dB"
    /// is the RMS-dBV-to-peak conversion, 20·log10(√2) ≈ 3.01 dB) — the same peak-volts convention
    /// <c>Simulation.SignalSynthesizer.DbvToPeakV</c> uses, confirmed by matching baremetal's
    /// single-range constant <c>1/(8·√2)</c> (Acquisition.cs:103) against <c>1/PeakVolts(18)</c>.
    /// Clamped to the int32 range as a safety margin: the calibration factor is a small (≈±1 dB)
    /// correction, so a signal already clipped to the range's own full scale could otherwise
    /// overflow by a few counts after the multiply.
    /// </summary>
    public int VoltsToRaw(double volts, double outputRangeDbv, int channel)
    {
        double calLinear = GetDacLinear(outputRangeDbv, channel);
        double fraction = volts / PeakVolts(outputRangeDbv) * calLinear;
        double raw = Math.Round(fraction * int.MaxValue);
        return (int)Math.Clamp(raw, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// Raw DAC counts back to volts -- the exact inverse of <see cref="VoltsToRaw"/>, so a recording
    /// of the stimulus reads in volts at the output terminals rather than in counts.
    ///
    /// <para>It exists for the stream recorder (<see cref="IStimulusTap"/>) and is worth the two
    /// lines rather than recording the volts that went *in*: the clamp above is the DAC clipping, so
    /// a generator asked for more than its output range can produce looks identical to one that was
    /// not until the counts are turned back round. Nothing measures with this.</para>
    /// </summary>
    public double DacRawToVolts(int raw, double outputRangeDbv, int channel)
    {
        double calLinear = GetDacLinear(outputRangeDbv, channel);
        return raw / (double)int.MaxValue * PeakVolts(outputRangeDbv) / calLinear;
    }

    private static double PeakVolts(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);

    private double ReadLinear(IReadOnlyDictionary<double, int> offsets, double rangeDbv, int channel)
    {
        if (!offsets.TryGetValue(rangeDbv, out int leftOffset))
            throw new ArgumentOutOfRangeException(nameof(rangeDbv), rangeDbv, "No calibration record for this range.");
        if (channel is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0 (left) or 1 (right).");

        int offset = leftOffset + (channel == 1 ? RightChannelOffset : 0);
        float correctionDb = BinaryPrimitives.ReadSingleLittleEndian(_page.AsSpan(offset + 2, 4));
        return Math.Pow(10.0, correctionDb / 20.0);
    }
}
