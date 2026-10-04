namespace Tonebench.Dsp;

/// <summary>The peak of a response trace: its highest sample, wherever that lands.</summary>
public readonly record struct ResponsePeak(int Bin, double FreqHz, double LevelDb);

/// <summary>
/// What a bandwidth figure is measured down from.
///
/// <para><see cref="Peak"/> is the trace's own maximum — the classic convention, and the right one
/// for a DUT whose response <i>is</i> a peak: a bandpass filter, a driver's motional impedance, a
/// tweeter.</para>
///
/// <para><see cref="BandMedian"/> is the median level over a stated reference band, which is how
/// an amplifier's response is actually specified ("20 Hz-20 kHz, ref 1 kHz"). Use it for anything
/// with a passband. See <see cref="BandwidthReferenceSpec"/> for why the peak fails there.</para>
/// </summary>
public enum BandwidthReferenceMode
{
    Peak = 0,
    BandMedian = 1,
}

/// <summary>
/// Which reference a bandwidth search should use, and — for
/// <see cref="BandwidthReferenceMode.BandMedian"/> — the band it is taken over.
///
/// <para><b>Why a passband reference exists at all.</b> Referencing the peak assumes the peak is in
/// the passband. An output transformer's leakage inductance resonating against its winding
/// capacitance breaks that assumption on every real amplifier: the resonance sits above the audio
/// band and is routinely several dB above the passband, so <c>peak - 1 dB</c> is a threshold the
/// whole passband is already below. The outward walk then steps off the resonance immediately and
/// both corners land on its own skirts — measured against the simulator's tube-amp model (a +4 dB
/// Q 3.5 peak at 38 kHz), the -1 dB "bandwidth" read 37.2-38.8 kHz. That is a correct measurement
/// of the resonance and says nothing whatever about the amplifier.</para>
///
/// <para><b>Median, not mean.</b> Same reason <c>MedianGainDb</c> uses one for the output-referred
/// level probe: a mean in dB is dragged down by any null in the reference band and a mean of
/// magnitudes is dragged up by any resonance in it, while a median ignores both unless they occupy
/// half the band.</para>
/// </summary>
/// <param name="LowHz">Bottom of the reference band. Ignored by <see cref="BandwidthReferenceMode.Peak"/>.</param>
/// <param name="HighHz">Top of the reference band. Ignored by <see cref="BandwidthReferenceMode.Peak"/>.</param>
public readonly record struct BandwidthReferenceSpec(
    BandwidthReferenceMode Mode,
    double LowHz,
    double HighHz)
{
    /// <summary>
    /// Bottom of the default reference band. Above the region where a transformer-coupled DUT's low
    /// end is already rolling off, so the reference is not dragged down by the very roll-off it is
    /// there to measure.
    /// </summary>
    public const double DefaultLowHz = 200.0;

    /// <summary>
    /// Top of the default reference band. A decade wide and centred (geometrically) near 630 Hz,
    /// which keeps 1 kHz — the frequency every datasheet references — well inside it while staying
    /// clear of the cone breakup and crossover region a loudspeaker puts above 2 kHz.
    /// </summary>
    public const double DefaultHighHz = 2000.0;

    /// <summary>The trace's own maximum, which is what this analyzer did before a reference band existed.</summary>
    public static readonly BandwidthReferenceSpec Peak = new(BandwidthReferenceMode.Peak, DefaultLowHz, DefaultHighHz);

    /// <summary>The default passband reference: the median over 200 Hz - 2 kHz.</summary>
    public static readonly BandwidthReferenceSpec DefaultBand =
        new(BandwidthReferenceMode.BandMedian, DefaultLowHz, DefaultHighHz);

    public static BandwidthReferenceSpec Band(double lowHz, double highHz) =>
        new(BandwidthReferenceMode.BandMedian, lowHz, highHz);

    /// <summary>Lowest reference-band edge a caller may ask for. Below the audio band, but not at DC, which has no logarithm.</summary>
    public const double MinHz = 1.0;

    /// <summary>Highest reference-band edge a caller may ask for: Nyquist at the fastest rate the QA403 offers.</summary>
    public const double MaxHz = 96_000.0;

    /// <summary>
    /// Rejects a band no median can be taken over. Lives here rather than on each request record so
    /// the chirp and the stepped sweep cannot come to disagree about what a legal reference band is —
    /// they show the same two boxes and quote the same numbers back.
    ///
    /// <para>A <see cref="BandwidthReferenceMode.Peak"/> spec is not checked: its edges are carried
    /// but never read, and rejecting an untouched default would block a bandpass measurement over a
    /// setting it does not use.</para>
    /// </summary>
    public void Validate()
    {
        if (Mode is not (BandwidthReferenceMode.Peak or BandwidthReferenceMode.BandMedian))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Mode), Mode, "A bandwidth reference is either the trace's peak or the median of a band.");
        }

        if (Mode == BandwidthReferenceMode.Peak)
            return;

        if (double.IsNaN(LowHz) || LowHz < MinHz || LowHz > MaxHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LowHz), LowHz, $"The reference band must start between {MinHz} and {MaxHz} Hz.");
        }

        if (double.IsNaN(HighHz) || HighHz < MinHz || HighHz > MaxHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HighHz), HighHz, $"The reference band must end between {MinHz} and {MaxHz} Hz.");
        }

        if (HighHz <= LowHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HighHz), HighHz, "The reference band must end above where it starts.");
        }
    }
}

