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
