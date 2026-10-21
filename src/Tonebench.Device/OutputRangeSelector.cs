namespace Tonebench.Device;

/// <summary>
/// Picks the smallest DAC output range that still fits what the generator is about to play.
///
/// The QA403's output range is an analog attenuator, so the DAC's own noise scales with it: running
/// a -15 dBV tone on the 18 dBV range throws away 33 dB of headroom and puts that noise straight
/// into a loopback measurement. QuantAsylum's own software does not expose the range at all — it
/// derives it from the requested amplitude, which is what this reproduces.
///
/// Deliberately not an autoranger: nothing here looks at measured signal. The generator config says
/// exactly what the DAC will be asked to produce, so the correct range is a pure function of it and
/// can be programmed before the first sample rather than converged on afterwards.
/// </summary>
public static class OutputRangeSelector
{
    /// <summary>
    /// Margin between the worst-case output peak and the selected range's full scale. Covers the
    /// per-channel DAC calibration factor, which <see cref="Hardware.Qa40xCalibration.VoltsToRaw"/>
    /// multiplies in after this decision and which can push a nominally-exact fit over full scale by
    /// a fraction of a dB. Not a protocol fact — a tuning constant.
    /// </summary>
    public const double HeadroomDb = 2.0;

    /// <summary>
    /// Crest factor allowed for a <b>continuous, random</b> noise stream, whose peak is unbounded.
    /// 12 dB ≈ 4 sigma: clipping is then rare enough not to show up as distortion in a spectrum,
    /// without wasting a whole range step. Not a protocol fact — a tuning constant.
    ///
    /// <para>Serves the continuous pink kind too, and at the same figure: shaping filters a Gaussian
    /// stream and leaves it Gaussian, and <see cref="PinkNoiseShaper"/> renormalises the result to
    /// the same RMS, so the peak-to-RMS ratio is the one white has.</para>
    ///
    /// <para><b>A periodic pink sequence is not covered by this and must not be.</b> That sequence is
    /// deterministic, so its crest is a measured property of the table rather than an allowance about
    /// a distribution — and it is not 12 dB: measured across every period from 8192 to 524288, all
    /// three sample rates and the whole cut-off range, <see cref="PeriodicPinkNoise.CrestFactorDb"/>
    /// runs from <b>11.37 to 14.49 dB</b>, rising with the period because more components make a
    /// longer tail. At the impedance screen's own record lengths it is 12.2–13.1 dB, so this constant
    /// under-read the peak by up to a decibel there and by 2.5 dB at the extreme — a quarter of a
    /// 10 dB range step and more, i.e. enough to park the DAC one rung too low and clip a stimulus
    /// that nothing then reports. <see cref="RequiredPeakVolts"/> therefore asks the table, whenever
    /// it is given a sample rate to build it with.</para>
    /// </summary>
    public const double NoiseCrestFactorDb = 12.0;

    /// <summary>
    /// The lowest range in <paramref name="supportedRangesDbv"/> whose full scale clears
    /// <paramref name="config"/>'s worst-case peak plus <see cref="HeadroomDb"/>, or the largest
    /// supported range when nothing fits (better a range that clips than one that clips harder).
    /// <paramref name="supportedRangesDbv"/> must be in ascending order, as
    /// <see cref="DeviceCapabilities.OutputRangesDbv"/> is.
    /// </summary>
    public static double Select(
        GeneratorConfig config,
        IReadOnlyList<double> supportedRangesDbv,
        double sampleRateHz = 0.0)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (supportedRangesDbv is null || supportedRangesDbv.Count == 0)
            throw new ArgumentException("At least one output range is required.", nameof(supportedRangesDbv));

        // One register serves both channels, so the range has to satisfy the louder of the two.
        double requiredPeakV = Math.Max(
            RequiredPeakVolts(config.Left, sampleRateHz),
            RequiredPeakVolts(config.Right, sampleRateHz));

        // The relative slack is float-rounding tolerance, nothing more. An amplitude exactly
        // HeadroomDb below a range's full scale is the common case, not a corner one — the ranges are
        // 10 dB apart and users pick round numbers — and comparing Pow(10, a/20) against
        // Pow(10, b/20) * Pow(10, HeadroomDb/20) for mathematically equal a and b lands either side
        // of equality depending on the values, which would silently cost a whole 10 dB range step.
        double neededPeakV = requiredPeakV * Math.Pow(10.0, HeadroomDb / 20.0) * (1.0 - 1e-9);

        foreach (double rangeDbv in supportedRangesDbv)
        {
            if (PeakVolts(rangeDbv) >= neededPeakV)
                return rangeDbv;
        }

