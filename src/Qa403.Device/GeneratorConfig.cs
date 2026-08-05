namespace Qa403.Device;

/// <summary>One generator tone: frequency and amplitude.</summary>
public readonly record struct GeneratorTone(double FrequencyHz, double AmplitudeDbv);

/// <summary>
/// Which stimulus a channel's generator produces. Exactly one is active per channel — this is a
/// mode selector, not a set of flags, mirroring the legacy app's <c>OutputSources</c> enum and the
/// fact that a real DAC output carries one signal at a time.
/// </summary>
public enum GeneratorKind
{
    /// <summary>Silence, regardless of the settings below.</summary>
    Off,

    /// <summary>The explicit <see cref="ChannelGeneratorConfig.Tones"/> list (the original sine generator).</summary>
    Tones,

    /// <summary>Log-spaced tone comb derived from <see cref="MultitoneSettings"/> — see <see cref="MultitoneGenerator"/>.</summary>
    Multitone,

    /// <summary>Gaussian white noise at <see cref="WhiteNoiseSettings.LevelDbv"/>.</summary>
    WhiteNoise,

    /// <summary>Exponential ("Farina") sine sweep, the stimulus a frequency-response measurement uses.</summary>
    EchoChirp,
}

/// <summary>
/// Fractional-octave smoothing width for a frequency-response trace. Stored on
/// <see cref="EchoChirpSettings"/> and round-tripped through the API, but not consumed yet: the FR
/// measurement itself (capture, deconvolution, smoothed trace) is a later phase — see
/// <c>docs/TODO.txt</c>. Expect this to move onto the measurement config when that lands.
/// </summary>
public enum SmoothingOctaves
{
    None,
    OneOver96,
    OneOver48,
    OneOver24,
    OneOver12,
    OneOver6,
    OneOver3,
}

/// <summary>Parameters for <see cref="GeneratorKind.Multitone"/>. The band is fixed — see <see cref="MultitoneGenerator"/>.</summary>
public sealed record MultitoneSettings
{
    public int TonesPerOctave { get; init; } = 5;

    /// <summary>
    /// Combined RMS of all tones, dBV referenced to 1 Vrms. Exact only at 0 dB/octave rolloff:
    /// a non-zero <see cref="RolloffDbPerOctave"/> attenuates the upper tones and so lowers the
    /// actual total. See <see cref="MultitoneGenerator.Build"/>.
    /// </summary>
    public double TotalRmsDbv { get; init; } = -50.0;

    /// <summary>Positive = each octave above the band start is this many dB lower.</summary>
    public double RolloffDbPerOctave { get; init; }
}

/// <summary>Parameters for <see cref="GeneratorKind.WhiteNoise"/>.</summary>
public sealed record WhiteNoiseSettings
{
    /// <summary>
    /// Broadband RMS, dBV referenced to 1 Vrms. Note this is a *level*, not the dBV/sqrt(Hz)
    /// spectral density convention used by the simulated DUT's own noise floor
    /// (<c>SimulatedSignalProfile.NoiseDensityDbvPerRootHz</c>) — the two are not interchangeable.
    /// </summary>
    public double LevelDbv { get; init; } = -20.0;
}

/// <summary>Parameters for <see cref="GeneratorKind.EchoChirp"/>.</summary>
public sealed record EchoChirpSettings
{
    /// <summary>Sweep amplitude, dBV referenced to 1 Vrms.</summary>
    public double AmplitudeDbv { get; init; } = -20.0;

    public double StartFrequencyHz { get; init; } = 20.0;

    public double EndFrequencyHz { get; init; } = 20_000.0;

    /// <summary>Display smoothing for the resulting frequency-response trace — stored only, see <see cref="SmoothingOctaves"/>.</summary>
    public SmoothingOctaves Smoothing { get; init; } = SmoothingOctaves.None;
}

/// <summary>
/// One channel's generator state: on/off, which <see cref="GeneratorKind"/> is active, and the
/// settings for each kind.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> and the settings blocks are init-only properties rather than positional
/// parameters so the original <c>new ChannelGeneratorConfig(enabled, tones)</c> call sites keep
/// working and keep meaning what they meant (a plain sine tone list).
///
/// <see cref="Tones"/> is the *effective* tone list: for <see cref="GeneratorKind.Multitone"/> it
/// holds the comb derived from <see cref="Multitone"/>, so everything downstream that reads the
/// generator's tones (device status, the UI's fundamental tracking) works for multitone without
/// knowing anything about how the comb was built.
/// </remarks>
public sealed record ChannelGeneratorConfig(bool Enabled, IReadOnlyList<GeneratorTone> Tones)
{
    public GeneratorKind Kind { get; init; } = GeneratorKind.Tones;

    public MultitoneSettings? Multitone { get; init; }

    public WhiteNoiseSettings? Noise { get; init; }

    public EchoChirpSettings? Chirp { get; init; }

    public static ChannelGeneratorConfig Off { get; } = new(Enabled: false, Tones: []) { Kind = GeneratorKind.Off };
}

/// <summary>Generator (DAC output) configuration, independent per channel. Mirrors <c>POST /api/generator</c>.</summary>
public sealed record GeneratorConfig(ChannelGeneratorConfig Left, ChannelGeneratorConfig Right)
{
    public static GeneratorConfig Off { get; } = new(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);
}
