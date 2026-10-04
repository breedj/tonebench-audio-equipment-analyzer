namespace Tonebench.Dsp.Tests;

internal static class TestSignals
{
    /// <summary>Sum of coherent cosines, each at an exact integer bin — no spectral leakage.</summary>
    public static double[] CoherentTone(int sampleCount, params (double AmplitudeV, int Bin)[] components)
    {
        var samples = new double[sampleCount];
        for (int n = 0; n < sampleCount; n++)
        {
            double sum = 0.0;
            foreach (var (amplitudeV, bin) in components)
                sum += amplitudeV * Math.Cos(2.0 * Math.PI * bin * n / sampleCount);
            samples[n] = sum;
        }

        return samples;
    }

    /// <summary>A single cosine at an arbitrary (possibly non-integer-bin) frequency.</summary>
    public static double[] Tone(int sampleCount, double amplitudeV, double frequencyHz, double sampleRateHz)
    {
        var samples = new double[sampleCount];
        for (int n = 0; n < sampleCount; n++)
            samples[n] = amplitudeV * Math.Cos(2.0 * Math.PI * frequencyHz * n / sampleRateHz);

        return samples;
    }

    public static double[] ZeroMeanNoise(int sampleCount, int seed)
    {
        var random = new Random(seed);
        var samples = new double[sampleCount];
        for (int n = 0; n < sampleCount; n++)
            samples[n] = random.NextDouble() * 2.0 - 1.0;

        double mean = samples.Average();
        for (int n = 0; n < sampleCount; n++)
            samples[n] -= mean;

        return samples;
    }

    /// <summary>Zero-mean Gaussian white noise with the given standard deviation (Box-Muller).</summary>
    public static double[] GaussianWhiteNoise(int sampleCount, double stdDev, int seed)
    {
        var random = new Random(seed);
        var samples = new double[sampleCount];
        for (int n = 0; n < sampleCount; n++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            samples[n] = z * stdDev;
        }

        double mean = samples.Average();
        for (int n = 0; n < sampleCount; n++)
            samples[n] -= mean;

        return samples;
    }
}
