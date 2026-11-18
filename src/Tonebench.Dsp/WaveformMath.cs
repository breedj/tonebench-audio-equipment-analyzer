namespace Tonebench.Dsp;

/// <summary>
/// What a math channel computes from the two captured waveforms.
///
/// <para>Channels are <b>A and B</b> here rather than left and right, which is the convention every
/// bench instrument's math menu uses and the one that keeps an expression readable: "A − B" says
/// what it does where "left minus right" says which sockets it came out of.</para>
///
/// <para><b>Both polarities of the difference are offered.</b> A differential reading cares which
/// way round it is — a transformer winding, a bridged amplifier, a shunt — and there is no second
/// operation to chain, so <c>BMinusA</c> is an entry rather than an invert applied afterwards.</para>
/// </summary>
public enum WaveformOperation
{
    /// <summary>A − B: the software differential probe, and the one this whole feature is for.</summary>
    AMinusB,

    /// <summary>B − A, which is the same reading with the probes the other way round.</summary>
    BMinusA,

    /// <summary>A + B. Twice the common-mode part of a differential pair, and the sum of a push-pull pair's halves.</summary>
    APlusB,

    /// <summary>
    /// A × B. Instantaneous power where one channel measures a voltage and the other a current
    /// through a shunt — in which case the figure is W rather than V², and the shunt's ohms are
    /// the reader's own arithmetic. See <see cref="WaveformMathUnit.VoltSquared"/>.
    /// </summary>
    ATimesB,

    /// <summary>
    /// A / B, bounded by <see cref="WaveformMath.DivisionLimit"/>. Only meaningful where B is a
    /// settled level; against two AC waveforms it rails every time B crosses zero, which is why
    /// the clamp is counted and reported rather than hidden.
    /// </summary>
    ADivB,

    /// <summary>−A.</summary>
    NegateA,

    /// <summary>−B.</summary>
    NegateB,

    /// <summary>dA/dt, in volts per second — a slew-rate trace. See <see cref="WaveformMath.Apply"/>.</summary>
    DerivativeA,

    /// <summary>dB/dt.</summary>
    DerivativeB,

    /// <summary>The integral of A, in volt-seconds, with its mean removed first.</summary>
    IntegralA,

    /// <summary>The integral of B.</summary>
    IntegralB,
}

/// <summary>
/// What a math trace's numbers are in.
///
/// <para><b>This is the honesty requirement of the whole feature.</b> Every reading in
/// <c>ScopeSliceMeasurements</c> is a bare <c>double</c> named <c>MaxV</c>, <c>RmsV</c> and so on,
/// and for A − B those names are right — for A × B, A / B, a derivative or an integral they are
/// not, and a figure labelled volts on a scale that is not volts is exactly the plausible wrong
/// answer this app's rules exist to prevent. So the unit travels with the trace and the panel, the
/// axis and the report all read it.</para>
/// </summary>
public enum WaveformMathUnit
{
    /// <summary>Volts — the additive operations and the negations.</summary>
    Volt,

    /// <summary>
    /// Volts squared. Nominally that is what A × B is; it is a power in watts only once the reader
    /// knows one channel is a current, and this layer does not.
    /// </summary>
    VoltSquared,

    /// <summary>Dimensionless — a ratio.</summary>
    Ratio,

    /// <summary>Volts per second. Quoted as V/µs at anything a real amplifier does.</summary>
    VoltPerSecond,

    /// <summary>Volt-seconds.</summary>
    VoltSecond,
}

