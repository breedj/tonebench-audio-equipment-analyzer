namespace Qa403.Dsp;

/// <summary>
/// The one sine a record is best explained by: <c>A·cos(2π·f·n/fs + φ) + C</c>. Frequency is
/// refined, not just taken, so the fit cancels the fundamental of a tone that is not on a
/// coherent bin — see <see cref="SineFit"/>.
/// </summary>
/// <param name="FreqHz">The fitted frequency.</param>
/// <param name="AmplitudeVpk">Peak amplitude.</param>
/// <param name="PhaseRad">Phase at sample 0 of the record the fit was made on.</param>
/// <param name="OffsetV">The DC term.</param>
/// <param name="Iterations">How many frequency refinements were taken before the change fell below the tolerance (or the cap was hit).</param>
public readonly record struct SineFitResult(double FreqHz, double AmplitudeVpk, double PhaseRad, double OffsetV, int Iterations)
{
    /// <summary>The fitted sine at sample index <paramref name="n"/> of the record it was fitted to.</summary>
    public double At(int n, double sampleRateHz) =>
        AmplitudeVpk * Math.Cos(2.0 * Math.PI * FreqHz * n / sampleRateHz + PhaseRad) + OffsetV;

    /// <summary>The fitted sine's RMS, for quoting the residual against it.</summary>
    public double Vrms => AmplitudeVpk / Math.Sqrt(2.0);
}

/// <summary>
/// Least-squares sine fitting, in the IEEE 1057 four-parameter form: the three linear parameters
/// (in-phase and quadrature amplitude, offset) are solved exactly at a trial frequency, then the
/// frequency is refined by Gauss–Newton on the linearised model and the three are re-solved, until
/// the frequency stops moving.
///
/// <para>This is what the THD residual view rests on. Subtracting a sine at the FFT's own bin
/// frequency cancels a coherent tone to the noise, but a tone a fraction of a bin off drifts in
/// phase across the record and leaves a residual that is the drift, not the DUT: half a bin is
/// π radians end to end, which reads as a fundamental-sized residual with nothing wrong. The
/// refinement is what makes an unsnapped or drifting tone cancel too. Time is measured from the
/// record's centre inside the solver so the frequency-derivative column is not correlated with the
/// amplitudes; the result is reported with its phase at sample 0.</para>
///
/// <para>Pure and allocation-free apart from the result, but <em>not</em> cheap: O(n) per
/// iteration, with two <see cref="Math.SinCos(double)"/> calls and about twenty multiply-
/// accumulates per sample per iteration, and three or four iterations is typical. Measured in
/// Release on a 1 kHz tone at 192 kHz: 8 ms at 8192 points, 16 ms at 131072, and <b>2.65 s over a
/// whole 1,048,576-point record</b> -- roughly a microsecond per thousand samples per iteration.
/// A caller on a display path therefore has to bound what it hands over rather than passing
/// whatever record it happens to hold; <c>SpectrumPipeline.ResidualFitCount</c> is the one that
/// does, and its remarks say what an unbounded fit cost there.</para>
/// </summary>
public static class SineFit
{
    /// <summary>The default cap on frequency refinements; convergence is usually reached in three or four.</summary>
    public const int DefaultMaxIterations = 8;

    /// <summary>A refinement that moves the frequency by less than this fraction of itself ends the loop.</summary>
    public const double RelativeFrequencyTolerance = 1e-10;