/// <summary>
/// The level every threshold in one bandwidth search is measured from, and the sample the outward
/// walk starts at.
/// </summary>
/// <param name="Index">
/// Where the search starts. For <see cref="BandwidthReferenceMode.Peak"/> that is the peak bin; for
/// a band median it is the sample nearest the reference band's <b>geometric</b> centre — the axis is
/// logarithmic, so the arithmetic centre of 200-2000 Hz would start the walk at 1100 Hz rather than
/// at the 632 Hz that sits half a decade from each edge.
/// </param>
/// <param name="FellBackToPeak">
/// The requested reference band held no samples inside the searched range, so this is the peak
/// instead. Worth surfacing rather than silently substituting: a 20 kHz-24 kHz measurement asked to
/// reference 200 Hz-2 kHz has not measured the band it is quoting against, and a number that does
/// not say so is the failure this whole type exists to prevent.
/// </param>
public readonly record struct ResponseReference(
    int Index,
    double FreqHz,
    double LevelDb,
    BandwidthReferenceMode Mode,
    bool FellBackToPeak);

/// <summary>
/// One bandwidth measurement. <see cref="LowerClamped"/>/<see cref="UpperClamped"/> mean the trace
/// never left the tolerance on that side within the searched band, so the corresponding
/// frequency is the edge of the search rather than a measured corner — the difference between
/// "the response rolls off at 63 kHz" and "it was still flat where we stopped looking", which a
/// bare number cannot express.
/// </summary>
/// <param name="Symmetric">
/// The band is where the trace stayed within <b>±</b><see cref="DropDb"/> of
/// <see cref="ReferenceDb"/>, not merely above <c>reference - drop</c>. See
/// <see cref="BandwidthAnalyzer"/> for when each is the right question.
/// </param>
/// <param name="ReferenceDb">
/// The level the thresholds were measured from, so a reader (or a chart) can draw them without
/// having to know which reference mode produced them.
/// </param>
public readonly record struct BandwidthResult(
    double DropDb,
    bool Symmetric,
    double ReferenceDb,
    double LowerHz,
    double UpperHz,
    double BandwidthHz,
    bool LowerClamped,
    bool UpperClamped);

