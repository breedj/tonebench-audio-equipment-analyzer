using Qa403.Device;
using Qa403.Dsp;

namespace Qa403.Data;

/// <summary>
/// Configured (on <c>measurement_type</c>) or as-run (on <c>measurement_result</c>) parameters for
/// the 'FFTSpectrum' measurement kind. Serialised to <c>params_json</c> on both rows -- the type
/// holds what is configured and re-appliable to the live app, the result snapshots what actually
/// produced its trace.
/// </summary>
public sealed record FftSpectrumParameters(
    int FftSize,
    WindowType Window,
    AveragingMode Averaging,
    int AverageCount,
    double MeasurementLoFreqHz,
    double MeasurementHiFreqHz,
    double NotchOctaves,
    int HarmonicCount,
    double FundamentalMinSearchHz,
    int OtherPeaksCount,
    bool ProcessLeft,
    bool ProcessRight,
    double SampleRateHz,
    int ChannelCount,
    int BlockFrameCount,
    double InputRangeDbv,
    double OutputRangeDbv,
    bool? AutoRangeEnabled,
    GeneratorConfig Generator,
    // The metrics-picker's selection at save time -- purely a UI display preference, never fed into
    // spectrumPipeline.Options. Optional with a default so rows saved before this field existed
    // still deserialize (every other property here is required); null means "use the THD %/Pk dBV
    // default" -- see useMetricSelection.ts.
    IReadOnlyList<string>? SelectedMetrics = null);

/// <summary>Which normalisation produced a stored trace -- 'amplitude' | 'psd' in <c>spectrum.norm</c>.</summary>
public enum SpectrumNorm
{
    Amplitude,
    Psd,
}

/// <summary>
/// One channel's full-resolution trace, ready to encode. Only <see cref="SpectrumNorm.Amplitude"/>
/// is ever written by <see cref="MeasurementRepository"/> in practice -- PSD is exactly recoverable
/// from S1/S2/fs (<c>PSD_k = A_k^2 * S1^2 / (2 * fs * S2)</c>), so storing both would double the
/// footprint for nothing. <see cref="Norm"/> still records which one produced the trace.
/// </summary>
public sealed record StoredSpectrumInput(
    int Channel,
    WindowType Window,
    WindowSums Sums,
    double SampleRateHz,
    double BinSizeHz,
    SpectrumNorm Norm,
    double[] Dbv);

public sealed record MeasurementMetric(int Channel, string Name, double Value, string? Unit);

/// <summary>
/// One save: a <c>measurement_result</c> row plus its single <c>capture</c> row (seq_index 0 --
/// see <see cref="MeasurementRepository"/> remarks on the future time-series seam), its per-channel
/// spectra and metrics. Everything is written in one transaction.
/// </summary>
public sealed record MeasurementSaveRequest(
    long MeasurementTypeId,
    string Name,
    string? Notes,
    string ParamsJson,
    string? DeviceName,
    string? Serial,
    string? FwVer,
    bool IsSimulated,
    double SampleRateHz,
    int FftSize,
    int Channels,
    double? InRangeDbv,
    double? OutRangeDbv,
    string? GenJson,
    IReadOnlyList<StoredSpectrumInput> Spectra,
    IReadOnlyList<MeasurementMetric> Metrics);

public sealed record ProjectRow(long Id, string Name, string? Notes, DateTimeOffset CreatedUtc);

public sealed record MeasurementTypeRow(
    long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, DateTimeOffset CreatedUtc);

public sealed record MeasurementFilter(
    long? ProjectId = null,
    long? MeasurementTypeId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Skip = 0,
    int Take = 100);

public sealed record ChannelHeadline(double ThdPercent, double ThdNPercent, double SnrDb);

public sealed record MeasurementSummary(
    long ResultId,
    string ProjectName,
    string MeasurementTypeName,
    string Name,
    string? Notes,
    DateTimeOffset TimestampUtc,
    double SampleRateHz,
    int FftSize,
    int Channels,
    IReadOnlyDictionary<int, ChannelHeadline> ChannelHeadlines);

public sealed record StoredSpectrum(
    int Channel, WindowType Window, double S1, double S2, double EnbwHz, double BinSizeHz,
    SpectrumNorm Norm, double[] Dbv);

public sealed record MeasurementDetail(
    long ResultId,
    long MeasurementTypeId,
    string ProjectName,
    string MeasurementTypeName,
    string Name,
    string? Notes,
    DateTimeOffset TimestampUtc,
    string ParamsJson,
    long CaptureId,
    double SampleRateHz,
    int FftSize,
    int Channels,
    double? InRangeDbv,
    double? OutRangeDbv,
    string? GenJson,
    IReadOnlyList<int> AvailableChannels,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>> MetricsByChannel);
