namespace Tonebench.Device.Simulation;

/// <summary>
/// Per-sample synthesis of a simulated DUT signal: the channel's generator stimulus (tones,
/// multitone comb, white or pink noise, or an exponential sweep) plus mains hum and white + 1/f
/// noise. Every deterministic part (tones, chirp, hum) is a pure function of elapsed time <c>t</c>,
/// so it is exactly reproducible regardless of block boundaries or timer jitter — only the noise
/// draws are stateful (RNG position, pink filter state), and that state lives here rather than in
/// <see cref="SimulatedDevice"/> so a stream restart gets a fresh synthesizer and a fresh noise
/// sequence. Note that since <see cref="GeneratorKind.WhiteNoise"/> exists, that statefulness now
/// reaches <see cref="ComputeChannelSample"/> too, not just the shared noise floor. The pink
/// stimulus is the one kind that is stateful and reproducible at once: its filter carries state, but
/// its input is a function of the frame index (<see cref="PinkNoiseShaper.WhiteAt"/>), which is what
/// makes the two channels carry the same noise.
/// </summary>
internal sealed class SignalSynthesizer(Random random, double sampleRateHz)
{
    private const double HumHarmonicDecayDb = -6.0;

    /// <summary>Raised-cosine fade at each end of a sweep, so the wrap back to the start frequency isn't a step.</summary>
    private const double EchoChirpFadeSeconds = 0.005;

    /// <summary>
    /// The DUT noise floor's own 1/f shaper. Uncalibrated by design — see <see cref="NextPinkSample"/>
    /// — and quite separate from the generator's, which is per channel and RMS-normalised.
    /// </summary>
    private readonly PinkNoiseShaper _floorPink = new(sampleRateHz);

    /// <summary>
    /// One shaper per generator channel for <see cref="GeneratorKind.PinkNoise"/>, for the reason
    /// <c>Hardware.Qa40xGeneratorWaveform</c> sets out at its own copy: one filter fed alternately
    /// from two channels produces two half-rate streams rather than two pink ones.
    /// </summary>
    private readonly PinkNoiseShaper[] _generatorPink =
        [new PinkNoiseShaper(sampleRateHz), new PinkNoiseShaper(sampleRateHz)];

    /// <summary>
    /// This channel's generator content for <paramref name="channelIndex"/> (0 = left, 1 = right):
    /// whichever <see cref="GeneratorKind"/> that channel has selected, or the legacy demo tone /
    /// intermodulation pair when that channel has never been configured — see
    /// <see cref="SimulatedSignalProfile.GeneratorLeft"/>.
    /// </summary>
    public double ComputeChannelSample(SimulatedSignalProfile profile, long frameIndex, int channelIndex)
    {
        var channel = channelIndex == 0 ? profile.GeneratorLeft : profile.GeneratorRight;
        double t = frameIndex / sampleRateHz;

        // Never configured: the legacy demo-tone path, which is also the only path the profile's
        // own IntermodulationMode override applies to (a DUT-simulation knob, not a stimulus kind).
        if (channel is null)
            return ComputeTones(profile, t, channelTones: null);

        if (!channel.Enabled)
            return 0.0;

        return channel.Kind switch
        {
            GeneratorKind.Off => 0.0,
            GeneratorKind.Tones => ComputeTones(profile, t, channel.Tones),
            GeneratorKind.Multitone => ComputeMultitone(channel.Tones, t),
            GeneratorKind.WhiteNoise => ComputeWhiteNoise(channel.Noise),
            GeneratorKind.PinkNoise => ComputePinkNoise(channel.Noise, channelIndex, frameIndex),
            GeneratorKind.EchoChirp => ComputeEchoChirp(channel.Chirp, frameIndex),

            // The same call the real TX loop makes (see Hardware.Qa40xGeneratorWaveform). Note no
            // SimulatedSignalProfile.HarmonicDbc stack is added on top, as ComputeTones does: these
            // shapes are harmonics, 48.3 % of them for a square, so a fake -46 dBc stack under that
            // would be unmeasurable and only muddy the one spectrum whose lines are known exactly.
            GeneratorKind.Square or GeneratorKind.Triangle =>
                PeriodicWave.Sample(channel.Kind, channel.Wave, frameIndex, sampleRateHz),

            _ => 0.0,
        };
    }

