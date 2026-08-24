namespace Qa403.Dsp;

/// <summary>The nearest coherent tone to a desired frequency: f = k·fs/N, k coprime to N.</summary>
public readonly record struct CoherentTone(double FrequencyHz, int Bin);

/// <summary>
/// Every test tone the app generates must land on an exact, coherent bin: k cycles in N
/// samples with k coprime to N, so the tone's DFT energy lands on exactly one bin with zero
/// leakage (and, since gcd(k, N) == 1, the time-domain waveform never repeats a sample value
/// at the same phase before the N-sample record ends).
/// </summary>
public static class CoherentFrequency
{
    /// <summary>
    /// Returns the nearest f = k·fs/N to <paramref name="desiredHz"/>, with k coprime to N.
    /// </summary>
    public static CoherentTone Nearest(double desiredHz, double sampleRateHz, int n)
    {
        if (n < 2)
            throw new ArgumentOutOfRangeException(nameof(n));
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        double exactBin = desiredHz * n / sampleRateHz;
        int rounded = Math.Clamp((int)Math.Round(exactBin, MidpointRounding.AwayFromZero), 1, n - 1);

        int bin = NearestCoprimeBin(rounded, exactBin, n);
        double frequencyHz = bin * sampleRateHz / n;
        return new CoherentTone(frequencyHz, bin);
    }

    /// <summary>
    /// The coprime bin closest to <paramref name="exactBin"/>, searching outward from
    /// <paramref name="roundedBin"/>.
    ///
    /// The tie-break is on distance from the *unrounded* bin, not on direction. It used to return
    /// the first coprime bin found while stepping up-then-down from the rounded one, which is a
    /// different thing: for a power-of-two N only odd bins are coprime, so a request landing on an
    /// even bin always has a coprime neighbour one bin up and one bin down, and "up first" took the
    /// higher one however much further away it was in Hz. Harmless in the middle of the band and
    /// gross at the bottom — at 192 kHz / 32768 (5.86 Hz bins) a 10 Hz request rounds to bin 2 and
    /// came back as bin 3, i.e. 17.58 Hz, a 76 % error that started a 10 Hz sweep at 17.6 Hz.
    ///
    /// This does not rescue a bin size that is simply too coarse for the frequency asked for —
    /// see <c>ThdSweepRequest.Validate</c> for the guard that does — it only stops the snap from
    /// adding avoidable error on top.
    /// </summary>
    private static int NearestCoprimeBin(int roundedBin, double exactBin, int n)
    {
        for (int delta = 0; delta < n; delta++)
        {
            int up = roundedBin + delta;
            int down = roundedBin - delta;

            bool upOk = up <= n - 1 && up >= 1 && Gcd(up, n) == 1;
            bool downOk = down >= 1 && down <= n - 1 && Gcd(down, n) == 1;

            // Both candidates at this delta are considered together, and any further delta is at
            // least as far from exactBin as the nearer of these two, so the first delta that hits
            // a coprime bin holds the answer.
            if (upOk && downOk)
                return Math.Abs(down - exactBin) < Math.Abs(up - exactBin) ? down : up;
            if (upOk)
                return up;
            if (downOk)
                return down;
        }

        throw new InvalidOperationException($"No bin in [1, {n - 1}] is coprime to N={n}.");
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a;
    }
}
