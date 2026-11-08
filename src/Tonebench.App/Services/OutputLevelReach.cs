namespace Tonebench.App.Services;

/// <summary>
/// Whether an output-referred run actually reached the level it was asked for, and what to say when
/// it did not.
///
/// <para>Asked for 2026-10-18: <i>"stop the measurement and show a message when the DUT output power
/// or DUT output voltage cannot be reached within 80% of the set level — there must be a problem
/// then, either the DUT is off or a connection is wrong"</i>. Until then a level probe that ran out
/// of generator simply logged a warning and measured at the ceiling, so a DUT that was off, muted or
/// patched to the wrong socket produced a complete, plausible-looking measurement of the analyzer's
/// own noise floor. The six kinds that carry a <b>Level mode</b> — THD vs frequency, stepped
/// response, frequency response, impulse response, crosstalk and output impedance — all stop now.</para>
///
/// <para><b>Only at the generator's ceiling</b>, which is what makes this safe to apply
/// unconditionally. There the probe has run out of corrections to make and the reading it is judged
/// on was taken at that ceiling, so a shortfall is a fact about the DUT. A probe that merely ran out
/// of iterations is in a different position: its last correction was applied and never measured, so
/// the newest reading is one step stale and would fail runs that were about to be right.</para>
///
/// <para><b>The floor is the other end of the same question</b> and lives here too, as
/// <see cref="FloorMessage"/>: a probe can also run out of generator downwards, when the DUT's gain
/// is high enough that reaching the target would need less drive than the search will produce. That
/// one was four hand-written strings until 2026-11-08 and had already drifted, which is the whole
/// argument for both of them being here.</para>
/// </summary>
public static class OutputLevelReach
{
    /// <summary>How much of the level asked for counts as having reached it.</summary>
    public const double MinFraction = 0.8;

    /// <summary>
    /// How far below the target, in dB, still counts as reaching it — <b>in the target's own
    /// quantity</b>, which is the whole reason this takes the unit.
    ///
    /// <para>80 % of a voltage is 1.94 dB down; 80 % of a power is 0.97 dB, because power goes as
    /// the square. Either way it is comfortably looser than the probes' own 0.5 dB convergence
    /// tolerance and vastly tighter than a DUT that is not there, which comes back tens of dB
    /// short.</para>
    /// </summary>
    public static double ShortfallLimitDb(ThdSweepLevelUnit unit) =>
        -(unit.IsPower() ? 10.0 : 20.0) * Math.Log10(MinFraction);

    /// <summary>Whether <paramref name="measuredDbv"/> is within <see cref="MinFraction"/> of <paramref name="targetDbv"/>, judged in the unit's own quantity.</summary>
    public static bool Reached(double targetDbv, double measuredDbv, ThdSweepLevelUnit unit) =>
        !double.IsNaN(measuredDbv) && measuredDbv >= targetDbv - ShortfallLimitDb(unit);

    /// <summary>
    /// What a run says when it stops for this. It quotes the figures in the unit the level was typed
    /// in — a user who asked for 1 W is told what fraction of a watt came back, not a pair of dBV —
    /// and names the three things that produce it, in the order they are worth checking.
    /// </summary>
    public static string Message(double targetDbv, double measuredDbv, ThdSweepLevelUnit unit, double loadOhms, double generatorCeilingDbv)
    {
        double got = FromDbv(measuredDbv, unit, loadOhms);
        double wanted = FromDbv(targetDbv, unit, loadOhms);
        double percent = wanted > 0.0 ? 100.0 * got / wanted : 0.0;

        return $"The DUT reached only {Format(got)} {unit.Label()} of the {Format(wanted)} {unit.Label()} asked for "
            + $"({percent:0.#} %) with the generator at its {generatorCeilingDbv:0.##} dBV ceiling. "
            + "Check that the DUT is powered on and connected the way the measurement expects, "
            + "and that it can produce this level.";
    }

