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
/// <para><b>Every model is normalised to unity in its passband</b>, so switching one on changes the
/// shape of what comes back and not its level. A DUT with gain would be more realistic and would
/// silently clip the simulated ADC on every screen that is not expecting it — the simulator has no
/// autoranging to save it (see <see cref="SimulatedDevice.AutoRangeEnabled"/>).</para>
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
}
