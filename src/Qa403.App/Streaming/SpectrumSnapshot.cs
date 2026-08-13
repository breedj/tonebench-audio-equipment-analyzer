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
/// One channel's full-resolution spectrum as last displayed, for a "save current capture"
/// action. <see cref="Dbv"/> is dBV (20*log10(Vrms)) per bin -- the amplitude-domain trace, full
/// resolution (not the ~1600-column decimated display frame). <see cref="Meters"/> is null when
/// the tick that produced this trace found no fundamental in the measurement band (e.g. the
/// loudest content in the whole spectrum sits above <c>MeasurementHiFreqHz</c>) -- the trace is
/// still perfectly saveable, it just has no derived THD/SNR/harmonics to go with it.
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
    SpectrumMeters? Meters);
