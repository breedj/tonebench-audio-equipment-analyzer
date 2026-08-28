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
    IReadOnlyList<string>? SelectedMetrics = null,
    // How many whole periods of the fundamental the oscilloscope chart shows per frame -- like
    // SelectedMetrics above, purely a UI display preference that never reaches the pipeline (the
    // server always sends the same 2048-sample scope frame; the client just draws a slice of it).
    // Optional with a default so rows saved before this field existed still deserialize; null means
    // "use the 4-period default" -- see useScopeDisplay.ts.
    int? ScopePeriods = null,
    // The spectrum chart's own axis bounds and line-style toggles -- same rationale as
    // SelectedMetrics/ScopePeriods above: purely a UI display preference, never fed into
    // spectrumPipeline.Options. Null means "use useChartDisplay.ts's own defaults" -- covers rows
    // saved before this field existed. ChannelVisibility (L/R) is deliberately not here: it always
    // mirrors ProcessLeft/ProcessRight (see ChartDisplayPanel.vue), so persisting it separately
    // could only ever disagree with those.
    double? ChartYTopDbv = null,
    double? ChartYBottomDbv = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    bool? ChartShowMinMax = null,
    bool? ChartShowPeakHold = null);

/// <summary>
/// Configured (on <c>measurement_type</c>) or as-run (on <c>measurement_result</c>) parameters for
/// the 'ThdVsFrequency' measurement kind -- the sweep's own settings, deliberately a separate
/// record from <see cref="FftSpectrumParameters"/> rather than more optional properties on it: a
/// sweep has no single generator config, no averaging mode the user picks, and no display
/// bandwidth, and half of FftSpectrumParameters would have to be nullable to pretend otherwise.
/// <see cref="MeasurementTypeRow.Kind"/> selects which one <c>params_json</c> holds.
/// </summary>
public sealed record ThdVsFrequencyParameters(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string LevelMode,
    double LevelValue,
    string LevelUnit,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool MeasureNoiseFloor,
    /// <summary>The generator amplitude the run actually used -- solved by probing for the output-referred modes, so it is only known after the fact.</summary>
    double GeneratorAmpDbv = 0.0,
    // The sweep chart's own axis bounds, per-trace visibility and line-style toggles -- purely a UI
    // display preference, never fed into the sweep runner. Optional with defaults so rows saved
    // before this field existed still deserialize; null means "use App.vue's own sweep* ref
    // defaults" -- see SweepDisplayPanel.vue.
    string? ChartUnit = null,
    bool? ChartShowNoiseFloor = null,
    bool? ChartShowMagnitude = null,
    bool? ChartShowThd = null,
    bool? ChartShowD2 = null,
    bool? ChartShowD3 = null,
    bool? ChartShowD4 = null,
    bool? ChartShowD5 = null,
    bool? ChartShowD6Plus = null,
    bool? ChartShowLeft = null,
    bool? ChartShowRight = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    double? ChartYMaxPercent = null,
    double? ChartYMinPercent = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null);

/// <summary>
/// One channel's small decimated FFT or oscilloscope trace at one sweep point -- a
/// <c>capture_preview</c> row. Capped at a couple hundred points; see that table's migration for
/// why this isn't the same quantized encoding <see cref="StoredSpectrumInput"/> uses.
/// </summary>
public sealed record CapturePreviewInput(int Channel, string Kind, float[] X, float[] Min, float[] Max);

/// <summary>The <c>capture_preview.kind</c> values -- mirrors Qa403.App.Streaming.SpectrumSnapshot's PreviewFft/PreviewScope.</summary>
public static class CapturePreviewKind
{
    public const string Fft = "fft";
    public const string Scope = "scope";
}

/// <summary>
/// One sweep point as a <c>capture</c> row: its own frequency, its <c>seq_index</c> position in
/// the sweep, the per-channel metrics measured there, and (when the sweep captured them) each
/// channel's small preview traces. No full-resolution spectrum -- see <see cref="SweepSaveRequest"/>.
/// </summary>
public sealed record SweepCaptureInput(
    int SeqIndex, double FreqHz, IReadOnlyList<MeasurementMetric> Metrics,
    IReadOnlyList<CapturePreviewInput>? Previews = null);

/// <summary>
/// One saved sweep: a <c>measurement_result</c> row plus N <c>capture</c> rows, one per frequency,
/// ordered by <c>seq_index</c> -- exactly the shape migration 003 kept <c>seq_index</c> for.
///
/// Metrics only, no <c>spectrum</c> rows. A 31-point stereo sweep at a 64k FFT would be ~60 MB of
/// traces for a chart that only ever plots eight scalars per point, and the sweep's whole value is
/// the scalars. A "store spectra too" option would hang off here.
/// </summary>
public sealed record SweepSaveRequest(
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
    IReadOnlyList<SweepCaptureInput> Captures);

/// <summary>One channel's decimated trace read back out of a saved sweep point. See <see cref="CapturePreviewInput"/>.</summary>
public sealed record StoredPreviewSeries(float[] X, float[] Min, float[] Max);

/// <summary>
/// One point read back out of a saved sweep: its frequency, its metrics, and its preview traces
/// (present only for a sweep that captured them -- every sweep does, in practice), all keyed by
/// channel.
/// </summary>
public sealed record StoredSweepPoint(
    int SeqIndex, double FreqHz,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>> MetricsByChannel,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, StoredPreviewSeries>>? PreviewsByChannel = null);

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
    /// <summary>The owning <c>measurement_type.kind</c> -- 'FFTSpectrum' | 'ThdVsFrequency'. Tells a
    /// consumer whether this result has spectra to read at all, or N metric-only sweep captures.</summary>
    string Kind,
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
