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
    bool? ChartShowPeakHold = null,
    // See ChartDisplayPanel.vue's "Autofit when measurement done" checkbox. A new type is created
    // with it on (MeasurementSaveService.BuildDefaultParametersJsonForNewType); null on a type saved
    // before this existed, same as the toggles above, and treated as false so nothing changes for it.
    bool? ChartAutofitOnDone = null,
    // Which of the FFT screen's secondary panels (waterfall, oscilloscope, harmonics, other peaks)
    // are drawn -- another display-only preference, on the same terms as the four above. Null means
    // "shown", which is what a row saved before these existed has to mean: the panels were always
    // drawn then, so a load must not make them disappear.
    bool? ChartShowWaterfall = null,
    bool? ChartShowOscilloscope = null,
    bool? ChartShowHarmonics = null,
    bool? ChartShowOtherPeaks = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null,
    /// <summary>
    /// The two-tone intermodulation analysis in force, if any -- see
    /// <c>SpectrumPipelineOptions.Intermodulation</c>. All null (the default, and every row saved
    /// before this existed) means no IMD figure. <paramref name="ImdPreset"/> is the generator
    /// panel's template name (an <c>IntermodulationPreset</c> enum name, "Custom" for a typed pair)
    /// and purely a UI convenience; the tones, method, order and product band limit are what the
    /// pipeline actually measured with, so a stored result can say what produced its figure.
    /// </summary>
    string? ImdPreset = null,
    string? ImdMethod = null,
    int? ImdMaxOrder = null,
    double? ImdToneLowHz = null,
    double? ImdToneHighHz = null,
    double? ImdMaxProductFreqHz = null,
    /// <summary>
    /// The noise weighting (a <c>WeightingStandard</c> name) and which user tables were in effect.
    /// On a result these come from the snapshot that was saved, not the live options, so a
    /// weighting changed after the capture cannot relabel it. Null on a row an older build wrote,
    /// which means unweighted. The user tables' names are recorded so a result can say which
    /// file it was weighted by after the slot has been reloaded with another.
    /// </summary>
    string? Weighting = null,
    bool? WeightingUser1 = null,
    bool? WeightingUser2 = null,
    string? WeightingUser1Name = null,
    string? WeightingUser2Name = null,
    /// <summary>
    /// The DUT's gain in dB, entered beside Load Ω, used to refer the noise density to the DUT's
    /// input ("Noise in nV/√Hz") and for the chart's input-referred unit. A type setting, saved
    /// with the result that used it. Null means 0 dB.
    /// </summary>
    double? DutGainDb = null,
    /// <summary>The chart's level unit: <c>dBV</c>, <c>dBVPerRootHz</c> or <c>dBVPerRootHzInput</c> (see the client's SpectrumUnit). Null means dBV.</summary>
    string? ChartUnit = null,
    /// <summary>Whether the chart draws the weighting curve in effect as a dashed overlay. Null means off.</summary>
    bool? ChartShowWeightingCurve = null,
    /// <summary>
    /// Whether the oscilloscope shows the THD residual (the record with its fitted fundamental
    /// removed) instead of the raw waveform -- <c>SpectrumPipelineOptions.ScopeResidual</c>, live
    /// pipeline state like the IMD spec, so it is read from the options on save and re-applied on
    /// load. Null on a row an older build wrote, meaning off.
    /// </summary>
    bool? ScopeResidual = null);

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
    /// <summary>Whether the FFT was sized per point. Absent on rows written before it was recorded; those ran fixed, which is what the default says.</summary>
    bool AdaptiveResolution = false,
    /// <summary>
    /// "Conventional" or "FrequencyNormalized" -- what each point's harmonics were divided by. A
    /// stored result needs it because a normalized run carries two sets of figures per point and
    /// only this says which one the run was about; rows written before it existed are conventional.
    /// </summary>
    string ThdMethod = "Conventional",
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
    double? ChartYBottomDb = null,
    /// <summary>See SweepDisplayPanel.vue's "Autofit when measurement done" checkbox. On for a new type (the Default*ParametersJson seeds); null on a type saved before this existed, treated as false.</summary>
    bool? ChartAutofitOnDone = null,
    /// <summary>
    /// Which component the readout interprets the curve as — None, Capacitor, Inductor or
    /// Loudspeaker. A display choice, so it sits with the other <c>Chart*</c> fields and is null on
    /// a type stored before it existed.
    /// </summary>
    string? ChartDutType = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

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

/// <summary>
/// Configured (on <c>measurement_type</c>) or as-run (on <c>measurement_result</c>) parameters for
/// the 'ThdVsAmplitude' measurement kind. Its own record rather than nullable fields on
/// <see cref="ThdVsFrequencyParameters"/>: the two axes disagree about most of their run settings
/// (there is no level mode to solve for, no adaptive resolution and no noise-floor pass here, and
/// the start/end pair is volts rather than hertz) and about their chart bounds, which are the x
/// axis' own unit.
/// </summary>
public sealed record ThdVsAmplitudeParameters(
    /// <summary>The one frequency every point is measured at.</summary>
    double FixedFreqHz,
    double StartAmplitudeVolt,
    double EndAmplitudeVolt,
    int StepsPerOctave,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    /// <summary>Whether the run captured a generator-off residual before stepping the level.</summary>
    bool MeasureNoiseFloor = true,
    // The sweep chart's display preferences, exactly as ThdVsFrequencyParameters carries its own --
    // never fed into the runner. Optional with defaults so a row saved before a field existed still
    // deserializes.
    /// <summary>Which quantity the x axis plots -- 'outputVolt' | 'outputWatt' | 'inputVolt'.</summary>
    string? ChartXAxis = null,
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
    /// <summary>X-axis bounds in the selected <see cref="ChartXAxis"/>'s own unit (volts or watts).</summary>
    double? ChartXFrom = null,
    double? ChartXTo = null,
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null,
    /// <summary>Off unless the user opts in -- see SweepDisplayPanel.vue's "Autofit when measurement done" checkbox. Null on a type saved before this existed, treated as false.</summary>
    bool? ChartAutofitOnDone = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null,
    /// <summary>
    /// End the sweep once a channel's THD exceeds this many percent; null means sweep to the end.
    /// A type setting like the rest of the grid, saved with the result so a curve that stops short
    /// can say it did so on purpose.
    /// </summary>
    double? StopThdPercent = null);

