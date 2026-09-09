namespace Qa403.Device.Simulation;

/// <summary>
/// The impedance fixture the simulator pretends is patched to it, as numbers anything may read.
///
/// <para>Public, and separate from <see cref="SimulatedDut"/> (which is internal), because these are
/// not implementation details of the filter cascade — they are what the user has to type into the
/// impedance screen for a simulated run to read true, what its manual page quotes, and what the
/// tests assert against. One copy, so those three cannot drift apart.</para>
///
/// <para>The wiring is the one a bench fixture uses, mirrored: the generator drives
/// <see cref="ReferenceOhms"/> in series with the load, and the <b>left</b> channel measures the
/// junction while the right carries the generator untouched. That is the opposite of a fixture wired
/// left-as-reference, and it follows from the simulator applying its DUT to the left channel only —
/// so an impedance run against <c>--simulate</c> wants its reference channel set to Right.</para>
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
}
