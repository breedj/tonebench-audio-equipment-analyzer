using Qa403.Dsp;

namespace Qa403.Data.Tests;

public class CsvExporterTests
{
    [Fact]
    public void Export_HasHeaderAndOneRowPerBin()
    {
        var detail = BuildDetail();
        var spectrum = BuildSpectrum(channel: 0, binCount: 5);

        string csv = CsvExporter.Export(detail, [spectrum]);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var commentLines = lines.Where(l => l.StartsWith('#')).ToArray();
        Assert.NotEmpty(commentLines);

        var dataLines = lines.Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal("freq_hz,ch0_dbv", dataLines[0]);
        Assert.Equal(5, dataLines.Length - 1); // header row + one row per bin
        Assert.Equal("0.000000,-100.0000", dataLines[1]);
    }

    [Fact]
    public void Export_HeaderCarriesProjectTypeAndParameters()
    {
        var detail = BuildDetail();
        var spectrum = BuildSpectrum(channel: 0, binCount: 3);

        string csv = CsvExporter.Export(detail, [spectrum]);

        Assert.Contains("# project: GM70", csv);
        Assert.Contains("# measurement_type: 1 kHz THD", csv);
        Assert.Contains("# name: before mod", csv);
        Assert.Contains("# params_json: {\"fftSize\":65536}", csv);
        Assert.Contains("# channel_0: window=Hann", csv);
        Assert.Contains("norm=amplitude", csv);
    }

    [Fact]
    public void Export_TwoChannels_OneColumnEach()
    {
        var detail = BuildDetail();
        var left = BuildSpectrum(channel: 0, binCount: 4);
        var right = BuildSpectrum(channel: 1, binCount: 4);

        string csv = CsvExporter.Export(detail, [left, right]);
        var header = csv.Split('\n').First(l => !l.StartsWith('#'));

        Assert.Equal("freq_hz,ch0_dbv,ch1_dbv", header);
    }

    [Fact]
    public void Export_MismatchedBinCounts_Throws()
    {
        var detail = BuildDetail();
        var left = BuildSpectrum(channel: 0, binCount: 4);
        var right = BuildSpectrum(channel: 1, binCount: 5);

        Assert.Throws<ArgumentException>(() => CsvExporter.Export(detail, [left, right]));
    }

    private static MeasurementDetail BuildDetail() => new(
        ResultId: 1, MeasurementTypeId: 1, ProjectName: "GM70", MeasurementTypeName: "1 kHz THD",
        Name: "before mod", Notes: "smoke test", TimestampUtc: new DateTimeOffset(2026, 8, 9, 8, 31, 16, TimeSpan.Zero),
        ParamsJson: """{"fftSize":65536}""", CaptureId: 1, SampleRateHz: 48000, FftSize: 65536, Channels: 1,
        InRangeDbv: 0, OutRangeDbv: -2, GenJson: null,
        AvailableChannels: [0], MetricsByChannel: new Dictionary<int, IReadOnlyDictionary<string, double>>());

    private static StoredSpectrum BuildSpectrum(int channel, int binCount)
    {
        var dbv = new double[binCount];
        Array.Fill(dbv, -100.0);
        return new StoredSpectrum(
            channel, WindowType.Hann, S1: 32768.0, S2: 24576.0, EnbwHz: 1.098,
            BinSizeHz: 48000.0 / 65536.0, SpectrumNorm.Amplitude, dbv);
    }
}
