namespace Qa403.Device.Simulation;

/// <summary>
/// Everything <see cref="SimulatedDevice"/> needs to synthesize an input signal. Immutable —
/// assign a new instance to <see cref="SimulatedDevice.SignalProfile"/> to change it; the
/// streaming loop picks up the new profile at the start of its next block.
/// </summary>
public sealed record SimulatedSignalProfile
{
    public double FundamentalFrequencyHz { get; init; } = 1000.0;

    /// <summary>Fundamental amplitude, dBV referenced to 1 Vrms.</summary>
    public double FundamentalAmplitudeDbv { get; init; } = 0.0;

    public bool HarmonicsEnabled { get; init; } = true;

    /// <summary>
    /// dBc for H2..H9, index 0 = H2. Defaults to a single-ended triode profile: H2 dominant at
    /// -46 dBc, H3 at -60 dBc, each subsequent harmonic ~12 dB down.
    /// </summary>
    public IReadOnlyList<double> HarmonicDbc { get; init; } = DefaultTriodeHarmonicDbc;

    public bool HumEnabled { get; init; } = true;

    public double HumFundamentalHz { get; init; } = 50.0;

    /// <summary>Level of the 50 Hz hum fundamental, dBV referenced to 1 Vrms.</summary>
    public double HumLevelDbv { get; init; } = -90.0;

    /// <summary>Hum components above the fundamental, each 6 dB down from the previous (50, 100, 150 Hz... by default).</summary>
    public int HumHarmonicCount { get; init; } = 3;

    public bool NoiseEnabled { get; init; } = true;

    /// <summary>White noise floor density, dBV/sqrt(Hz).</summary>
    public double NoiseDensityDbvPerRootHz { get; init; } = -150.0;

    /// <summary>
    /// Adds 1/f texture below a few hundred Hz on top of the white floor. Approximate by
    /// construction (a Kellet pink filter has no closed-form absolute calibration) — the
    /// density above is the flat, verified floor; disable this for a purely white-noise
    /// measurement.
    /// </summary>
    public bool PinkNoiseEnabled { get; init; } = true;

    public IntermodulationMode Intermodulation { get; init; } = IntermodulationMode.None;

    /// <summary>Total two-tone amplitude (both tones combined), dBV referenced to 1 Vrms. Only used when <see cref="Intermodulation"/> is not <see cref="IntermodulationMode.None"/>.</summary>
    public double IntermodulationAmplitudeDbv { get; init; } = 0.0;

    public static IReadOnlyList<double> DefaultTriodeHarmonicDbc { get; } = BuildDefaultTriodeHarmonicDbc();

    private static IReadOnlyList<double> BuildDefaultTriodeHarmonicDbc()
    {
        var dbc = new double[8]; // H2..H9
        dbc[0] = -46.0; // H2
        dbc[1] = -60.0; // H3
        for (int i = 2; i < dbc.Length; i++)
            dbc[i] = dbc[i - 1] - 12.0; // H4..H9

        return dbc;
    }
}
