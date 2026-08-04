namespace Qa403.Device.Simulation;

/// <summary>
/// Per-sample synthesis of a simulated DUT signal: coherent fundamental + harmonics (or a
/// two-tone IMD pair), mains hum, white + 1/f noise. The deterministic part (tones, hum) is a
/// pure function of elapsed time <c>t</c>, so it is exactly reproducible regardless of block
/// boundaries or timer jitter — only the noise draw is stateful (RNG position, pink filter
/// state), and that state lives here rather than in <see cref="SimulatedDevice"/> so a stream
/// restart gets a fresh synthesizer and a fresh noise sequence.
/// </summary>
internal sealed class SignalSynthesizer(Random random)
{
    private const double HumHarmonicDecayDb = -6.0;
    private double _pinkB0, _pinkB1, _pinkB2;

    public double NextSample(SimulatedSignalProfile profile, double t, double sampleRateHz)
    {
        double sample = ComputeTones(profile, t) + ComputeHum(profile, t);

        if (profile.NoiseEnabled)
            sample += NextNoiseSample(profile, sampleRateHz);

        return sample;
    }

    /// <summary>
    /// Soft-knee saturation: exactly linear (zero added distortion) below <paramref name="kneeFraction"/>
    /// of <paramref name="clipPeakV"/>, then tanh-compresses smoothly to the ±clipPeakV asymptote.
    /// The knee matters: a clipper active across the whole range (plain tanh(x/peak)) would pollute
    /// even small signals with a faint cubic term and swamp the deep default harmonics (H9 = -132 dBc).
    /// </summary>
    public static double SoftClip(double sample, double clipPeakV, double kneeFraction = 0.9)
    {
        if (clipPeakV <= 0.0)
            return sample;

        double threshold = clipPeakV * kneeFraction;
        double magnitude = Math.Abs(sample);
        if (magnitude <= threshold)
            return sample;

        double headroom = clipPeakV - threshold;
        double excess = magnitude - threshold;
        double compressed = threshold + headroom * Math.Tanh(excess / headroom);
        return Math.Sign(sample) * compressed;
    }

    public static double DbvToPeakV(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);

    private static double ComputeTones(SimulatedSignalProfile profile, double t)
    {
        if (profile.Intermodulation != IntermodulationMode.None)
            return ComputeIntermodulation(profile, t);

        double fundamentalPeakV = DbvToPeakV(profile.FundamentalAmplitudeDbv);
        double sum = fundamentalPeakV * Math.Cos(2.0 * Math.PI * profile.FundamentalFrequencyHz * t);

        if (profile.HarmonicsEnabled)
        {
            var harmonicDbc = profile.HarmonicDbc;
            for (int i = 0; i < harmonicDbc.Count; i++)
            {
                int order = i + 2;
                double amplitudeV = fundamentalPeakV * Math.Pow(10.0, harmonicDbc[i] / 20.0);
                sum += amplitudeV * Math.Cos(2.0 * Math.PI * profile.FundamentalFrequencyHz * order * t);
            }
        }

        return sum;
    }

    private static double ComputeIntermodulation(SimulatedSignalProfile profile, double t)
    {
        double totalPeakV = DbvToPeakV(profile.IntermodulationAmplitudeDbv);

        return profile.Intermodulation switch
        {
            IntermodulationMode.Smpte => Smpte(totalPeakV, t),
            IntermodulationMode.Ccif => Ccif(totalPeakV, t),
            _ => 0.0,
        };
    }

    // 60 Hz : 7 kHz at a 4:1 amplitude ratio -> the two peaks sum to 5x, split 4x/1x.
    private static double Smpte(double totalPeakV, double t)
    {
        double highPeakV = totalPeakV / 5.0;
        double lowPeakV = 4.0 * highPeakV;
        return lowPeakV * Math.Cos(2.0 * Math.PI * 60.0 * t) + highPeakV * Math.Cos(2.0 * Math.PI * 7000.0 * t);
    }

    private static double Ccif(double totalPeakV, double t)
    {
        double eachPeakV = totalPeakV / 2.0;
        return eachPeakV * Math.Cos(2.0 * Math.PI * 19000.0 * t) + eachPeakV * Math.Cos(2.0 * Math.PI * 20000.0 * t);
    }

    private static double ComputeHum(SimulatedSignalProfile profile, double t)
    {
        if (!profile.HumEnabled)
            return 0.0;

        double levelV = DbvToPeakV(profile.HumLevelDbv);
        double sum = 0.0;
        for (int h = 1; h <= profile.HumHarmonicCount; h++)
        {
            double harmonicPeakV = levelV * Math.Pow(10.0, HumHarmonicDecayDb * (h - 1) / 20.0);
            sum += harmonicPeakV * Math.Cos(2.0 * Math.PI * profile.HumFundamentalHz * h * t);
        }

        return sum;
    }

    // White noise calibration: for Gaussian white noise with time-domain stddev sigma, the
    // one-sided PSD is flat at sigma^2/(fs/2) V^2/Hz (see Qa403.Dsp.Tests golden test 5), so
    // sigma = density * sqrt(fs/2) reproduces a target density in dBV/sqrt(Hz) exactly.
    private double NextNoiseSample(SimulatedSignalProfile profile, double sampleRateHz)
    {
        double densityLinear = Math.Pow(10.0, profile.NoiseDensityDbvPerRootHz / 20.0);
        double sigma = densityLinear * Math.Sqrt(sampleRateHz / 2.0);
        double white = sigma * NextGaussian();

        if (!profile.PinkNoiseEnabled)
            return white;

        return white + NextPinkSample(white);
    }

    private double NextGaussian()
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Paul Kellet's "economy" pink filter: rolls a white input off towards 1/f. No exact
    // absolute calibration (see SimulatedSignalProfile.PinkNoiseEnabled) — driven directly by
    // the already-calibrated white sample so its magnitude tracks the configured density.
    private double NextPinkSample(double white)
    {
        _pinkB0 = 0.99765 * _pinkB0 + white * 0.0990460;
        _pinkB1 = 0.96300 * _pinkB1 + white * 0.2965164;
        _pinkB2 = 0.57000 * _pinkB2 + white * 1.0526913;
        return (_pinkB0 + _pinkB1 + _pinkB2) * 0.25;
    }
}