        return supportedRangesDbv[^1];
    }

    /// <summary>
    /// Whether <paramref name="config"/> would clip against a range of <paramref name="rangeDbv"/>
    /// — i.e. whether the DAC is being asked for more than that range's full scale.
    ///
    /// <para>Separate from <see cref="Select"/>, and deliberately not "Select returned something
    /// bigger": Select keeps <see cref="HeadroomDb"/> in hand, so a range only 1 dB above the peak
    /// is one Select would not have chosen and is also not clipping. This is the question a
    /// <see cref="Tonebench.App.Services.RangingMode.Manual"/> output range has to be asked — the user
    /// picked it, so the app's own margin is not theirs to enforce, but silent clipping is worth
    /// saying out loud. Exact equality does not clip: a tone at precisely a range's full scale is
    /// the common case (the ranges are 10 dB apart and users pick round numbers), and the
    /// calibration factor that can push it over is what <see cref="HeadroomDb"/> covers.</para>
    /// </summary>
    public static bool Clips(GeneratorConfig config, double rangeDbv, double sampleRateHz = 0.0)
    {
        ArgumentNullException.ThrowIfNull(config);

        double requiredPeakV = Math.Max(
            RequiredPeakVolts(config.Left, sampleRateHz),
            RequiredPeakVolts(config.Right, sampleRateHz));
        return requiredPeakV > PeakVolts(rangeDbv) * (1.0 + 1e-9);
    }

    /// <summary>
    /// Worst-case instantaneous peak, in volts, that one channel's generator can produce. Zero for a
    /// disabled or Off channel — which is why a silent generator selects the *smallest* range, the
    /// quietest state the DAC has.
    /// </summary>
    /// <param name="sampleRateHz">
    /// The rate the stimulus will be played at, where the caller knows it. Only a <b>periodic</b>
    /// pink sequence reads it, and only to ask that sequence what its own crest factor is; 0 means
    /// "not known", which falls back to <see cref="NoiseCrestFactorDb"/>. Optional so every caller
    /// that cannot know the rate — a test, a panel's estimate — still gets the allowance rather than
    /// a wrong answer.
    /// </param>
    public static double RequiredPeakVolts(ChannelGeneratorConfig channel, double sampleRateHz = 0.0)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!channel.Enabled)
            return 0.0;

        return channel.Kind switch
        {
            GeneratorKind.Off => 0.0,

            // Tones sum coherently at t=0 (every one is a cosine starting at its own peak — see
            // Qa40xGeneratorWaveform.SumTones), so the true worst case really is the sum of peaks,
            // not an RMS combination. Covers Multitone too: Tones holds the derived comb.
            GeneratorKind.Tones or GeneratorKind.Multitone => channel.Tones.Sum(t => PeakVolts(t.AmplitudeDbv)),

            GeneratorKind.WhiteNoise or GeneratorKind.PinkNoise => channel.Noise is { } noise
                ? Math.Pow(10.0, (noise.LevelDbv + NoiseCrestDbFor(channel.Kind, noise, sampleRateHz)) / 20.0)
                : 0.0,

            // A sweep is one sinusoid at a time, so its peak is just its amplitude.
            GeneratorKind.EchoChirp => channel.Chirp is { } chirp ? PeakVolts(chirp.AmplitudeDbv) : 0.0,

            // Not PeakVolts: these two are the one place a level does not imply a sine's crest
            // factor. A square's peak is its rms (3.01 dB less headroom than a sine at the same
            // dBV) and a triangle's is sqrt(3) times it (4.77 dB more) — PeriodicWave.PeakVolts is
            // the single definition, and using the sine conversion here would park a triangle one
            // whole range step too low and clip every edge of it.
            GeneratorKind.Square or GeneratorKind.Triangle => channel.Wave is { } wave
                ? PeriodicWave.PeakVolts(channel.Kind, wave.LevelDbv)
                : 0.0,

            _ => 0.0,
        };
    }

    /// <summary>
    /// The crest factor to allow this noise stimulus: the periodic pink sequence's own measured one
    /// where it can be built, and <see cref="NoiseCrestFactorDb"/>'s allowance otherwise.
    ///
    /// <para>Never throws and never lets the table's construction decide a range: a cut-off outside
    /// <see cref="PeriodicPinkNoise.MinCutoffHz"/>..<see cref="PeriodicPinkNoise.MaxCutoffHz"/>, a
    /// period that is not a power of two, or anything else the table refuses, falls back to the
    /// allowance — a range chosen from a 12 dB guess is a measurement that may clip, while an
    /// exception here is an acquisition that will not start at all.</para>
    /// </summary>
    private static double NoiseCrestDbFor(GeneratorKind kind, WhiteNoiseSettings noise, double sampleRateHz)
    {
        if (kind != GeneratorKind.PinkNoise || noise.PeriodFrames <= 0 || !(sampleRateHz > 0.0))
            return NoiseCrestFactorDb;

        try
        {
            return PeriodicPinkNoise.For(noise.PeriodFrames, sampleRateHz, noise.CutoffHz).CrestFactorDb;
        }
        catch (ArgumentException)
        {
            return NoiseCrestFactorDb;
        }
    }

    /// <summary>dBV (rms-referenced) to peak volts — the same convention <c>Qa40xCalibration.VoltsToRaw</c> and the generator waveform use.</summary>
    private static double PeakVolts(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);
}
