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
    /// Searches outward from the rounded bin, alternating up/down, preferring the higher bin
    /// on ties.
    /// </summary>
    public static CoherentTone Nearest(double desiredHz, double sampleRateHz, int n)
    {
        if (n < 2)
            throw new ArgumentOutOfRangeException(nameof(n));
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        double exactBin = desiredHz * n / sampleRateHz;
        int rounded = (int)Math.Round(exactBin, MidpointRounding.AwayFromZero);
        rounded = Math.Clamp(rounded, 1, n - 1);

        int bin = NearestCoprimeBin(rounded, n);
        double frequencyHz = bin * sampleRateHz / n;
        return new CoherentTone(frequencyHz, bin);
    }

    private static int NearestCoprimeBin(int start, int n)
    {
        for (int delta = 0; delta < n; delta++)
        {
            int up = start + delta;
            if (up <= n - 1 && up >= 1 && Gcd(up, n) == 1)
                return up;

            int down = start - delta;
            if (down >= 1 && down <= n - 1 && Gcd(down, n) == 1)
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
