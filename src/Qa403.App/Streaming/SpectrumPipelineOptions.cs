using Qa403.Dsp;

namespace Qa403.App.Streaming;

/// <summary>Live-reconfigurable knobs for <see cref="SpectrumPipeline"/>. Reassign <see cref="SpectrumPipeline.Options"/> to change these mid-stream.</summary>
public sealed record SpectrumPipelineOptions
{
    /// <summary>Power-of-two FFT length. Larger values raise frequency resolution and per-tick CPU cost, not frame rate — see <see cref="TargetFrameRateHz"/>.</summary>
    public int FftSize { get; init; } = 65536;

    public WindowType Window { get; init; } = WindowType.Hann;

    public AveragingMode Averaging { get; init; } = AveragingMode.Linear;

    /// <summary>Spectra averaged together (linear) or the effective time constant (exponential, alpha = 2/(count+1)). Ignored when <see cref="Averaging"/> is <see cref="AveragingMode.Off"/>.</summary>
    public int AverageCount { get; init; } = 8;

    /// <summary>
    /// When true, each channel stops updating once it reaches <see cref="AverageCount"/> averages
    /// (or, with <see cref="Averaging"/> set to <see cref="AveragingMode.Off"/>, after its first
    /// captured spectrum) — the pipeline holds that final frame instead of continuing to reprocess
    /// new samples, and flags the completing meters frame with <see cref="StreamFlags.Held"/>.
    /// Mirrors a bench analyzer's "Single" sweep button: start, capture settles, then stops on its
    /// own instead of free-running.
    ///
    /// Not a persisted UI setting — it is turned on only by the "Single" action (POST
    /// /api/acquisition/spectrum-capture, which also calls <see cref="SpectrumPipeline.Flush"/> to
    /// arm a fresh capture) and turned back off by the next plain Run (POST
    /// /api/acquisition/start), so a normal continuous run is never left holding from a previous
    /// Single press. Off by default (continuous/free-run).
    /// </summary>
    public bool SingleShot { get; init; } = false;

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

    /// <summary>
    /// Harmonics reported per meters frame, order 1 (fundamental) through this order — 12 gives
    /// fundamental + H2..H12, matching QuantAsylum's own convention (their THD sums H2..H12; see
    /// reference/previous version/QA40x_AUDIO_ANALYSER/frmThdFrequency.cs).
    /// </summary>
    public int HarmonicCount { get; init; } = 12;

    /// <summary>
    /// Lower edge of the measurement bandwidth for THD+N, SNR and the noise floor, in Hz.
    ///
    /// Not a display setting — this is what those figures are integrated over, and quoting any of
    /// them without a bandwidth is meaningless. The default is the standard 20 Hz - 20 kHz audio
    /// band. Critically it also keeps DC out: on a DC-coupled input a few mV of offset lives in
    /// bin 0, and integrating it reported THD+N ~33 dB worse than reality.
    /// </summary>
    public double MeasurementLoFreqHz { get; init; } = 20.0;

    /// <summary>
    /// Upper edge of the measurement bandwidth for THD+N, SNR and the noise floor, in Hz. Clamped
    /// to Nyquist. Also bounds which harmonics count toward THD, matching QuantAsylum's
    /// <c>/ThdDb/{freq}/{stopHz}</c>.
    /// </summary>
    public double MeasurementHiFreqHz { get; init; } = 20000.0;

    /// <summary>
    /// Half-width, in octaves, of the notch taken out around the fundamental before integrating
    /// THD+N/SNR — see <see cref="SpectrumMetrics.DefaultNotchOctaves"/>. 0.5 matches QuantAsylum's
    /// PyQa40x. Lower it only if you need to resolve noise very close to the carrier.
    /// </summary>
    public double NotchOctaves { get; init; } = SpectrumMetrics.DefaultNotchOctaves;

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

    // Only the left channel is on out of the box — matching the generator's own left-only default
    // (see MeasurementSaveService.BuildParameters) so a fresh measurement has one clean channel to
    // look at rather than an unused right channel cluttering the chart.
    public bool ProcessRight { get; init; } = false;
}
