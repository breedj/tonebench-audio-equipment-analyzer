namespace Tonebench.Device.Simulation;

/// <summary>
/// The simulator's channel-to-channel coupling, on when <see cref="SimulatedSignalProfile.CrosstalkEnabled"/>
/// is: a copy of each channel's output, filtered and scaled, added to the other. Symmetric, so a
/// crosstalk run reads the same curve in both directions.
///
/// <para>The shape is the tutorial's: about −70 dB through the low and middle of the band, rising
/// 6 dB per octave above a few kilohertz the way capacitive coupling between adjacent channels does
/// (the SGC amplifier in the QA403 tutorial reads −69 dB low, −64 dB at 1 kHz, −51 dB at 20 kHz).
/// One analog zero at <see cref="CornerHz"/> gives the rise; a pole at <see cref="PlateauHz"/>, well
/// above the audio band, keeps the prototype proper so the bilinear transform has something to map.
/// The exact sampled response is <see cref="CouplingDb"/>, which is what the tests assert against —
/// the analog figures above are what the model was designed to, not what it is pinned to.</para>
///
/// <para>Applied <b>after the DUT and before hum and noise</b>: the victim carries a copy of the
/// aggressor's <em>output</em>, and the coupling is not a noise source. One filter state per source
/// channel, because one filter fed alternately by two channels returns two interleaved half-rate
/// streams that are neither channel's spectrum (the pink-noise rule).</para>
/// </summary>
public sealed class SimulatedCrosstalk
{
    /// <summary>The coupling in the flat part of the band, dB.</summary>
    public const double FloorDb = -70.0;

    /// <summary>Where the coupling starts rising at 6 dB per octave.</summary>
    public const double CornerHz = 6000.0;

    /// <summary>Where the rise levels off — above the band, present so the prototype is a proper rational function.</summary>
    public const double PlateauHz = 60_000.0;

    private readonly Biquad _section;
    private readonly double[] _state = new double[4];

    public SimulatedCrosstalk(double sampleRateHz)
    {
        _section = Section(sampleRateHz);
    }

    private static Biquad Section(double sampleRateHz)
    {
        double wz = 2.0 * Math.PI * CornerHz;
        double wp = 2.0 * Math.PI * PlateauHz;
        // H(s) = k0 · (1 + s/wz) / (1 + s/wp): numerator (0·s² + s/wz + 1), denominator (0·s² + s/wp + 1).
        return Biquad.FromAnalog(0.0, 1.0 / wz, 1.0, 0.0, 1.0 / wp, 1.0, sampleRateHz)
            .Scaled(Math.Pow(10.0, FloorDb / 20.0));
    }

    /// <summary>
    /// The exact coupling of the sampled model at <paramref name="freqHz"/>, in dB — the oracle a
    /// measurement of it is checked against. −70 dB at 1 kHz; about −59.6 dB at 20 kHz.
    /// </summary>
    public static double CouplingDb(double freqHz, double sampleRateHz) =>
        20.0 * Math.Log10(Section(sampleRateHz).ResponseAt(2.0 * Math.PI * freqHz / sampleRateHz).Magnitude);

    /// <summary>What <paramref name="x"/>, a sample of channel <paramref name="fromChannel"/>'s output, adds to the other channel. Stateful; call once per frame per source channel, in order.</summary>
    public double Couple(int fromChannel, double x)
    {
        int i = fromChannel * 2;
        double y = (_section.B0 * x) + _state[i];
        _state[i] = (_section.B1 * x) - (_section.A1 * y) + _state[i + 1];
        _state[i + 1] = (_section.B2 * x) - (_section.A2 * y);
        return y;
    }
}
