using System.Numerics;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp;

/// <summary>Which window slides along the impulse response, and so what the surface means.</summary>
public enum CsdWindowMode
{
    /// <summary>
    /// ARTA's window, and deliberately its shape rather than one of our own: a <b>rectangle</b>
    /// carrying a Blackman4 taper over its first and last <c>RiseTimeMs</c>, flat at one in
    /// between. The flat middle is what makes this Bunton and Small's unit step — every sample from
    /// the slice's own time to the end of the block counts at full weight, which is what a
    /// cumulative spectrum integrates — and the tapers are only there to keep each edge from being
    /// a step, whose leakage would bury the decay being looked for.
    ///
    /// <para>It was a short rise and a long cosine fall until 2026-10-24, which is a different
    /// window and reads a different surface: the sample half way along the block was already 6 dB
    /// down and the last one was zero, so the window discarded most of the tail the measurement
    /// exists to show and the direct sound fell away as it slid through the window's own shape. The
    /// decay <i>rate</i> was unaffected, since that bias is the same in every slice — which is why
    /// the simulator's bandpass still reads its 1.36 dB per millisecond — but the dynamic range and
    /// the height of every ridge against the direct sound were the window's as much as the
    /// DUT's.</para>
    /// </summary>
    Apodizing = 0,

    /// <summary>
    /// A plain Hann window, centred on the slice's time — an ordinary short-time Fourier transform.
    /// Symmetric, so each slice looks as far forward as back, which reads a resonance's build-up as
    /// well as its decay but smears the impulse itself across half a window.
    /// </summary>
    Hann = 1,
}

/// <summary>
/// How to slice an impulse response into a cumulative spectral decay.
/// </summary>
/// <param name="FftLength">
/// The transform length, in samples, and the surface's whole frequency resolution:
/// <c>fs / FftLength</c>. Power of two. This is the measurement's one real trade — a longer
/// transform resolves lower, and blurs the decay it is being used to look at over its own length.
/// </param>
/// <param name="BlockShiftSamples">Samples between one slice and the next. The surface's time step.</param>
/// <param name="BlockCount">How many slices. The surface spans <c>(BlockCount-1) blocks of BlockShiftSamples</c>.</param>
/// <param name="RiseTimeMs">
/// The apodizing window's rise, in milliseconds. Ignored by <see cref="CsdWindowMode.Hann"/>.
/// </param>
/// <param name="StartBeforePeakMs">
/// How far ahead of the impulse the first slice sits, in milliseconds. Zero — the default and what
/// every slice did until 2026-10-25 — starts the surface at the impulse itself.
///
/// <para><b>Why it is offered.</b> ARTA's time zero is the cursor, which its manual has the user set
/// some way before the peak, so the front of an ARTA surface is the window sliding <i>towards</i>
/// the arrival rather than away from it — which is why one can read flat for several milliseconds
/// where ours has already collapsed. Matching that needs the same head start, and asking for it in
/// milliseconds is the one way to say it that means the same thing at every sample rate.</para>
///
/// <para><b>The time axis goes negative rather than being re-zeroed.</b> A slice is labelled by
/// where it sits relative to the <i>impulse</i>, so a pre-roll puts the first slices at &#8722;2.5 ms
/// and the impulse still at 0. Re-zeroing on the first slice, as ARTA does, would make "3 ms" mean
/// three milliseconds after wherever somebody happened to click.</para>
/// </param>
public sealed record CsdSettings(
    int FftLength,
    int BlockShiftSamples,
    int BlockCount,
    CsdWindowMode WindowMode,
    double RiseTimeMs,
    double LowHz,
    double HighHz,
    int DisplayColumns,
    double StartBeforePeakMs = 0.0)
{
    public const int MinFftLength = 64;

    public const int MaxFftLength = 65536;

    public const int MaxBlockCount = 200;

    public const double MinRiseTimeMs = 0.02;

    public const double MaxRiseTimeMs = 1.0;

    public const int MaxDisplayColumns = 1000;

    /// <summary>
    /// The furthest ahead of the impulse a surface may start. Generous — what actually bounds it is
    /// the record in front of the impulse, which <see cref="CumulativeSpectralDecay.Compute"/>
    /// checks against rather than guessing here.
    /// </summary>
    public const double MaxStartBeforePeakMs = 50.0;

    public void Validate()
    {
        if (FftLength is < MinFftLength or > MaxFftLength || (FftLength & (FftLength - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FftLength), FftLength,
                $"The transform length must be a power of two between {MinFftLength} and {MaxFftLength}.");
        }

        if (BlockShiftSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(BlockShiftSamples), BlockShiftSamples, "The block shift must be at least one sample.");

        if (BlockCount is < 2 or > MaxBlockCount)
            throw new ArgumentOutOfRangeException(nameof(BlockCount), BlockCount, $"Between 2 and {MaxBlockCount} blocks.");

        if (WindowMode == CsdWindowMode.Apodizing && (double.IsNaN(RiseTimeMs) || RiseTimeMs is < MinRiseTimeMs or > MaxRiseTimeMs))
        {
            throw new ArgumentOutOfRangeException(
                nameof(RiseTimeMs), RiseTimeMs, $"The rise time runs from {MinRiseTimeMs} to {MaxRiseTimeMs} ms.");
        }

        if (double.IsNaN(LowHz) || LowHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(LowHz), LowHz, "The band's bottom must be above zero.");

        if (double.IsNaN(HighHz) || HighHz <= LowHz)
            throw new ArgumentOutOfRangeException(nameof(HighHz), HighHz, "The band's top must be above its bottom.");

        if (DisplayColumns is < 2 or > MaxDisplayColumns)
            throw new ArgumentOutOfRangeException(nameof(DisplayColumns), DisplayColumns, $"Between 2 and {MaxDisplayColumns} columns.");

        if (double.IsNaN(StartBeforePeakMs) || StartBeforePeakMs is < 0.0 or > MaxStartBeforePeakMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartBeforePeakMs), StartBeforePeakMs,
                $"The head start runs from 0 to {MaxStartBeforePeakMs:0.#} ms.");
        }
    }
}

