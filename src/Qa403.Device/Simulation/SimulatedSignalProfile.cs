namespace Qa403.Device.Simulation;

/// <summary>
/// Everything <see cref="SimulatedDevice"/> needs to synthesize an input signal. Immutable —
/// assign a new instance to <see cref="SimulatedDevice.SignalProfile"/> to change it; the
/// streaming loop picks up the new profile at the start of its next block.
/// </summary>
public sealed record SimulatedSignalProfile
{
    /// <summary>
    /// The left channel's generator state from the last <see cref="IAnalyzerDevice.ConfigureGeneratorAsync"/>
    /// call: <c>null</c> means "never configured", which falls back to the fixed
    /// <see cref="FundamentalFrequencyHz"/>/<see cref="FundamentalAmplitudeDbv"/> demo tone below
    /// (so the simulator shows something before the UI's Generator panel is touched). A non-null
    /// config that is disabled, or whose <see cref="ChannelGeneratorConfig.Kind"/> is
    /// <see cref="GeneratorKind.Off"/>, means silence at the generator — same as real hardware.
    ///
    /// For <see cref="GeneratorKind.Tones"/> each configured tone gets its own
    /// <see cref="HarmonicDbc"/> stack, same as the legacy single-fundamental path, standing in for
    /// DUT nonlinearity acting on that tone. The other kinds are synthesized without that stack —
    /// see <see cref="SignalSynthesizer"/>. <see cref="GeneratorRight"/> is independent.
    /// </summary>
    public ChannelGeneratorConfig? GeneratorLeft { get; init; }

    /// <summary>Right-channel counterpart to <see cref="GeneratorLeft"/> — same conventions, independent state.</summary>
    public ChannelGeneratorConfig? GeneratorRight { get; init; }

    /// <summary>Fallback tone used only while <see cref="GeneratorLeft"/>/<see cref="GeneratorRight"/> is <c>null</c> — see its doc.</summary>
    public double FundamentalFrequencyHz { get; init; } = 1000.0;

    /// <summary>Fallback tone amplitude, dBV referenced to 1 Vrms — see <see cref="GeneratorLeft"/>.</summary>
    public double FundamentalAmplitudeDbv { get; init; } = 0.0;

    public bool HarmonicsEnabled { get; init; } = true;

    /// <summary>
    /// Which device under test sits between the generator and the input — a frequency response with
    /// poles that ring, rather than the additive harmonics/hum/noise the rest of this record
    /// describes. See <see cref="SimulatedDutModel"/>, including why it is applied to the left
    /// channel only.
    /// </summary>
    public SimulatedDutModel Dut { get; init; } = SimulatedDutModel.StraightWire;

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

    /// <summary>
    /// Couples each channel's output into the other through <see cref="SimulatedCrosstalk"/> — a
    /// −70 dB floor rising 6 dB per octave above 6 kHz, symmetric. Off by default: every ratio
    /// measurement that reads the two inputs against each other would otherwise carry it.
    /// </summary>
    public bool CrosstalkEnabled { get; init; } = false;

    /// <summary>
    /// The output impedance the simulated amplifier presents — see <see cref="SimulatedOutputStage"/>.
    /// <see cref="SimulatedOutputStageModel.None"/> by default: an ideal source, so every existing
    /// measurement is untouched until both this and <see cref="Load"/> say otherwise.
    /// </summary>
    public SimulatedOutputStageModel OutputStage { get; init; } = SimulatedOutputStageModel.None;

    /// <summary>What hangs on the simulated amplifier's output. <see cref="SimulatedLoad.Open"/> by default.</summary>
    public SimulatedLoad Load { get; init; } = SimulatedLoad.Open;

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
