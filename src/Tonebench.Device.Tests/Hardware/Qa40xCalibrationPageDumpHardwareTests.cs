using System.Buffers.Binary;
using System.Text;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// A <b>read-only diagnostic</b>: dumps the 512-byte factory calibration page from the attached
/// analyzer and decodes every field of it. Writes one register (the page select, 0x0D) and then
/// only reads; it never touches a relay, a range, the generator or the stream.
///
/// <para>Why it exists. docs/protocol.md §5.1 derives, from QuantAsylum's own QA40x.exe, that the
/// page's float32 per-range <c>Value</c> is not a small per-unit trim but the whole gain constant,
/// nominally <b>+9.0309 dB = 20·log10(2√2)</b> on every ADC range — the factor that converts
/// "±0.5 full scale, peak" into "volts, rms". That is a prediction about real flash contents, and
/// this is the one thing that can check it. If the eight ADC floats come back near zero instead,
/// the reading in §5.1 is wrong and the 2√2 is somewhere else.</para>
///
/// <para>It also prints the header fields and verifies the two Barker words and the CRC-16, none of
/// which any other source in this repository documents — so a mismatch here is itself the finding.
/// Asserts only that the page is readable and self-consistent; every figure goes to the test
/// output for reading.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa40xCalibrationPageDumpHardwareTests(ITestOutputHelper output)
{
    /// <summary>20·log10(2·sqrt(2)) — the constant §5.1 predicts every ADC trim carries.</summary>
    private const double PredictedAdcTrimDb = 9.0309;

    private static readonly int[] AdcRanges = [0, 6, 12, 18, 24, 30, 36, 42];
    private static readonly int[] DacRanges = [-12, -2, 8, 18];

    [Fact]
    public async Task DumpFactoryCalibrationPage()
    {
        byte[] page = await ReadPageAsync(CancellationToken.None);

        var sb = new StringBuilder();
        sb.AppendLine("=== raw page 0 (512 bytes) ===");
        for (int row = 0; row < 192; row += 16)
            sb.AppendLine($"{row:D3}: {Convert.ToHexString(page.AsSpan(row, 16))}");
        sb.AppendLine($"504: {Convert.ToHexString(page.AsSpan(504, 8))}");

        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(o, 4));

        sb.AppendLine();
        sb.AppendLine("=== header ===");
        sb.AppendLine($"  FlashDataVersion  {U32(0)}");
        sb.AppendLine($"  HardwareRevision  {U32(4)}");
        sb.AppendLine($"  BuildGroup        {U32(8)}");
        sb.AppendLine($"  Barker1           0x{U32(12):X8}   expected 0xDEAD0023   {(U32(12) == 0xDEAD0023 ? "OK" : "MISMATCH")}");
        sb.AppendLine($"  AdcRanges         {U32(16)}");
        sb.AppendLine($"  DacRanges         {U32(20)}");
        sb.AppendLine($"  Barker2           0x{U32(168):X8}   expected 0xDEAD0011   {(U32(168) == 0xDEAD0011 ? "OK" : "MISMATCH")}");

        ushort crcStored = (ushort)((page[510] << 8) + page[511]);
        ushort crcCalc = Crc16(page, 510);
        sb.AppendLine($"  CRC-16            stored 0x{crcStored:X4}  computed 0x{crcCalc:X4}   {(crcStored == crcCalc ? "OK" : "MISMATCH")}");

        sb.AppendLine();
        sb.AppendLine("=== ADC records (offset 24, 6 bytes each, left then right) ===");
        sb.AppendLine("  range   off    L.Level  L.Value dB     R.Level  R.Value dB");
        var adcTrims = new List<double>();
        for (int i = 0; i < AdcRanges.Length; i++)
        {
            int off = 24 + i * 12;
            (short ll, float lv) = Record(page, off);
            (short rl, float rv) = Record(page, off + 6);
            adcTrims.Add(lv);
            adcTrims.Add(rv);
            sb.AppendLine($"  {AdcRanges[i],5}  {off,4}    {ll,7}  {lv,11:F4}     {rl,7}  {rv,11:F4}");
        }

        sb.AppendLine();
        sb.AppendLine("=== DAC records (offset 120, 6 bytes each, left then right) ===");
        sb.AppendLine("  range   off    L.Level  L.Value dB     R.Level  R.Value dB");
        for (int i = 0; i < DacRanges.Length; i++)
        {
            int off = 120 + i * 12;
            (short ll, float lv) = Record(page, off);
            (short rl, float rv) = Record(page, off + 6);
            sb.AppendLine($"  {DacRanges[i],5}  {off,4}    {ll,7}  {lv,11:F4}     {rl,7}  {rv,11:F4}");
        }

        double mean = adcTrims.Average();
        sb.AppendLine();
        sb.AppendLine("=== the §5.1 prediction ===");
        sb.AppendLine($"  predicted nominal ADC trim   {PredictedAdcTrimDb:F4} dB  (= 20*log10(2*sqrt(2)))");
        sb.AppendLine($"  measured mean of 16 ADC trims {mean,8:F4} dB");
        sb.AppendLine($"  spread  {adcTrims.Min():F4} .. {adcTrims.Max():F4} dB");
        sb.AppendLine($"  deviation of the mean from the prediction  {mean - PredictedAdcTrimDb:+0.0000;-0.0000} dB");
        sb.AppendLine(Math.Abs(mean - PredictedAdcTrimDb) < 1.0
            ? "  => CONFIRMED: the trim carries the 2*sqrt(2); it is not a small per-unit correction."
            : "  => NOT confirmed. docs/protocol.md 5.1 needs revisiting.");

        output.WriteLine(sb.ToString());

        Assert.NotEqual(0, page.Count(b => b != 0));          // the page was actually read
        Assert.Equal(crcCalc, crcStored);                      // and is self-consistent
    }

    private static (short Level, float Value) Record(byte[] page, int offset) =>
        (BinaryPrimitives.ReadInt16LittleEndian(page.AsSpan(offset, 2)),
         BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(offset + 2, 4)));

    /// <summary>CRC-16, poly 0x8005, init 0, MSB-first, no final XOR (QA40x.exe <c>j.cs</c>).</summary>
    private static ushort Crc16(byte[] data, int length)
    {
        int crc = 0;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i] << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = ((crc & 0x8000) != 0) ? ((crc << 1) ^ 0x8005) : (crc << 1);
        }

        return (ushort)(crc & 0xFFFF);
    }

    private static async Task<byte[]> ReadPageAsync(CancellationToken ct)
    {
        using var transport = new Qa40xTransport();
        transport.Open();

        await transport.WriteRegisterAsync(
            Qa40xRegisters.CalibrationPageSelect, Qa40xRegisters.CalibrationPageSelectFactoryPage, ct);

        byte[] page = new byte[Qa40xRegisters.CalibrationPageBytes];
        for (int offset = 0; offset < page.Length; offset += 4)
        {
            uint word = await transport.ReadRegisterAsync(Qa40xRegisters.CalibrationPageStream, ct);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset, 4), word);
        }

        return page;
    }
}
