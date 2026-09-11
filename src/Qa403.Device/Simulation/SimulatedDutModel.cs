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
/// <para><b>A DUT is on the left channel only</b>, which is how you would wire the bench: left
/// measures the DUT's output, right taps the generator directly as a reference. Filtering both
/// would make the impulse response's <c>RightChannel</c> reference mode divide the DUT straight
/// back out and report a flat line. <b>An impedance fixture is on the right channel only</b>, for
/// the same reason read the other way round — see <see cref="SimulatedDutModelExtensions.MeasuredChannel"/>.</para>
///
/// <para><b>Every DUT model is normalised to unity in its passband</b>, so switching one on changes
/// the shape of what comes back and not its level. A DUT with gain would be more realistic and would
/// silently clip the simulated ADC on every screen that is not expecting it — the simulator has no
/// autoranging to save it (see <see cref="SimulatedDevice.AutoRangeEnabled"/>). <see cref="PhonoRiaa"/>
/// is the one DUT allowed to break this, and its own remarks say why.</para>
///
/// <para><b>The four nonlinear models — <see cref="TriodeSingleEnded"/> through
/// <see cref="TransformerCoreSaturation"/> — carry their own distortion</b>, and selecting one
/// switches off the additive harmonic stack the synthesizer otherwise puts on the generator
/// (<see cref="SimulatedSignalProfile.HarmonicDbc"/>). That stack is level-independent by
/// construction, so under it a THD-vs-amplitude sweep is a flat line whatever the DUT; with it out
/// of the way the right channel is a clean generator tap, as on a real bench, and the left carries
/// only what the model does to the signal.</para>
///
/// <para><b>The <c>Impedance*</c> entries are the documented exception, because they are not
/// DUTs at all — they are <i>fixtures</i>.</b> Each one stands for a reference resistor with a load
/// hanging off it, and what the left channel carries is the junction between the two. A voltage
/// divider is never unity by construction, so these attenuate: the 8 ohm load reads 22.6 dB down on
/// the generator. That is safe in the one direction that matters here — a divider only ever
/// attenuates, so no amount of it can clip an input the way a model with gain would. They are wired
/// the way a bench fixture is: the <b>left</b> input taps the generator side of the reference resistor
/// and the <b>right</b> input the junction with the load, so the impedance screen's default —
/// reference channel Left — is right for the simulator too. (They used to sit on the left like every
/// other model, which put the measured node on the reference input; every simulated impedance then
/// needed the reference flipped to Right, and forgetting it read the 8 ohm fixture as −108 ohms.)
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

    /// <summary>
    /// A single-ended triode stage: flat, with a soft asymmetric transfer curve
    /// <c>y = x + k2 x^2 - k3 x^3</c> whose second harmonic grows in proportion to the drive.
    ///
    /// <para><b>The first model whose distortion depends on level</b>, and therefore the first that
    /// gives a THD-vs-amplitude sweep anything to draw. Every linear DUT above reads the same THD at
    /// every level, because the only distortion in the simulator until now was the fixed harmonic
    /// stack added to the <i>generator</i> — the same -46 dBc H2 at 1 mV and at 1 V. Selecting any
    /// of the nonlinear models switches that stack off, so what is measured is the DUT's own.</para>
    ///
    /// <para>And an answer you can work out: for a polynomial <c>H2/H1 = k2 A / 2</c> and
    /// <c>H3/H1 = k3 A^2 / 4</c> with <c>A</c> the peak drive, so H2 is 0.5% at 1 V peak and climbs
    /// exactly 1 dB per dB of level. The input is limited at <see cref="SimulatedDut.TriodeRailVolts"/>
    /// first, which keeps the polynomial monotonic and stands in for the stage running out of swing.</para>
    /// </summary>
    TriodeSingleEnded = 9,

    /// <summary>
    /// A class-AB output stage biased a little cold: a gain notch around zero,
    /// <c>y = x (1 - a e^(-x^2 / 2w^2))</c>, that every half-cycle passes through twice.
    ///
    /// <para>The signature is that <b>distortion rises as the level falls</b> — the opposite of every
    /// other amplifier — because the notch is a fixed width and a smaller signal spends more of its
    /// cycle inside it. On a THD-vs-amplitude sweep that is a hump peaking near -33 dBV; below that
    /// the whole signal sits inside the notch, where the stage is merely linear at reduced gain, and
    /// THD falls again. Odd symmetric, so odd harmonics only.</para>
    /// </summary>
    ClassAbCrossover = 10,

    /// <summary>
    /// A solid-state amplifier: flat and nearly distortionless (a cubic term at about -100 dBc at
    /// 1 V) until it meets its supply rail at <see cref="SimulatedDut.ClipRailVolts"/> peak, where it
    /// clips with a short soft knee.
    ///
    /// <para>The classic "THD versus power" knee, placed so the default 1 mV–1 V amplitude sweep
    /// reaches it right at its top: a flat floor and then a wall.</para>
    /// </summary>
    SolidStateClipping = 11,

    /// <summary>
    /// An output transformer whose core saturates: the linear response is flat, but the flux —
    /// the signal integrated below <see cref="SimulatedDut.CoreCornerHz"/> — is limited with a
    /// <c>tanh</c>, so third-harmonic distortion rises 18 dB per octave towards the bottom of the
    /// band and is absent above a couple of hundred hertz.
    ///
    /// <para>The model for a THD-vs-<i>frequency</i> sweep, and the one every transformer-coupled
    /// amplifier draws in real life. Bounded by construction, and computable: with the flux
    /// amplitude <c>A_f = A |LP(f)|</c> the small-signal third harmonic is
    /// <c>A_f^3 / (12 Phi^2)</c> relative to <c>A</c>, about 3% at 20 Hz for 1 V peak.</para>
    /// </summary>
    TransformerCoreSaturation = 12,

    /// <summary>
    /// A phono preamplifier: the RIAA playback curve, unity at 1 kHz, +19.3 dB at 20 Hz and -19.6 dB
    /// at 20 kHz, as one bilinear-mapped section of the three time constants (3180, 318, 75 us).
    ///
    /// <para><b>The one DUT model that is not unity across its passband</b>, deliberately: the
    /// curve is only recognisable referenced to 1 kHz, which is how every phono stage is specified.
    /// It is safe by default — the frequency-response runner ranges from the widest input range, and
    /// the spectrum screen's default tone is at 1 kHz — but a loud tone at the bottom of the band
    /// comes back nearly ten times larger than it went in, and will meet the simulated ADC's clip on
    /// the default 12 dBV range.</para>
    /// </summary>
    PhonoRiaa = 13,

    /// <summary>
    /// A digital processor: a fourth-order Butterworth low-pass at 20 kHz and
    /// <see cref="SimulatedDut.ProcessorLatencyMs"/> of pure latency.
    ///
    /// <para>The only model with a delay. Everything else here answers within a sample, so the
    /// frequency-response screen's "removed N sample delay" readout was never more than the chirp's
    /// arbitrary rotation, and the impulse response's peak was always at zero. This one puts the
    /// impulse 12 ms in — 576 samples at 48 kHz — and gives the latency readout a real number.</para>
    /// </summary>
    DigitalProcessor = 14,

    /// <summary>
    /// The impedance fixture with a <b>47 uF capacitor</b> and 0.3 ohms of ESR across it: |Z| falling
    /// 6 dB per octave from 68 ohms at 50 Hz to 0.4 ohms at 10 kHz, at a phase heading for -90 degrees.
    ///
    /// <para>The mirror of <see cref="ImpedanceReactive"/>, and the test that the phase axis has its
    /// sign right: an inductor and a capacitor read the same climb on a log-magnitude plot in opposite
    /// directions, and only the phase says which is which.</para>
    /// </summary>
    ImpedanceCapacitive = 15,

    /// <summary>
    /// The fixture with its leads <b>shorted</b>: 0.12 ohms and 0.6 uH, which is what a real pair of
    /// leads measured on the bench. Here so the short-calibration step can be run against the
    /// simulator — the open standard is <see cref="ImpedanceOpen"/>, or a straight wire.
    /// </summary>
    ImpedanceShort = 16,

    /// <summary>
    /// The fixture with nothing across it but 100 pF of stray capacitance. A straight wire is also an
    /// open, and works as the open standard; this one exists because a straight wire's divider ratio
    /// is <i>exactly</i> one, and <c>Z = R H / (1 - H)</c> of exactly one is a division by zero on a
    /// noise-free measurement. 100 pF puts the ratio 0.07 degrees off unity at 20 kHz, which reads as
    /// open on every plot and is finite everywhere.
    /// </summary>
    ImpedanceOpen = 17,
}

public static class SimulatedDutModelExtensions
{
    /// <summary>The <c>Impedance*</c> family: a reference resistor with a load hanging off it, rather than a device with a response.</summary>
    public static bool IsImpedanceFixture(this SimulatedDutModel model) => model
        is SimulatedDutModel.ImpedanceResistive
        or SimulatedDutModel.ImpedanceReactive
        or SimulatedDutModel.ImpedanceLoudspeaker
        or SimulatedDutModel.ImpedanceCapacitive
        or SimulatedDutModel.ImpedanceShort
        or SimulatedDutModel.ImpedanceOpen;

    /// <summary>
    /// The input channel a model is applied to: 0 (left) for a DUT, 1 (right) for an impedance
    /// fixture. Both follow the bench. A DUT's output is measured on the left with the generator
    /// tapped on the right as the reference; a series-resistor fixture has its generator side on
    /// the left — the impedance screen's default reference — and the junction with the load on the
    /// right. One channel each, never both: filtering both cancels the model out of any ratio.
    /// </summary>
    public static int MeasuredChannel(this SimulatedDutModel model) => model.IsImpedanceFixture() ? 1 : 0;
}
