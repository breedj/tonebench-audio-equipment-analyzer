using System.Globalization;
using Tonebench.Dsp;

namespace Tonebench.Data.Tests;

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

    /// <summary>
    /// Every sweep writes the level it ran at on every point, so a frequency sweep's rows all carry
    /// one and the same generator_volt. That must not make it a level-stepped sweep: keyed on the
    /// metric's presence, every frequency sweep saved since the amplitude axis existed exported
    /// with a constant 0.1 V as its leading column and no frequency column at all.
    /// </summary>
    [Fact]
    public void ExportSweep_FrequencySweepWithItsLevelOnEveryPoint_StillLeadsWithFrequency()
    {
        var detail = BuildSweepDetail();
        StoredSweepPoint At(int seq, double hz)
        {
            var metrics = SweepMetrics(-1.0);
            metrics["generator_dbv"] = -20.0;
            metrics["generator_volt"] = 0.1;
            return new StoredSweepPoint(seq, hz, new Dictionary<int, IReadOnlyDictionary<string, double>> { [0] = metrics });
        }

        string csv = CsvExporter.ExportSweep(detail, [At(0, 100.0), At(1, 1000.0)]);
        var dataLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();

        Assert.StartsWith("freq_hz,ch0_magnitude_db,", dataLines[0]);
        Assert.StartsWith("100.000000,", dataLines[1]);
        Assert.StartsWith("1000.000000,", dataLines[2]);
    }

    /// <summary>
    /// A frequency-normalized sweep carries a second set of figures per point, appended as their
    /// own columns after the conventional ones -- and only then, so a conventional sweep's file
    /// keeps the header it always had. A point near the top of the extended grid has no normalized
    /// figures at all (nothing above it was measured), and its fields are empty, not 0.
    /// </summary>
    [Fact]
    public void ExportSweep_FrequencyNormalizedSweep_AppendsTheNormalizedColumns()
    {
        var detail = BuildSweepDetail();
        var normalized = SweepMetrics(-1.0);
        normalized["nthd_percent"] = 0.02;
        normalized["nh2_percent"] = 0.016;
        normalized["nh3_percent"] = 0.008;
        normalized["nd6plus_percent"] = 0.001;
        var referenced = new StoredSweepPoint(0, 1000.0, new Dictionary<int, IReadOnlyDictionary<string, double>> { [0] = normalized });
        var topOfGrid = new StoredSweepPoint(1, 80000.0, new Dictionary<int, IReadOnlyDictionary<string, double>> { [0] = SweepMetrics(-3.0) });

        string csv = CsvExporter.ExportSweep(detail, [referenced, topOfGrid]);
        var dataLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();

        Assert.EndsWith(
            "ch0_noise_percent,ch0_nthd_percent,ch0_nd2_percent,ch0_nd3_percent,ch0_nd4_percent,ch0_nd5_percent,ch0_nd6plus_percent",
            dataLines[0]);

        var first = dataLines[1].Split(',');
        Assert.Equal(15, first.Length);
        Assert.Equal("0.020000", first[9]);
        Assert.Equal("0.016000", first[10]);
        Assert.Equal("0.008000", first[11]);
        Assert.Equal("", first[12]);   // nd4: no reference at this point
        Assert.Equal("0.001000", first[14]);

        var top = dataLines[2].Split(',');
        Assert.Equal(15, top.Length);
        Assert.All(top[9..], field => Assert.Equal("", field));
    }

    /// <summary>
    /// An amplitude sweep's rows are told apart by the level, not the frequency -- every point
    /// shares one tone, so a freq_hz column would repeat the same number down the file and identify
    /// nothing. The frequency moves into a header comment instead, where it is stated once.
    /// </summary>
    [Fact]
    public void ExportSweep_LevelSteppedSweep_LeadsWithTheGeneratorVoltage()
    {
        var detail = BuildAmplitudeSweepDetail();

        string csv = CsvExporter.ExportSweep(detail, [AmplitudePoint(0, -40.0), AmplitudePoint(1, -20.0)]);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToArray();
        var dataLines = lines.Where(l => !l.StartsWith('#')).ToArray();

        Assert.Contains(lines, l => l.StartsWith("# freq_hz: 1000.000000"));

        Assert.Equal(
            "generator_volt,ch0_output_dbv,ch0_power_w,ch0_magnitude_db,ch0_thd_percent,ch0_d2_percent,"
                + "ch0_d3_percent,ch0_d4_percent,ch0_d5_percent,ch0_d6plus_percent,ch0_noise_percent",
            dataLines[0]);

        // 0.01 V and 0.1 V -- the levels, ascending, not the repeated 1 kHz.
        Assert.StartsWith("0.010000,", dataLines[1]);
        Assert.StartsWith("0.100000,", dataLines[2]);
    }

    [Fact]
    public void ExportSweep_NoPoints_Throws()
    {
        Assert.Throws<ArgumentException>(() => CsvExporter.ExportSweep(BuildSweepDetail(), []));
    }


    /// <summary>
    /// An IMD sweep is level-stepped like the THD amplitude sweep (its leading column is the
    /// sine-equivalent generator level) but its columns are its own -- the two output conventions,
    /// the IMD total and the orders -- selected by the <c>imd_percent</c> row no other sweep writes.
    /// </summary>
    [Fact]
    public void ExportSweep_ImdSweep_UsesTheImdColumnsAndLeadsWithTheSineEquivalentLevel()
    {
        var detail = BuildSweepDetail();
        StoredSweepPoint At(int seq, double generatorVolt)
        {
            var metrics = new Dictionary<string, double>
            {
                ["fundamental_hz"] = 60.0,
                ["fundamental_dbv"] = 20.0 * Math.Log10(generatorVolt),
                ["gain_db"] = 0.0,
                ["power_watt"] = generatorVolt * generatorVolt / 8.0,
                ["noise_percent"] = 0.001,
                ["generator_dbv"] = 20.0 * Math.Log10(generatorVolt),
                ["generator_volt"] = generatorVolt,
                ["imd_percent"] = 0.02,
                ["imd_tone_low_hz"] = 58.59,
                ["imd_tone_high_hz"] = 7001.95,
                ["imd_tone_low_dbv"] = -1.94,
                ["imd_tone_high_dbv"] = -13.98,
                ["imd_sine_equivalent_dbv"] = 20.0 * Math.Log10(generatorVolt),
                ["imd_combined_rms_dbv"] = 20.0 * Math.Log10(generatorVolt) - 2.28,
                ["im2_percent"] = 0.015,
                ["im3_percent"] = 0.013,
            };
            return new StoredSweepPoint(seq, 60.0, new Dictionary<int, IReadOnlyDictionary<string, double>> { [0] = metrics });
        }

        string csv = CsvExporter.ExportSweep(detail, [At(0, 0.1), At(1, 0.2)]);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLines = lines.Where(l => !l.StartsWith('#')).ToArray();

        Assert.Contains("# tone_low_hz: 58.590000", csv);
        Assert.Contains("# tone_high_hz: 7001.950000", csv);
        Assert.Equal(
            "generator_volt,ch0_output_sine_eq_dbv,ch0_output_rms_dbv,ch0_power_w,ch0_magnitude_db,ch0_imd_percent,"
                + "ch0_im2_percent,ch0_im3_percent,ch0_im4_percent,ch0_im5_percent,ch0_noise_percent",
            dataLines[0]);
        Assert.StartsWith("0.100000,", dataLines[1]);
        Assert.StartsWith("0.200000,", dataLines[2]);
        var fields = dataLines[1].Split(',');
        Assert.Equal("0.020000", fields[5]);
        Assert.Equal("", fields[8]); // im4 absent, not zero
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

    /// <summary>One level-swept point: the frequency-sweep metrics plus the two that mark the axis.</summary>
    private static StoredSweepPoint AmplitudePoint(int seqIndex, double generatorDbv)
    {
        double volt = Math.Pow(10.0, generatorDbv / 20.0);
        var metrics = SweepMetrics(generatorDbv).ToDictionary(kv => kv.Key, kv => kv.Value);
        metrics["generator_dbv"] = generatorDbv;
        metrics["generator_volt"] = volt;
        metrics["fundamental_dbv"] = generatorDbv;
        metrics["power_watt"] = volt * volt / 8.0;

        return new StoredSweepPoint(seqIndex, 1000.0, new Dictionary<int, IReadOnlyDictionary<string, double>>
        {
            [0] = metrics,
        });
    }

    private static MeasurementDetail BuildAmplitudeSweepDetail() => BuildDetail() with
    {
        Kind = "ThdVsAmplitude",
        MeasurementTypeName = "THD vs amplitude",
        ParamsJson = """{"fixedFreqHz":1000}""",
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


    /// <summary>
    /// An impedance export is in <b>ohms</b>, not in the dB the rows physically store. Nobody wants a
    /// loudspeaker's impedance in decibels, and a column headed <c>_db</c> holding dB relative to one
    /// ohm would be true and useless.
    /// </summary>
    [Fact]
    public void ExportImpedance_WritesOhmsRatherThanTheDbTheRowsStore()
    {
        // 18.0618 dB relative to 1 ohm is exactly 8 ohms.
        string csv = CsvExporter.ExportImpedance(BuildImpedanceDetail(), [BuildImpedanceTrace()]);
        string[] lines = csv.Split('\n');

        string header = lines.Single(l => l.StartsWith("freq_hz"));
        Assert.Contains("z_min_ohms", header);
        Assert.Contains("z_max_ohms", header);
        Assert.Contains("z_phase_deg", header);
        Assert.Contains("z_coherence", header);
        Assert.DoesNotContain("_db", header);

        // Parsed rather than matched as a substring: the magnitudes are stored as float32, so 8 ohms
        // makes the round trip as 7.999998 and an exact-text assertion would be testing float
        // precision rather than the export.
        Assert.Equal(8.0, Field(lines, "100.", header, "z_min_ohms"), 5);
        Assert.Equal(8.0, Field(lines, "100.", header, "z_max_ohms"), 5);
        Assert.Equal(0.0, Field(lines, "100.", header, "z_phase_deg"), 6);
    }

    /// <summary>
    /// R and X alongside, from the same magnitude and phase. Not extra information — but it is the
    /// form the numbers get used in, and recomputing them in a spreadsheet is where a degrees/radians
    /// slip happens.
    /// </summary>
    [Fact]
    public void ExportImpedance_AlsoWritesResistanceAndReactance()
    {
        string csv = CsvExporter.ExportImpedance(BuildImpedanceDetail(), [BuildImpedanceTrace()]);
        string[] lines = csv.Split('\n');

        string header = lines.Single(l => l.StartsWith("freq_hz"));
        Assert.Contains("z_resistance_ohms", header);
        Assert.Contains("z_reactance_ohms", header);

        // 8 ohms at +45 degrees is 8/sqrt(2) either way.
        double expected = 8.0 / Math.Sqrt(2.0);
        Assert.Equal(expected, Field(lines, "200.", header, "z_resistance_ohms"), 5);
        Assert.Equal(expected, Field(lines, "200.", header, "z_reactance_ohms"), 5);
    }

    /// <summary>
    /// An output-impedance result goes through the impedance exporter: its <c>zout</c> row is an
    /// impedance curve (ohms, R/X columns) and its two gain rows are plain dB, like a calibration
    /// standard's — so the file says what Zout was derived from.
    /// </summary>
    [Fact]
    public void ExportImpedance_WithZoutKind_EmitsOhmsColumns_AndTheGainRowsInDb()
    {
        var detail = BuildDetail() with { Kind = "OutputImpedance", MeasurementTypeName = "Damping factor", ParamsJson = """{"nominalOhms":8}""" };
        // -30 dB relative to 1 ohm is 0.0316 ohm.
        var zout = BuildImpedanceTrace() with { Kind = ResponseTraceKind.OutputImpedance, MagnitudeMinDb = [-30.0f, -30.0f], MagnitudeMaxDb = [-30.0f, -30.0f], PeakDb = -30.0 };
        var loaded = BuildOpenCalTrace() with { Kind = ResponseTraceKind.GainLoad1, MagnitudeMinDb = [28.0f, 28.0f], MagnitudeMaxDb = [28.0f, 28.0f] };
        var open = BuildOpenCalTrace() with { Kind = ResponseTraceKind.GainLoad2, MagnitudeMinDb = [28.034f, 28.034f], MagnitudeMaxDb = [28.034f, 28.034f] };

        string csv = CsvExporter.ExportImpedance(detail, [zout, loaded, open]);
        string[] lines = csv.Split('\n');
        string header = lines.Single(l => l.StartsWith("freq_hz"));

        Assert.Contains("zout_min_ohms", header);
        Assert.Contains("zout_resistance_ohms", header);
        Assert.Contains("gain_load1_min_db", header);
        Assert.Contains("gain_load2_max_db", header);
        Assert.DoesNotContain("gain_load1_min_ohms", header);
        Assert.Equal(0.0316, Field(lines, "100.", header, "zout_max_ohms"), 3);
        Assert.Equal(28.034, Field(lines, "100.", header, "gain_load2_max_db"), 3);
        Assert.Contains("# zout_peak_ohms: 0.031623", csv);
        Assert.Contains("# kind: OutputImpedance", csv);
    }

    /// <summary>
    /// The peak is a resonance, so it is a comment in ohms rather than the dB a response's peak is.
    /// </summary>
    [Fact]
    public void ExportImpedance_HeaderCarriesThePeakInOhmsAndTheParameters()
    {
        string csv = CsvExporter.ExportImpedance(BuildImpedanceDetail(), [BuildImpedanceTrace()]);

        const string prefix = "# z_peak_ohms: ";
        string peak = csv.Split('\n').Single(l => l.StartsWith(prefix))[prefix.Length..];
        Assert.Equal(8.0, double.Parse(peak, CultureInfo.InvariantCulture), 5);

        Assert.Contains("# z_peak_hz: 200.0000", csv);
        Assert.Contains("# kind: Impedance", csv);

        // Without the reference resistor and the calibration none of these numbers can be reproduced.
        Assert.Contains("\"referenceOhms\":100", csv);
    }

    /// <summary>
    /// A calibration standard is a ratio rather than an impedance, so its columns stay in dB and it
    /// gets no R and X. The kind prefix is what keeps the two readable in one table.
    /// </summary>
    [Fact]
    public void ExportImpedance_ACalibrationStandardKeepsItsDbColumns()
    {
        string csv = CsvExporter.ExportImpedance(
            BuildImpedanceDetail(), [BuildImpedanceTrace(), BuildOpenCalTrace()]);

        string header = csv.Split('\n').Single(l => l.StartsWith("freq_hz"));

        Assert.Contains("z_min_ohms", header);
        Assert.Contains("z_open_min_db", header);
        Assert.DoesNotContain("z_open_min_ohms", header);
        Assert.DoesNotContain("z_open_resistance_ohms", header);
    }

    [Fact]
    public void ExportImpedance_NoTraces_Throws() =>
        Assert.Throws<ArgumentException>(() => CsvExporter.ExportImpedance(BuildImpedanceDetail(), []));

    /// <summary>
    /// A crosstalk result is response rows under its own kinds and goes through the response
    /// renderer unchanged: the magnitude columns are the crosstalk in dB, one curve per direction,
    /// and the (meaningless, zero) bandwidth comments are carried like any response trace's.
    /// </summary>
    [Fact]
    public void ExportResponse_WithCrosstalkKinds_EmitsXtalkColumns()
    {
        var detail = BuildDetail() with { Kind = "Crosstalk", MeasurementTypeName = "Crosstalk", ParamsJson = """{"direction":"Both"}""" };
        string csv = CsvExporter.ExportResponse(detail, [BuildCrosstalkTrace(ResponseTraceKind.CrosstalkLeftToRight, -70f), BuildCrosstalkTrace(ResponseTraceKind.CrosstalkRightToLeft, -80f)]);
        string[] lines = csv.Split('\n');

        string header = lines.Single(l => l.StartsWith("freq_hz"));
        Assert.Equal(
            "freq_hz,xtalk_l2r_min_db,xtalk_l2r_max_db,xtalk_l2r_phase_deg,xtalk_l2r_coherence,xtalk_r2l_min_db,xtalk_r2l_max_db,xtalk_r2l_phase_deg,xtalk_r2l_coherence",
            header);
        Assert.Equal(-70.0, Field(lines, "100.", header, "xtalk_l2r_max_db"), 3);
        Assert.Equal(-80.0, Field(lines, "100.", header, "xtalk_r2l_max_db"), 3);
        Assert.Contains("# xtalk_l2r_peak_db: -70.0000", csv);
        Assert.Contains("# kind: Crosstalk", csv);
    }

    /// <summary>
    /// The delays are written in microseconds, per trace, only when the row carries them: a row
    /// saved before migration 012 has none and gets no columns rather than empty ones.
    /// </summary>
    [Fact]
    public void ExportResponse_WritesDelayColumnsInMicroseconds()
    {
        var gain = BuildCrosstalkTrace(ResponseTraceKind.Gain, 0f) with
        {
            GroupDelayS = [2.083e-3f, 12.5e-6f],
            PhaseDelayS = [2.100e-3f, -3.0e-6f],
        };

        string csv = CsvExporter.ExportResponse(BuildDetail() with { Kind = "FrequencyResponse" }, [gain]);
        string[] lines = csv.Split('\n');
        string header = lines.Single(l => l.StartsWith("freq_hz"));

        Assert.Equal("freq_hz,gain_min_db,gain_max_db,gain_phase_deg,gain_coherence,gain_group_delay_us,gain_phase_delay_us", header);
        Assert.Equal(2083.0, Field(lines, "100.", header, "gain_group_delay_us"), 2);
        Assert.Equal(12.5, Field(lines, "200.", header, "gain_group_delay_us"), 2);
        Assert.Equal(-3.0, Field(lines, "200.", header, "gain_phase_delay_us"), 2);
    }

    [Fact]
    public void ExportResponse_OmitsDelayColumnsForARowThatNeverRecordedThem()
    {
        var left = BuildCrosstalkTrace(ResponseTraceKind.Left, -20f);
        var gain = BuildCrosstalkTrace(ResponseTraceKind.Gain, 0f) with { GroupDelayS = [1e-6f, 1e-6f], PhaseDelayS = [1e-6f, 1e-6f] };

        string csv = CsvExporter.ExportResponse(BuildDetail() with { Kind = "FrequencyResponse" }, [left, gain]);
        string header = csv.Split('\n').Single(l => l.StartsWith("freq_hz"));

        Assert.Equal(
            "freq_hz,left_min_db,left_max_db,left_phase_deg,left_coherence,gain_min_db,gain_max_db,gain_phase_deg,gain_coherence,gain_group_delay_us,gain_phase_delay_us",
            header);
        Assert.Equal(1.0, Field(csv.Split('\n'), "100.", header, "gain_group_delay_us"), 3);
    }

    private static StoredResponseTrace BuildCrosstalkTrace(string kind, float db) =>
        new(kind, StartHz: 100.0, EndHz: 200.0,
            PeakDb: db, PeakHz: 100.0,
            new ResponseBandwidth(0, 0, false, false), new ResponseBandwidth(0, 0, false, false),
            FreqHz: [100.0f, 200.0f],
            MagnitudeMinDb: [db, db],
            MagnitudeMaxDb: [db, db],
            PhaseDeg: [0.0f, 0.0f],
            Coherence: [1.0f, 1.0f]);

    /// <summary>One named column of the row starting with <paramref name="rowPrefix"/>, by header position.</summary>
    private static double Field(string[] lines, string rowPrefix, string header, string column)
    {
        int index = Array.IndexOf(header.Split(','), column);
        Assert.True(index >= 0, $"no column named {column} in: {header}");

        string row = lines.First(l => l.StartsWith(rowPrefix));
        return double.Parse(row.Split(',')[index], CultureInfo.InvariantCulture);
    }

    private static MeasurementDetail BuildImpedanceDetail() => BuildDetail() with
    {
        Kind = "Impedance",
        MeasurementTypeName = "Woofer impedance",
        ParamsJson = """{"referenceOhms":100,"openCal":null}""",
    };

    /// <summary>Two columns: 8 ohms purely resistive, then 8 ohms at +45 degrees.</summary>
    private static StoredResponseTrace BuildImpedanceTrace()
    {
        const float eightOhmsDb = 18.061799f;
        return new StoredResponseTrace(
            ResponseTraceKind.Impedance, StartHz: 100.0, EndHz: 200.0,
            PeakDb: 18.061799, PeakHz: 200.0,
            new ResponseBandwidth(0, 0, false, false), new ResponseBandwidth(0, 0, false, false),
            FreqHz: [100.0f, 200.0f],
            MagnitudeMinDb: [eightOhmsDb, eightOhmsDb],
            MagnitudeMaxDb: [eightOhmsDb, eightOhmsDb],
            PhaseDeg: [0.0f, 45.0f],
            Coherence: [1.0f, 1.0f]);
    }

    private static StoredResponseTrace BuildOpenCalTrace() =>
        new(ResponseTraceKind.ImpedanceOpen, StartHz: 100.0, EndHz: 200.0,
            PeakDb: 0.0, PeakHz: 100.0,
            new ResponseBandwidth(0, 0, false, false), new ResponseBandwidth(0, 0, false, false),
            FreqHz: [100.0f, 200.0f],
            MagnitudeMinDb: [0.0f, 0.0f],
            MagnitudeMaxDb: [0.0f, 0.0f],
            PhaseDeg: [0.0f, 0.1f],
            Coherence: [1.0f, 1.0f]);

    private static StoredSpectrum BuildSpectrum(int channel, int binCount)
    {
        var dbv = new double[binCount];
        Array.Fill(dbv, -100.0);
        return new StoredSpectrum(
            channel, WindowType.Hann, S1: 32768.0, S2: 24576.0, EnbwHz: 1.098,
            BinSizeHz: 48000.0 / 65536.0, SpectrumNorm.Amplitude, dbv);
    }
}