    /// <summary>
    /// Content shared identically across every channel: mains hum plus noise. Call once per
    /// frame (not once per channel) so hum/noise stay correlated across channels exactly like
    /// today's single-signal-duplicated-across-channels behaviour — only the generator tones
    /// (<see cref="ComputeChannelSample"/>) are meant to diverge per channel.
    /// </summary>
    public double ComputeSharedAdditive(SimulatedSignalProfile profile, double t, double sampleRateHz)
    {
        double sample = ComputeHum(profile, t);

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

    /// <summary>
    /// Symmetric hard clip at ±<paramref name="clipPeakV"/>: what a DAC does when it runs out of
    /// codes at 0 dBFS — the waveform is truncated flat, with no knee and no compression below the
    /// limit. Deliberately not <see cref="SoftClip"/>: that knee models an analog input stage
    /// easing into saturation, and reusing it here would understate the harmonics a converter
    /// actually produces when it is driven past full scale.
    /// </summary>
    public static double HardClip(double sample, double clipPeakV)
    {
        if (clipPeakV <= 0.0)
            return sample;

        return Math.Clamp(sample, -clipPeakV, clipPeakV);
    }

    public static double DbvToPeakV(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);

    private static double ComputeTones(SimulatedSignalProfile profile, double t, IReadOnlyList<GeneratorTone>? channelTones)
    {
        if (profile.Intermodulation != IntermodulationMode.None)
            return ComputeIntermodulation(profile, t);

        if (channelTones is { } generatorTones)
            return ComputeGeneratorTones(profile, generatorTones, t);

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

    /// <summary>Each configured generator tone is treated as its own fundamental, with its own harmonic stack (see <see cref="SimulatedSignalProfile.GeneratorLeft"/>).</summary>
    private static double ComputeGeneratorTones(SimulatedSignalProfile profile, IReadOnlyList<GeneratorTone> tones, double t)
    {
        double sum = 0.0;
        var harmonicDbc = profile.HarmonicDbc;

        foreach (var tone in tones)
        {
            double peakV = DbvToPeakV(tone.AmplitudeDbv);
            sum += peakV * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * t);

            if (!profile.HarmonicsEnabled)
                continue;

            for (int i = 0; i < harmonicDbc.Count; i++)
            {
                int order = i + 2;
                double amplitudeV = peakV * Math.Pow(10.0, harmonicDbc[i] / 20.0);
                sum += amplitudeV * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * order * t);
            }
        }

        return sum;
    }

    /// <summary>
    /// The multitone comb, summed as bare cosines. Deliberately skips the per-tone
    /// <see cref="SimulatedSignalProfile.HarmonicDbc"/> stack that <see cref="ComputeGeneratorTones"/>
    /// applies: the default comb is ~60 tones, and 60 x 8 harmonics per sample per channel at
    /// 192 kS/s stereo is ~184 M cos() per second of audio, well past what the streaming loop can
    /// afford (see CLAUDE.md "Performance constraints"). Fundamentals only is ~23 M/s. The soft
    /// clipper in <see cref="SimulatedDevice"/> still supplies nonlinearity at high levels.
    /// </summary>
    private static double ComputeMultitone(IReadOnlyList<GeneratorTone> tones, double t)
    {
        double sum = 0.0;
        for (int i = 0; i < tones.Count; i++)
        {
            var tone = tones[i];
            sum += DbvToPeakV(tone.AmplitudeDbv) * Math.Cos(2.0 * Math.PI * tone.FrequencyHz * t);
        }

        return sum;
    }

    /// <summary>
    /// Gaussian white noise at the configured broadband RMS. <c>LevelDbv</c> is a level referenced
    /// to 1 Vrms, so sigma is the level itself — no peak conversion (unlike a tone's dBV) and no
    /// sqrt(fs/2) density conversion (unlike <see cref="NextNoiseSample"/>, which is calibrated in
    /// dBV/sqrt(Hz)). Drawn per channel, so left and right are independent, as two real generator
    /// channels would be.
    /// </summary>
    private double ComputeWhiteNoise(WhiteNoiseSettings? settings)
    {
        if (settings is null)
            return 0.0;

        double sigma = Math.Pow(10.0, settings.LevelDbv / 20.0);
        return sigma * NextGaussian();
    }