/// <summary>
/// Finds the corners of a frequency-response trace: the band over which it stays within a stated
/// tolerance of a stated reference.
///
/// <para><b>First crossing outward from the reference, not nearest value anywhere.</b> The reference
/// WinForms app searched every bin below the peak for whichever came closest in level to
/// <c>peak · 10^(dB/20)</c> (<c>frmFrequencyResponse.cs:1262-1320</c>). On a rippled response that
/// picks whichever ripple happens to sit nearest the threshold, which can be an octave away from
/// the actual roll-off and jumps around between runs. Walking outward and stopping at the first
/// crossing is both stable and what a reader would point at on the plot.</para>
///
/// <para><b>The reference is not necessarily the peak</b> — see <see cref="BandwidthReferenceSpec"/>,
/// which carries the measurement that made this configurable. Note the walk starts at
/// <see cref="ResponseReference.Index"/> rather than at the peak bin, and that this is half the fix:
/// a correct reference level searched outward from a peak that sits on an out-of-band resonance
/// still terminates on that resonance's skirts on the very first step.</para>
///
/// <para><b>Symmetric versus one-sided, and why the two readings differ.</b> A one-sided search asks
/// where the response has fallen <i>below</i> the reference, which is the right question for a
/// roll-off figure and the reason −3 dB is quoted that way. It is the wrong question for a flatness
/// figure: against a passband reference, a downward-only −1 dB search walks straight up through a
/// +4.5 dB resonance without stopping and reports a corner on its far side — true, and thoroughly
/// misleading. A ±1 dB search stops at the resonance's rising edge, which is what "within 1 dB"
/// means. Under <see cref="BandwidthReferenceMode.Peak"/> the two are identical by construction,
/// since nothing in the trace exceeds its own maximum.</para>
///
/// <para>The crossing is then interpolated <b>in log frequency</b> between the two bracketing
/// samples, because the axis is logarithmic and the roll-off is close to straight in that space.
/// This also removes the reference app's off-by-one, where the lower corner was reported as
/// <c>(i+1)·df</c> and the upper as <c>i·df</c>. Interpolating in log frequency is also what lets
/// the same code serve a log-spaced stepped sweep unchanged — see the frequency-array overloads.</para>
///
/// <para>Run this on the <b>smoothed</b> trace. On raw bins a single noisy bin at −3.1 dB ends the
/// search early, and the reported bandwidth then tracks the noise floor rather than the DUT.
/// (A stepped sine has nothing to smooth: every sample is already an averaged measurement of one
/// tone rather than one noisy bin.)</para>
/// </summary>
public static class BandwidthAnalyzer
{
    /// <summary>
    /// The highest bin of <paramref name="traceDb"/> within <c>[firstBin, lastBin]</c>. DC is
    /// excluded by <paramref name="firstBin"/> defaulting to 1 — it carries the DUT's offset, not
    /// its response, and is routinely the largest value in the array.
    /// </summary>
    public static ResponsePeak FindPeak(
        ReadOnlySpan<double> traceDb,
        double binHz,
        int firstBin = 1,
        int lastBin = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        return FindPeakCore(traceDb, FrequencyAxis.Uniform(binHz), firstBin, lastBin);
    }

    /// <summary>
    /// The highest sample of <paramref name="traceDb"/>, where <paramref name="freqHz"/> gives each
    /// sample's own frequency rather than a constant bin spacing — a stepped sweep's log-spaced
    /// grid.
    ///
    /// <para><paramref name="firstIndex"/> defaults to 0, not 1: there is no DC sample to skip here.
    /// Every entry is a tone that was actually played.</para>
    /// </summary>
    public static ResponsePeak FindPeak(
        ReadOnlySpan<double> traceDb,
        ReadOnlySpan<double> freqHz,
        int firstIndex = 0,
        int lastIndex = int.MaxValue)
    {
        RequireMatchingAxis(traceDb, freqHz);
        return FindPeakCore(traceDb, FrequencyAxis.Explicit(freqHz), firstIndex, lastIndex);
    }

    /// <summary>
    /// The level <paramref name="spec"/> asks for, ready to be handed to every <c>Find</c> call for
    /// the same trace.
    ///
    /// <para>Separate from <c>Find</c> so a trace's two readings share one reference: computing it
    /// per reading would sort the band twice for an answer that cannot differ, and — worse — would
    /// let a future change give the ±1 dB and −3 dB rows different references without anything
    /// saying so.</para>
    /// </summary>
    public static ResponseReference FindReference(
        ReadOnlySpan<double> traceDb,
        double binHz,
        BandwidthReferenceSpec spec,
        int firstBin = 1,
        int lastBin = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        return FindReferenceCore(traceDb, FrequencyAxis.Uniform(binHz), spec, firstBin, lastBin);
    }

