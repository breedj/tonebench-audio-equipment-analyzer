using Qa403.Dsp;

namespace Qa403.App.Streaming;

/// <summary>Live-reconfigurable knobs for <see cref="SpectrumPipeline"/>. Reassign <see cref="SpectrumPipeline.Options"/> to change these mid-stream.</summary>
public sealed record SpectrumPipelineOptions
{
    /// <summary>Power-of-two FFT length. Larger values raise frequency resolution and per-tick CPU cost, not frame rate — see <see cref="TargetFrameRateHz"/>.</summary>
    public int FftSize { get; init; } = 65536;

    public WindowType Window { get; init; } = WindowType.Hann;

    public AveragingMode Averaging { get; init; } = AveragingMode.Off;

    /// <summary>Spectra averaged together (linear) or the effective time constant (exponential, alpha = 2/(count+1)). Ignored when <see cref="Averaging"/> is <see cref="AveragingMode.Off"/>.</summary>
    public int AverageCount { get; init; } = 8;

    /// <summary>Decimated columns sent per frame, log-spaced across [<see cref="DisplayLoFreqHz"/>, Nyquist].</summary>
    public int DisplayColumns { get; init; } = 1600;

    public double DisplayLoFreqHz { get; init; } = 10.0;

    /// <summary>
    /// Display tick rate. A <see cref="PeriodicTimer"/> paces the worker at this rate regardless
    /// of <see cref="FftSize"/>; if one tick's FFT+encode+broadcast work runs long, the timer
    /// simply does not queue up missed ticks — the next tick fires at its own scheduled time,
    /// which is what decouples display rate from both acquisition rate and FFT cost.
    /// </summary>
    public int TargetFrameRateHz { get; init; } = 20;

    /// <summary>Harmonics reported per meters frame, order 1 (fundamental) through this order — 9 gives fundamental + H2..H9.</summary>
    public int HarmonicCount { get; init; } = 9;

    /// <summary>
    /// Lower bound, in Hz, for locating the fundamental by peak search. Excludes DC and mains
    /// hum (50/60 Hz and its low harmonics) from being mistaken for the fundamental.
    /// </summary>
    public double FundamentalMinSearchHz { get; init; } = 200.0;

    /// <summary>
    /// Non-harmonic peaks (mains hum, IMD/mixing products, other spurs) reported per meters frame,
    /// strongest first, excluding the fundamental and its harmonics. Scanned from
    /// <see cref="DisplayLoFreqHz"/>, not <see cref="FundamentalMinSearchHz"/> — hum at 50/100 Hz
    /// is exactly the kind of thing this list exists to surface.
    /// </summary>
    public int OtherPeaksCount { get; init; } = 20;

    /// <summary>
    /// Whether the left/right channel is processed (FFT + meters) and broadcast at all. The
    /// device still always samples both channels — a stereo ADC can't do otherwise — this only
    /// controls DSP/broadcast, i.e. what the UI's channel checkboxes actually mean.
    /// </summary>
    public bool ProcessLeft { get; init; } = true;

    public bool ProcessRight { get; init; } = true;
}
