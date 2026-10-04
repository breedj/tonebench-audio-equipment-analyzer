using System.Numerics;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp;

/// <summary>
/// One harmonic order's distortion, plotted against the <b>fundamental</b> frequency that produced
/// it.
/// </summary>
/// <param name="Order">k, i.e. 2 for the second harmonic.</param>
/// <param name="FundamentalHz">Ascending. Runs from the sweep's start to <see cref="UpperValidHz"/>.</param>
/// <param name="LevelDbc">The harmonic relative to the linear response at the same fundamental, dB.</param>
/// <param name="UpperValidHz">
/// Where this curve has to stop, and it is a different frequency for every order. The deconvolution
/// divides by the stimulus, which has no energy above the sweep's end f2, so the k-th harmonic can
/// only be observed for output frequencies up to f2 — that is fundamentals up to f2/k. The fifth
/// harmonic curve therefore ends at a fifth of the sweep's top, not at the top.
/// </param>
public sealed record HarmonicPacket(
    int Order,
    double[] FundamentalHz,
    double[] LevelDbc,
    double UpperValidHz);

/// <summary>
/// Pulls the DUT's harmonic distortion out of a swept-sine deconvolution, as a curve per order
/// against fundamental frequency.
///
/// <para><b>Where the packets are.</b> With <c>f(t) = f1·e^{Lt/T}</c> and <c>L = ln(f2/f1)</c>, the
/// sweep reaches frequency f at <c>t(f) = (T/L)·ln(f/f1)</c>. Energy emerging at output frequency v
/// as the k-th harmonic was generated when the <i>input</i> was at v/k, i.e. at <c>t(v/k)</c>, while
/// the deconvolution assigns output frequency v to <c>t(v)</c>. So it lands at
/// <c>t(v/k) - t(v) = -(T/L)·ln k</c> — <b>independent of v</b>, which is exactly why it collapses
/// into one compact packet instead of smearing, and negative, which over a circular record means
/// index <c>(peak - N·ln(k)/L) mod N</c>. The same formula <see cref="ImpulseGate.ForSweep"/> uses
/// to decide what to throw away.</para>
///
/// <para><b>What the packet's own spectrum means.</b> Stationary phase gives
/// <c>|X(v)|^2 = A^2·T/(2Lv)</c> for the stimulus and <c>|Y_k(v)|^2 = a_k(v/k)^2·T/(2Lv)</c> for the
/// k-th harmonic — the <i>same</i> exponential rate, so both <c>T/(2Lv)</c> factors cancel and
/// <c>|P_k(v)| = a_k(v/k)/A</c>. In words: <b>the packet spectrum evaluated at v reports the
/// harmonic amplitude produced by a fundamental at v/k, referred to the drive.</b> Relabelling the
/// packet's frequency axis <c>f -> f/k</c> is therefore the whole mapping — and the denominator of
/// the dBc figure is the linear response at <c>f_fund</c>, <b>not</b> at <c>k·f_fund</c>. Getting
/// that backwards is a shape error on any DUT that is not flat rather than an offset, which is why
/// <c>HarmonicPacketExtractorTests</c> pins it with a DUT that distorts over only part of the
/// band — on a flat one the two conventions differ only by a shift nobody notices.</para>
///
/// <para>Pure; nothing here touches a device. The supplied <see cref="IFftBackend"/> is used at
/// several transform lengths and is not thread-safe, so give it one of its own rather than sharing
/// the pipeline's.</para>
/// </summary>
public sealed class HarmonicPacketExtractor
{
    /// <summary>
    /// Smallest packet window. Below this the resolution on the fundamental axis
    /// (<c>fs/(W·k)</c>) is too coarse to be worth plotting.
    /// </summary>
    public const int MinPacketWindow = 1024;

    /// <summary>
    /// Fraction of the sample rate above which the analyzer's own anti-alias roll-off, rather than
    /// the DUT, is what a curve would be reporting.
    /// </summary>
    /// <remarks>
    /// A placeholder for a measured figure: drive a tone towards Nyquist through a loopback and read
    /// where the response departs. Deliberately conservative until then.
    /// </remarks>
    public const double AntiAliasNyquistFraction = 0.45;

    private readonly int _fftSize;
    private readonly double _sampleRateHz;
    private readonly double _chirpStartHz;
    private readonly double _chirpEndHz;
    private readonly double _sweepRate;
    private readonly IFftBackend _fft;

