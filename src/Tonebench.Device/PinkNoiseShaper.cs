namespace Tonebench.Device;

/// <summary>
/// Pink (1/f) shaping for a white input: Paul Kellet's three-pole "economy" filter, one instance per
/// independent noise stream.
///
/// <para><b>Why this is a type rather than three lines in each caller.</b> The coefficients are used
/// in three places — the real device's TX synthesis (<see cref="Hardware.Qa40xGeneratorWaveform"/>),
/// the simulator's generator, and the simulator's own DUT noise floor — and the first two are
/// deliberately independent implementations that must nevertheless shape identically, or a stimulus
/// verified against <c>--simulate</c> would be a different stimulus on hardware. Sharing the filter
/// is the cheap half of the <c>ChirpFormulaAgreementTests</c> arrangement; unlike the chirp there is
/// no sample-for-sample agreement to pin, because only the spectral shape is meant to match.</para>
///
/// <para><b>Two roles, and an instance serves one of them.</b> <see cref="Next"/> is the bare filter,
/// which is what the simulated DUT's noise floor wants. <see cref="NextStimulus"/> adds the two
/// things a signal fed to a real load needs — no DC, and a known RMS — and carries its own state, so
/// mixing the two calls on one instance would interleave two filters' inputs.</para>
///
/// <para><b>The shape is an approximation and that is fine here.</b> The filter tracks &#8722;3 dB per
/// octave to within a fraction of a dB across the decades that matter and flattens towards both ends
/// of the spectrum. Nothing measures the stimulus itself: the impedance measurement is a ratio of two
/// channels that both see the same drive, so all the stimulus has to do is put energy in every bin,
/// and pink puts more of it where a divider measurement is noise-limited than white does.</para>
/// </summary>
internal sealed class PinkNoiseShaper(double sampleRateHz)
{
    /// <summary>Pole of each one-pole section, i.e. its feedback coefficient.</summary>
    private static readonly double[] Poles = [0.99765, 0.96300, 0.57000];

    /// <summary>Input gain of each section.</summary>
    private static readonly double[] Gains = [0.0990460, 0.2965164, 1.0526913];

    /// <summary>
    /// Corner of the DC blocker on <see cref="NextStimulus"/>, in Hz. Below the 4 Hz floor every
    /// analysis here starts at, and 1.0 dB down at that floor.
    ///
    /// <para><b>A pink filter integrates its white input, so what comes out has a slowly wandering
    /// offset</b> — a third of the record's power below 3 Hz, in a 32768-point record at 48 kHz. Three
    /// separate reasons not to play that at a DUT. It is <i>DC into the load</i>, which for a
    /// loudspeaker means a voice coil sitting off centre and heating; it is spent DAC headroom, since
    /// <see cref="OutputRangeSelector"/> ranges for the peak and the wander is most of it; and it
    /// wrecks the accuracy of the measurement the stimulus exists for. That last one is the
    /// measurable one: <see cref="Tonebench.Dsp.FrequencyResponseEstimator"/> regularises by
    /// <c>1e-6 x max(Sxx)</c>, so a reference spectrum whose DC bin towers 41 dB over its 10 kHz bin
    /// biases every high bin low — measured end to end against the resistive fixture, <b>0.20 dB</b>
    /// at 10 kHz, against 0.006 dB for a chirp. With this blocker it is 0.01 dB.</para>
    /// </summary>
    public const double HighPassHz = 2.0;

    /// <summary>
    /// RMS of <see cref="Next"/>'s output for a unit-variance white input — about <b>2.882</b>.
    ///
    /// <para>Computed rather than written down, so it cannot drift away from the coefficients above.
    /// Each section is a one-pole IIR whose impulse response is <c>c*a^n</c>, so for white input the
    /// output variance is the double sum below: all three sections are driven by the <i>same</i>
    /// sample, so their outputs are correlated and the cross terms count.</para>
    /// </summary>
    public static readonly double RmsGain = ComputeRmsGain(HighPassPole(0.0));

    /// <summary>
    /// The same for <see cref="NextStimulus"/>'s output, which the DC blocker makes slightly smaller
    /// and — since that blocker has a corner in Hz — dependent on the sample rate.
    ///
    /// <para>This is what makes a pink generator's <c>LevelDbv</c> mean the broadband RMS a white
    /// one's does, which is in turn what lets <see cref="OutputRangeSelector"/> pick an output range
    /// from the configured level instead of from a measurement.</para>
    /// </summary>
    public double StimulusRmsGain { get; } = ComputeRmsGain(HighPassPole(sampleRateHz));

    private readonly double _highPassPole = HighPassPole(sampleRateHz);

    private double _b0;
    private double _b1;
    private double _b2;
    private double _lastShaped;
    private double _lastBlocked;

    /// <summary>
    /// Shaped output for one white input sample, at this filter's own gain
    /// (<see cref="RmsGain"/>) and with its DC left in place.
    /// </summary>
    public double Next(double white)
    {
        _b0 = (Poles[0] * _b0) + (white * Gains[0]);
        _b1 = (Poles[1] * _b1) + (white * Gains[1]);
        _b2 = (Poles[2] * _b2) + (white * Gains[2]);
        return _b0 + _b1 + _b2;
    }

    /// <summary>
    /// Shaped, DC-blocked, and normalised to the RMS of the white sample that drove it — so a stream
    /// of <c>sigma * NextStimulus(gaussian)</c> has a broadband RMS of <c>sigma</c> and no offset.
    /// </summary>
    public double NextStimulus(double white)
    {
        double shaped = Next(white);

        // One-pole DC blocker: y[n] = x[n] - x[n-1] + r*y[n-1].
        _lastBlocked = shaped - _lastShaped + (_highPassPole * _lastBlocked);
        _lastShaped = shaped;

        return _lastBlocked / StimulusRmsGain;
    }

