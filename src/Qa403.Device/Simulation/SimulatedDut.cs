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

    /// <summary>A resonance: <paramref name="gainDb"/> of lift (or cut) at <paramref name="freqHz"/>, unity well away from it.</summary>
    public static Biquad Peaking(double freqHz, double q, double gainDb, double sampleRateHz)
    {
        var (cos, alpha) = Prepare(freqHz, q, sampleRateHz);
        double a = Math.Pow(10.0, gainDb / 40.0);
        return Normalise(
            1.0 + (alpha * a), -2.0 * cos, 1.0 - (alpha * a),
            1.0 + (alpha / a), -2.0 * cos, 1.0 - (alpha / a));
    }
}

/// <summary>
/// A simulated DUT's signal path: a cascade of biquads and, optionally, one delayed reflection.
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
        double seed = 0.0)
    {
        Model = model;
        SampleRateHz = sampleRateHz;
        _sections = sections;
        _state = new double[sections.Length * 2];
        _oscillator = oscillator;
        _railVolts = railVolts;

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

            _ => null,
        };
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
