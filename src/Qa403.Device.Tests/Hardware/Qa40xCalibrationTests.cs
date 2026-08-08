using System.Buffers.Binary;
using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>
/// Byte-offset parsing and the raw-counts&lt;-&gt;volts formulas, against a synthetic 512-byte
/// page built from the layout documented in docs/protocol.md §2.4 (not real hardware data).
/// </summary>
public class Qa40xCalibrationTests
{
    [Fact]
    public void GetAdcLinear_ReadsTheDbCorrectionAtTheDocumentedOffset()
    {
        // AdcOffsets[6] = 36 (PyQa40x control.py:80) -> left record at 36, right at 42.
        byte[] page = NewPage();
        WriteRecord(page, 36, correctionDb: 2.0f);
        WriteRecord(page, 42, correctionDb: -3.0f);
        var cal = Qa40xCalibration.FromPage(page);

        Assert.Equal(Math.Pow(10.0, 2.0 / 20.0), cal.GetAdcLinear(6.0, channel: 0), precision: 9);
        Assert.Equal(Math.Pow(10.0, -3.0 / 20.0), cal.GetAdcLinear(6.0, channel: 1), precision: 9);
    }

    [Fact]
    public void GetDacLinear_ReadsTheDbCorrectionAtTheDocumentedOffset()
    {
        // DacOffsets[18] = 156 (PyQa40x control.py:107) -> left record at 156, right at 162.
        byte[] page = NewPage();
        WriteRecord(page, 156, correctionDb: 1.5f);
        WriteRecord(page, 162, correctionDb: -0.5f);
        var cal = Qa40xCalibration.FromPage(page);

        Assert.Equal(Math.Pow(10.0, 1.5 / 20.0), cal.GetDacLinear(18.0, channel: 0), precision: 9);
        Assert.Equal(Math.Pow(10.0, -0.5 / 20.0), cal.GetDacLinear(18.0, channel: 1), precision: 9);
    }

    [Fact]
    public void GetAdcLinear_RejectsUnsupportedRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Qa40xCalibration.FromPage(NewPage()).GetAdcLinear(3.0, 0));

    [Fact]
    public void GetAdcLinear_RejectsInvalidChannel() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Qa40xCalibration.FromPage(NewPage()).GetAdcLinear(6.0, 2));

    [Fact]
    public void RawToVolts_MatchesPyQa40xFormula()
    {
        // volts = (raw / int32.MaxValue) * calLinear * 10^((inputRangeDbv - 6) / 20)  (analyzer.py:179-190)
        byte[] page = NewPage();
        WriteRecord(page, 36, correctionDb: 2.0f); // AdcOffsets[6] = 36
        var cal = Qa40xCalibration.FromPage(page);

        int raw = int.MaxValue / 2;
        double volts = cal.RawToVolts(raw, inputRangeDbv: 6.0, channel: 0);

        double expected = (raw / (double)int.MaxValue) * Math.Pow(10.0, 2.0 / 20.0) * Math.Pow(10.0, (6.0 - 6.0) / 20.0);
        Assert.Equal(expected, volts, precision: 9);
    }

    [Fact]
    public void VoltsToRaw_MatchesPyQa40xFormula()
    {
        // raw = round(volts / PeakVolts(outputRangeDbv) * calDacLinear * int32.MaxValue) (analyzer.py:121-141)
        byte[] page = NewPage();
        WriteRecord(page, 144, correctionDb: -1.0f); // DacOffsets[8] = 144
        var cal = Qa40xCalibration.FromPage(page);

        double peakVolts = Math.Sqrt(2.0) * Math.Pow(10.0, 8.0 / 20.0);
        double volts = peakVolts / 2.0;
        int raw = cal.VoltsToRaw(volts, outputRangeDbv: 8.0, channel: 0);

        double calLinear = Math.Pow(10.0, -1.0 / 20.0);
        int expected = (int)Math.Round(volts / peakVolts * calLinear * int.MaxValue);
        Assert.Equal(expected, raw);
    }

    [Fact]
    public void VoltsToRaw_ClampsToInt32RangeRatherThanOverflowing()
    {
        byte[] page = NewPage();
        WriteRecord(page, 156, correctionDb: 6.0f); // large positive correction can push the product past full scale
        var cal = Qa40xCalibration.FromPage(page);

        double peakVolts = Math.Sqrt(2.0) * Math.Pow(10.0, 18.0 / 20.0);
        int raw = cal.VoltsToRaw(peakVolts, outputRangeDbv: 18.0, channel: 0);

        Assert.Equal(int.MaxValue, raw);
    }

    private static byte[] NewPage() => new byte[Qa40xRegisters.CalibrationPageBytes];

    /// <summary>Writes one 6-byte record: int16 level (unused, 0) + float32 dB correction, both little-endian (PyQa40x control.py:88-89).</summary>
    private static void WriteRecord(byte[] page, int offset, float correctionDb)
    {
        BinaryPrimitives.WriteInt16LittleEndian(page.AsSpan(offset), 0);
        BinaryPrimitives.WriteSingleLittleEndian(page.AsSpan(offset + 2), correctionDb);
    }
}