    /// <summary>
    /// The white sample that drives the shaper at <paramref name="frameIndex"/>: a hash of the frame
    /// index, not a draw from a running RNG.
    ///
    /// <para><b>Both generator channels must play the same noise, and this is what makes them.</b> A
    /// stimulus is only a stimulus if the reference channel carries it too. On a bench that is
    /// automatic — one output drives the fixture and both inputs tap the same network — but the
    /// simulator models the right input as the right <i>generator</i> channel, so two channels
    /// pulling successive samples from one RNG would hand the analysis two unrelated noise streams,
    /// and an impedance run against <c>--simulate</c> would read coherence near zero and a
    /// meaningless Z. Deriving the sample from the frame index instead makes pink noise a pure
    /// function of that index, exactly as the tones and the chirp are, which is also what lets a
    /// re-primed TX queue rebuild the same frames and get the same audio.</para>
    ///
    /// <para>The shaper itself is still recursive and still per channel, so two channels agree
    /// because they are fed the same inputs from the same start, not because they share state.</para>
    ///
    /// <para>SplitMix64 twice, then Box-Muller — the same distribution the white generator draws
    /// from, which is what lets both kinds share
    /// <see cref="OutputRangeSelector.NoiseCrestFactorDb"/>.</para>
    /// </summary>
    public static double WhiteAt(long frameIndex)
    {
        double u1 = Uniform(Mix(((ulong)frameIndex * 2UL) + 1UL));
        double u2 = Uniform(Mix((ulong)frameIndex * 2UL));
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// The bare shaping filter's power response at <paramref name="freqHz"/>, relative to a white
    /// input of the same RMS and with no DC blocker in the way. Exists for the tests, which check the
    /// &#8722;3 dB per octave slope in closed form rather than by transforming a noise stream and
    /// hoping the variance settles.
    /// </summary>
    public static double PowerResponse(double freqHz, double sampleRateHz) =>
        Response(freqHz, sampleRateHz, highPassPole: 0.0, rmsGain: RmsGain);

    /// <summary>The same for what <see cref="NextStimulus"/> actually emits, DC blocker included.</summary>
    public double StimulusPowerResponse(double freqHz) =>
        Response(freqHz, sampleRateHz, _highPassPole, StimulusRmsGain);

    private static double Response(double freqHz, double sampleRateHz, double highPassPole, double rmsGain)
    {
        var z = System.Numerics.Complex.Exp(new System.Numerics.Complex(0.0, -2.0 * Math.PI * freqHz / sampleRateHz));

        var h = System.Numerics.Complex.Zero;
        for (int i = 0; i < Poles.Length; i++)
            h += Gains[i] / (1.0 - (Poles[i] * z));

        if (highPassPole > 0.0)
            h *= (1.0 - z) / (1.0 - (highPassPole * z));

        return (h * System.Numerics.Complex.Conjugate(h)).Real / (rmsGain * rmsGain);
    }

    /// <summary>The DC blocker's pole for a sample rate, or 0 — meaning "no blocker" — for a rate of 0.</summary>
    private static double HighPassPole(double sampleRateHz) =>
        sampleRateHz > 0.0 ? Math.Exp(-2.0 * Math.PI * HighPassHz / sampleRateHz) : 0.0;

    /// <summary>
    /// RMS of the cascade's output for unit-variance white input.
    ///
    /// <para>Partial fractions rather than a settling simulation, so it is exact and costs nothing at
    /// construction. Each section contributes a pole; the DC blocker <c>(1 - z^-1)/(1 - r z^-1)</c>
    /// adds a fourth and rescales the other three by its own value at each of their poles. The
    /// cascade is strictly proper, so it is a bare sum of one-pole terms with no direct path, and the
    /// variance of a sum of one-poles driven by one white input is
    /// <c>sum(K_j K_k / (1 - p_j p_k))</c> over every pair.</para>
    /// </summary>
    private static double ComputeRmsGain(double highPassPole)
    {
        if (highPassPole <= 0.0)
        {
            double bare = 0.0;
            for (int i = 0; i < Poles.Length; i++)
            {
                for (int j = 0; j < Poles.Length; j++)
                    bare += Gains[i] * Gains[j] / (1.0 - (Poles[i] * Poles[j]));
            }

            return Math.Sqrt(bare);
        }

        double r = highPassPole;
        double[] poles = [Poles[0], Poles[1], Poles[2], r];
        double[] residues = new double[4];

        for (int i = 0; i < Poles.Length; i++)
            residues[i] = Gains[i] * (Poles[i] - 1.0) / (Poles[i] - r);

        double blockerResidue = 0.0;
        for (int i = 0; i < Poles.Length; i++)
            blockerResidue += Gains[i] / (r - Poles[i]);

        residues[3] = (r - 1.0) * blockerResidue;

        double variance = 0.0;
        for (int j = 0; j < poles.Length; j++)
        {
            for (int k = 0; k < poles.Length; k++)
                variance += residues[j] * residues[k] / (1.0 - (poles[j] * poles[k]));
        }

        return Math.Sqrt(variance);
    }

    /// <summary>SplitMix64's finalising mix — a full-period bijection, so distinct frames give unrelated draws.</summary>
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>The top 53 bits as a double in (0, 1] — never 0, which <c>Log</c> would not survive.</summary>
    private static double Uniform(ulong bits) => ((bits >> 11) + 1UL) * (1.0 / 9007199254740993.0);
}