    /// <summary>
    /// Fits one sine to <paramref name="samples"/>, starting the frequency search at
    /// <paramref name="initialFreqHz"/> (the FFT's peak bin is the usual source, and needs to be
    /// within about a bin of the truth).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than four samples, or a non-positive rate or frequency.</exception>
    public static SineFitResult Fit(
        ReadOnlySpan<double> samples, double sampleRateHz, double initialFreqHz, int maxIterations = DefaultMaxIterations)
    {
        if (samples.Length < 4)
            throw new ArgumentOutOfRangeException(nameof(samples), samples.Length, "A sine fit needs at least four samples.");
        if (!(sampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "The sample rate must be positive.");
        if (!(initialFreqHz > 0.0) || initialFreqHz >= sampleRateHz / 2.0)
            throw new ArgumentOutOfRangeException(nameof(initialFreqHz), initialFreqHz, "The initial frequency must be between 0 and Nyquist.");

        int n = samples.Length;
        double centre = (n - 1) / 2.0;
        double omega = 2.0 * Math.PI * initialFreqHz / sampleRateHz; // rad per sample

        // Three-parameter solve at the trial frequency.
        (double a, double b, double c) = SolveLinear(samples, omega, centre);

        int iterations = 0;
        for (; iterations < maxIterations; iterations++)
        {
            // Four-parameter Gauss-Newton step: linearise in Δω about the current (a, b, c, ω).
            // Model: y ≈ a·cos(ωτ) + b·sin(ωτ) + c + Δω·τ·(−a·sin(ωτ) + b·cos(ωτ)), τ = i − centre.
            double dOmega = SolveFrequencyStep(samples, omega, centre, a, b, c);
            if (!double.IsFinite(dOmega))
                break;

            // Never jump further than one FFT bin's worth (2π/n rad per sample) at a time, nor past
            // DC or Nyquist: the linearisation is only good within the main lobe of the record's
            // own sinc, and a wild step from outside it would land on a frequency no record can
            // carry. Eight one-bin steps still reach anything a peak search could have handed over.
            double maxStep = Math.Min(2.0 * Math.PI / n, Math.Min(omega, Math.PI - omega) / 2.0);
            dOmega = Math.Clamp(dOmega, -maxStep, maxStep);

            double next = omega + dOmega;
            (a, b, c) = SolveLinear(samples, next, centre);
            bool converged = Math.Abs(dOmega) <= RelativeFrequencyTolerance * omega;
            omega = next;
            if (converged)
            {
                iterations++;
                break;
            }
        }

        // a·cos(ωτ) + b·sin(ωτ) = R·cos(ωτ + φ0) with R = √(a²+b²), φ0 = atan2(−b, a); then move
        // the phase reference from the record centre to sample 0: τ = n − centre.
        double amplitude = Math.Sqrt(a * a + b * b);
        double phaseAtCentre = Math.Atan2(-b, a);
        double phaseAtZero = phaseAtCentre - omega * centre;
        phaseAtZero = Math.IEEERemainder(phaseAtZero, 2.0 * Math.PI);

        return new SineFitResult(omega * sampleRateHz / (2.0 * Math.PI), amplitude, phaseAtZero, c, iterations);
    }

    /// <summary>
    /// Writes <c>samples[start + i] − fit(start + i)</c> into <paramref name="residual"/> for
    /// <c>residual.Length</c> samples: the record with its fundamental removed. <paramref name="start"/>
    /// is the index into the record the fit was made on, so a tail of the record can be subtracted
    /// in place without re-fitting.
    /// </summary>
    public static void Subtract(ReadOnlySpan<double> samples, in SineFitResult fit, double sampleRateHz, int start, Span<float> residual)
    {
        if (start < 0 || start + residual.Length > samples.Length)
            throw new ArgumentOutOfRangeException(nameof(start), start, "The residual must lie inside the fitted record.");

        double omega = 2.0 * Math.PI * fit.FreqHz / sampleRateHz;
        for (int i = 0; i < residual.Length; i++)
        {
            int n = start + i;
            residual[i] = (float)(samples[n] - (fit.AmplitudeVpk * Math.Cos(omega * n + fit.PhaseRad) + fit.OffsetV));
        }
    }

    /// <summary>RMS of the record minus the fit over the whole record, in the record's own units.</summary>
    public static double ResidualRms(ReadOnlySpan<double> samples, in SineFitResult fit, double sampleRateHz)
    {
        double omega = 2.0 * Math.PI * fit.FreqHz / sampleRateHz;
        double sum = 0.0;
        for (int n = 0; n < samples.Length; n++)
        {
            double r = samples[n] - (fit.AmplitudeVpk * Math.Cos(omega * n + fit.PhaseRad) + fit.OffsetV);
            sum += r * r;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>The exact least-squares (a, b, c) for y ≈ a·cos(ωτ) + b·sin(ωτ) + c, τ measured from <paramref name="centre"/>.</summary>
    private static (double A, double B, double C) SolveLinear(ReadOnlySpan<double> y, double omega, double centre)
    {
        // Normal equations of a 3-column design [cos, sin, 1]. Accumulated directly; the columns
        // are near-orthogonal for any record longer than a period, so no scaling is needed.
        double scc = 0, scs = 0, sc1 = 0, sss = 0, ss1 = 0, s11 = y.Length;
        double scy = 0, ssy = 0, s1y = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double t = i - centre;
            (double s, double cs) = Math.SinCos(omega * t);
            double v = y[i];
            scc += cs * cs;
            scs += cs * s;
            sc1 += cs;
            sss += s * s;
            ss1 += s;
            scy += cs * v;
            ssy += s * v;
            s1y += v;
        }

        Span<double> m =
        [
            scc, scs, sc1, scy,
            scs, sss, ss1, ssy,
            sc1, ss1, s11, s1y,
        ];
        Solve(m, 3);
        return (m[3], m[7], m[11]);
    }

    /// <summary>
    /// One Gauss–Newton step in ω, with (a, b, c) free to move alongside it: the Δω that, on the
    /// linearised model, best explains what the current fit leaves over.
    /// </summary>
    private static double SolveFrequencyStep(ReadOnlySpan<double> y, double omega, double centre, double a, double b, double c)
    {
        // Design columns: cos, sin, 1, and d/dω = τ·(−a·sin + b·cos). Right-hand side: the residual.
        Span<double> ata = stackalloc double[16];
        Span<double> atr = stackalloc double[4];
        ata.Clear();
        atr.Clear();
        Span<double> col = stackalloc double[4];

        for (int i = 0; i < y.Length; i++)
        {
            double t = i - centre;
            (double s, double cs) = Math.SinCos(omega * t);
            col[0] = cs;
            col[1] = s;
            col[2] = 1.0;
            col[3] = t * (-a * s + b * cs);
            double r = y[i] - (a * cs + b * s + c);

            for (int p = 0; p < 4; p++)
            {
                atr[p] += col[p] * r;
                for (int q = 0; q < 4; q++)
                    ata[p * 4 + q] += col[p] * col[q];
            }
        }

        Span<double> m = stackalloc double[20];
        for (int p = 0; p < 4; p++)
        {
            for (int q = 0; q < 4; q++)
                m[p * 5 + q] = ata[p * 4 + q];
            m[p * 5 + 4] = atr[p];
        }

        Solve(m, 4);
        return m[3 * 5 + 4];
    }

    /// <summary>
    /// Gaussian elimination with partial pivoting on an augmented <paramref name="size"/> × (size+1)
    /// row-major matrix; the solution lands in the last column.
    /// </summary>
    private static void Solve(Span<double> m, int size)
    {
        int cols = size + 1;
        for (int k = 0; k < size; k++)
        {
            int pivot = k;
            double best = Math.Abs(m[k * cols + k]);
            for (int r = k + 1; r < size; r++)
            {
                double v = Math.Abs(m[r * cols + k]);
                if (v > best)
                {
                    best = v;
                    pivot = r;
                }
            }

            if (best == 0.0)
            {
                // Singular: leave NaN in the solution so the caller stops refining.
                for (int r = 0; r < size; r++)
                    m[r * cols + size] = double.NaN;
                return;
            }

            if (pivot != k)
            {
                for (int q = 0; q < cols; q++)
                    (m[k * cols + q], m[pivot * cols + q]) = (m[pivot * cols + q], m[k * cols + q]);
            }

            double d = m[k * cols + k];
            for (int r = k + 1; r < size; r++)
            {
                double f = m[r * cols + k] / d;
                if (f == 0.0)
                    continue;
                for (int q = k; q < cols; q++)
                    m[r * cols + q] -= f * m[k * cols + q];
            }
        }

        for (int k = size - 1; k >= 0; k--)
        {
            double v = m[k * cols + size];
            for (int q = k + 1; q < size; q++)
                v -= m[k * cols + q] * m[q * cols + size];
            m[k * cols + size] = v / m[k * cols + k];
        }
    }
}
