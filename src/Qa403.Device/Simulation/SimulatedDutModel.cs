namespace Qa403.Device.Simulation;

/// <summary>
/// Which device under test the simulator pretends is patched between the generator and the
/// analyzer input.
///
/// <para>Everything else in <see cref="SimulatedSignalProfile"/> shapes the signal
/// <em>additively</em> — a harmonic stack, hum, a noise floor — which is enough to exercise a
/// spectrum but leaves the simulated DUT with a perfectly flat response and no memory at all. That
/// makes it useless for the measurements whose whole subject is what a DUT does over
/// <i>time</i>: an impulse response is a single spike, its gated magnitude is a straight line, and
/// both decay surfaces show the null case. These models supply the missing half — a frequency
/// response with poles that ring, and in one case a reflection.</para>
///
/// <para><b>The DUT is on the left channel only</b>, which is how you would wire the bench: left
/// measures the DUT's output, right taps the generator directly as a reference. Filtering both
/// would make the impulse response's <c>RightChannel</c> reference mode divide the DUT straight
/// back out and report a flat line.</para>
///
/// <para><b>Every DUT model is normalised to unity in its passband</b>, so switching one on changes
/// the shape of what comes back and not its level. A DUT with gain would be more realistic and would
/// silently clip the simulated ADC on every screen that is not expecting it — the simulator has no
/// autoranging to save it (see <see cref="SimulatedDevice.AutoRangeEnabled"/>).</para>
///
/// <para><b>The three <c>Impedance*</c> entries are the documented exception, because they are not
/// DUTs at all — they are <i>fixtures</i>.</b> Each one stands for a reference resistor with a load
/// hanging off it, and what the left channel carries is the junction between the two. A voltage
/// divider is never unity by construction, so these attenuate: the 8 ohm load reads 22.6 dB down on
/// the generator. That is safe in the one direction that matters here — a divider only ever
/// attenuates, so no amount of it can clip an input the way a model with gain would. Note that they
/// invert the usual channel convention as a side effect: the left channel is the <i>measured</i> node
/// and the right is the clean reference, so an impedance measurement run against the simulator wants
/// its reference channel set to Right, the mirror image of a bench fixture wired left-as-reference.
/// The resistor they assume is <see cref="SimulatedImpedanceFixture.ReferenceOhms"/>, and entering a
/// different one on the impedance screen scales every reading by the ratio.</para>
/// </summary>
public enum SimulatedDutModel
{
    /// <summary>No DUT: the generator's output reaches the input unaltered. The default, and what the simulator has always done.</summary>
    StraightWire = 0,

    /// <summary>
    /// A push-pull valve amplifier with an output transformer: rolled off at both ends, with the
    /// transformer's own resonance just above the audio band and a gentle low-frequency lift where
    /// the primary inductance and the feedback loop meet.
    ///
    /// <para>Most of its high-frequency character sits above 24 kHz, so it is only visible when the
    /// span is set to 96 kHz — which is exactly the argument for measuring an amplifier at 192 kHz,
    /// and worth being able to demonstrate.</para>
    /// </summary>
    TubeAmplifier = 1,

    /// <summary>
    /// A two-way loudspeaker in a room: a bass alignment, a cabinet panel resonance that rings for
    /// a long time, a cone breakup peak, a tweeter roll-off — and a wall reflection three
    /// milliseconds behind the direct sound.
    ///
    /// <para>The model to reach for when demonstrating the impulse-response screen. The reflection
    /// is what the display gate exists to remove, and the two resonances are what the decay
    /// surfaces exist to show.</para>
    /// </summary>
    Loudspeaker = 2,

    /// <summary>
    /// A single second-order band-pass at 1 kHz with a Q of 20 — 50 Hz wide, unity at the centre,
    /// 6 dB per octave away either side.
    ///
    /// <para><b>The one model whose answer you can work out on paper</b>, and the reason it is here
    /// rather than as a fourth plausible picture. A second-order resonance's envelope decays as
    /// <c>e^(-w0 t / 2Q)</c>, which over one period of its own centre frequency is exactly
    /// <c>e^(-pi/Q)</c> — <c>27.29 / Q</c> dB, or 1.36 dB per period here, at every Q and every
    /// frequency. Point a burst decay at it and that is the slope it must draw. A cumulative spectral
    /// decay reads the same ring against milliseconds instead, which here happens to be the same
    /// number — 1.36 dB per millisecond — only because a period of 1 kHz <i>is</i> a millisecond.
    /// It is a check on the analysis rather than a demonstration of a DUT.</para>
    ///
    /// <para>Nothing else is in the path: no roll-off at the ends, no reflection, no second
    /// resonance to interfere with the first. That is what makes the number exact — see the
    /// loudspeaker's cabinet panel, which reads +4.99 dB rather than the +4.0 in its coefficients
    /// because the reflection's comb sits on top of it.</para>
    /// </summary>
    BandpassFilter = 3,

