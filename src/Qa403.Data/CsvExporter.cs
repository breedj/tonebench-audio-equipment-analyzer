using System.Globalization;
using System.Text;

namespace Qa403.Data;

/// <summary>
/// Renders one measurement result's full-resolution spectra as CSV -- not the decimated display
/// columns <c>MeasurementsEndpoints</c> serves the chart, the actual stored bins. Header comment
/// lines (<c>#</c>-prefixed) carry project, type, name, timestamp, fs, FFT size, per-channel
/// window/S1/S2/ENBW/norm, and the as-run parameters, so the file is interpretable standalone,
/// without the database.
/// </summary>
public static class CsvExporter
{
    public static string Export(MeasurementDetail detail, IReadOnlyList<StoredSpectrum> spectra)
    {
        if (spectra.Count == 0)
            throw new ArgumentException("At least one channel's spectrum is required.", nameof(spectra));

        int binCount = spectra[0].Dbv.Length;
        foreach (var spectrum in spectra)
        {
            if (spectrum.Dbv.Length != binCount)
                throw new ArgumentException("All channels must share the same bin count to align into one CSV.", nameof(spectra));
        }

        var sb = new StringBuilder();
        WriteHeaderComments(sb, detail, spectra);

        sb.Append("freq_hz");
        foreach (var spectrum in spectra)
            sb.Append(",ch").Append(spectrum.Channel).Append("_dbv");
        sb.Append('\n');

        double binSizeHz = spectra[0].BinSizeHz;
        for (int bin = 0; bin < binCount; bin++)
        {
            sb.Append((bin * binSizeHz).ToString("F6", CultureInfo.InvariantCulture));
            foreach (var spectrum in spectra)
                sb.Append(',').Append(spectrum.Dbv[bin].ToString("F4", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The seven quantities <c>ThdSweepChart</c> plots, in its own trace order. Each is stored as a
    /// <c>metric</c> row per channel per frequency (see <c>MeasurementSaveService.BuildSweepMetrics</c>),
    /// so a saved sweep exports as exactly what was on screen -- not a re-derivation of it.
    /// </summary>
    private static readonly (string Metric, string Column)[] SweepColumns =
    [
        ("gain_db", "magnitude_db"),
        ("thd_percent", "thd_percent"),
        ("h2_percent", "d2_percent"),
        ("h3_percent", "d3_percent"),
        ("h4_percent", "d4_percent"),
        ("h5_percent", "d5_percent"),
        ("d6plus_percent", "d6plus_percent"),
        ("noise_percent", "noise_percent"),
    ];

    /// <summary>
    /// Renders a saved THD-vs-frequency sweep as CSV: one row per frequency, one column group per
    /// channel. A sweep result has no <c>spectrum</c> rows at all (metrics only, by design -- see
    /// <see cref="SweepSaveRequest"/>), so <see cref="Export"/> cannot render one and this is the
    /// separate path rather than a branch inside it.
    ///
    /// A metric missing for a channel at one frequency is written as an empty field, not 0: a
    /// harmonic above Nyquist is genuinely absent there (<c>BuildSweepMetrics</c> stops at the
    /// highest order actually found), and a 0 would plot as a real -infinity reading.
    /// </summary>
    public static string ExportSweep(MeasurementDetail detail, IReadOnlyList<StoredSweepPoint> points)
    {
        if (points.Count == 0)
            throw new ArgumentException("A sweep with at least one point is required.", nameof(points));

        var channels = points
            .SelectMany(p => p.MetricsByChannel.Keys)
            .Distinct()
            .Order()
            .ToList();

        var sb = new StringBuilder();
        WriteComment(sb, "project", detail.ProjectName);
        WriteComment(sb, "measurement_type", detail.MeasurementTypeName);
        WriteComment(sb, "kind", detail.Kind);
        WriteComment(sb, "name", detail.Name);
        WriteComment(sb, "notes", detail.Notes ?? "");
        WriteComment(sb, "timestamp_utc", Qa403Timestamp.FormatUtc(detail.TimestampUtc));
        WriteComment(sb, "sample_rate_hz", detail.SampleRateHz.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "fft_size", detail.FftSize.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "point_count", points.Count.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "params_json", detail.ParamsJson);

        sb.Append("freq_hz");
        foreach (int channel in channels)
            foreach (var (_, column) in SweepColumns)
                sb.Append(",ch").Append(channel).Append('_').Append(column);
        sb.Append('\n');

        foreach (var point in points.OrderBy(p => p.SeqIndex))
        {
            sb.Append(point.FreqHz.ToString("F6", CultureInfo.InvariantCulture));
            foreach (int channel in channels)
            {
                point.MetricsByChannel.TryGetValue(channel, out var metrics);
                foreach (var (metric, _) in SweepColumns)
                {
                    sb.Append(',');
                    if (metrics is not null && metrics.TryGetValue(metric, out double value))
                        sb.Append(value.ToString("F6", CultureInfo.InvariantCulture));
                }
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static void WriteHeaderComments(StringBuilder sb, MeasurementDetail detail, IReadOnlyList<StoredSpectrum> spectra)
    {
        WriteComment(sb, "project", detail.ProjectName);
        WriteComment(sb, "measurement_type", detail.MeasurementTypeName);
        WriteComment(sb, "name", detail.Name);
        WriteComment(sb, "notes", detail.Notes ?? "");
        WriteComment(sb, "timestamp_utc", Qa403Timestamp.FormatUtc(detail.TimestampUtc));
        WriteComment(sb, "sample_rate_hz", detail.SampleRateHz.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "fft_size", detail.FftSize.ToString(CultureInfo.InvariantCulture));
        WriteComment(sb, "params_json", detail.ParamsJson);

        foreach (var spectrum in spectra)
        {
            string norm = spectrum.Norm == SpectrumNorm.Amplitude ? "amplitude" : "psd";
            WriteComment(
                sb,
                $"channel_{spectrum.Channel}",
                $"window={spectrum.Window} s1={spectrum.S1.ToString(CultureInfo.InvariantCulture)} "
                    + $"s2={spectrum.S2.ToString(CultureInfo.InvariantCulture)} enbw_hz={spectrum.EnbwHz.ToString(CultureInfo.InvariantCulture)} "
                    + $"norm={norm}");
        }
    }

    private static void WriteComment(StringBuilder sb, string key, string value)
    {
        // '#'-prefixed, never parsed as a delimited field -- newline stripping is enough escaping.
        sb.Append("# ").Append(key).Append(": ").Append(value.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
    }
}
