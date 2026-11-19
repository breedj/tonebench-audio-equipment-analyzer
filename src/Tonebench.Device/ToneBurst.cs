namespace Tonebench.Device;

/// <summary>
/// The tone-burst stimulus, <see cref="GeneratorKind.ToneBurst"/>: a sine gated on for a whole
/// number of cycles and off for a whole number more, repeating.
///
/// <para><b>What it is for.</b> A continuous tone tells you what an amplifier does once it has
/// settled; a burst tells you what it does on the way there and on the way back, which is where
/// rail sag, thermal modulation, protection circuits and recovery from clipping live. It is the
/// classic amplifier transient test, and the one stimulus here whose answer cannot be read off any
/// steady-state measurement.</para>
///
/// <para>Shared between the hardware synthesizer and the simulator's, like
/// <see cref="PeriodicWave"/> and for its reason: a simulator that gated at a different place than
/// the hardware is a DUT that behaves differently under <c>--simulate</c>.</para>
///
/// <para><b>The carrier's period is quantised to whole frames and the gate counts cycles, which is
/// what makes the whole stimulus periodic.</b> The burst repeats every
/// <c>(on + off) × M</c> frames, exactly, for the same reason the chirp's period is a frame count:
/// a repeat that landed between samples would walk the waveform off its own grid and smear every
/// line. The carrier itself goes through <see cref="PeriodicWave.PeriodFrames"/>, so a burst and a
/// square asked for the same frequency play the same frequency.</para>
///
/// <para><b>The gate is hard and needs no window.</b> The carrier is <c>sin</c> starting at phase
/// zero and the gate opens and closes on whole cycles, so both edges fall on a zero crossing of the
/// carrier: there is no step to taper. A raised-cosine envelope would be a different measurement —
/// it would hide exactly the instantaneous onset the test exists to apply.</para>
/// </summary>
public static class ToneBurst
{
    /// <summary>At least one cycle on; a burst of none is silence wearing a frequency.</summary>
    public const int MinCyclesOn = 1;

    /// <summary>
    /// Nothing below this off, which is a continuous tone and is allowed: it is the control case a
    /// reader compares the burst against, and refusing it would mean leaving the screen to get it.
    /// </summary>
    public const int MinCyclesOff = 0;

    /// <summary>
    /// A ceiling on either count, so the burst period cannot overflow the frame arithmetic. 100 000
    /// cycles of a 20 Hz carrier is 83 minutes, which is past any stimulus and well short of the
    /// <c>long</c> the period is held in.
    /// </summary>
    public const int MaxCycles = 100_000;

    /// <summary>One cycle of the carrier, in frames — the shape family's own quantiser.</summary>
    public static int CycleFrames(ToneBurstSettings? settings, double sampleRateHz) =>
        settings is null ? 0 : PeriodicWave.PeriodFrames(settings.FrequencyHz, sampleRateHz);

    /// <summary>The carrier frequency really played: <c>fs / M</c>, which is what status reports.</summary>
    public static double EffectiveFrequencyHz(ToneBurstSettings? settings, double sampleRateHz) =>
        settings is null ? 0.0 : PeriodicWave.EffectiveFrequencyHz(settings.FrequencyHz, sampleRateHz);

    /// <summary>Cycles on, clamped to what this class will play.</summary>
    public static int CyclesOn(ToneBurstSettings? settings) =>
        settings is null ? 0 : Math.Clamp(settings.CyclesOn, MinCyclesOn, MaxCycles);

    /// <summary>Cycles off, clamped to what this class will play.</summary>
    public static int CyclesOff(ToneBurstSettings? settings) =>
        settings is null ? 0 : Math.Clamp(settings.CyclesOff, MinCyclesOff, MaxCycles);

    /// <summary>
    /// How long one on-plus-off group lasts, in frames, or 0 where there is no carrier to gate.
    /// A <c>long</c> because at the 100 000-cycle ceiling and a 4-frame carrier this is still only
    /// 800 000, but at a 20 Hz carrier on 192 kHz it is 1.9 billion — past <c>int</c>.
    /// </summary>
    public static long BurstPeriodFrames(ToneBurstSettings? settings, double sampleRateHz)
    {
        int cycle = CycleFrames(settings, sampleRateHz);
        if (cycle <= 0)
            return 0;

        return (long)(CyclesOn(settings) + CyclesOff(settings)) * cycle;
    }

