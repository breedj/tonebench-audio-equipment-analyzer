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
    (float FreqHz, float Dbc)[] OtherPeaks);

/// <summary>
/// One channel's full-resolution spectrum as last displayed, for a "save current capture"
/// action. <see cref="Dbv"/> is dBV (20*log10(Vrms)) per bin -- the amplitude-domain trace, full
/// resolution (not the ~1600-column decimated display frame).
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
    SpectrumMeters Meters);
