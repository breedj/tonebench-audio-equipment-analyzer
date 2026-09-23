using System.Numerics;

namespace Qa403.Device.Simulation;

/// <summary>
/// One biquad section's coefficients, normalised so <c>a0 = 1</c>. Built with the usual
/// Audio-EQ-Cookbook designs.
///
/// <para>Its own tiny implementation rather than anything from <c>Qa403.Dsp</c> on purpose:
/// <c>Qa403.Device</c> does not reference that project, and the simulator is the one place in the
/// app that has to <em>generate</em> a response rather than measure one. Nothing here is on a
/// measurement path.</para>
/// </summary>
internal readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    private static (double Cos, double Alpha) Prepare(double freqHz, double q, double sampleRateHz)
    {
        // A pole asked for above Nyquist cannot be designed at all, and clamping is the honest
        // answer: a transformer resonance at 42 kHz is genuinely out of band at 48 kHz, and the
        // model should flatten there rather than fold it back down the spectrum.
        double f0 = Math.Clamp(freqHz, 1.0, sampleRateHz * 0.45);
        double w0 = 2.0 * Math.PI * f0 / sampleRateHz;
        return (Math.Cos(w0), Math.Sin(w0) / (2.0 * Math.Max(0.05, q)));
    }

    private static Biquad Normalise(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);

    public static Biquad LowPass(double freqHz, double q, double sampleRateHz)
    {
        var (cos, alpha) = Prepare(freqHz, q, sampleRateHz);
        double b1 = 1.0 - cos;
        return Normalise(b1 / 2.0, b1, b1 / 2.0, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
    }

    public static Biquad HighPass(double freqHz, double q, double sampleRateHz)
    {
        var (cos, alpha) = Prepare(freqHz, q, sampleRateHz);
        double b0 = (1.0 + cos) / 2.0;
        return Normalise(b0, -(1.0 + cos), b0, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
    }

    /// <summary>
    /// A second-order band-pass, unity at <paramref name="freqHz"/> and falling away 6 dB per octave
    /// either side of it. The constant-peak-gain form, so the centre stays at 0 dB whatever
    /// <paramref name="q"/> is.
    /// </summary>
    public static Biquad BandPass(double freqHz, double q, double sampleRateHz)
    {
        var (cos, alpha) = Prepare(freqHz, q, sampleRateHz);
        return Normalise(alpha, 0.0, -alpha, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
    }

    /// <summary>
    /// A resonator with its pole pair placed at an explicit radius, which is the one design here that
    /// is not from the cookbook — every cookbook form is stable by construction, and
    /// <paramref name="poleRadius"/> above 1 is exactly what this exists to allow.
    ///
    /// <para>Poles at <c>r e^(±j w0)</c> give the denominator <c>1 - 2 r cos(w0) z^-1 + r^2 z^-2</c>.
    /// The numerator is the band-pass one, so the section has a zero at DC and at Nyquist and cannot
    /// pass a standing offset — an oscillator that drifts to a rail and stays there is not a model of
    /// anything.</para>
    /// </summary>
    public static Biquad Resonator(double freqHz, double excess, double sampleRateHz)
    {
        double f0 = Math.Clamp(freqHz, 1.0, sampleRateHz * 0.45);
        double w0 = 2.0 * Math.PI * f0 / sampleRateHz;

        // The pole radius is 1 + excess*w0^2 rather than a fixed number, and that scaling is not
        // decoration. A clipped oscillator's limit cycle does not sit at its small-signal pole: the
        // clipping drops the loop gain to exactly what sustains oscillation, and the frequency moves
        // to where the phase still closes, cos(w) = cos(w0)/r. How far it moves depends on how far
        // outside the circle the pole sits *relative to its distance from the real axis* — so a
        // radius fixed per sample pulls further at every higher sample rate. Measured: a fixed
        // 1.0005 sings at 1029 Hz at 48 kHz and 1390 Hz at 192 kHz for a pole placed at 1000 Hz.
        // Scaling the excess to w0^2 makes the pull a constant fraction instead: 1% everywhere.
        double poleRadius = 1.0 + (excess * w0 * w0);
        double gain = (1.0 - poleRadius) / 2.0;
        return new Biquad(gain, 0.0, -gain, -2.0 * poleRadius * Math.Cos(w0), poleRadius * poleRadius);
    }

    /// <summary>
    /// A frequency-independent gain, as a section with no memory. The impedance fixtures need one
    /// because a purely resistive load is a plain voltage divider -- a real response with no filter
    /// in it at all -- and expressing that as a section keeps <see cref="SimulatedDut.Process"/> a
    /// single uniform loop rather than growing a special case.
    /// </summary>
    public static Biquad Gain(double gain) => new(gain, 0.0, 0.0, 0.0, 0.0);

    /// <summary>
    /// One analog section <c>(b2 s^2 + b1 s + b0) / (a2 s^2 + a1 s + a0)</c> mapped to the z plane by
    /// the bilinear transform. The one design here written in terms of a circuit rather than a corner
    /// frequency, because that is how an impedance divider is stated.
    ///
    /// <para><b>Deliberately not prewarped.</b> Prewarping pins one chosen frequency and moves every
    /// other one, which is right for a filter specified by its corner and wrong here: what these
    /// models have to be is <i>exactly</i> analysable, not exactly 45 Hz. The sampled system's true
    /// impedance is whatever <see cref="ResponseAt"/> says at each bin, and that is what the tests
    /// assert against. At audio frequencies against a 48 kHz clock the warping is invisible anyway --
    /// <c>tan(x) ~ x</c> to seven figures at 45 Hz.</para>
    /// </summary>
    public static Biquad FromAnalog(
        double b2, double b1, double b0, double a2, double a1, double a0, double sampleRateHz) =>
        Bilinear(b2, b1, b0, a2, a1, a0, 2.0 * sampleRateHz);

    /// <summary>
    /// The same mapping <b>prewarped</b> so that <paramref name="prewarpHz"/> lands exactly on
    /// itself — the right choice for a filter specified by its corner, which is what
    /// <see cref="FilterPrototype.Sections"/> builds, and the wrong one for a circuit (see the
    /// overload above). <c>k = w / tan(w T / 2)</c> in place of <c>2 fs</c>; the two agree to
    /// <c>(wT)^2/12</c>, i.e. within 0.6 % for 2 kHz at 48 kHz, which is the size of the corner
    /// error the plain mapping would put on the response.
    /// </summary>
    public static Biquad FromAnalog(
        double b2, double b1, double b0, double a2, double a1, double a0, double sampleRateHz, double prewarpHz)
    {
        double w = 2.0 * Math.PI * Math.Clamp(prewarpHz, 1.0, sampleRateHz * 0.45);
        return Bilinear(b2, b1, b0, a2, a1, a0, w / Math.Tan(w / (2.0 * sampleRateHz)));
    }

    private static Biquad Bilinear(
        double b2, double b1, double b0, double a2, double a1, double a0, double k)
    {
        // s -> k(1 - z^-1)/(1 + z^-1). Multiplying through by (1 + z^-1)^2 turns each analog
        // coefficient into three z-plane ones.
        double kk = k * k;

        return Normalise(
            (b2 * kk) + (b1 * k) + b0,
            2.0 * (b0 - (b2 * kk)),
            (b2 * kk) - (b1 * k) + b0,
            (a2 * kk) + (a1 * k) + a0,
            2.0 * (a0 - (a2 * kk)),
            (a2 * kk) - (a1 * k) + a0);
    }

    /// <summary>
    /// This section's exact response at a normalised angular frequency, i.e. <c>H(e^(j w))</c>. What
    /// the impedance fixture tests compare against: the discrete filter that actually produced the
    /// samples, not the analog prototype it was mapped from.
    /// </summary>
    public Complex ResponseAt(double omega)
    {
        var z1 = Complex.FromPolarCoordinates(1.0, -omega);
        var z2 = z1 * z1;
        return (B0 + (B1 * z1) + (B2 * z2)) / (1.0 + (A1 * z1) + (A2 * z2));
    }

    /// <summary>A resonance: <paramref name="gainDb"/> of lift (or cut) at <paramref name="freqHz"/>, unity well away from it.</summary>
    public static Biquad Peaking(double freqHz, double q, double gainDb, double sampleRateHz)
    {
        var (cos, alpha) = Prepare(freqHz, q, sampleRateHz);
        double a = Math.Pow(10.0, gainDb / 40.0);
        return Normalise(
            1.0 + (alpha * a), -2.0 * cos, 1.0 - (alpha * a),
            1.0 + (alpha / a), -2.0 * cos, 1.0 - (alpha / a));
    }

    /// <summary>This section with its numerator scaled by <paramref name="gain"/> — the same shape, a different level.</summary>
    public Biquad Scaled(double gain) => new(B0 * gain, B1 * gain, B2 * gain, A1, A2);

    /// <summary>
    /// The RIAA playback curve — time constants 3180, 318 and 75 us — normalised to unity at 1 kHz.
    ///
    /// <para><b>Not the bilinear transform</b>, on purpose. The analog curve keeps falling 6 dB per
    /// octave past its 75 us pole, out to well beyond the audio band; the bilinear transform pins a
    /// zero at Nyquist instead, so at 48 kHz it reads about -28 dB at 20 kHz against the -19.6 dB
    /// every phono stage is specified at. The poles and the zero are placed by the matched-z
    /// transform (<c>z = e^(sT)</c>), which puts each one at its own frequency at every sample rate
    /// and errs the other way, by about +2.6 dB at 20 kHz at 48 kHz, because a section with one
    /// zero does not roll off as fast as the analog tail. The second zero a biquad has to offer is
    /// then placed on the negative real axis at exactly the radius that makes 20 kHz read the analog
    /// figure at this sample rate — solved in closed form below. Measured against the analog curve:
    /// within 0.34 dB everywhere at 48 kHz (the worst point is 15 kHz), within 0.01 dB at 96 and
    /// 192 kHz.</para>
    /// </summary>
    public static Biquad RiaaPlayback(double sampleRateHz)
    {
        const double t1 = 3180e-6, t2 = 318e-6, t3 = 75e-6;
        double period = 1.0 / sampleRateHz;

        // Matched z: each first-order factor (1 + s tau) becomes (1 - e^(-T/tau) z^-1).
        double zero = Math.Exp(-period / t2);
        double pole1 = Math.Exp(-period / t1);
        double pole3 = Math.Exp(-period / t3);
        var matched = new Biquad(1.0, -zero, 0.0, -(pole1 + pole3), pole1 * pole3);

        // The analog curve's 20 kHz level relative to 1 kHz, and the matched-z section's own; the
        // corrective zero (1 + a z^-1)/(1 + a) has to supply the ratio between them at 20 kHz.
        static Complex Analog(double freqHz)
        {
            var s = new Complex(0.0, 2.0 * Math.PI * freqHz);
            return (1.0 + (s * t2)) / ((1.0 + (s * t1)) * (1.0 + (s * t3)));
        }

        double omega1k = 2.0 * Math.PI * 1000.0 / sampleRateHz;
        double omega20k = 2.0 * Math.PI * 20_000.0 / sampleRateHz;
        double wanted = Analog(20_000.0).Magnitude / Analog(1000.0).Magnitude;
        double have = matched.ResponseAt(omega20k).Magnitude / matched.ResponseAt(omega1k).Magnitude;

        // |1 + a e^(-jw)| / (1 + a) = t  =>  a^2 (1 - t^2) + 2a (cos w - t^2) + (1 - t^2) = 0.
        // The zero also touches 1 kHz, but by parts in ten thousand, so one pass is enough.
        double t = wanted / have;
        double c = Math.Cos(omega20k);
        double a = 0.0;
        if (t < 1.0)
        {
            double qa = 1.0 - (t * t);
            double qb = 2.0 * (c - (t * t));
            double disc = (qb * qb) - (4.0 * qa * qa);
            if (disc >= 0.0)
            {
                double root = (-qb - Math.Sqrt(disc)) / (2.0 * qa);
                if (root is > 0.0 and < 1.0)
                    a = root;
            }
        }

        var shaped = new Biquad(
            matched.B0, matched.B1 + (a * matched.B0), a * matched.B1, matched.A1, matched.A2);

        return shaped.Scaled(1.0 / shaped.ResponseAt(omega1k).Magnitude);
    }
}

/// <summary>
/// The memoryless (or nearly so) nonlinearity a <see cref="SimulatedDut"/> applies after its filter
/// cascade. <see cref="None"/> for the linear models, which are the ones that existed before any of
/// the others.
/// </summary>
internal enum Waveshaper
{
    None,

    /// <summary>A soft asymmetric polynomial — H2 in proportion to level. See <see cref="SimulatedDutModel.TriodeSingleEnded"/>.</summary>
    Triode,

    /// <summary>A gain notch around zero — distortion that grows as the level falls. See <see cref="SimulatedDutModel.ClassAbCrossover"/>.</summary>
    Crossover,

    /// <summary>Clean, then a rail. See <see cref="SimulatedDutModel.SolidStateClipping"/>.</summary>
    Clipper,

    /// <summary>A flux limit behind a one-pole integrator — distortion that grows towards LF. See <see cref="SimulatedDutModel.TransformerCoreSaturation"/>.</summary>
    CoreSaturation,
}

/// <summary>
/// A simulated DUT's signal path: a cascade of biquads, then optionally a <see cref="Waveshaper"/>,
/// a pure delay and one delayed reflection, in that order.
///
/// <para>Stateful, and <b>single channel</b> — the DUT sits on the left input only, so there is one
/// filter chain and no per-channel bookkeeping. See <see cref="SimulatedDutModel"/> for why the
/// right channel is left as a straight wire.</para>
///
/// <para>Created for the life of one acquisition, exactly as <see cref="SignalSynthesizer"/> is, so
/// restarting a stream starts the filters from rest rather than from whatever the last run left in
/// them.</para>
/// </summary>
/// <remarks>
/// Direct-form II transposed, which is the form that stays well behaved with a high-Q section at
/// the bottom of the band: a 45 Hz pole at 192 kHz sits close enough to the unit circle that the
/// coefficient arithmetic matters.
/// </remarks>
internal sealed class SimulatedDut
{
    private readonly Biquad[] _sections;

    /// <summary>Two state words per section, laid out as <c>[section * 2 + n]</c>.</summary>
    private readonly double[] _state;

    private readonly double[]? _reflectionRing;
    private readonly double _reflectionGain;
    private int _reflectionCursor;

    /// <summary>
    /// A pure delay on the through path, or null. Distinct from <see cref="_reflectionRing"/>, which
    /// <i>adds</i> a late copy to the direct sound: this one replaces it, so the whole output arrives
    /// late — which is what a processor's latency is.
    /// </summary>
    private readonly double[]? _delayRing;
    private int _delayCursor;

    private readonly Waveshaper _shaper;

    /// <summary>The core-saturation model's flux integrator: a one-pole low-pass and its state.</summary>
    private readonly double _fluxCoefficient;
    private double _flux;

    /// <summary>
    /// A self-oscillating pole pair added to the through path, or null for a DUT that does not sing.
    /// Only its denominator is used — see <see cref="NextOscillatorSample"/>.
    /// </summary>
    private readonly Biquad? _oscillator;

    /// <summary>
    /// The supply rail the oscillator clips against. For an unstable pole this is the only thing
    /// standing between the simulator and an infinity, and then a NaN in every downstream buffer.
    /// </summary>
    private readonly double _railVolts;

    private double _oscState0;
    private double _oscState1;

    private SimulatedDut(
        SimulatedDutModel model,
        double sampleRateHz,
        Biquad[] sections,
        double reflectionMs,
        double reflectionGain,
        Biquad? oscillator = null,
        double railVolts = 0.0,
        double seed = 0.0,
        Waveshaper shaper = Waveshaper.None,
        double throughDelayMs = 0.0)
    {
        Model = model;
        SampleRateHz = sampleRateHz;
        _sections = sections;
        _state = new double[sections.Length * 2];
        _oscillator = oscillator;
        _railVolts = railVolts;
        _shaper = shaper;
        _fluxCoefficient = 1.0 - Math.Exp(-2.0 * Math.PI * CoreCornerHz / sampleRateHz);

        int delaySamples = (int)Math.Round(throughDelayMs * sampleRateHz / 1000.0);
        if (delaySamples > 0)
            _delayRing = new double[delaySamples];

        // An unstable pole multiplies whatever it is given, and multiplying exactly zero leaves
        // exactly zero — with the generator off, an oscillator seeded from silence would stay silent
        // for ever. A real one starts from its own noise; this starts from a number small enough
        // that it takes about half a second to become audible, which is near enough the same thing.
        _oscState0 = seed;

        int reflectionSamples = (int)Math.Round(reflectionMs * sampleRateHz / 1000.0);
        _reflectionGain = reflectionGain;
        if (reflectionSamples > 0 && reflectionGain != 0.0)
            _reflectionRing = new double[reflectionSamples];
    }

    public SimulatedDutModel Model { get; }

    public double SampleRateHz { get; }

    /// <summary>
    /// True for the models that distort on their own. <see cref="SimulatedDevice"/> reads it to
    /// switch off the synthesizer's additive harmonic stack, which would otherwise sit under the
    /// model at a fixed -46 dBc and turn every level-dependent curve back into a flat line.
    /// </summary>
    public bool SuppliesOwnDistortion => _shaper != Waveshaper.None;

    /// <summary>
    /// Builds the processor for a model, or <c>null</c> for <see cref="SimulatedDutModel.StraightWire"/>
    /// — a null return is what keeps the default path exactly as it was, with no filter in it at all.
    /// </summary>
    public static SimulatedDut? For(SimulatedDutModel model, double sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        return model switch
        {
            SimulatedDutModel.TubeAmplifier => new SimulatedDut(
                model, sampleRateHz,
                [
                    // The output transformer's low end: primary inductance rolling off, with the
                    // small lift just above it that a feedback loop around a transformer usually has.
                    Biquad.HighPass(12.0, 0.75, sampleRateHz),
                    Biquad.Peaking(45.0, 3.0, 1.8, sampleRateHz),

                    // And its high end: leakage inductance resonating against the winding
                    // capacitance, then falling away. Above 24 kHz, so only a 96 kHz span sees it —
                    // which is the whole argument for measuring an amplifier at 192 kHz.
                    Biquad.Peaking(38_000.0, 3.5, 4.0, sampleRateHz),
                    Biquad.LowPass(46_000.0, 0.9, sampleRateHz),
                ],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.Loudspeaker => new SimulatedDut(
                model, sampleRateHz,
                [
                    // A sealed-box alignment.
                    Biquad.HighPass(45.0, 1.0, sampleRateHz),

                    // A cabinet panel: barely visible on a magnitude plot, impossible to miss on a
                    // decay surface, which is the reason it is here.
                    Biquad.Peaking(230.0, 20.0, 4.0, sampleRateHz),

                    // The crossover's own dip, and the cone's first breakup mode above it.
                    Biquad.Peaking(900.0, 1.4, -3.0, sampleRateHz),
                    Biquad.Peaking(4_200.0, 12.0, 6.0, sampleRateHz),

                    Biquad.LowPass(19_000.0, 0.9, sampleRateHz),
                ],
                // A wall about a metre further round than the direct path, 12 dB down and inverted:
                // what the impulse screen's display gate exists to cut out.
                reflectionMs: 3.0, reflectionGain: -0.25),

            SimulatedDutModel.BandpassFilter => new SimulatedDut(
                model, sampleRateHz,
                // One section, and nothing else in the path — see the model's own remarks on why
                // that is the point rather than an omission.
                [Biquad.BandPass(BandpassCentreHz, BandpassQ, sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.MarginallyStable => new SimulatedDut(
                model, sampleRateHz,
                // Flat, plus one resonance so sharp it takes a third of a second to die. Nothing
                // unstable — see the model's own remarks.
                [Biquad.Peaking(MarginalCentreHz, MarginalQ, MarginalGainDb, sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.Oscillating => new SimulatedDut(
                model, sampleRateHz,
                // No sections at all: the through path is unity, and the oscillation is added to it.
                [],
                reflectionMs: 0.0, reflectionGain: 0.0,
                oscillator: Biquad.Resonator(OscillationPoleHz, OscillationExcess, sampleRateHz),
                railVolts: OscillationRailVolts,
                seed: OscillationSeed),

            SimulatedDutModel.ImpedanceResistive => new SimulatedDut(
                model, sampleRateHz,
                // A resistive divider has no memory: one gain section, and nothing else.
                [Biquad.Gain(SimulatedImpedanceFixture.ResistiveOhms / (SimulatedImpedanceFixture.ReferenceOhms + SimulatedImpedanceFixture.ResistiveOhms))],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.ImpedanceReactive => new SimulatedDut(
                model, sampleRateHz,
                // H(s) = (Rs + sL) / ((R + Rs) + sL): first order, so the s^2 terms are zero.
                [Biquad.FromAnalog(
                    0.0, SimulatedImpedanceFixture.ReactiveHenry, SimulatedImpedanceFixture.ReactiveOhms,
                    0.0, SimulatedImpedanceFixture.ReactiveHenry, SimulatedImpedanceFixture.ReferenceOhms + SimulatedImpedanceFixture.ReactiveOhms,
                    sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.ImpedanceLoudspeaker => new SimulatedDut(
                model, sampleRateHz,
                [LoudspeakerDivider(sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            // The nonlinear models are flat: no sections, the shaper is the whole DUT. Any linear
            // colour would sit on top of the one number each of them exists to make checkable.
            SimulatedDutModel.TriodeSingleEnded => new SimulatedDut(
                model, sampleRateHz, [], reflectionMs: 0.0, reflectionGain: 0.0,
                shaper: Waveshaper.Triode),

            SimulatedDutModel.ClassAbCrossover => new SimulatedDut(
                model, sampleRateHz, [], reflectionMs: 0.0, reflectionGain: 0.0,
                shaper: Waveshaper.Crossover),

            SimulatedDutModel.SolidStateClipping => new SimulatedDut(
                model, sampleRateHz, [], reflectionMs: 0.0, reflectionGain: 0.0,
                shaper: Waveshaper.Clipper),

            SimulatedDutModel.TransformerCoreSaturation => new SimulatedDut(
                model, sampleRateHz, [], reflectionMs: 0.0, reflectionGain: 0.0,
                shaper: Waveshaper.CoreSaturation),

            SimulatedDutModel.PhonoRiaa => new SimulatedDut(
                model, sampleRateHz,
                [Biquad.RiaaPlayback(sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.DigitalProcessor => new SimulatedDut(
                model, sampleRateHz,
                [
                    // A fourth-order Butterworth: two second-order sections at the Qs whose product
                    // is maximally flat, 1/(2 cos(pi/8)) and 1/(2 cos(3pi/8)).
                    Biquad.LowPass(ProcessorCornerHz, 0.5412, sampleRateHz),
                    Biquad.LowPass(ProcessorCornerHz, 1.3066, sampleRateHz),
                ],
                reflectionMs: 0.0, reflectionGain: 0.0,
                throughDelayMs: ProcessorLatencyMs),

            SimulatedDutModel.ImpedanceCapacitive => new SimulatedDut(
                model, sampleRateHz,
                // Z = ESR + 1/(sC), so H = Z/(R + Z) = (1 + s ESR C) / (1 + s (R + ESR) C).
                [Biquad.FromAnalog(
                    0.0, SimulatedImpedanceFixture.CapacitiveEsrOhms * SimulatedImpedanceFixture.CapacitiveFarad, 1.0,
                    0.0, (SimulatedImpedanceFixture.ReferenceOhms + SimulatedImpedanceFixture.CapacitiveEsrOhms) * SimulatedImpedanceFixture.CapacitiveFarad, 1.0,
                    sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.ImpedanceShort => new SimulatedDut(
                model, sampleRateHz,
                // The same series R + sL form as the reactive fixture, at lead-sized values.
                [Biquad.FromAnalog(
                    0.0, SimulatedImpedanceFixture.ShortHenry, SimulatedImpedanceFixture.ShortOhms,
                    0.0, SimulatedImpedanceFixture.ShortHenry, SimulatedImpedanceFixture.ReferenceOhms + SimulatedImpedanceFixture.ShortOhms,
                    sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.ImpedanceOpen => new SimulatedDut(
                model, sampleRateHz,
                // Z = 1/(sC) alone: H = 1 / (1 + s R C).
                [Biquad.FromAnalog(
                    0.0, 0.0, 1.0,
                    0.0, SimulatedImpedanceFixture.ReferenceOhms * SimulatedImpedanceFixture.OpenStrayFarad, 1.0,
                    sampleRateHz)],
                reflectionMs: 0.0, reflectionGain: 0.0),

            // The four classic alignments: pure cascades, so ResponseAt is exact for them, and unity
            // at DC by construction -- see FilterPrototype.Sections.
            SimulatedDutModel.ButterworthLowPass => new SimulatedDut(
                model, sampleRateHz, FilterPrototype.Butterworth.Sections(FilterCornerHz, sampleRateHz),
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.ChebyshevLowPass => new SimulatedDut(
                model, sampleRateHz, FilterPrototype.Chebyshev.Sections(FilterCornerHz, sampleRateHz),
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.BesselLowPass => new SimulatedDut(
                model, sampleRateHz, FilterPrototype.Bessel.Sections(FilterCornerHz, sampleRateHz),
                reflectionMs: 0.0, reflectionGain: 0.0),

            SimulatedDutModel.EllipticLowPass => new SimulatedDut(
                model, sampleRateHz, FilterPrototype.Elliptic.Sections(FilterCornerHz, sampleRateHz),
                reflectionMs: 0.0, reflectionGain: 0.0),

            _ => null,
        };
    }

    /// <summary>
    /// The triode's second-order coefficient. <c>H2/H1 = k2 A / 2</c> for a peak drive <c>A</c>, so
    /// 0.5% at 1 V peak, and 1 dB more for every dB of level — the single-ended signature.
    /// </summary>
    public const double TriodeK2 = 0.01;

    /// <summary>Its compressive cubic: <c>H3/H1 = k3 A^2 / 4</c>, 0.05% at 1 V peak, and 2 dB per dB.</summary>
    public const double TriodeK3 = 0.002;

    /// <summary>
    /// Where the stage runs out of swing, in volts peak. The polynomial is applied to the input
    /// limited here, which keeps it monotonic (<c>3 k3 rail^2 &lt; 1</c>) — a cubic left to itself
    /// folds over at high drive and reads as gain that falls with level, which is not a triode.
    /// </summary>
    public const double TriodeRailVolts = 4.0;

    /// <summary>How deep the crossover notch is: the stage's gain at zero is <c>1 - CrossoverDepth</c>.</summary>
    public const double CrossoverDepth = 0.2;

    /// <summary>
    /// How wide it is, in volts. THD peaks where the drive is a couple of widths — near 30 mV peak,
    /// -33 dBV — and is negligible by the time an ordinary measurement level is reached, which is
    /// what keeps the model unity gain everywhere but at whisper levels.
    /// </summary>
    public const double CrossoverWidthVolts = 0.02;

    /// <summary>
    /// The solid-state model's supply rail, in volts peak: 1 V, i.e. -3 dBV for a sine, so the
    /// default 1 mV–1 V RMS amplitude sweep meets it exactly at its top.
    /// </summary>
    public const double ClipRailVolts = 1.0;

    /// <summary>The knee ahead of the rail, as a fraction of it: short, since a transistor stage saturates abruptly.</summary>
    public const double ClipKneeFraction = 0.97;

    /// <summary>Its residual cubic below the rail — <c>k3 A^2 / 4</c> is -100 dBc at 1 V peak, so the floor is a number rather than nothing.</summary>
    public const double ClipK3 = 4.0e-5;

    /// <summary>
    /// The corner of the flux integrator in <see cref="SimulatedDutModel.TransformerCoreSaturation"/>.
    /// Below it the flux follows the signal; above it it falls 6 dB per octave, and the cubic
    /// distortion falls three times as fast — 18 dB per octave.
    /// </summary>
    public const double CoreCornerHz = 30.0;

    /// <summary>
    /// The flux limit, in the same volts as the signal. Chosen so a 1 V peak tone at 20 Hz reads
    /// about 3% third harmonic: the small-signal figure is <c>A_f^3 / (12 Phi^2)</c> relative to
    /// <c>A</c>, with <c>A_f = A |LP(f)|</c> and |LP(20 Hz)| = 0.83.
    /// </summary>
    public const double CoreFluxLimitVolts = 1.26;

    /// <summary>
    /// Where the four classic low-pass filters put their corner. 2 kHz on purpose: an octave above
    /// the FFT screen's default 1 kHz tone, so that tone sits in the passband of all four (−0.004 dB
    /// on the Butterworth, −0.27 dB in the Chebyshev's ripple), and a full decade below the
    /// 20 kHz edge, so a 24 kHz span shows the whole transition and a decade of stopband --
    /// 100 dB of it on the all-pole designs, which is about where the simulator's floor is.
    /// </summary>
    public const double FilterCornerHz = 2_000.0;

    /// <summary>The digital processor's latency. 576 samples at 48 kHz, 2304 at 192 kHz.</summary>
    public const double ProcessorLatencyMs = 12.0;

    /// <summary>Its anti-alias low-pass corner: -3 dB here, fourth order, maximally flat below it.</summary>
    public const double ProcessorCornerHz = 20_000.0;

    /// <summary>
    /// The divider for <see cref="SimulatedDutModel.ImpedanceLoudspeaker"/>: a voice-coil resistance
    /// in series with a parallel RLC, seen across the reference resistor.
    ///
    /// <para>Writing the resonance as <c>Rres * (s w0/Q) / (s^2 + s w0/Q + w0^2)</c> and adding
    /// <c>Re</c> makes Z a ratio of two quadratics, so <c>Z/(R + Z)</c> is a ratio of two quadratics
    /// as well -- one biquad, exactly. That is the whole reason the voice-coil inductance is left
    /// out: <c>Re + sLe</c> plus the resonance is a ratio of cubics, which needs a factorisation and
    /// stops the peak being a number anyone can check.</para>
    ///
    /// <para>Two values fall straight out and are what the tests assert: <c>Z(0) = Re</c>, and at
    /// <c>s = j w0</c> everything but the resonance cancels to leave <c>Z = Re + Rres</c> exactly.</para>
    /// </summary>
    private static Biquad LoudspeakerDivider(double sampleRateHz)
    {
        double w0 = 2.0 * Math.PI * SimulatedImpedanceFixture.SpeakerResonanceHz;
        double damping = w0 / SimulatedImpedanceFixture.SpeakerQ;
        double re = SimulatedImpedanceFixture.SpeakerReOhms;
        double peak = SimulatedImpedanceFixture.SpeakerPeakOhms;
        double r = SimulatedImpedanceFixture.ReferenceOhms;

        return Biquad.FromAnalog(
            re, peak * damping, re * w0 * w0,
            r + re, (r + peak) * damping, (r + re) * w0 * w0,
            sampleRateHz);
    }

    /// <summary>Where <see cref="SimulatedDutModel.MarginallyStable"/> rings, and how sharply.</summary>
    public const double MarginalCentreHz = 1000.0;

    public const double MarginalQ = 500.0;

    public const double MarginalGainDb = 12.0;

    /// <summary>
    /// Where <see cref="SimulatedDutModel.Oscillating"/>'s small-signal pole sits. The limit cycle
    /// itself lands about 1% above this — see <see cref="Biquad.Resonator"/>, and
    /// <see cref="OscillationExcess"/> for why that figure is the same at every sample rate.
    /// </summary>
    public const double OscillationPoleHz = 1000.0;

    /// <summary>
    /// How far outside the unit circle the pole pair sits, as a multiple of w0^2. Above zero, which
    /// is the whole point. 0.01 buys a 1% frequency pull and a loop that grows from
    /// <see cref="OscillationSeed"/> to the rail in half a second at 48 kHz, two at 192 kHz.
    /// </summary>
    public const double OscillationExcess = 0.01;

    /// <summary>
    /// The supply rail it clips against, in volts peak. Chosen to sit well inside the analyzer's most
    /// sensitive input range: the demonstration is that a free-running tone wrecks every measurement,
    /// not that it also happens to overload the ADC.
    /// </summary>
    public const double OscillationRailVolts = 0.5;

    /// <summary>
    /// What it starts from. See the constructor for why it cannot start from silence; the value is
    /// small enough to read as a noise floor and large enough that the warm-up is under two seconds
    /// at every sample rate.
    /// </summary>
    public const double OscillationSeed = 0.01;

    /// <summary>Where <see cref="SimulatedDutModel.BandpassFilter"/> sits. Round, and in the middle of the band.</summary>
    public const double BandpassCentreHz = 1000.0;

    /// <summary>
    /// Its quality factor, which fixes everything else about it: a 50 Hz bandwidth, and a ring that
    /// loses <see cref="BandpassDbPerPeriod"/> per cycle.
    /// </summary>
    public const double BandpassQ = 20.0;

    /// <summary>
    /// What a second-order resonance of <see cref="BandpassQ"/> loses per period of its own centre
    /// frequency: its envelope decays as <c>e^(-w0 t / 2Q)</c>, which over one period is
    /// <c>e^(-pi/Q)</c>, i.e. <c>20 log10(e) * pi / Q = 27.29 / Q</c> dB.
    ///
    /// <para>This is the number the burst decay should read straight off its own axis, and it is why
    /// this model exists: every other DUT here is a plausible picture, and this one is an answer you
    /// can check.</para>
    /// </summary>
    public const double BandpassDbPerPeriod = 27.2875 / BandpassQ;

    /// <summary>
    /// The filter cascade's exact response at one frequency -- the discrete transfer function that
    /// actually produced the samples, which is what an impedance fixture's expected impedance has to
    /// be derived from rather than the analog circuit it was mapped from.
    ///
    /// <para><b>The cascade only.</b> The waveshaper, the delay, the reflection and the oscillator
    /// are not included: a nonlinearity has no transfer function, the delay and the reflection are
    /// delay lines, and the oscillator is not a response to the input at all. Only the
    /// <c>Impedance*</c> fixtures, which are pure cascades, get a meaningful answer out of this.</para>
    /// </summary>
    public Complex ResponseAt(double freqHz)
    {
        double omega = 2.0 * Math.PI * freqHz / SampleRateHz;

        var response = Complex.One;
        foreach (var section in _sections)
            response *= section.ResponseAt(omega);

        return response;
    }

    /// <summary>One sample through the DUT. Stateful, so it must be called once per frame in frame order.</summary>
    public double Process(double x)
    {
        for (int s = 0; s < _sections.Length; s++)
        {
            var section = _sections[s];
            int i = s * 2;

            double y = (section.B0 * x) + _state[i];
            _state[i] = (section.B1 * x) - (section.A1 * y) + _state[i + 1];
            _state[i + 1] = (section.B2 * x) - (section.A2 * y);
            x = y;
        }

        x = Shape(x);

        if (_delayRing is not null)
        {
            double delayed = _delayRing[_delayCursor];
            _delayRing[_delayCursor] = x;
            _delayCursor = (_delayCursor + 1) % _delayRing.Length;
            x = delayed;
        }

        if (_reflectionRing is not null)
        {
            double delayed = _reflectionRing[_reflectionCursor];
            _reflectionRing[_reflectionCursor] = x;
            _reflectionCursor = (_reflectionCursor + 1) % _reflectionRing.Length;
            x += _reflectionGain * delayed;
        }

        return x + NextOscillatorSample();
    }

    /// <summary>
    /// The nonlinearity, after the cascade and before the delay. Every curve here has unit slope
    /// through the origin (or, for the crossover, deliberately not — see its remarks), so the
    /// models stay unity gain at the levels the linear measurements run at, and every one is
    /// bounded, so no drive the generator can produce turns into an infinity downstream.
    /// </summary>
    private double Shape(double x)
    {
        switch (_shaper)
        {
            case Waveshaper.Triode:
            {
                double v = Math.Clamp(x, -TriodeRailVolts, TriodeRailVolts);
                return v + (TriodeK2 * v * v) - (TriodeK3 * v * v * v);
            }

            case Waveshaper.Crossover:
            {
                double w = CrossoverWidthVolts;
                return x * (1.0 - (CrossoverDepth * Math.Exp(-(x * x) / (2.0 * w * w))));
            }

            case Waveshaper.Clipper:
                return SignalSynthesizer.SoftClip(x - (ClipK3 * x * x * x), ClipRailVolts, ClipKneeFraction);

            case Waveshaper.CoreSaturation:
            {
                // The flux is the signal integrated — a one-pole low-pass at the corner — and the
                // core passes only Phi*tanh(flux/Phi) of it. What the output loses is the difference,
                // which is ~flux^3 / (3 Phi^2) when small and never more than the flux itself.
                _flux += _fluxCoefficient * (x - _flux);
                double phi = CoreFluxLimitVolts;
                double lost = _flux - (phi * Math.Tanh(_flux / phi));
                return x - lost;
            }

            default:
                return x;
        }
    }

    /// <summary>
    /// The self-oscillation, added to the through path rather than filtering it — an amplifier that
    /// has gone unstable still amplifies, and its output is the signal <i>plus</i> a tone the input
    /// had no part in. Modelling it as a section in series would instead make the DUT a filter that
    /// happens to ring, which is a different (and much less interesting) thing.
    ///
    /// <para>Autonomous: the input is not read at all, so only the pole pair matters. Starting from
    /// <see cref="OscillationSeed"/> it grows as <c>r^n</c> until the rail catches it, then stays
    /// there as a limit cycle.</para>
    ///
    /// <para><b>The clamp is inside the recursion, before the state update.</b> That ordering is the
    /// whole safety property: the values fed back are the clamped ones, so a pole outside the unit
    /// circle drives a bounded limit cycle instead of an exponential. Clamping the return value
    /// instead would bound what the analyzer sees while leaving the state to run away behind it —
    /// worse than not clamping at all, because it would look correct right up until the first NaN.</para>
    /// </summary>
    private double NextOscillatorSample()
    {
        if (_oscillator is not { } pole)
            return 0.0;

        double y = Math.Clamp(_oscState0, -_railVolts, _railVolts);
        _oscState0 = (-pole.A1 * y) + _oscState1;
        _oscState1 = -pole.A2 * y;
        return y;
    }
}