/// <summary>
/// Cumulative spectral decay: the spectrum of a window slid along the impulse response, one slice
/// per step, so a resonance that keeps ringing after the impulse has gone shows up as a ridge that
/// outlives everything around it.
///
/// <para><b>It reads the sweep-gated impulse response, not the display-gated one</b>, and the
/// difference is the whole measurement. A display gate tapers the impulse to nothing over a few
/// milliseconds; run a decay analysis on that and the ridge you measure is the window's, decaying
/// on schedule at every frequency at once. The sweep gate is a different thing — it deletes the
/// harmonic packets deconvolution leaves <i>ahead</i> of the impulse, keeps half the record after
/// it, and leaves the decay alone. <c>availablePostSamples</c> is where even that runs out, and a
/// request whose slices would reach past it is refused rather than answered with the gate's own
/// edge.</para>
/// </summary>
public static class CumulativeSpectralDecay
{
    /// <summary>Levels below this are floored, so a slice of exact zeros does not serialise as -Infinity.</summary>
    public const double FloorDb = -300.0;

    /// <summary>
    /// Computes the surface. <paramref name="peakIndex"/> is the linear impulse and becomes t = 0;
    /// <paramref name="ir"/> is read <b>circularly</b>, as every consumer of a periodic sweep's
    /// impulse response must.
    /// </summary>
    /// <param name="availablePostSamples">
    /// How far past the impulse the record is real rather than gate. The surface may not reach past
    /// it — see the type's own remarks.
    /// </param>
    /// <param name="availablePreSamples">
    /// And how far <i>before</i> it, which is what bounds
    /// <see cref="CsdSettings.StartBeforePeakMs"/>: reading further back than that is reading the
    /// gate's own taper, or — on a sweep — the harmonic packets the gate exists to delete, either of
    /// which would be drawn as the DUT's own run-up. Checked rather than clamped, for the reason the
    /// post bound is.
    /// </param>
    public static DecaySurface Compute(
        ReadOnlySpan<double> ir,
        int peakIndex,
        double sampleRateHz,
        int availablePostSamples,
        CsdSettings settings,
        IFftBackend fft,
        int availablePreSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(fft);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        settings.Validate();

        int length = settings.FftLength;
        if (length > ir.Length)
            throw new ArgumentOutOfRangeException(nameof(settings), "The transform is longer than the impulse response it would slice.");

        var window = BuildWindow(settings, sampleRateHz, out int peakOffset);

        int preSamples = (int)Math.Round(settings.StartBeforePeakMs * sampleRateHz / 1000.0);
        if (preSamples > availablePreSamples)
        {
            double haveMs = availablePreSamples * 1000.0 / sampleRateHz;
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"A {settings.StartBeforePeakMs:0.##} ms head start reads before the impulse, and only {haveMs:0.##} ms of the record in front of it is real. "
                    + "Lower it, or lengthen the gate.");
        }

