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

    [Fact]
    public void ExportSweep_HasOneRowPerFrequencyAndTheChartsOwnColumns()
    {
        var detail = BuildSweepDetail();

        string csv = CsvExporter.ExportSweep(detail, [SweepPoint(0, 100.0), SweepPoint(1, 1000.0)]);
        var dataLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();

        Assert.Equal(
            "freq_hz,ch0_magnitude_db,ch0_thd_percent,ch0_d2_percent,ch0_d3_percent,ch0_d4_percent,"
                + "ch0_d5_percent,ch0_d6plus_percent,ch0_noise_percent",
            dataLines[0]);
        Assert.Equal(2, dataLines.Length - 1);
        Assert.StartsWith("100.000000,", dataLines[1]);
        Assert.StartsWith("1000.000000,", dataLines[2]);
    }

    [Fact]
    public void ExportSweep_BothChannels_GivesEachItsOwnColumnGroup()
    {
        var detail = BuildSweepDetail();
        var stereo = new StoredSweepPoint(0, 1000.0, new Dictionary<int, IReadOnlyDictionary<string, double>>
        {
            [0] = SweepMetrics(-1.0),
            [1] = SweepMetrics(-2.0),
        });

        string csv = CsvExporter.ExportSweep(detail, [stereo]);
        var dataLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();

        Assert.EndsWith("ch1_d6plus_percent,ch1_noise_percent", dataLines[0]);
        Assert.Equal(17, dataLines[1].Split(',').Length); // freq + 8 columns x 2 channels
        Assert.Equal("-1.000000", dataLines[1].Split(',')[1]);
        Assert.Equal("-2.000000", dataLines[1].Split(',')[9]);
    }

    /// <summary>
    /// A harmonic above Nyquist is genuinely not measured at that frequency, so its field is empty
    /// rather than 0 -- a 0 would read back as a real -infinity dBc point on the chart.
    /// </summary>
    [Fact]
    public void ExportSweep_MetricMissingAtOneFrequency_LeavesTheFieldEmpty()
    {
        var detail = BuildSweepDetail();
        var withoutD5 = SweepMetrics(-1.0).Where(kv => kv.Key != "h5_percent").ToDictionary(kv => kv.Key, kv => kv.Value);
        var point = new StoredSweepPoint(0, 19000.0, new Dictionary<int, IReadOnlyDictionary<string, double>>
        {
            [0] = withoutD5,
        });

        string csv = CsvExporter.ExportSweep(detail, [point]);
        string row = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(l => !l.StartsWith('#'));

        Assert.Equal("", row.Split(',')[6]); // freq, magnitude, thd, d2, d3, d4, d5
    }

    [Fact]
    public void ExportSweep_NoPoints_Throws()
    {
        Assert.Throws<ArgumentException>(() => CsvExporter.ExportSweep(BuildSweepDetail(), []));
    }

    private static StoredSweepPoint SweepPoint(int seqIndex, double freqHz) =>
        new(seqIndex, freqHz, new Dictionary<int, IReadOnlyDictionary<string, double>> { [0] = SweepMetrics(-1.0) });

    /// <summary>Metric names as MeasurementSaveService.BuildSweepMetrics writes them.</summary>
    private static Dictionary<string, double> SweepMetrics(double gainDb) => new()
    {
        ["gain_db"] = gainDb,
        ["thd_percent"] = 0.01,
        ["h2_percent"] = 0.008,
        ["h3_percent"] = 0.004,
        ["h4_percent"] = 0.002,
        ["h5_percent"] = 0.001,
        ["d6plus_percent"] = 0.0005,
        ["noise_percent"] = 0.0001,
    };

    private static MeasurementDetail BuildSweepDetail() => BuildDetail() with
    {
        Kind = "ThdVsFrequency",
        MeasurementTypeName = "THD vs frequency",
        ParamsJson = """{"startFreqHz":20}""",
    };

    private static MeasurementDetail BuildDetail() => new(
        ResultId: 1, MeasurementTypeId: 1, ProjectName: "GM70", MeasurementTypeName: "1 kHz THD",
        Kind: "FFTSpectrum",
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
