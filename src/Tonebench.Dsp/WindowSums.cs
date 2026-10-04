namespace Tonebench.Dsp;

/// <summary>
/// S1/S2 per Heinzel/Rüdiger/Schilling: S1 = Σw[n], S2 = Σw[n]². Store alongside every
/// trace — a spectrum recorded under an unknown window is uninterpretable later.
/// </summary>
public readonly record struct WindowSums(double S1, double S2, int Length)
{
    public double EnbwBins => Length * S2 / (S1 * S1);

    public double EnbwHz(double sampleRateHz) => sampleRateHz * S2 / (S1 * S1);
}