    /// <summary>
    /// As <see cref="FindReference(ReadOnlySpan{double}, double, BandwidthReferenceSpec, int, int)"/>,
    /// for a trace whose samples carry their own frequencies.
    /// </summary>
    public static ResponseReference FindReference(
        ReadOnlySpan<double> traceDb,
        ReadOnlySpan<double> freqHz,
        BandwidthReferenceSpec spec,
        int firstIndex = 0,
        int lastIndex = int.MaxValue)
    {
        RequireMatchingAxis(traceDb, freqHz);
        return FindReferenceCore(traceDb, FrequencyAxis.Explicit(freqHz), spec, firstIndex, lastIndex);
    }

    /// <summary>
    /// The band over which <paramref name="traceDb"/> stays within <paramref name="dropDb"/> of its
    /// peak. <paramref name="dropDb"/> is positive (pass 3 for the −3 dB bandwidth).
    /// </summary>
    public static BandwidthResult Find(
        ReadOnlySpan<double> traceDb,
        double binHz,
        double dropDb,
        int firstBin = 1,
        int lastBin = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropDb);

        var axis = FrequencyAxis.Uniform(binHz);
        var reference = FindReferenceCore(traceDb, axis, BandwidthReferenceSpec.Peak, firstBin, lastBin);
        return FindCore(traceDb, axis, dropDb, reference, symmetric: false, firstBin, lastBin);
    }

    /// <summary>
    /// As <see cref="Find(ReadOnlySpan{double}, double, double, int, int)"/>, for a trace whose
    /// samples carry their own frequencies instead of sitting on a uniform grid.
    /// </summary>
    public static BandwidthResult Find(
        ReadOnlySpan<double> traceDb,
        ReadOnlySpan<double> freqHz,
        double dropDb,
        int firstIndex = 0,
        int lastIndex = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropDb);
        RequireMatchingAxis(traceDb, freqHz);

        var axis = FrequencyAxis.Explicit(freqHz);
        var reference = FindReferenceCore(traceDb, axis, BandwidthReferenceSpec.Peak, firstIndex, lastIndex);
        return FindCore(traceDb, axis, dropDb, reference, symmetric: false, firstIndex, lastIndex);
    }

    /// <summary>
    /// The band around <paramref name="reference"/> over which <paramref name="traceDb"/> stays
    /// within <paramref name="dropDb"/> of it — below only, or on both sides when
    /// <paramref name="symmetric"/>.
    /// </summary>
    public static BandwidthResult Find(
        ReadOnlySpan<double> traceDb,
        double binHz,
        double dropDb,
        ResponseReference reference,
        bool symmetric,
        int firstBin = 1,
        int lastBin = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropDb);

        return FindCore(traceDb, FrequencyAxis.Uniform(binHz), dropDb, reference, symmetric, firstBin, lastBin);
    }

    /// <summary>
    /// As <see cref="Find(ReadOnlySpan{double}, double, double, ResponseReference, bool, int, int)"/>,
    /// for a trace whose samples carry their own frequencies.
    /// </summary>
    public static BandwidthResult Find(
        ReadOnlySpan<double> traceDb,
        ReadOnlySpan<double> freqHz,
        double dropDb,
        ResponseReference reference,
        bool symmetric,
        int firstIndex = 0,
        int lastIndex = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropDb);
        RequireMatchingAxis(traceDb, freqHz);

        return FindCore(traceDb, FrequencyAxis.Explicit(freqHz), dropDb, reference, symmetric, firstIndex, lastIndex);
    }

    private static ResponsePeak FindPeakCore(
        ReadOnlySpan<double> traceDb,
        scoped FrequencyAxis axis,
        int firstIndex,
        int lastIndex)
    {
        (int first, int last) = ClampRange(traceDb.Length, firstIndex, lastIndex);

        int peakIndex = first;
        double peakDb = double.NegativeInfinity;
        for (int i = first; i <= last; i++)
        {
            if (traceDb[i] > peakDb)
            {
                peakDb = traceDb[i];
                peakIndex = i;
            }
        }

        return new ResponsePeak(peakIndex, axis.At(peakIndex), peakDb);
    }

    private static ResponseReference FindReferenceCore(
        ReadOnlySpan<double> traceDb,
        scoped FrequencyAxis axis,
        BandwidthReferenceSpec spec,
        int firstIndex,
        int lastIndex)
    {
        (int first, int last) = ClampRange(traceDb.Length, firstIndex, lastIndex);

        if (spec.Mode == BandwidthReferenceMode.BandMedian)
        {
            (int bandFirst, int bandLast) = BandIndices(axis, spec, first, last);
            if (bandFirst <= bandLast)
            {
                double median = MedianDb(traceDb, bandFirst, bandLast);
                int anchor = AnchorIndex(axis, spec, bandFirst, bandLast);
                return new ResponseReference(anchor, axis.At(anchor), median, spec.Mode, FellBackToPeak: false);
            }
        }

        var peak = FindPeakCore(traceDb, axis, first, last);
        return new ResponseReference(
            peak.Bin, peak.FreqHz, peak.LevelDb,
            BandwidthReferenceMode.Peak,
            FellBackToPeak: spec.Mode != BandwidthReferenceMode.Peak);
    }

    /// <summary>
    /// The samples of <c>[first, last]</c> that fall inside the reference band. Empty (first &gt;
    /// last) when the band and the searched range do not overlap, which is what makes the caller
    /// fall back to the peak.
    /// </summary>
    private static (int First, int Last) BandIndices(
        scoped FrequencyAxis axis,
        BandwidthReferenceSpec spec,
        int first,
        int last)
    {
        double lowHz = Math.Min(spec.LowHz, spec.HighHz);
        double highHz = Math.Max(spec.LowHz, spec.HighHz);

        int bandFirst = first;
        while (bandFirst <= last && axis.At(bandFirst) < lowHz)
            bandFirst++;

        int bandLast = last;
        while (bandLast >= bandFirst && axis.At(bandLast) > highHz)
            bandLast--;

        return (bandFirst, bandLast);
    }

    /// <summary>
    /// The sample nearest the reference band's geometric centre — see
    /// <see cref="ResponseReference.Index"/> for why geometric.
    /// </summary>
    private static int AnchorIndex(
        scoped FrequencyAxis axis,
        BandwidthReferenceSpec spec,
        int bandFirst,
        int bandLast)
    {
        double lowHz = Math.Max(Math.Min(spec.LowHz, spec.HighHz), double.Epsilon);
        double highHz = Math.Max(Math.Max(spec.LowHz, spec.HighHz), double.Epsilon);
        double centreHz = Math.Sqrt(lowHz * highHz);

        int best = bandFirst;
        double bestDistance = double.PositiveInfinity;
        for (int i = bandFirst; i <= bandLast; i++)
        {
            double freq = axis.At(i);
            if (freq <= 0.0)
                continue;

            double distance = Math.Abs(Math.Log(freq) - Math.Log(centreHz));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The median of <c>[first, last]</c>. Copies before sorting because the caller's trace is the
    /// one the chart draws and the corners are searched on — sorting it in place would replace the
    /// response with a monotonic ramp.
    /// </summary>
    private static double MedianDb(ReadOnlySpan<double> traceDb, int first, int last)
    {
        int count = last - first + 1;
        var scratch = new double[count];
        traceDb.Slice(first, count).CopyTo(scratch);
        Array.Sort(scratch);

        return (count % 2) == 1
            ? scratch[count / 2]
            : 0.5 * (scratch[(count / 2) - 1] + scratch[count / 2]);
    }

    private static BandwidthResult FindCore(
        ReadOnlySpan<double> traceDb,
        scoped FrequencyAxis axis,
        double dropDb,
        ResponseReference reference,
        bool symmetric,
        int firstIndex,
        int lastIndex)
    {
        (int first, int last) = ClampRange(traceDb.Length, firstIndex, lastIndex);

        double lowThreshold = reference.LevelDb - dropDb;
        // Positive infinity rather than a flag: a one-sided search is then the symmetric one with an
        // upper threshold nothing can reach, and both walks below stay a single comparison pair
        // instead of branching per sample on the mode.
        double highThreshold = symmetric ? reference.LevelDb + dropDb : double.PositiveInfinity;

        int anchor = Math.Clamp(reference.Index, first, last);

        double lowerHz = axis.At(first);
        bool lowerClamped = true;
        for (int i = anchor - 1; i >= first; i--)
        {
            if (Crosses(traceDb[i], lowThreshold, highThreshold, out double threshold))
            {
                lowerHz = InterpolateCrossing(axis, i, traceDb[i], i + 1, traceDb[i + 1], threshold);
                lowerClamped = false;
                break;
            }
        }

        double upperHz = axis.At(last);
        bool upperClamped = true;
        for (int i = anchor + 1; i <= last; i++)
        {
            if (Crosses(traceDb[i], lowThreshold, highThreshold, out double threshold))
            {
                upperHz = InterpolateCrossing(axis, i - 1, traceDb[i - 1], i, traceDb[i], threshold);
                upperClamped = false;
                break;
            }
        }

        return new BandwidthResult(
            dropDb,
            symmetric,
            reference.LevelDb,
            lowerHz,
            upperHz,
            Math.Max(0.0, upperHz - lowerHz),
            lowerClamped,
            upperClamped);
    }

    /// <summary>
    /// Whether <paramref name="levelDb"/> has left the tolerance, and which threshold it left
    /// through — the crossing has to be interpolated against that one, not against whichever is
    /// nearer.
    /// </summary>
    private static bool Crosses(double levelDb, double lowThreshold, double highThreshold, out double threshold)
    {
        if (levelDb <= lowThreshold)
        {
            threshold = lowThreshold;
            return true;
        }

        if (levelDb >= highThreshold)
        {
            threshold = highThreshold;
            return true;
        }

        threshold = 0.0;
        return false;
    }

    /// <summary>
    /// Where the line between two adjacent samples crosses <paramref name="threshold"/>,
    /// interpolated in log frequency. Falls back to the nearer sample's own frequency when the two
    /// levels are equal, which would otherwise divide by zero.
    /// </summary>
    private static double InterpolateCrossing(
        scoped FrequencyAxis axis,
        int indexA,
        double levelA,
        int indexB,
        double levelB,
        double threshold)
    {
        double freqA = axis.At(indexA);
        double freqB = axis.At(indexB);

        // Bin 0 has no logarithm; the caller's firstBin keeps us off it in practice, but a caller
        // passing firstBin 0 should get a sane answer rather than a NaN.
        if (freqA <= 0.0)
            return freqB;

        double span = levelB - levelA;
        if (Math.Abs(span) < double.Epsilon)
            return freqA;

        double t = (threshold - levelA) / span;
        t = Math.Clamp(t, 0.0, 1.0);

        double logFreq = Math.Log10(freqA) + (t * (Math.Log10(freqB) - Math.Log10(freqA)));
        return Math.Pow(10.0, logFreq);
    }

    private static (int First, int Last) ClampRange(int length, int firstBin, int lastBin)
    {
        if (length < 2)
            throw new ArgumentException("A response trace needs at least two bins.", nameof(length));

        int first = Math.Clamp(firstBin, 0, length - 1);
        int last = Math.Clamp(lastBin, first, length - 1);
        return (first, last);
    }

    private static void RequireMatchingAxis(ReadOnlySpan<double> traceDb, ReadOnlySpan<double> freqHz)
    {
        if (freqHz.Length != traceDb.Length)
        {
            throw new ArgumentException(
                $"The frequency axis has {freqHz.Length} entries but the trace has {traceDb.Length}.",
                nameof(freqHz));
        }
    }

    /// <summary>
    /// Where a sample sits in frequency: either a constant bin spacing (an FFT trace) or one
    /// frequency per sample (a stepped sweep's log-spaced grid).
    ///
    /// <para>The whole of this analyzer only ever asks "what frequency is sample i", so making that
    /// the one thing the two cases disagree about is what lets both share the search and the
    /// interpolation verbatim. A <c>ref struct</c> so the explicit case carries the caller's span
    /// rather than a copy — a full-resolution FFT trace would otherwise cost a 262144-element array
    /// per call to express something the uniform case already knows from one number.</para>
    /// </summary>
    private readonly ref struct FrequencyAxis
    {
        private readonly double _binHz;
        private readonly ReadOnlySpan<double> _freqHz;

        private FrequencyAxis(double binHz, ReadOnlySpan<double> freqHz)
        {
            _binHz = binHz;
            _freqHz = freqHz;
        }

        public static FrequencyAxis Uniform(double binHz) => new(binHz, default);

        public static FrequencyAxis Explicit(ReadOnlySpan<double> freqHz) => new(0.0, freqHz);

        public double At(int index) => _freqHz.IsEmpty ? index * _binHz : _freqHz[index];
    }
}
