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