    public HarmonicPacketExtractor(
        int fftSize, double sampleRateHz, double chirpStartHz, double chirpEndHz, IFftBackend fft)
    {
        if (fftSize < 4 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two of at least 4.", nameof(fftSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chirpStartHz);
        if (chirpEndHz <= chirpStartHz)
            throw new ArgumentOutOfRangeException(nameof(chirpEndHz), "End frequency must exceed the start frequency.");

        _fftSize = fftSize;
        _sampleRateHz = sampleRateHz;
        _chirpStartHz = chirpStartHz;
        _chirpEndHz = chirpEndHz;
        _sweepRate = Math.Log(chirpEndHz / chirpStartHz);
        _fft = fft ?? throw new ArgumentNullException(nameof(fft));
    }

    /// <summary>Samples the k-th packet sits <i>ahead</i> of the linear impulse: <c>N·ln(k)/L</c>.</summary>
    public double PacketOffsetSamples(int order)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, 1);
        return _fftSize * Math.Log(order) / _sweepRate;
    }

    /// <summary>
    /// Half the smaller of the k-th packet's two neighbour gaps — how far either side of it the
    /// record can be read before another order's packet is dragged in. The packets crowd together as
    /// k rises (their spacing goes as <c>ln k</c>), so this shrinks with order and the window with it.
    /// </summary>
    public int PacketGuardSamples(int order)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, 2);

        double previous = PacketOffsetSamples(order - 1);
        double here = PacketOffsetSamples(order);
        double next = PacketOffsetSamples(order + 1);

        return (int)(Math.Min(here - previous, next - here) / 2.0);
    }

    /// <summary>The transform length used for the k-th packet: the largest power of two fitting inside its guard.</summary>
    public int PacketWindowLength(int order)
    {
        int span = 2 * PacketGuardSamples(order);
        int window = MinPacketWindow;
        while (window * 2 <= span)
            window *= 2;
        return Math.Min(window, _fftSize);
    }

    /// <summary>The highest fundamental at which the k-th harmonic can be observed.</summary>
    public double UpperValidHz(int order) =>
        Math.Min(_chirpEndHz, AntiAliasNyquistFraction * _sampleRateHz) / order;

    /// <param name="ungatedImpulse">
    /// From <see cref="FrequencyResponseEstimator.CompleteImpulse"/>'s <c>Ungated</c> array. The
    /// gated one will not do — gating is precisely what deletes these packets.
    /// </param>
    /// <param name="peakIndex">Where the linear impulse sits in that array.</param>
    /// <param name="linearMagnitude">
    /// The gated estimate's own <c>|H|</c> per bin, on the analysis grid (<c>fs/N</c>) — the
    /// denominator of the dBc figure.
    /// </param>
    public IReadOnlyList<HarmonicPacket> Extract(
        ReadOnlySpan<double> ungatedImpulse,
        int peakIndex,
        ReadOnlySpan<double> linearMagnitude,
        int maxOrder = 5,
        WindowType window = WindowType.Hann)
    {
        if (ungatedImpulse.Length != _fftSize)
            throw new ArgumentException($"Impulse response must be {_fftSize} samples.", nameof(ungatedImpulse));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOrder, 2);

        var packets = new List<HarmonicPacket>(maxOrder - 1);

        for (int order = 2; order <= maxOrder; order++)
        {
            double upperValidHz = UpperValidHz(order);
            if (upperValidHz <= _chirpStartHz)
                continue;

            packets.Add(ExtractOne(ungatedImpulse, peakIndex, linearMagnitude, order, window, upperValidHz));
        }

        return packets;
    }

    private HarmonicPacket ExtractOne(
        ReadOnlySpan<double> ungatedImpulse,
        int peakIndex,
        ReadOnlySpan<double> linearMagnitude,
        int order,
        WindowType windowType,
        double upperValidHz)
    {
        int length = PacketWindowLength(order);
        int centre = peakIndex - (int)Math.Round(PacketOffsetSamples(order));

        // The packet sits at the window's own centre, where every taper is unity, and it is compact
        // relative to that window by construction. So the taper only suppresses what leaks in at the
        // edges, and dividing by the window's coherent gain on top of it would put a flat 6 dB error
        // (for Hann) across the whole curve rather than correcting anything. Pinned by
        // Extract_OfACubicDut_ReadsTheAmplitudeTheAlgebraPredicts, which knows the right answer in
        // closed form.
        var taper = Window.Get(windowType, length).Values;
        var scratch = new double[length];
        for (int j = 0; j < length; j++)
        {
            int source = (((centre - (length / 2) + j) % _fftSize) + _fftSize) % _fftSize;
            scratch[j] = ungatedImpulse[source] * taper[j];
        }

        var spectrum = new Complex[(length / 2) + 1];
        _fft.Forward(scratch, spectrum);

        double packetBinHz = _sampleRateHz / length;
        double analysisBinHz = _sampleRateHz / _fftSize;

        int firstBin = (int)Math.Ceiling(order * _chirpStartHz / packetBinHz);
        int lastBin = Math.Min((int)Math.Floor(order * upperValidHz / packetBinHz), spectrum.Length - 1);

        int count = Math.Max(0, lastBin - firstBin + 1);
        var fundamentalHz = new double[count];
        var levelDbc = new double[count];

        for (int i = 0; i < count; i++)
        {
            int bin = firstBin + i;
            double fundamental = bin * packetBinHz / order;

            fundamentalHz[i] = fundamental;
            levelDbc[i] = FrequencyResponseEstimator.ToDb(
                spectrum[bin].Magnitude
                / Math.Max(Interpolate(linearMagnitude, fundamental / analysisBinHz), 1e-30));
        }

        return new HarmonicPacket(order, fundamentalHz, levelDbc, upperValidHz);
    }

    /// <summary>
    /// Linear interpolation onto a fractional bin. The packet's own grid is <c>fs/W</c> and the
    /// linear response's is <c>fs/N</c>, so the two never line up and a nearest-bin read would put a
    /// staircase on the dBc curve.
    /// </summary>
    private static double Interpolate(ReadOnlySpan<double> values, double bin)
    {
        if (bin <= 0.0)
            return values[0];
        if (bin >= values.Length - 1)
            return values[^1];

        int low = (int)bin;
        double fraction = bin - low;
        return (values[low] * (1.0 - fraction)) + (values[low + 1] * fraction);
    }
}