/// <summary>
/// Configured or as-run parameters for the 'ImdVsAmplitude' measurement kind: a two-tone pair
/// stepped through a ladder of generator levels. The level fields are sine-equivalent volts (the
/// single sine whose peak equals the pair's peak -- see <c>Qa403.Dsp.IntermodulationPresets</c>).
/// <see cref="Preset"/> and <see cref="Method"/> are enum names (<c>IntermodulationPreset</c>,
/// <c>IntermodulationMethod</c>) so the JSON reads.
///
/// The chart display fields reuse <see cref="ThdVsAmplitudeParameters"/>' names on purpose: the
/// IMD sweep is drawn by the same chart, and <c>ChartShowThd</c>/<c>ChartShowD2..D5</c>/
/// <c>ChartShowD6Plus</c> there mean the IMD total, the IM2..IM5 orders and IM6+ (see
/// <c>web/src/imd/points.ts</c>). Renaming them would cost the client a second display record for
/// no information.
/// </summary>
public sealed record ImdVsAmplitudeParameters(
    string Preset,
    double ToneLowHz,
    double ToneHighHz,
    /// <summary>Amplitude ratio low:high -- 4 for SMPTE/DIN, 1 for CCIF.</summary>
    double LowToHighRatio,
    string Method,
    int MaxOrder,
    double StartLevelVolt,
    double EndLevelVolt,
    int StepsPerOctave,
    double LoadOhms,
    bool ProcessLeft,
    bool ProcessRight,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool MeasureNoiseFloor = true,
    /// <summary>Highest product frequency counted; null means the measurement band's upper edge (in-band products only).</summary>
    double? MaxProductFreqHz = null,
    string? ChartXAxis = null,
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
    double? ChartXFrom = null,
    double? ChartXTo = null,
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null,
    bool? ChartAutofitOnDone = null,
    /// <summary>The settle time the run discarded after each stimulus change, in ms -- a run record, null on a type's own params_json.</summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

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

/// <summary><c>SortOrder</c> is the user's own sidebar ordering (drag handle), not a name sort -- see ProjectRepository.ReorderTypesAsync.</summary>
public sealed record MeasurementTypeRow(
    long Id, long ProjectId, string Kind, string Name, string? Notes, string ParamsJson, int SortOrder, DateTimeOffset CreatedUtc);

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

/// <summary>
/// One frequency-response curve on the way into the database — a <c>response_trace</c> row.
///
/// <para>The arrays are the decimated display columns, all the same length. <see cref="Kind"/> is
/// <c>'left'</c>, <c>'right'</c> or <c>'gain'</c>; the gain curve is a ratio between two inputs and
/// so has no channel of its own, which is why this is keyed by kind rather than by channel like
/// every other per-curve table.</para>
/// </summary>
public sealed record ResponseTraceInput(
    string Kind,
    double StartHz,
    double EndHz,
    double PeakDb,
    double PeakHz,
    ResponseBandwidth OneDb,
    ResponseBandwidth ThreeDb,
    float[] FreqHz,
    float[] MagnitudeMinDb,
    float[] MagnitudeMaxDb,
    float[] PhaseDeg,
    float[] Coherence,
    /// <summary>
    /// The level both readings were measured down from, and which rule produced it ("Peak" or
    /// "BandMedian"). Null for a curve that has no bandwidth readings worth referencing — an
    /// impedance trace, whose corners mean nothing — and never written by an older build, whose
    /// rows read back as null and draw no reference line.
    /// </summary>
    double? ReferenceDb = null,
    string? ReferenceMode = null,
    /// <summary>
    /// Group delay and phase delay per column, in seconds — derived from the full-resolution
    /// phase before decimation, so they cannot be re-derived from <see cref="PhaseDeg"/> on load.
    /// Null for a curve that carries no meaningful transfer phase (an impedance trace), and never
    /// written by an older build, whose rows read back as null and draw no delay curve.
    /// </summary>
    float[]? GroupDelayS = null,
    float[]? PhaseDelayS = null);

/// <summary>
/// A stored ±1 dB or −3 dB reading. The clamped flags say the curve never left the tolerance on
/// that side within the measured band, so the frequency is where the search stopped rather than a
/// corner — a distinction a bare number cannot carry, and one the old app lost.
///
/// <para>Which of the two a row is (a symmetric flatness band or a one-sided roll-off one) is not
/// stored, because it follows from the column pair: <c>bw1_*</c> is always the ±1 dB reading and
/// <c>bw3_*</c> the −3 dB one. Rows written before symmetric searching existed are unaffected —
/// they were referenced to the peak, and nothing exceeds a trace's own peak, so the two searches
/// return the same corners there.</para>
/// </summary>
public sealed record ResponseBandwidth(double LowerHz, double UpperHz, bool LowerClamped, bool UpperClamped);

/// <summary>The <c>response_trace.kind</c> values — mirrors Qa403.App.Services.FrequencyResponseTraceKind.</summary>
public static class ResponseTraceKind
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Gain = "gain";

    /// <summary>
    /// An impedance curve: magnitude in <b>dB relative to 1 ohm</b> rather than a gain, phase in
    /// degrees.
    ///
    /// <para>Impedance reuses this table rather than taking one of its own, and that is deliberate:
    /// the columns already are a decimated magnitude/phase/coherence curve, and <c>kind</c> is a
    /// TEXT column, so new values need no migration. It also sidesteps the trap this project has hit
    /// twice — a new child table of <c>capture</c> that nobody added to
    /// <c>MeasurementRepository.DeleteResultAsync</c> and <c>ProjectRepository.DeleteTypeAsync</c>,
    /// orphaning rows silently until SQLite recycled a capture rowid onto them.</para>
    /// </summary>
    public const string Impedance = "z";

    /// <summary>The open standard behind an impedance measurement, stored as the measured ratio in plain dB.</summary>
    public const string ImpedanceOpen = "z_open";

    /// <summary>The short standard, likewise.</summary>
    public const string ImpedanceShort = "z_short";

    /// <summary>
    /// A crosstalk curve with the left channel driven: the right input relative to the left, in dB.
    /// The same rows as a response for the same reason impedance uses them; the bandwidth columns
    /// are zero and the reference is the peak, since a crosstalk trace has no corners to report.
    /// </summary>
    public const string CrosstalkLeftToRight = "xtalk_l2r";

    /// <summary>A crosstalk curve with the right channel driven: the left input relative to the right, in dB.</summary>
    public const string CrosstalkRightToLeft = "xtalk_r2l";

    /// <summary>
    /// An amplifier's output impedance from the load-step measurement, dB relative to 1 ohm like
    /// <see cref="Impedance"/> — it is an impedance curve and the CSV exporter and the chart treat
    /// it as one; the result's kind (OutputImpedance) says what it is of.
    /// </summary>
    public const string OutputImpedance = "zout";

    /// <summary>The output-impedance run's gain into its first load, dB. Kept beside <see cref="OutputImpedance"/> so a stored result carries what it was derived from.</summary>
    public const string GainLoad1 = "gain_load1";

    /// <summary>The output-impedance run's gain into its second load (or open), dB. See <see cref="GainLoad1"/>.</summary>
    public const string GainLoad2 = "gain_load2";
}

