namespace Qa403.Device.Hardware;

/// <summary>
/// Per-sample TX (DAC) waveform synthesis for a <see cref="ChannelGeneratorConfig"/>, in the same
/// peak-volts convention <see cref="Qa40xCalibration.VoltsToRaw"/> expects.
///
/// Deliberately independent from <c>Simulation.SignalSynthesizer</c> rather than reusing it:
/// that class's tone computation adds a DUT-simulation-only fake harmonic stack
/// (<c>SimulatedSignalProfile.HarmonicsEnabled</c>/<c>HarmonicDbc</c>) meant to make the
/// *simulated device's own analog stage* look realistic — real hardware must output exactly the
/// configured tones/comb/noise/chirp and nothing else, or every measurement taken against it
/// would be measuring software-added distortion instead of the DUT's. The actually-reusable
/// subset turns out small enough to re-derive standalone: <see cref="GeneratorKind.Tones"/> and
/// <see cref="GeneratorKind.Multitone"/> both reduce to "sum the configured tones" — see
/// <see cref="ChannelGeneratorConfig.Tones"/>'s own doc comment: it already holds the *effective*
/// list for both kinds (the derived comb, in the multitone case). White noise has no pink shaping
/// (that's the DUT-only knob too). The chirp phase formula is the one piece of real math shared
/// with the simulator, re-derived here rather than exposed cross-namespace, to keep this file
/// fully decoupled from <c>Simulation</c>.
/// </summary>
internal sealed class Qa40xGeneratorWaveform(Random random)
{
    /// <summary>Matches <c>Simulation.SignalSynthesizer.EchoChirpRepeatSeconds</c> — the spec exposes no sweep length, so a fixed period keeps the chirp a pure function of t.</summary>
    private const double EchoChirpRepeatSeconds = 1.0;

    /// <summary>Matches <c>Simulation.SignalSynthesizer.EchoChirpFadeSeconds</c> — raised-cosine fade at each sweep-wrap boundary.</summary>
    private const double EchoChirpFadeSeconds = 0.005;

    /// <summary>This channel's generator output at time <paramref name="t"/> seconds, in volts.</summary>
    public double ComputeSample(ChannelGeneratorConfig channel, double t)
    {
        if (!channel.Enabled)
            return 0.0;

        return channel.Kind switch
        {
            GeneratorKind.Off => 0.0,
            GeneratorKind.Tones => SumTones(channel.Tones, t),
            GeneratorKind.Multitone => SumTones(channel.Tones, t),
            GeneratorKind.WhiteNoise => ComputeWhiteNoise(channel.Noise),
            GeneratorKind.EchoChirp => ComputeEchoChirp(channel.Chirp, t),
            _ => 0.0,
        };
    }

    /// <summary>Bare sum of cosines at each tone's own peak volts — no added harmonics (see type remarks).</summary>
    private static double SumTones(IReadOnlyList<GeneratorTone> tones, double t)
    {
        double sum = 0.0;
        for (int i = 0; i < tones.Count; i++)
        {
            var tone = tones[i];
            sum += PeakVolts(tone.AmplitudeDbv) * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * t);
        }

        return sum;
    }

    /// <summary>Gaussian white noise at the configured broadband RMS (see <see cref="WhiteNoiseSettings.LevelDbv"/>'s own doc comment: a level, not a spectral density — sigma is the level itself).</summary>
    private double ComputeWhiteNoise(WhiteNoiseSettings? settings)
    {
        if (settings is null)
            return 0.0;

        double sigma = Math.Pow(10.0, settings.LevelDbv / 20.0);
        return sigma * NextGaussian();
    }

    /// <summary>Same closed-form exponential sweep as <c>Simulation.SignalSynthesizer.ComputeEchoChirp</c> — see that method's doc comment for the derivation.</summary>
    private static double ComputeEchoChirp(EchoChirpSettings? settings, double t)
    {
        if (settings is null)
            return 0.0;

        double startHz = settings.StartFrequencyHz;
        double endHz = settings.EndFrequencyHz;
        if (startHz <= 0.0 || endHz <= startHz)
            return 0.0;

        double tau = t % EchoChirpRepeatSeconds;
        if (tau < 0.0)
            tau += EchoChirpRepeatSeconds;

        double k = Math.Log(endHz / startHz);
        double phase = 2.0 * Math.PI * startHz * EchoChirpRepeatSeconds / k
            * (Math.Exp(k * tau / EchoChirpRepeatSeconds) - 1.0);

        return PeakVolts(settings.AmplitudeDbv) * Math.Sin(phase) * EchoChirpFade(tau);
    }

    private static double EchoChirpFade(double tau)
    {
        double fromEdge = Math.Min(tau, EchoChirpRepeatSeconds - tau);
        if (fromEdge >= EchoChirpFadeSeconds)
            return 1.0;

        return 0.5 * (1.0 - Math.Cos(Math.PI * fromEdge / EchoChirpFadeSeconds));
    }

    private static double PeakVolts(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);

    private double NextGaussian()
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
