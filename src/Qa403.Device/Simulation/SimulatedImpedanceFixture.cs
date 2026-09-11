namespace Qa403.Device.Simulation;

/// <summary>
/// The impedance fixture the simulator pretends is patched to it, as numbers anything may read.
///
/// <para>Public, and separate from <see cref="SimulatedDut"/> (which is internal), because these are
/// not implementation details of the filter cascade — they are what the user has to type into the
/// impedance screen for a simulated run to read true, what its manual page quotes, and what the
/// tests assert against. One copy, so those three cannot drift apart.</para>
///
/// <para>The wiring is the one a bench fixture uses: the generator drives <see cref="ReferenceOhms"/>
/// in series with the load, the <b>left</b> channel carries the generator side untouched and the
/// <b>right</b> channel measures the junction. So the impedance screen's default reference channel,
/// Left, is correct against <c>--simulate</c> — unlike every DUT model, which sits on the left (see
/// <see cref="SimulatedDutModelExtensions.MeasuredChannel"/>).</para>
/// </summary>
public static class SimulatedImpedanceFixture
{
    /// <summary>
    /// The series reference resistor. The impedance screen's own reference-resistor setting has to
    /// match this to read true against the simulator; entering a different one scales every reading
    /// by the ratio.
    /// </summary>
    public const double ReferenceOhms = 100.0;

    /// <summary>
    /// The load on <see cref="SimulatedDutModel.ImpedanceResistive"/>: flat, real, and the number a
    /// correct measurement must report at every frequency.
    /// </summary>
    public const double ResistiveOhms = 8.0;

    /// <summary>The series resistance of <see cref="SimulatedDutModel.ImpedanceReactive"/>.</summary>
    public const double ReactiveOhms = 6.0;

    /// <summary>Its series inductance, which puts the 45-degree point at <c>R/(2 pi L)</c>, about 955 Hz.</summary>
    public const double ReactiveHenry = 1.0e-3;

    /// <summary>The voice-coil resistance of <see cref="SimulatedDutModel.ImpedanceLoudspeaker"/>, and therefore its impedance at DC.</summary>
    public const double SpeakerReOhms = 6.4;

    /// <summary>Its impedance at resonance, exactly — the motional term is all that survives there.</summary>
    public const double SpeakerPeakOhms = 60.0;

    /// <summary>Where that peak sits.</summary>
    public const double SpeakerResonanceHz = 45.0;

    /// <summary>How sharp it is. A mechanical Q in the range a real driver in free air actually has.</summary>
    public const double SpeakerQ = 2.5;

    /// <summary>The capacitor of <see cref="SimulatedDutModel.ImpedanceCapacitive"/>: 47 uF, so |Z| is 3.4 ohms at 1 kHz.</summary>
    public const double CapacitiveFarad = 47.0e-6;

    /// <summary>Its equivalent series resistance, which is where the phase stops short of -90 degrees at the top of the band.</summary>
    public const double CapacitiveEsrOhms = 0.3;

    /// <summary>
    /// The resistance of the shorted leads in <see cref="SimulatedDutModel.ImpedanceShort"/>. With
    /// <see cref="ShortHenry"/> these are the figures a real lead set measured on the bench at 0 dBV,
    /// once the drive was high enough to lift them out of the noise.
    /// </summary>
    public const double ShortOhms = 0.12;

    /// <summary>The leads' inductance, which is what lifts the short clear of the noise above a few kilohertz.</summary>
    public const double ShortHenry = 0.6e-6;

    /// <summary>
    /// The stray capacitance across the open terminals in <see cref="SimulatedDutModel.ImpedanceOpen"/>.
    /// Present so the divider ratio is <i>nearly</i> one rather than exactly one — see the model's own
    /// remarks — and small enough to be invisible: 0.07 degrees at 20 kHz against the 100 ohm reference.
    /// </summary>
    public const double OpenStrayFarad = 100.0e-12;
}
