namespace Qa403.Device;

/// <summary>One generator tone: frequency and amplitude.</summary>
public readonly record struct GeneratorTone(double FrequencyHz, double AmplitudeDbv);

/// <summary>One channel's generator state: on/off and its tone list.</summary>
public sealed record ChannelGeneratorConfig(bool Enabled, IReadOnlyList<GeneratorTone> Tones)
{
    public static ChannelGeneratorConfig Off { get; } = new(Enabled: false, Tones: []);
}

/// <summary>Generator (DAC output) configuration, independent per channel. Mirrors <c>POST /api/generator</c>.</summary>
public sealed record GeneratorConfig(ChannelGeneratorConfig Left, ChannelGeneratorConfig Right)
{
    public static GeneratorConfig Off { get; } = new(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);
}
