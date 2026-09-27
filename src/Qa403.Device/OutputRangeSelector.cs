namespace Qa403.Device;

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
    /// Crest factor allowed for Gaussian noise, whose peak is unbounded. 12 dB ≈ 4 sigma: clipping
    /// is then rare enough not to show up as distortion in a spectrum, without wasting a whole range
    /// step. Not a protocol fact — a tuning constant.
    ///
    /// <para>Serves the pink kind too, and at the same figure: shaping filters a Gaussian stream and
    /// leaves it Gaussian, and <see cref="PinkNoiseShaper"/> renormalises the result to the same RMS,
    /// so the peak-to-RMS ratio is the one white has.</para>
    /// </summary>
    public const double NoiseCrestFactorDb = 12.0;

    /// <summary>
    /// The lowest range in <paramref name="supportedRangesDbv"/> whose full scale clears
    /// <paramref name="config"/>'s worst-case peak plus <see cref="HeadroomDb"/>, or the largest
    /// supported range when nothing fits (better a range that clips than one that clips harder).
    /// <paramref name="supportedRangesDbv"/> must be in ascending order, as
    /// <see cref="DeviceCapabilities.OutputRangesDbv"/> is.
    /// </summary>
    public static double Select(GeneratorConfig config, IReadOnlyList<double> supportedRangesDbv)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (supportedRangesDbv is null || supportedRangesDbv.Count == 0)
            throw new ArgumentException("At least one output range is required.", nameof(supportedRangesDbv));

        // One register serves both channels, so the range has to satisfy the louder of the two.
        double requiredPeakV = Math.Max(RequiredPeakVolts(config.Left), RequiredPeakVolts(config.Right));

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
    /// <see cref="Qa403.App.Services.RangingMode.Manual"/> output range has to be asked — the user
    /// picked it, so the app's own margin is not theirs to enforce, but silent clipping is worth
    /// saying out loud. Exact equality does not clip: a tone at precisely a range's full scale is
    /// the common case (the ranges are 10 dB apart and users pick round numbers), and the
    /// calibration factor that can push it over is what <see cref="HeadroomDb"/> covers.</para>
    /// </summary>
    public static bool Clips(GeneratorConfig config, double rangeDbv)
    {
        ArgumentNullException.ThrowIfNull(config);

        double requiredPeakV = Math.Max(RequiredPeakVolts(config.Left), RequiredPeakVolts(config.Right));
        return requiredPeakV > PeakVolts(rangeDbv) * (1.0 + 1e-9);
    }

    /// <summary>
    /// Worst-case instantaneous peak, in volts, that one channel's generator can produce. Zero for a
    /// disabled or Off channel — which is why a silent generator selects the *smallest* range, the
    /// quietest state the DAC has.
    /// </summary>
    public static double RequiredPeakVolts(ChannelGeneratorConfig channel)
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
                ? Math.Pow(10.0, (noise.LevelDbv + NoiseCrestFactorDb) / 20.0)
                : 0.0,

            // A sweep is one sinusoid at a time, so its peak is just its amplitude.
            GeneratorKind.EchoChirp => channel.Chirp is { } chirp ? PeakVolts(chirp.AmplitudeDbv) : 0.0,

            _ => 0.0,
        };
    }

    /// <summary>dBV (rms-referenced) to peak volts — the same convention <c>Qa40xCalibration.VoltsToRaw</c> and the generator waveform use.</summary>
    private static double PeakVolts(double dbv) => Math.Sqrt(2.0) * Math.Pow(10.0, dbv / 20.0);
}