    /// <summary>
    /// The generator's pink noise: the same draw as <see cref="ComputeWhiteNoise"/>, shaped to 1/f
    /// and renormalised so <c>LevelDbv</c> still means the same broadband RMS. Shares its
    /// coefficients — and its frame-indexed white input — with the real device's TX synthesis
    /// through <see cref="PinkNoiseShaper"/>, so a stimulus verified against <c>--simulate</c> is the
    /// stimulus hardware plays. That input is what makes left and right identical, which this
    /// simulator needs and the impedance measurement rests on: the right channel stands in for the
    /// reference tap of a fixture that a bench drives with the very same signal.
    ///
    /// <para>Note this is <i>not</i> the filter behind <see cref="NextPinkSample"/>: that one shapes
    /// the simulated DUT's own noise floor, is deliberately uncalibrated, and must keep its state
    /// apart from a stimulus that comes and goes with the generator.</para>
    /// </summary>
    private double ComputePinkNoise(WhiteNoiseSettings? settings, int channelIndex, long frameIndex)
    {
        if (settings is null)
            return 0.0;

        double sigma = Math.Pow(10.0, settings.LevelDbv / 20.0);

        // Periodic when a measurement asked for it: a pure function of the frame index, so both
        // channels play the same sequence and a re-primed queue rebuilds it. The filtered-white form
        // below is recursive and therefore cannot do either on its own -- it relies on being called
        // once per frame in order.
        if (settings.PeriodFrames > 1)
            return sigma * PeriodicNoise.For(settings.PeriodFrames, sampleRateHz, settings.CutoffHz, settings.Shape).Sample(frameIndex);

        var shaper = _generatorPink[Math.Clamp(channelIndex, 0, _generatorPink.Length - 1)];
        return sigma * shaper.NextStimulus(PinkNoiseShaper.WhiteAt(frameIndex));
    }

    /// <summary>
    /// Exponential ("Farina") sine sweep from <c>StartFrequencyHz</c> to <c>EndFrequencyHz</c>,
    /// restarting every <see cref="EchoChirpSettings.RepeatFrames"/> frames. Instantaneous frequency
    /// is <c>f(tau) = f1 (f2/f1)^(tau/T)</c>, so the phase integrates to
    /// <c>2*pi*f1*T/k * (e^(k*tau/T) - 1)</c> with <c>k = ln(f2/f1)</c>. Uses sin (not cos) so the
    /// sweep starts at zero crossing, and fades in/out so the wrap isn't a step.
    ///
    /// <para>The period is a frame count, and the wrap is computed with integer modulo on the frame
    /// index, because the frequency-response measurement sets it to its own FFT size and depends on
    /// the two matching exactly — see <see cref="EchoChirpSettings.RepeatFrames"/>. That the
    /// simulator honours this too is what lets the measurement be tested without hardware.</para>
    /// </summary>
    private double ComputeEchoChirp(EchoChirpSettings? settings, long frameIndex)
    {
        if (settings is null)
            return 0.0;

        double startHz = settings.StartFrequencyHz;
        double endHz = settings.EndFrequencyHz;
        if (startHz <= 0.0 || endHz <= startHz || settings.RepeatFrames < 1)
            return 0.0;

        long frameInPeriod = frameIndex % settings.RepeatFrames;
        if (frameInPeriod < 0)
            frameInPeriod += settings.RepeatFrames;

        double periodSeconds = settings.RepeatFrames / sampleRateHz;
        double tau = frameInPeriod / sampleRateHz;

        double k = Math.Log(endHz / startHz);
        double phase = 2.0 * Math.PI * startHz * periodSeconds / k
            * (Math.Exp(k * tau / periodSeconds) - 1.0);

        return DbvToPeakV(settings.AmplitudeDbv) * Math.Sin(phase) * EchoChirpFade(tau, periodSeconds);
    }

    private static double EchoChirpFade(double tau, double periodSeconds)
    {
        double fromEdge = Math.Min(tau, periodSeconds - tau);
        if (fromEdge >= EchoChirpFadeSeconds)
            return 1.0;

        return 0.5 * (1.0 - Math.Cos(Math.PI * fromEdge / EchoChirpFadeSeconds));
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
    // one-sided PSD is flat at sigma^2/(fs/2) V^2/Hz (see Tonebench.Dsp.Tests golden test 5), so
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
    // the already-calibrated white sample so its magnitude tracks the configured density. The
    // raw (un-normalised) shaper output times 0.25, which is what this has always been: the
    // coefficients moved into PinkNoiseShaper so the generator can share them, and the arithmetic
    // is unchanged, floor level included.
    private double NextPinkSample(double white) => _floorPink.Next(white) * 0.25;
}