    /// <summary>
    /// What the generator-driven level mode is called on the response screens, which is one of
    /// <b>two</b> spellings because two panels still carry their own copy of the mode list: the
    /// frequency-response and impulse-response screens label the control <i>Set generator by</i>
    /// and its first option <i>Input voltage</i>, while the THD sweep, stepped response, crosstalk
    /// and output-impedance screens label it <i>Level mode</i> / <i>Generator voltage</i>.
    ///
    /// <para>Constants here rather than a sentence written into each runner, because
    /// <see cref="FloorMessage"/> has to name the control the reader is actually looking at — a
    /// message naming the other screen's spelling sends them hunting for a control that is not
    /// there, which is how the report this was written for was first answered. Move
    /// <c>FrequencyResponseConfigPanel</c>'s and <c>ImpulseResponseConfigPanel</c>'s local
    /// <c>LEVEL_MODES</c> onto <c>measurements/levelMode.ts</c> and these collapse into one.</para>
    /// </summary>
    public const string ResponseGeneratorModeLabel = "Input voltage";

    /// <inheritdoc cref="ResponseGeneratorModeLabel"/>
    public const string SweepGeneratorModeLabel = "Generator voltage";

    /// <summary>
    /// What a run says when the level probe runs out of generator at the <b>bottom</b> rather than
    /// the top: the DUT's gain is high enough that reaching the target would need less drive than
    /// the search will go to.
    ///
    /// <para>The mirror of <see cref="Message"/> and here for the same reason. It was four
    /// hand-written strings, one per probe, and they had drifted: the chirp's and the impulse's
    /// said only that "the DUT's gain looks too high to measure this way", naming no lever, while
    /// the stepped family's said <i>"Check that the DUT is connected and powered"</i> — the
    /// opposite fault, and the wording <see cref="ThdSweepRunner"/>'s copy had already been
    /// corrected away from. Reported 2026-11-08 by a user measuring a tape head preamp who asked
    /// for 5 mV of DUT output and was told his gain was too high after he had turned it down.</para>
    ///
    /// <para><b>The figure that explains it is the gain an output-referred target can still
    /// measure</b>, which is the target minus the floor and so collapses as the target falls:
    /// 60 dB at a 0 dBV target against the chirp's −60 dBV floor, 14 dB at 5 mV, and −6 dB there on
    /// the stepped family's −40 dBV floor (<c>ProbeStartDbv</c> being two-sided, where the chirp
    /// starts at −40 and may go on down to −60). That is why a preamp hits this at all, and why
    /// turning its gain down does not help — the lever is to drive the DUT's input directly.</para>
    /// </summary>
    public static string FloorMessage(
        double targetDbv,
        ThdSweepLevelUnit unit,
        double loadOhms,
        double generatorFloorDbv,
        string generatorModeLabel)
    {
        double wanted = FromDbv(targetDbv, unit, loadOhms);
        double gainLimitDb = targetDbv - generatorFloorDbv;

        // A target below the floor leaves a *negative* limit, i.e. only a DUT that attenuates could
        // reach it. True either way, and worth saying in the direction the reader has to act on.
        string reach = gainLimitDb > 0.0
            ? $"a DUT with less than {gainLimitDb:0.#} dB of gain"
            : $"a DUT that attenuates by more than {-gainLimitDb:0.#} dB";

        return $"The requested output level of {Format(wanted)} {unit.Label()} would need the generator at or below "
            + $"{generatorFloorDbv:0.##} dBV, which is as low as the level search goes — at that target an "
            + $"output-referred level can only measure {reach}. To measure a DUT with more gain, set the level "
            + $"by {generatorModeLabel} instead and enter what you want at the DUT's input; otherwise raise the "
            + "target, or attenuate ahead of the DUT.";
    }

    /// <summary>A level in dBV back in the unit it was asked for — <see cref="ThdSweepLevelUnits.ToDbv"/> inverted.</summary>
    public static double FromDbv(double dbv, ThdSweepLevelUnit unit, double loadOhms)
    {
        double volts = Math.Pow(10.0, dbv / 20.0);
        return unit switch
        {
            ThdSweepLevelUnit.Dbv => dbv,
            ThdSweepLevelUnit.Volt => volts,
            ThdSweepLevelUnit.MilliVolt => volts * 1000.0,
            ThdSweepLevelUnit.Watt => loadOhms > 0.0 ? volts * volts / loadOhms : 0.0,
            ThdSweepLevelUnit.MilliWatt => loadOhms > 0.0 ? 1000.0 * volts * volts / loadOhms : 0.0,
            _ => dbv,
        };
    }

    /// <summary>Three significant figures, so 0.0312 W and 31.2 W both read sensibly without a unit-specific format.</summary>
    private static string Format(double value) =>
        Math.Abs(value) >= 100.0 ? value.ToString("0") : value.ToString("0.###");
}
