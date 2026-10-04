using System.Numerics;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp;

/// <summary>Which window slides along the impulse response, and so what the surface means.</summary>
public enum CsdWindowMode
{
    /// <summary>
    /// A short rise and a long fall — the classical cumulative-spectral-decay window. The rise is
    /// what keeps the leading edge from being a step (a step transforms into a wall of leakage that
    /// buries the very decay being looked for); the long fall is what stops the far end of the
    /// block ringing. Its peak sits at the slice's own time, so the front slice reads the impulse at
    /// full weight.
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
public sealed record CsdSettings(
    int FftLength,
    int BlockShiftSamples,
    int BlockCount,
    CsdWindowMode WindowMode,
    double RiseTimeMs,
    double LowHz,
    double HighHz,
    int DisplayColumns)
{
    public const int MinFftLength = 64;

    public const int MaxFftLength = 65536;

    public const int MaxBlockCount = 200;

    public const double MinRiseTimeMs = 0.02;

    public const double MaxRiseTimeMs = 1.0;

    public const int MaxDisplayColumns = 1000;

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
    public static DecaySurface Compute(
        ReadOnlySpan<double> ir,
        int peakIndex,
        double sampleRateHz,
        int availablePostSamples,
        CsdSettings settings,
        IFftBackend fft)
    {
        ArgumentNullException.ThrowIfNull(fft);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        settings.Validate();

        int length = settings.FftLength;
        if (length > ir.Length)
            throw new ArgumentOutOfRangeException(nameof(settings), "The transform is longer than the impulse response it would slice.");

        var window = BuildWindow(settings, sampleRateHz, out int peakOffset);

        // Where the last slice's window ends, relative to the impulse. Checked rather than clamped:
        // a surface that quietly stopped short would still be drawn against the axis the request
        // asked for, and its last slices would be the gate's edge rather than the DUT's decay.
        int reach = ((settings.BlockCount - 1) * settings.BlockShiftSamples) - peakOffset + length;
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
            int start = peakIndex + (j * settings.BlockShiftSamples) - peakOffset;
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

            slices.Add((j * settings.BlockShiftSamples * 1000.0 / sampleRateHz, columns));
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
    /// The sliding window, and where its peak sits inside it — which is what a slice's time label
    /// means. Both modes label a slice by the sample its window weights most, so "3 ms" is the
    /// spectrum of the response 3 ms after the impulse in either of them.
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

        // Rise then fall, both raised cosines, meeting at 1.0. At least one sample of rise and at
        // most half the block: a rise longer than the fall would be a window that looks further
        // backwards than forwards, which is the opposite of what a decay analysis is for.
        int rise = Math.Clamp((int)Math.Round(settings.RiseTimeMs * sampleRateHz / 1000.0), 1, length / 2);

        for (int i = 0; i < rise; i++)
            window[i] = 0.5 * (1.0 - Math.Cos(Math.PI * i / rise));

        int fall = length - rise;
        for (int i = rise; i < length; i++)
            window[i] = 0.5 * (1.0 + Math.Cos(Math.PI * (i - rise) / fall));

        peakOffset = rise;
        return window;
    }
}