/// <summary>
/// A crosstalk measurement type's <c>params_json</c>: the stepped grid and level of
/// <see cref="SteppedResponseParameters"/> minus what a crosstalk run has no use for (a reference
/// channel, a phase, a bandwidth reference), plus which channel is the aggressor. Run settings are
/// required; the <c>Chart*</c> fields and the as-run levels are nullable for the same reasons as
/// on the other kinds.
/// </summary>
/// <param name="Direction">"LeftToRight", "RightToLeft" or "Both".</param>
/// <param name="GeneratorAmpDbvLeftToRight">The generator level the left-driven pass actually used — solved by probing in the output-referred modes, so only known after the fact, and per pass. Null on a type's own params_json and for a pass the run did not make.</param>
public sealed record CrosstalkParameters(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string Direction,
    string LevelMode,
    double LevelValue,
    string LevelUnit,
    double LoadOhms,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool AdaptiveResolution,
    double? GeneratorAmpDbvLeftToRight = null,
    double? GeneratorAmpDbvRightToLeft = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    bool? ChartAutofitOnDone = null,
    /// <summary>The settle time the run discarded after each stimulus change, in ms — a run record, null on a type's own params_json.</summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

/// <summary>
/// An output-impedance (damping factor) measurement type's <c>params_json</c>: the run settings the
/// panel edits, the two run records (<see cref="GeneratorAmpDbv"/>, <see cref="InputRangeDbv"/> —
/// the one range both passes were read at, which is what makes a 0.03 dB gain difference a
/// measurement), and the chart state. <see cref="LoadOhms"/> is the resistor connected for the
/// first pass — it is also what a power target converts through — <see cref="SecondLoadOhms"/>
/// the one for the second pass (null: the output left open, the tutorial's way, which a tube
/// amplifier must not be asked for), and <see cref="NominalOhms"/> is what the damping factor is
/// quoted against, since a specification sheet always says 8 Ω whatever tap or load was used.
/// </summary>
public sealed record OutputImpedanceParameters(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string LevelMode,
    double LevelValue,
    string LevelUnit,
    double LoadOhms,
    double? SecondLoadOhms,
    double NominalOhms,
    bool RightChannelIsReference,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool AdaptiveResolution,
    double? GeneratorAmpDbv = null,
    double? InputRangeDbv = null,
    double? ChartZTopOhms = null,
    double? ChartZBottomOhms = null,
    string? ChartZScale = null,
    bool? ChartShowDampingFactor = null,
    bool? ChartShowPhase = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    double? ChartPhaseTopDeg = null,
    double? ChartPhaseBottomDeg = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    bool? ChartAutofitOnDone = null,
    /// <summary>The settle time the run discarded after each stimulus change, in ms — a run record, null on a type's own params_json.</summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

/// <summary>
/// An impedance measurement type's <c>params_json</c>.
///
/// <para>Run settings are required and must round-trip exactly. Every <c>Chart*</c> field is
/// nullable so a type stored before one existed keeps the app's defaults rather than failing to
/// load.</para>
///
/// <para><b>The two calibrations live here</b>, which is what makes a fixture calibration outlast the
/// app. They are the decimated curves — a few hundred log-spaced points each, not a bin per analysis
/// bin — so the JSON stays a sane size; see <see cref="ImpedanceCalibration"/>.</para>
/// </summary>
/// <summary>
/// Defaults for <see cref="ImpedanceParameters"/> that a record's own primary constructor cannot
/// express — a default may not reference a member of the type being declared, and this project
/// cannot see <c>Qa403.App</c>'s copy. Pinned to it by
/// <c>ImpedanceCalibrationLevelTests.TheStoredDefaultMatchesTheRequestsOwn</c>.
/// </summary>
public static class ImpedanceParametersDefaults
{
    public const double CalibrationLevelDbv = 0.0;
}

public sealed record ImpedanceParameters(
    string Method,
    string Stimulus,
    double ReferenceOhms,
    bool RightChannelIsReference,
    double FrequencySpanHz,
    int ResolutionMultiplier,
    double ChirpStartHz,
    double LevelValue,
    string LevelUnit,
    int SmoothingDenominator,
    int AverageCount,
    string CableMode,
    double FixedCableOhms,
    double FixedCableMillihenry,
    /// <summary>The drive level calibration runs use, in the same unit as <c>LevelValue</c>.</summary>
    double CalibrationLevelValue = ImpedanceParametersDefaults.CalibrationLevelDbv,
    double? AnalyzerInputOhms = null,
    bool ApplySweepGate = false,
    ImpedanceCalibration? OpenCal = null,
    ImpedanceCalibration? ShortCal = null,
    /// <summary>
    /// Whether this measurement adopts the globally configured fixture instead of its own standards.
    /// A new type is created with this already true when a global calibration exists — see
    /// <c>MeasurementSaveService.DefaultImpedanceParametersJson</c>.
    /// </summary>
    bool UseGlobalFixture = false,
    double? GeneratorAmpDbv = null,
    /// <summary>
    /// What the open/short captures play, independently of <c>Stimulus</c>. Null on a type stored
    /// before the second stimulus existed, where the chirp was the only thing either could be.
    /// </summary>
    string? CalibrationStimulus = null,
    /// <summary>
    /// The stepped-sine grid. All nullable, so a type stored before the stepped stimulus existed
    /// opens on the app's defaults rather than failing to load — the same rule the <c>Chart*</c>
    /// fields follow, and for the same reason.
    /// </summary>
    double? StartFreqHz = null,
    double? EndFreqHz = null,
    int? StepsPerOctave = null,
    bool? AdaptiveResolution = null,
    string? ChartView = null,
    bool? ChartShowPhase = null,
    bool? ChartShowCoherence = null,
    bool? ChartShowShortCal = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    double? ChartZTopOhms = null,
    double? ChartZBottomOhms = null,
    string? ChartZScale = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    double? ChartPhaseTopDeg = null,
    double? ChartPhaseBottomDeg = null,
    bool? ChartAutofitOnDone = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

/// <summary>One save of a frequency-response measurement: a result row, its single capture, and the curves.</summary>
public sealed record ResponseSaveRequest(
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
    IReadOnlyList<ResponseTraceInput> Traces);

/// <summary>One frequency-response curve read back out of the database.</summary>
public sealed record StoredResponseTrace(
    string Kind,
    double StartHz,
    double EndHz,
    double PeakDb,
    double PeakHz,
    ResponseBandwidth OneDb,
    ResponseBandwidth ThreeDb,
    float[] FreqHz,
    float[] MagnitudeMinDb,
    float[] MagnitudeMaxDb,
    float[] PhaseDeg,
    float[] Coherence,
    /// <summary>Null on a row written before the reference was recorded — see <see cref="ResponseTraceInput.ReferenceDb"/>.</summary>
    double? ReferenceDb = null,
    string? ReferenceMode = null,
    /// <summary>Null on a row written before the delays were recorded — see <see cref="ResponseTraceInput.GroupDelayS"/>.</summary>
    float[]? GroupDelayS = null,
    float[]? PhaseDelayS = null);

/// <summary>
/// A frequency-response measurement type's <c>params_json</c>: the run settings, then the chart's
/// display state.
///
/// <para>Same split, and same reason, as <see cref="ThdVsFrequencyParameters"/> — the run settings
/// are what the measurement was taken with and must round-trip exactly, while the <c>Chart*</c>
/// fields are how it was being looked at and are all nullable so an older stored type simply keeps
/// the app's defaults instead of failing to load.</para>
/// </summary>
public sealed record FrequencyResponseParameters(
    double FrequencySpanHz,
    int ResolutionMultiplier,
    string LevelMode,
    double LevelValue,
    string LevelUnit,
    bool RightChannelIsReference,
    int SmoothingDenominator,
    int AverageCount,
    double? GeneratorAmpDbv = null,
    string? ChartView = null,
    bool? ChartShowLeft = null,
    bool? ChartShowRight = null,
    bool? ChartShowPhase = null,
    bool? ChartShowCoherence = null,
    bool? ChartShowOneDb = null,
    bool? ChartShowThreeDb = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    /// <summary>
    /// Null on a type saved before the phase axis was independently rangeable, in which case the
    /// client falls back to its own ±180° default. Kept as a pair with
    /// <see cref="ChartPhaseBottomDeg"/> rather than folded into <see cref="ChartYTopDb"/>'s pair:
    /// the phase axis is unwrapped and can run past ±180°, so it needs a window of its own and
    /// cannot share the dB one's.
    /// </summary>
    double? ChartPhaseTopDeg = null,
    double? ChartPhaseBottomDeg = null,
    /// <summary>
    /// Which delay curve the chart draws on its own right-hand axis — "None", "Group" or "Phase" —
    /// and that axis' window in microseconds. Null on a type saved before the delay curves existed,
    /// in which case the client keeps its defaults (no curve, ±1000 µs).
    /// </summary>
    string? ChartDelayView = null,
    double? ChartDelayTopUs = null,
    double? ChartDelayBottomUs = null,
    /// <summary>Null on a type saved before the chirp start was configurable; the runner then uses its default.</summary>
    double? ChirpStartHz = null,
    /// <summary>
    /// The load an <c>OutputPower</c> level was defined against, in ohms. Null on a type saved
    /// before the power mode existed — which is also every type whose level is a voltage, where it
    /// means nothing.
    /// </summary>
    double? LoadOhms = null,
    /// <summary>
    /// The FR (dBV) view's own y window, independent of <see cref="ChartYTopDb"/>/<see
    /// cref="ChartYBottomDb"/> which (despite the name) is now the Gain (dB) view's window only --
    /// null on a type saved before the two views had separate ranges, in which case the client
    /// falls back to its own default rather than reusing the Gain pair.
    /// </summary>
    double? ChartYTopDbv = null,
    double? ChartYBottomDbv = null,
    /// <summary>Off unless the user opts in -- see FrequencyResponseDisplayPanel.vue's "Autofit when measurement done" checkbox. Null on a type saved before this existed, treated as false.</summary>
    bool? ChartAutofitOnDone = null,
    /// <summary>
    /// Which projection the chart draws -- "Bode", "Nyquist" or "Nichols". Null on a type saved
    /// before the polar views existed, where "Bode" is the behaviour that build had.
    /// </summary>
    string? ChartProjection = null,
    /// <summary>How a Nyquist plot maps magnitude onto radius -- "Linear" or "Db".</summary>
    string? ChartNyquistRadius = null,
    double? ChartNyquistRadiusFloorDb = null,
    /// <summary>Adds 180 degrees to the measured phase: the summing-junction inversion a loop-gain injection may or may not include, which the analyzer cannot see.</summary>
    bool? ChartInvertLoopSign = null,
    /// <summary>Put back the bulk delay the chirp estimator removed, so the contour is the physical one. No effect on a stepped run, which removes none.</summary>
    bool? ChartRestoreDelay = null,
    /// <summary>Nyquist and Nichols: draw the Poles and zeros panel's model over the measurement. Null on a type saved before the checkbox existed, treated as on -- which is what those builds did.</summary>
    bool? ChartShowFittedModel = null,
    /// <summary>
    /// The Poles and zeros panel's <b>Auto</b> checkbox: the fit order follows the knee search
    /// rather than the two numbers below. Null on a type saved before the orders were stored, which
    /// is treated as on -- automatic is what those builds always did.
    /// </summary>
    bool? ChartFitAutoOrder = null,
    /// <summary>
    /// The denominator order the fit is pinned to when Auto is off -- how many poles the model has.
    ///
    /// <para>Stored with the measurement despite describing an <i>analysis</i> rather than the
    /// screen's layout, which was the reason it deliberately was not until 2026-09-24. The risk that
    /// reasoning named -- an order restored onto a different DUT's curve -- does not arise, because
    /// params_json belongs to one measurement type, i.e. to one DUT and one setup; and the order is
    /// a judgement the user reached by looking at the residual, which is exactly the kind of thing
    /// that should survive closing the app.</para>
    /// </summary>
    int? ChartFitPoles = null,
    /// <summary>The numerator order -- how many zeros -- pinned separately, and never more than <see cref="ChartFitPoles"/>.</summary>
    int? ChartFitZeros = null,
    /// <summary>
    /// Pole-zero map: leave out the roots the fit placed outside the swept band. A display choice
    /// only -- the fit, its residual and the panel's table are unchanged. Null on a type saved
    /// before the map existed, treated as false.
    /// </summary>
    bool? ChartHideOutOfBandRoots = null,
    /// <summary>
    /// How both s-plane views lay out radius: "Linear" or "LogModulus". Null on a type saved before
    /// the |H(s)| surface existed, treated as linear -- which is what those builds drew, so an
    /// existing type opens looking untouched.
    /// </summary>
    string? ChartSurfaceScale = null,
    /// <summary>
    /// |H(s)| surface: how the same field is laid out -- "Heatmap" (the plane from above, with
    /// contours) or "Relief" (an oblique surface where a pole is a spike). One evaluated field
    /// serves both, so they cannot disagree about a level. Null means the heat map.
    /// </summary>
    string? ChartSurfaceMode = null,
    /// <summary>
    /// |H(s)| surface: dB above the passband reference that the colour ramp reaches. A pole runs to
    /// infinity, so this is where it saturates rather than a level anything attains. An offset from
    /// the passband and not an absolute level, so the window means the same on a 0 dB filter and a
    /// +26 dB amplifier.
    /// </summary>
    double? ChartSurfaceTopDb = null,
    /// <summary>|H(s)| surface: dB below the passband reference that the colour ramp reaches.</summary>
    double? ChartSurfaceBottomDb = null,
    /// <summary>|H(s)| surface: which colour ramp, from the same set the decay surfaces offer.</summary>
    string? ChartSurfacePalette = null,
    /// <summary>
    /// |H(s)| surface: draw the iso-dB contour lines. They are what makes the surface quantitative
    /// rather than decorative, and what keeps it readable after a report desaturates the colours.
    /// </summary>
    bool? ChartSurfaceContours = null,
    /// <summary>
    /// What the ±1 dB and −3 dB corners were measured down from: "BandMedian" or "Peak". Null on a
    /// type saved before the reference was configurable, where the peak is what that build did.
    /// </summary>
    string? BandwidthReference = null,
    /// <summary>
    /// The reference band's edges, in Hz. Null on a type saved before it existed. A run setting
    /// rather than a <c>Chart*</c> one: it changes the numbers in the Measurements table, not how
    /// they are drawn, so a stored result that cannot name its reference band cannot say what its
    /// corners mean.
    /// </summary>
    double? BandwidthReferenceLowHz = null,
    double? BandwidthReferenceHighHz = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

/// <summary>
/// A stepped-sine response measurement type's <c>params_json</c>: the run settings, then the chart's
/// display state.
///
/// <para>Its own record rather than more optional properties on
/// <see cref="FrequencyResponseParameters"/>, for the reason <see cref="ThdVsAmplitudeParameters"/>
/// is separate from <see cref="ThdVsFrequencyParameters"/> — the two disagree about most of their
/// run settings. A chirp is described by a span and a resolution multiplier; a stepped sweep by a
/// frequency range, a step density and an FFT size, and neither set means anything to the other.
/// What they do share is the <c>Chart*</c> half, which describes how a response is being looked at
/// rather than how it was taken, and which is nullable throughout so a type stored before a field
/// existed simply keeps the app's defaults instead of failing to load.</para>
///
/// <para>Every run setting has to be here. A stored result that cannot say what produced it is the
/// failure mode <c>ChirpStartHz</c> caused on the chirp path: three runs at different settings came
/// back identical and nothing complained.</para>
/// </summary>
public sealed record SteppedResponseParameters(
    double StartFreqHz,
    double EndFreqHz,
    int StepsPerOctave,
    string LevelMode,
    double LevelValue,
    string LevelUnit,
    double LoadOhms,
    bool GenerateLeft,
    bool GenerateRight,
    int FftSize,
    WindowType Window,
    int AverageCount,
    double SampleRateHz,
    bool AdaptiveResolution,
    bool RightChannelIsReference,
    /// <summary>The generator amplitude the run actually used — solved by probing for the output-referred modes, so it is only known after the fact.</summary>
    double? GeneratorAmpDbv = null,
    string? ChartView = null,
    bool? ChartShowLeft = null,
    bool? ChartShowRight = null,
    bool? ChartShowPhase = null,
    bool? ChartShowCoherence = null,
    bool? ChartShowOneDb = null,
    bool? ChartShowThreeDb = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null,
    double? ChartXFromHz = null,
    double? ChartXToHz = null,
    /// <summary>Null on a type saved before the phase axis was independently rangeable — see <see cref="FrequencyResponseParameters.ChartPhaseTopDeg"/>.</summary>
    double? ChartPhaseTopDeg = null,
    double? ChartPhaseBottomDeg = null,
    /// <summary>The delay curve and its axis window — see <see cref="FrequencyResponseParameters.ChartDelayView"/>.</summary>
    string? ChartDelayView = null,
    double? ChartDelayTopUs = null,
    double? ChartDelayBottomUs = null,
    /// <summary>The FR (dBV) view's own y window — see <see cref="FrequencyResponseParameters.ChartYTopDbv"/>.</summary>
    double? ChartYTopDbv = null,
    double? ChartYBottomDbv = null,
    /// <summary>Off unless the user opts in -- see FrequencyResponseDisplayPanel.vue's "Autofit when measurement done" checkbox. Null on a type saved before this existed, treated as false.</summary>
    bool? ChartAutofitOnDone = null,
    /// <summary>
    /// Which projection the chart draws -- "Bode", "Nyquist" or "Nichols". Null on a type saved
    /// before the polar views existed, where "Bode" is the behaviour that build had.
    /// </summary>
    string? ChartProjection = null,
    /// <summary>How a Nyquist plot maps magnitude onto radius -- "Linear" or "Db".</summary>
    string? ChartNyquistRadius = null,
    double? ChartNyquistRadiusFloorDb = null,
    /// <summary>Adds 180 degrees to the measured phase: the summing-junction inversion a loop-gain injection may or may not include, which the analyzer cannot see.</summary>
    bool? ChartInvertLoopSign = null,
    /// <summary>Put back the bulk delay the chirp estimator removed, so the contour is the physical one. No effect on a stepped run, which removes none.</summary>
    bool? ChartRestoreDelay = null,
    /// <summary>Nyquist and Nichols: draw the Poles and zeros panel's model over the measurement. Null on a type saved before the checkbox existed, treated as on -- which is what those builds did.</summary>
    bool? ChartShowFittedModel = null,
    /// <summary>
    /// The Poles and zeros panel's <b>Auto</b> checkbox: the fit order follows the knee search
    /// rather than the two numbers below. Null on a type saved before the orders were stored, which
    /// is treated as on -- automatic is what those builds always did.
    /// </summary>
    bool? ChartFitAutoOrder = null,
    /// <summary>
    /// The denominator order the fit is pinned to when Auto is off -- how many poles the model has.
    ///
    /// <para>Stored with the measurement despite describing an <i>analysis</i> rather than the
    /// screen's layout, which was the reason it deliberately was not until 2026-09-24. The risk that
    /// reasoning named -- an order restored onto a different DUT's curve -- does not arise, because
    /// params_json belongs to one measurement type, i.e. to one DUT and one setup; and the order is
    /// a judgement the user reached by looking at the residual, which is exactly the kind of thing
    /// that should survive closing the app.</para>
    /// </summary>
    int? ChartFitPoles = null,
    /// <summary>The numerator order -- how many zeros -- pinned separately, and never more than <see cref="ChartFitPoles"/>.</summary>
    int? ChartFitZeros = null,
    /// <summary>
    /// Pole-zero map: leave out the roots the fit placed outside the swept band. A display choice
    /// only -- the fit, its residual and the panel's table are unchanged. Null on a type saved
    /// before the map existed, treated as false.
    /// </summary>
    bool? ChartHideOutOfBandRoots = null,
    /// <summary>
    /// How both s-plane views lay out radius: "Linear" or "LogModulus". Null on a type saved before
    /// the |H(s)| surface existed, treated as linear -- which is what those builds drew, so an
    /// existing type opens looking untouched.
    /// </summary>
    string? ChartSurfaceScale = null,
    /// <summary>
    /// |H(s)| surface: how the same field is laid out -- "Heatmap" (the plane from above, with
    /// contours) or "Relief" (an oblique surface where a pole is a spike). One evaluated field
    /// serves both, so they cannot disagree about a level. Null means the heat map.
    /// </summary>
    string? ChartSurfaceMode = null,
    /// <summary>
    /// |H(s)| surface: dB above the passband reference that the colour ramp reaches. A pole runs to
    /// infinity, so this is where it saturates rather than a level anything attains. An offset from
    /// the passband and not an absolute level, so the window means the same on a 0 dB filter and a
    /// +26 dB amplifier.
    /// </summary>
    double? ChartSurfaceTopDb = null,
    /// <summary>|H(s)| surface: dB below the passband reference that the colour ramp reaches.</summary>
    double? ChartSurfaceBottomDb = null,
    /// <summary>|H(s)| surface: which colour ramp, from the same set the decay surfaces offer.</summary>
    string? ChartSurfacePalette = null,
    /// <summary>
    /// |H(s)| surface: draw the iso-dB contour lines. They are what makes the surface quantitative
    /// rather than decorative, and what keeps it readable after a report desaturates the colours.
    /// </summary>
    bool? ChartSurfaceContours = null,
    /// <summary>
    /// What the ±1 dB and −3 dB corners were measured down from: "BandMedian" or "Peak". Null on a
    /// type saved before the reference was configurable, where the peak is what that build did.
    /// </summary>
    string? BandwidthReference = null,
    /// <summary>
    /// The reference band's edges, in Hz. Null on a type saved before it existed. A run setting
    /// rather than a <c>Chart*</c> one: it changes the numbers in the Measurements table, not how
    /// they are drawn, so a stored result that cannot name its reference band cannot say what its
    /// corners mean.
    /// </summary>
    double? BandwidthReferenceLowHz = null,
    double? BandwidthReferenceHighHz = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);

/// <summary>The <c>impulse_result.kind</c> values — mirrors the subset of FrequencyResponseTraceKind an impulse response has.</summary>
public static class ImpulseTraceKind
{
    public const string Left = "left";
    public const string Gain = "gain";
}

/// <summary>
/// One impulse response on the way into the database.
/// </summary>
/// <param name="Ir">
/// The gated impulse response at full resolution, truncated around its peak. Not decimated, unlike
/// every curve stored beside it: a decimated impulse response cannot be re-gated, re-transformed or
/// analysed for decay, and those are the only reasons to keep it at all.
/// </param>
/// <param name="PeakIndex">Where the linear impulse sits inside <paramref name="Ir"/> — the array keeps samples on both sides of it.</param>
public sealed record ImpulseTraceInput(
    string Kind,
    double SampleRateHz,
    int PeakIndex,
    double PeakVolts,
    double GatePreMs,
    double GatePostMs,
    string GateWindow,
    double ResolutionLimitHz,
    string ReferenceMode,
    string? ReferenceName,
    double? LatencySamples,
    float[] Ir);

/// <summary>One harmonic order's distortion curve on the way into the database.</summary>
public sealed record HarmonicCurveInput(
    int Order,
    double UpperValidHz,
    float[] FundamentalHz,
    float[] LevelDbc);

/// <summary>
/// One save of an impulse-response measurement: a result row, its single capture, the gated
/// magnitude/phase curves (which go into <c>response_trace</c>, because they are the same curves a
/// frequency response produces), the impulse responses, and the harmonic curves.
/// </summary>
public sealed record ImpulseSaveRequest(
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
    IReadOnlyList<ResponseTraceInput> Traces,
    IReadOnlyList<ImpulseTraceInput> Impulses,
    IReadOnlyList<HarmonicCurveInput> Harmonics);

/// <summary>One impulse response read back out of the database.</summary>
public sealed record StoredImpulseTrace(
    string Kind,
    double SampleRateHz,
    int PeakIndex,
    double PeakVolts,
    double GatePreMs,
    double GatePostMs,
    string GateWindow,
    double ResolutionLimitHz,
    string ReferenceMode,
    string? ReferenceName,
    double? LatencySamples,
    float[] Ir);

/// <summary>One harmonic curve read back out of the database.</summary>
public sealed record StoredHarmonicCurve(
    int Order,
    double UpperValidHz,
    float[] FundamentalHz,
    float[] LevelDbc);

/// <summary>
/// The impulse-response-specific half of a stored result. The magnitude and phase curves are not
/// here — they are <c>response_trace</c> rows and come back from
/// <see cref="MeasurementRepository.GetResponseTracesAsync"/>, the same as a frequency response's.
/// </summary>
public sealed record StoredImpulseResult(
    IReadOnlyList<StoredImpulseTrace> Impulses,
    IReadOnlyList<StoredHarmonicCurve> Harmonics);

/// <summary>
/// A stored loopback calibration: the analyzer's own impulse response, with the peak sitting
/// <see cref="PeakOffset"/> samples into <see cref="Ir"/>.
/// </summary>
public sealed record StoredLoopbackReference(
    long Id,
    string Name,
    DateTimeOffset TakenUtc,
    double SampleRateHz,
    int SourceFftSize,
    double ChirpStartHz,
    double ChirpEndHz,
    double RemovedDelaySamples,
    int PeakOffset,
    float[] Ir,
    string? DeviceSerial,
    bool IsSimulated);

/// <summary>
/// An impulse-response measurement type's <c>params_json</c>: the run settings, then the chart's
/// display state.
///
/// <para>Every run setting has to appear here, and that is not a formality — a stored result whose
/// params cannot say what produced it is not a measurement, it is a picture. The same field-by-field
/// discipline as the wire record, and for the same reason: a setting added to the request and
/// forgotten here comes back as a default and the result claims settings the user did not use.</para>
/// </summary>
public sealed record ImpulseResponseParameters(
    double FrequencySpanHz,
    int ResolutionMultiplier,
    string LevelMode,
    double LevelValue,
    string LevelUnit,
    int SmoothingDenominator,
    int AverageCount,
    string ReferenceMode,
    long? ReferenceCalibrationId,
    double GatePreMs,
    double GatePostMs,
    string GateWindow,
    bool ExtractHarmonics,
    int MaxHarmonicOrder,
    /// <summary>What the level probe settled the generator at. Read-only history — a run does not take it as an input.</summary>
    double? GeneratorAmpDbv = null,
    double ChirpStartHz = 2.0,
    double LoadOhms = 8.0,
    string? ChartView = null,
    bool? ChartNormalisedImpulse = null,
    bool? ChartShowPhase = null,
    bool? ChartShowCoherence = null,
    bool? ChartThickLines = null,
    bool? ChartShowDataPoints = null,
    /// <summary>The impulse view's own level window. The other two views keep theirs separately —
    /// they read different quantities (dB re peak, gain, dBc), so one shared window lands on the
    /// previous view's range every time the selector moves.</summary>
    double? ChartYTopDb = null,
    double? ChartYBottomDb = null,
    double? ChartResponseYTopDb = null,
    double? ChartResponseYBottomDb = null,
    double? ChartHarmonicsYTopDb = null,
    double? ChartHarmonicsYBottomDb = null,
    double? ChartPhaseTopDeg = null,
    double? ChartPhaseBottomDeg = null,
    /// <summary>The Response view's delay curve and its axis window — see <see cref="FrequencyResponseParameters.ChartDelayView"/>.</summary>
    string? ChartDelayView = null,
    double? ChartDelayTopUs = null,
    double? ChartDelayBottomUs = null,
    double? ChartTimeFromMs = null,
    double? ChartTimeToMs = null,
    double? ChartFreqFromHz = null,
    double? ChartFreqToHz = null,
    /// <summary>How the two decay surfaces are drawn — stacked curves or a sonogram — and in what colours.</summary>
    string? ChartDecayMode = null,
    string? ChartDecayPalette = null,
    bool? ChartDecayColoredCurves = null,
    double? ChartDecayRangeDb = null,
    /// <summary>The cumulative spectral decay's analysis. Not run settings: they are questions put to
    /// a record already captured, and changing one redraws rather than re-measures. Stored all the
    /// same, because a surface is unreadable without knowing which analysis produced it.</summary>
    int? ChartCsdFftLength = null,
    int? ChartCsdBlockShiftSamples = null,
    int? ChartCsdBlockCount = null,
    string? ChartCsdWindowMode = null,
    double? ChartCsdRiseTimeMs = null,
    /// <summary>The burst decay's analysis, on the same footing.</summary>
    int? ChartBurstMaxPeriods = null,
    int? ChartBurstPointsPerPeriod = null,
    string? ChartBurstResolution = null,
    /// <summary>
    /// The settle time the run discarded after each stimulus change, in ms -- the Configuration
    /// dialog's "Capture delay", applied server-side (see DeviceSessionService.CaptureDelayMs). A
    /// run record, not a type setting: null on a type's own params_json and on results saved
    /// before it was recorded, and the value the session actually used on a saved result.
    /// </summary>
    double? CaptureDelayMs = null,
    /// <summary>
    /// The external input gain the app was configured with when the result was saved, in dB per
    /// input (see ExternalGainSetting; a 10:1 divider is −20). Every stored level is already the
    /// DUT's, with this undone; the value is kept so a result can say what was in the path. The
    /// setting at save time, not stamped per sample -- null on a type's own params_json and on
    /// results saved before it was recorded.
    /// </summary>
    double? ExternalGainLeftDb = null,
    double? ExternalGainRightDb = null);