/// <summary>
/// The math channel: one derived waveform from the two captured ones, on the same sample grid.
///
/// <para><b>It is computed on the samples and never on the drawn columns</b>, and that is not a
/// preference about where code lives — it is arithmetic. A decimated column carries the min and the
/// max of what it covers, and <c>max(A) − max(B)</c> is not <c>max(A − B)</c>: the two extremes
/// need not even fall at the same sample, so subtracting one envelope from another draws a
/// difference trace of a signal neither channel carried. On a differential pair, where A and B are
/// nearly equal and the whole point is the small difference between them, that error is the entire
/// reading. Measured on a column of 120 samples of two 1 V sines a millivolt apart, the
/// envelope difference is zero where the true difference is the millivolt.</para>
///
/// <para>Pure, and in <c>Tonebench.Dsp</c>, so every operation below is checkable against
/// arithmetic on a synthetic pair rather than against itself.</para>
/// </summary>
public static class WaveformMath
{
    /// <summary>
    /// How far <see cref="WaveformOperation.ADivB"/> is allowed to go before it is clamped — a
    /// ratio of a thousand, i.e. 60 dB.
    ///
    /// <para>A clamp rather than a gap or a refusal, and each of the three was weighed. A
    /// <b>gap</b> cannot be expressed: the wire carries <c>float[]</c>, both traces share one x
    /// array, and a NaN in that array would truncate the whole JSON response part way through —
    /// the rule this app's status records are built around. A <b>refusal</b> of the whole trace
    /// would be the answer on almost every signal, since any AC waveform takes its denominator
    /// through zero once a cycle, which would make the operation unreachable rather than honest.
    /// So it rails, the way a bench instrument's does, and the number of samples it railed on is
    /// counted and reported — because a trace that spends most of its time at the limit is one
    /// whose reader needs to be told that, rather than one that merely looks busy.</para>
    /// </summary>
    public const double DivisionLimit = 1000.0;

    /// <summary>The unit an operation's numbers are in.</summary>
    public static WaveformMathUnit UnitOf(WaveformOperation operation) => operation switch
    {
        WaveformOperation.ATimesB => WaveformMathUnit.VoltSquared,
        WaveformOperation.ADivB => WaveformMathUnit.Ratio,
        WaveformOperation.DerivativeA or WaveformOperation.DerivativeB => WaveformMathUnit.VoltPerSecond,
        WaveformOperation.IntegralA or WaveformOperation.IntegralB => WaveformMathUnit.VoltSecond,
        _ => WaveformMathUnit.Volt,
    };

    /// <summary>Whether the operation reads channel A at all.</summary>
    public static bool NeedsA(WaveformOperation operation) => operation switch
    {
        WaveformOperation.NegateB or WaveformOperation.DerivativeB or WaveformOperation.IntegralB => false,
        _ => true,
    };

    /// <summary>
    /// Whether it reads channel B.
    ///
    /// <para>Asked before anything is computed, because a measurement that captures one input has
    /// no second waveform to combine and the honest answer is no trace with a reason — not a trace
    /// against an array of zeros, which would draw A itself and call it A − B.</para>
    /// </summary>
    public static bool NeedsB(WaveformOperation operation) => operation switch
    {
        WaveformOperation.NegateA or WaveformOperation.DerivativeA or WaveformOperation.IntegralA => false,
        _ => true,
    };

    /// <summary>
    /// Computes one window of the math channel into <paramref name="destination"/>, and answers how
    /// many samples were clamped (<see cref="WaveformOperation.ADivB"/> only; zero for everything
    /// else).
    ///
    /// <para><b>The derivative is a central difference</b>, <c>(x[i+1] − x[i−1]) · fs / 2</c>, with
    /// a one-sided difference at the two ends so the result is the same length as its input — which
    /// it has to be, the waveform and the math trace sharing one x array. Central rather than
    /// forward for two reasons that pull the same way: a forward difference answers for the
    /// midpoint between two samples and so puts the whole trace half a sample early, and its noise
    /// gain is twice the central one's. Note what no choice of difference can fix: differentiation
    /// is a 6 dB/octave high-pass, so at 192 kS/s the top of the band is lifted some 73 dB over
    /// 20 Hz. A derivative trace of a quiet signal is a picture of the quantisation noise, and a
    /// slew rate read off an edge of about one sample is this analyzer's own figure rather than the
    /// DUT's — the same caveat the rise-time reading already carries.</para>
    ///
    /// <para><b>The integral removes the mean first</b>, and that is the one decision here that
    /// changes what the trace means. Integrating a constant gives a ramp, so any DC at all walks
    /// the result off the screen and nothing else in the window is visible beside it — and on this
    /// hardware that DC is not even the DUT's: both the QA403 and a sound card are AC-coupled, so
    /// what is left is the front end's own offset, measured at about −4.5 mV on the left input.
    /// Integrated over a one-second record that is −4.5 mV·s of pure artefact, which is larger than
    /// most of what anyone would integrate for. So the mean of the window goes first, and the
    /// consequence is stated rather than hidden: the trace starts at zero and shows the AC part's
    /// running integral, which is flux or charge, and it is <i>not</i> an absolute one.</para>
    /// </summary>
    /// <param name="operation">What to compute.</param>
    /// <param name="a">Channel A's samples, or empty where the operation does not read it.</param>
    /// <param name="b">Channel B's samples, or empty where it does not read it.</param>
    /// <param name="sampleRateHz">Needed by the derivative and the integral; ignored by the rest.</param>
    /// <param name="destination">Where the result goes. Must be at least as long as the inputs.</param>
    public static int Apply(
        WaveformOperation operation,
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        double sampleRateHz,
        Span<double> destination)
    {
        int n = NeedsA(operation) ? a.Length : b.Length;
        if (NeedsA(operation) && NeedsB(operation))
            n = Math.Min(a.Length, b.Length);

        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, n);
        if (n == 0)
            return 0;

