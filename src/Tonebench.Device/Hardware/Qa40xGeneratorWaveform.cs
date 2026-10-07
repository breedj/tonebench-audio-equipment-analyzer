namespace Tonebench.Device.Hardware;

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
/// list for both kinds (the derived comb, in the multitone case). The chirp phase formula is the
/// one piece of real math shared with the simulator, re-derived here rather than exposed
/// cross-namespace, to keep this file fully decoupled from <c>Simulation</c>. The 1/f shaping the
/// pink kind needs is the exception: it is a filter rather than a formula, so both implementations
/// hold an instance of <see cref="PinkNoiseShaper"/> instead of a copy of its coefficients. <c>Tonebench.Dsp.ExponentialChirp</c> holds a third copy —
/// the one the frequency-response analysis builds its reference spectrum from — and
/// <c>Tonebench.App.Tests.Services.ChirpFormulaAgreementTests</c> pins all three together sample for
/// sample. That test is not optional: a drift between the played sweep and the reference it is
/// deconvolved against does not fail loudly, it quietly reports the drift as DUT response.
/// </summary>
internal sealed class Qa40xGeneratorWaveform(Random random, double sampleRateHz)
{
    /// <summary>Matches <c>Simulation.SignalSynthesizer.EchoChirpFadeSeconds</c> and <c>Tonebench.Dsp.ExponentialChirp.DefaultFadeSeconds</c> — raised-cosine fade at each sweep-wrap boundary.</summary>
    private const double EchoChirpFadeSeconds = 0.005;

    /// <summary>
    /// One 1/f shaper per generator channel, indexed by the <c>channelIndex</c> argument.
    ///
    /// <para><b>Per channel, not one shared.</b> A single filter fed alternately from the two
    /// channels would see each channel's samples at half the rate its poles were designed for, and
    /// would hand back an interleaving of two half-rate streams rather than two pink ones — a
    /// spectrum that is neither channel's, and correlated between them.</para>
    ///
    /// <para>The two nevertheless produce <i>identical</i> audio from identical settings, because
    /// their input is <see cref="PinkNoiseShaper.WhiteAt"/> — see there for why a stimulus channel
    /// and its reference cannot be independent draws.</para>
    /// </summary>
    private readonly PinkNoiseShaper[] _pink =
        [new PinkNoiseShaper(sampleRateHz), new PinkNoiseShaper(sampleRateHz)];

    /// <summary>
    /// This channel's generator output at <paramref name="frameIndex"/> frames into the stream, in volts.
    ///
    /// <para>The frame index rather than a time in seconds, because
    /// <see cref="EchoChirpSettings.RepeatFrames"/> is a frame count and the whole
    /// frequency-response measurement depends on the wrap landing on exactly that frame. Deriving
    /// the frame back out of a <c>double</c> time would be one rounding away from a period that is
    /// off by a sample, which puts a slow drifting ripple on every measured trace.</para>
    /// </summary>
    /// <param name="channelIndex">
    /// Which generator channel this is: 0 = left, 1 = right. Only the stateful kinds read it, to keep
    /// their streams apart (see the pink shapers above); it defaults, so the deterministic kinds can
    /// still be asked for a sample without naming a channel.
    /// </param>
    public double ComputeSample(ChannelGeneratorConfig channel, long frameIndex, int channelIndex = 0)
    {
        if (!channel.Enabled)
            return 0.0;

        double t = frameIndex / sampleRateHz;

        return channel.Kind switch
        {
            GeneratorKind.Off => 0.0,
            GeneratorKind.Tones => SumTones(channel.Tones, t),
            GeneratorKind.Multitone => SumTones(channel.Tones, t),
            GeneratorKind.WhiteNoise => ComputeWhiteNoise(channel.Noise),
            GeneratorKind.PinkNoise => ComputePinkNoise(channel.Noise, channelIndex, frameIndex),
            GeneratorKind.EchoChirp => ComputeEchoChirp(channel.Chirp, frameIndex),

            // Shared with the simulator rather than re-derived here, unlike the chirp: the period
            // quantisation and the per-shape peak are arithmetic a second copy could get subtly
            // wrong, and a simulator at a different level or a different frequency than the
            // hardware is a DUT that behaves differently under --simulate.
            GeneratorKind.Square or GeneratorKind.Triangle =>
                PeriodicWave.Sample(channel.Kind, channel.Wave, frameIndex, sampleRateHz),

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

    /// <summary>
    /// The same noise shaped to 1/f, renormalised so <see cref="WhiteNoiseSettings.LevelDbv"/> still
    /// names the broadband RMS it names for white — which is exactly what
    /// <see cref="OutputRangeSelector.RequiredPeakVolts"/> assumes when it derives an output range
    /// from the configured level instead of from a measurement.
    /// </summary>
    private double ComputePinkNoise(WhiteNoiseSettings? settings, int channelIndex, long frameIndex)
    {
        if (settings is null)
            return 0.0;

        double sigma = Math.Pow(10.0, settings.LevelDbv / 20.0);
        var shaper = _pink[Math.Clamp(channelIndex, 0, _pink.Length - 1)];
        return sigma * shaper.NextStimulus(PinkNoiseShaper.WhiteAt(frameIndex));
    }

    /// <summary>Same closed-form exponential sweep as <c>Simulation.SignalSynthesizer.ComputeEchoChirp</c> — see that method's doc comment for the derivation.</summary>
    private double ComputeEchoChirp(EchoChirpSettings? settings, long frameIndex)
    {
        if (settings is null)
            return 0.0;

        double startHz = settings.StartFrequencyHz;
        double endHz = settings.EndFrequencyHz;
        if (startHz <= 0.0 || endHz <= startHz || settings.RepeatFrames < 1)
            return 0.0;

        // Integer modulo on the frame index, so the wrap lands on exactly the requested frame no
        // matter how long the stream has been running.
        long frameInPeriod = frameIndex % settings.RepeatFrames;
        if (frameInPeriod < 0)
            frameInPeriod += settings.RepeatFrames;

        double periodSeconds = settings.RepeatFrames / sampleRateHz;
        double tau = frameInPeriod / sampleRateHz;

        double k = Math.Log(endHz / startHz);
        double phase = 2.0 * Math.PI * startHz * periodSeconds / k
            * (Math.Exp(k * tau / periodSeconds) - 1.0);

        return PeakVolts(settings.AmplitudeDbv) * Math.Sin(phase) * EchoChirpFade(tau, periodSeconds);
    }

    private static double EchoChirpFade(double tau, double periodSeconds)
    {
        double fromEdge = Math.Min(tau, periodSeconds - tau);
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
