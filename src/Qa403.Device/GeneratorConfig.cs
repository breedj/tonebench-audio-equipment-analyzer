namespace Qa403.Device;

/// <summary>One generator tone: frequency and amplitude.</summary>
public readonly record struct GeneratorTone(double FrequencyHz, double AmplitudeDbv);

/// <summary>Generator (DAC output) configuration. Mirrors <c>POST /api/generator</c>.</summary>
public sealed record GeneratorConfig(bool Enabled, IReadOnlyList<GeneratorTone> Tones)
{
    public static GeneratorConfig Off { get; } = new(Enabled: false, Tones: []);
}