    /// <summary>How often the burst repeats, in Hz — the figure a scope's timebase is set from.</summary>
    public static double RepetitionHz(ToneBurstSettings? settings, double sampleRateHz)
    {
        long period = BurstPeriodFrames(settings, sampleRateHz);
        return period <= 0 ? 0.0 : sampleRateHz / period;
    }

    /// <summary>The fraction of the time the carrier is actually playing.</summary>
    public static double DutyFraction(ToneBurstSettings? settings)
    {
        int on = CyclesOn(settings);
        int total = on + CyclesOff(settings);
        return total <= 0 ? 0.0 : (double)on / total;
    }

    /// <summary>
    /// This stimulus's value at <paramref name="frameIndex"/>, in volts.
    ///
    /// <para>Integer modulo on the frame index, like every other periodic kind here: the repeat has
    /// to land on exactly the same frame however long the stream has been running.</para>
    /// </summary>
    public static double Sample(ToneBurstSettings? settings, long frameIndex, double sampleRateHz)
    {
        if (settings is null)
            return 0.0;

        int cycle = CycleFrames(settings, sampleRateHz);
        if (cycle <= 0)
            return 0.0;

        long burst = BurstPeriodFrames(settings, sampleRateHz);
        if (burst <= 0)
            return 0.0;

        long phase = frameIndex % burst;
        if (phase < 0)
            phase += burst;

        long onFrames = (long)CyclesOn(settings) * cycle;
        if (phase >= onFrames)
            return 0.0;

        // Phase within the carrier cycle, so the sine is built from an exact integer ratio rather
        // than from a frequency in hertz -- the same reason PeriodicWave works in frames.
        long withinCycle = phase % cycle;
        return PeakVolts(settings.LevelDbv) * Math.Sin(2.0 * Math.PI * withinCycle / cycle);
    }

    /// <summary>
    /// Peak volts for a level in dBV. <b>The level is the carrier's own rms while it is playing</b>,
    /// not the stimulus's overall rms — which is what a reader means by "a 0 dBV burst", and what
    /// makes the burst comparable with a continuous tone at the same setting. The overall rms is
    /// lower by the square root of the duty (<see cref="OverallRmsDbv"/>), and the peak the DAC has
    /// to clear is this one at any duty, so narrowing the burst never changes the output range.
    /// </summary>
    public static double PeakVolts(double levelDbv) => Math.Sqrt(2.0) * Math.Pow(10.0, levelDbv / 20.0);

    /// <summary>
    /// The whole stimulus's rms, dBV: the carrier's level plus <c>10·log10(duty)</c>. What a
    /// broadband level meter would read, and the figure to compare against a continuous tone when
    /// the question is energy rather than amplitude.
    /// </summary>
    public static double OverallRmsDbv(ToneBurstSettings? settings)
    {
        if (settings is null)
            return double.NegativeInfinity;

        double duty = DutyFraction(settings);
        return duty <= 0.0 ? double.NegativeInfinity : settings.LevelDbv + 10.0 * Math.Log10(duty);
    }

    /// <summary>
    /// The level the <i>carrier line</i> reads in a spectrum, dBV: the gate's own DC term times the
    /// carrier, i.e. the level plus <c>20·log10(duty)</c>.
    ///
    /// <para>This is what goes in <see cref="ChannelGeneratorConfig.Tones"/>, on the same rule the
    /// shapes follow — the effective tone list names the line the analyzer will really measure, not
    /// the amplitude that was typed. The rest of the energy is in the sidebands the gating puts at
    /// multiples of the repetition rate either side of the carrier, which is the burst's spectrum
    /// and not an error. A burst is therefore a <b>time-domain</b> stimulus: read it on the scope
    /// screen, not off a THD figure, which is why the noise-integrated tiles stand down for it.</para>
    /// </summary>
    public static double CarrierLineDbv(ToneBurstSettings? settings)
    {
        if (settings is null)
            return double.NegativeInfinity;

        double duty = DutyFraction(settings);
        return duty <= 0.0 ? double.NegativeInfinity : settings.LevelDbv + 20.0 * Math.Log10(duty);
    }
}