        // Where the last slice's window ends, relative to the impulse. Checked rather than clamped:
        // a surface that quietly stopped short would still be drawn against the axis the request
        // asked for, and its last slices would be the gate's edge rather than the DUT's decay.
        int reach = ((settings.BlockCount - 1) * settings.BlockShiftSamples) - preSamples - peakOffset + length;
        if (reach > availablePostSamples)
        {
            double haveMs = availablePostSamples * 1000.0 / sampleRateHz;
            double wantMs = reach * 1000.0 / sampleRateHz;
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"These slices would read {wantMs:0.##} ms past the impulse, and only {haveMs:0.##} ms of the record is real. "
                    + "Use fewer blocks, a smaller shift or a shorter transform — or measure at a higher resolution, which lengthens the record.");
        }

        double binHz = sampleRateHz / length;
        double lowHz = Math.Max(settings.LowHz, binHz);
        double highHz = Math.Min(settings.HighHz, sampleRateHz / 2.0);
        if (highHz <= lowHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"A {length}-point transform at {sampleRateHz:0} Hz resolves nothing below {binHz:0.#} Hz, which leaves no band to draw.");
        }

        var freqHz = LogFrequencyGrid.Columns(lowHz, highHz, settings.DisplayColumns);

        var block = new double[length];
        var spectrum = new Complex[(length / 2) + 1];
        var magnitude = new double[spectrum.Length];

        var slices = new List<(double TimeMs, float[] Magnitude)>(settings.BlockCount);
        double surfacePeak = 0.0;

        for (int j = 0; j < settings.BlockCount; j++)
        {
            int start = peakIndex - preSamples + (j * settings.BlockShiftSamples) - peakOffset;
            for (int i = 0; i < length; i++)
            {
                int source = (((start + i) % ir.Length) + ir.Length) % ir.Length;
                block[i] = ir[source] * window[i];
            }

            fft.Forward(block, spectrum);
            for (int k = 0; k < spectrum.Length; k++)
                magnitude[k] = spectrum[k].Magnitude;

            var columns = new float[settings.DisplayColumns];
            LogFrequencyGrid.ReduceMax(magnitude, binHz, lowHz, highHz, columns);

            foreach (float value in columns)
                surfacePeak = Math.Max(surfacePeak, value);

            // Labelled against the impulse, so a head start reads as negative time rather than
            // re-zeroing the axis on wherever the surface happens to begin.
            slices.Add((((j * settings.BlockShiftSamples) - preSamples) * 1000.0 / sampleRateHz, columns));
        }

        // One reference for the whole surface, deliberately: the front slice keeps the DUT's
        // frequency response, so a resonance reads as a ridge that is both tall and slow rather
        // than as one more curve at 0 dB like every other frequency.
        double reference = Math.Max(surfacePeak, 1e-30);

        var result = new List<DecaySlice>(slices.Count);
        foreach (var (timeMs, columns) in slices)
        {
            var levels = new float[columns.Length];
            for (int c = 0; c < columns.Length; c++)
                levels[c] = (float)Math.Max(20.0 * Math.Log10(Math.Max(columns[c], 1e-30) / reference), FloorDb);

            result.Add(new DecaySlice(timeMs, 0, levels));
        }

        return new DecaySurface(DecayAxis.Milliseconds, freqHz, result, binHz);
    }

    /// <summary>
    /// The sliding window, and where the sample a slice is labelled with sits inside it.
    /// <see cref="CsdWindowMode.Apodizing"/> labels a slice by the <b>first sample it weights
    /// fully</b> — the lower limit of the integral, everything after it counting too — while
    /// <see cref="CsdWindowMode.Hann"/> labels one by the sample it weights most, its centre. So
    /// "3 ms" is the spectrum of the response from 3 ms after the impulse in the one, and of the
    /// response around 3 ms after it in the other.
    /// </summary>
    public static double[] BuildWindow(CsdSettings settings, double sampleRateHz, out int peakOffset)
    {
        int length = settings.FftLength;
        var window = new double[length];

        if (settings.WindowMode == CsdWindowMode.Hann)
        {
            Window.Get(WindowType.Hann, length).Values.CopyTo(window);
            peakOffset = length / 2;
            return window;
        }

        // A rectangle with a Blackman4 taper over each end, which is ARTA's apodizing window to the
        // letter. At least one sample of taper and at most half the block each side: at exactly
        // half there is no flat part left and the window is a plain Blackman-Harris, which is the
        // sensible end of the range rather than a special case.
        int rise = Math.Clamp((int)Math.Round(settings.RiseTimeMs * sampleRateHz / 1000.0), 1, length / 2);

        for (int i = 0; i < length; i++)
            window[i] = 1.0;

        // The rising half of a (2*rise + 1)-point symmetric Blackman-Harris, so it reaches exactly
        // one at the first flat sample; mirrored onto the last `rise` samples. The two ranges
        // cannot overlap, since rise is at most half the block.
        for (int i = 0; i < rise; i++)
        {
            double taper = Blackman4At(i / (2.0 * rise));
            window[i] = taper;
            window[length - 1 - i] = taper;
        }

        peakOffset = rise;
        return window;
    }

    /// <summary>
    /// The minimum four-term Blackman-Harris at <paramref name="x"/> over [0, 1] — ARTA's
    /// "Blackman4", the taper its apodizing window applies to each end of the rectangle. Exactly
    /// 1.0 at the middle, and 6e-5 rather than zero at the ends, which is this window's own
    /// definition and not a rounding error.
    /// </summary>
    private static double Blackman4At(double x)
    {
        const double A0 = 0.35875, A1 = 0.48829, A2 = 0.14128, A3 = 0.01168;
        double t = 2.0 * Math.PI * x;
        return A0 - (A1 * Math.Cos(t)) + (A2 * Math.Cos(2.0 * t)) - (A3 * Math.Cos(3.0 * t));
    }
}
