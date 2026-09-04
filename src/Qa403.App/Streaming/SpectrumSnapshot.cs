using System.Numerics;
using Qa403.Dsp;

namespace Qa403.App.Streaming;

/// <summary>
/// The scalar figures <see cref="SpectrumPipeline"/> derives from one channel's PSD each tick --
/// fundamental, THD/THD+N/SNR, noise floor, peak/RMS, harmonics, and other spectral peaks. Shared
/// between the live meters broadcast and <see cref="SpectrumPipeline.LatestSnapshots"/> so a saved
/// measurement's metrics are exactly what the meters panel showed, not re-derived separately.
/// </summary>
public sealed record SpectrumMeters(
    float FundamentalFreqHz,
    float BinSizeHz,
    float ThdPercent,
    float ThdNPercent,
    float SnrDb,
    float FundamentalDbv,
    float NoiseFloorDbv,
    float PeakDbv,
    float TotalRmsDbv,
    Harmonic[] Harmonics,
    (float FreqHz, float Dbc)[] OtherPeaks,
    /// <summary>
    /// How many spectra have contributed to this tick's (possibly averaged) trace so far, capped
    /// at the configured target — see <see cref="Qa403.Dsp.SpectrumAverager.Count"/>. 0 when
    /// averaging is off.
    /// </summary>
    int AveragesTaken);

/// <summary>
/// A signal decimated to at most a few hundred points for a small preview chart -- never the
/// full-resolution trace. <see cref="X"/> is frequency (Hz) for a spectrum preview or elapsed time
/// (s) for a scope preview; <see cref="Min"/>/<see cref="Max"/> are the min/max of whatever source
/// samples/bins fell into that column, matching <see cref="Qa403.Dsp.SpectrumDecimator"/>'s
/// "never the mean" rule -- a single clipped peak or a harmonic spike must survive decimation in at
/// least one of the two arrays, not get averaged away. All three arrays are the same length.
/// </summary>
public sealed record SweepPreviewSeries(float[] X, float[] Min, float[] Max);

/// <summary>
/// One channel's full-resolution spectrum as last displayed, for a "save current capture"
/// action. <see cref="Dbv"/> is dBV (20*log10(Vrms)) per bin -- the amplitude-domain trace, full
/// resolution (not the ~1600-column decimated display frame). <see cref="Meters"/> is null when
/// the tick that produced this trace found no fundamental in the measurement band (e.g. the
/// loudest content in the whole spectrum sits above <c>MeasurementHiFreqHz</c>) -- the trace is
/// still perfectly saveable, it just has no derived THD/SNR/harmonics to go with it.
///
/// <see cref="PreviewFft"/>/<see cref="PreviewScope"/> are populated only when
/// <see cref="SpectrumPipelineOptions.CapturePreview"/> was on for the tick that produced this
/// snapshot -- see that option's remarks for why this isn't computed on every live tick.
///
/// <see cref="Complex"/> is likewise populated only under
/// <see cref="SpectrumPipelineOptions.RetainComplexSpectrum"/>. It is the raw one-sided DFT of the
/// windowed record, phase included, which <see cref="Dbv"/> cannot express -- the frequency-response
/// measurement divides two of them to get a transfer function, and a magnitude alone would leave it
/// unable to tell a delay from a filter.
/// </summary>
public sealed record SpectrumSnapshot(
    StreamChannel Channel,
    uint Seq,
    DateTimeOffset CapturedUtc,
    double SampleRateHz,
    double BinSizeHz,
    int FftSize,
    WindowType Window,
    WindowSums Sums,
    double[] Dbv,
    SpectrumMeters? Meters,
    SweepPreviewSeries? PreviewFft = null,
    SweepPreviewSeries? PreviewScope = null,
    Complex[]? Complex = null);