    /// <summary>
    /// An amplifier whose feedback loop has almost run out of phase margin: flat, with one very
    /// high-Q resonance at 1 kHz that rings for about a third of a second.
    ///
    /// <para>Nothing here is unstable — every pole is inside the unit circle and the output is a
    /// response to the input like any other DUT. What it demonstrates is what <i>nearly</i> unstable
    /// looks like on the decay surfaces: at Q 500 a resonance loses 27.29/Q = 0.055 dB per period,
    /// so a thirty-period burst decay falls barely 1.6 dB and the ridge simply refuses to die. On a
    /// magnitude plot it is a peak like any other; on a decay chart it is unmistakable, which is the
    /// argument for having decay charts at all.</para>
    /// </summary>
    MarginallyStable = 4,

    /// <summary>
    /// An amplifier that has gone the rest of the way and is oscillating: a resonator whose poles sit
    /// <i>outside</i> the unit circle, clipping against its own supply rail.
    ///
    /// <para><b>The rail is not a detail, it is what makes this a model rather than an overflow.</b>
    /// An unstable pole grows without bound, and in <c>double</c> that reaches infinity in about a
    /// minute and a half at this pole radius — after which the first subtraction produces a NaN and
    /// it spreads into the FFT, the metrics and anything saved. A real amplifier that sings clips at
    /// its rail and settles into a limit cycle, so the clamp lives <i>inside</i> the feedback path
    /// (see <see cref="SimulatedDut.Process"/>): the state that is fed back is the clamped output,
    /// which bounds the loop by construction rather than by hoping.</para>
    ///
    /// <para><b>It is the only DUT here whose output is not a response to its input</b>, and that is
    /// the point of it. Every measurement in this app assumes otherwise: sweep deconvolution smears
    /// a free-running tone across the whole record, coherence collapses, the output-level probe
    /// chases a signal the generator is not producing, and the input ranger steps on something that
    /// was never asked for. A reproducible case of that is worth having — a good half of the rules
    /// in CLAUDE.md are about a probe being fooled by something it did not expect.</para>
    ///
    /// <para>It starts from a small seed rather than from silence and takes a second or so to reach
    /// the rail, which is both what a real oscillator does and what stops the model being silent for
    /// ever: with the generator off, an exactly-zero input into an exactly-zero state stays zero.</para>
    ///
    /// <para>The pole is placed at 1 kHz and the limit cycle lands near <b>1010 Hz</b>, about 1%
    /// above it. That is not sloppiness — clipping drops the loop gain to exactly what sustains
    /// oscillation, and the frequency moves to where the phase still closes. An oscillator whose
    /// frequency depends on its amplitude is the real behaviour; see
    /// <see cref="SimulatedDut.OscillationExcess"/> for how it is kept to the same 1% at every
    /// sample rate.</para>
    /// </summary>
    Oscillating = 5,

    /// <summary>
    /// An impedance fixture with a plain <b>8 ohm resistor</b> across its terminals: a
    /// <see cref="SimulatedImpedanceFixture.ReferenceOhms"/> reference resistor in series, and the left
    /// channel on the junction.
    ///
    /// <para><b>The impedance measurement's calibration target</b>, and here for the same reason
    /// <see cref="BandpassFilter"/> is: it is the one whose answer needs no modelling to predict. A
    /// resistor has the same impedance at every frequency and no reactance whatever, so a correct
    /// measurement reads <b>8.000 ohms at 0.00 degrees</b> from end to end of the sweep — a flat line
    /// through a chain that involves a chirp, a two-channel ratio, an open calibration and a divider
    /// inversion. Anything that bends it is a bug in one of those, not a property of the load.</para>
    ///
    /// <para>There is no filter in the path at all: a resistive divider is a frequency-independent
    /// gain, so this model is a single <see cref="Biquad.Gain"/> section with no memory.</para>
    /// </summary>
    ImpedanceResistive = 6,

    /// <summary>
    /// The same fixture with a <b>6 ohm resistor in series with 1 mH</b> — the simplest load that has
    /// a phase angle, so it is what says the measurement recovers reactance and not just magnitude.
    ///
    /// <para>Its impedance rises from 6.0 ohms at DC through 45 degrees at about 955 Hz and on up at
    /// 6 dB per octave, which is a straight line on a log-log |Z| plot and a monotonic climb on the
    /// phase axis. Reading a rising magnitude with a <i>flat</i> phase is the classic symptom of the
    /// two channels being taken from different records, so this model catches that too.</para>
    /// </summary>
    ImpedanceReactive = 7,

    /// <summary>
    /// The same fixture with a loudspeaker on it: <b>6.4 ohms</b> of voice-coil resistance with a
    /// motional resonance at <b>45 Hz</b> peaking at <b>60 ohms</b>.
    ///
    /// <para>The shape an impedance sweep is normally run to see, and still exactly predictable: the
    /// voice coil in series with a parallel RLC makes the divider a single second-order section, so
    /// the peak really is <c>Re + Rres</c> at the resonance and <c>Re</c> at DC, with nothing else in
    /// the path to comb it. Deliberately without the voice coil's own inductance, which would make
    /// the impedance a ratio of cubics and cost exactly the property that makes this checkable.</para>
    /// </summary>
    ImpedanceLoudspeaker = 8,
}