        switch (operation)
        {
            case WaveformOperation.AMinusB:
                for (int i = 0; i < n; i++) destination[i] = a[i] - b[i];
                return 0;

            case WaveformOperation.BMinusA:
                for (int i = 0; i < n; i++) destination[i] = b[i] - a[i];
                return 0;

            case WaveformOperation.APlusB:
                for (int i = 0; i < n; i++) destination[i] = a[i] + b[i];
                return 0;

            case WaveformOperation.ATimesB:
                for (int i = 0; i < n; i++) destination[i] = a[i] * b[i];
                return 0;

            case WaveformOperation.ADivB:
                return Divide(a, b, n, destination);

            case WaveformOperation.NegateA:
                for (int i = 0; i < n; i++) destination[i] = -a[i];
                return 0;

            case WaveformOperation.NegateB:
                for (int i = 0; i < n; i++) destination[i] = -b[i];
                return 0;

            case WaveformOperation.DerivativeA:
                Differentiate(a, n, sampleRateHz, destination);
                return 0;

            case WaveformOperation.DerivativeB:
                Differentiate(b, n, sampleRateHz, destination);
                return 0;

            case WaveformOperation.IntegralA:
                Integrate(a, n, sampleRateHz, destination);
                return 0;

            case WaveformOperation.IntegralB:
                Integrate(b, n, sampleRateHz, destination);
                return 0;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown waveform operation.");
        }
    }

    /// <summary>
    /// A / B, railed at <see cref="DivisionLimit"/>, counting every sample that railed.
    ///
    /// <para>Nothing here may produce a NaN or an infinity, whatever the denominator does: both
    /// would reach a <c>float[]</c> on the wire and take the response down with them. An infinity
    /// fails the limit test and is railed like any other overrun; the one case the test cannot
    /// catch is <c>0 / 0</c>, which is answered as zero — the ratio of two quantities that are both
    /// nothing — and counted, since it is a sample the reader must not take at face value.</para>
    /// </summary>
    private static int Divide(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int n, Span<double> destination)
    {
        int clamped = 0;
        for (int i = 0; i < n; i++)
        {
            double numerator = a[i];
            double denominator = b[i];
            double q;

            if (denominator == 0.0)
            {
                q = numerator == 0.0 ? 0.0 : (numerator > 0.0 ? DivisionLimit : -DivisionLimit);
                clamped++;
            }
            else
            {
                q = numerator / denominator;
                if (!(q > -DivisionLimit && q < DivisionLimit))
                {
                    q = q > 0.0 ? DivisionLimit : -DivisionLimit;
                    clamped++;
                }
            }

            destination[i] = q;
        }

        return clamped;
    }

    private static void Differentiate(ReadOnlySpan<double> source, int n, double sampleRateHz, Span<double> destination)
    {
        if (n == 1)
        {
            destination[0] = 0.0;
            return;
        }

        destination[0] = (source[1] - source[0]) * sampleRateHz;
        for (int i = 1; i < n - 1; i++)
            destination[i] = (source[i + 1] - source[i - 1]) * sampleRateHz * 0.5;
        destination[n - 1] = (source[n - 1] - source[n - 2]) * sampleRateHz;
    }

    private static void Integrate(ReadOnlySpan<double> source, int n, double sampleRateHz, Span<double> destination)
    {
        double sum = 0.0;
        for (int i = 0; i < n; i++)
            sum += source[i];
        double mean = sum / n;

        double dt = 1.0 / sampleRateHz;
        destination[0] = 0.0;
        for (int i = 1; i < n; i++)
            destination[i] = destination[i - 1] + (((source[i - 1] - mean) + (source[i] - mean)) * 0.5 * dt);
    }
}
