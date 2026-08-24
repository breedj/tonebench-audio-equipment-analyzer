namespace Qa403.Dsp;

/// <summary>
/// How much of a captured time-domain window to show on an oscilloscope preview, given the
/// fundamental it was driven at. Ported from the previous app's THD-vs-frequency sweep screen: one
/// period reads as "nothing" once the period gets short enough that a single cycle is only a few
/// pixels wide, so the window widens in steps as frequency rises.
/// </summary>
public static class ScopeDisplayWindow
{
    /// <summary>
    /// Seconds of signal to display for a tone at <paramref name="fundamentalFreqHz"/>. One period
    /// below 5 kHz; the multiplier steps up through 1.5x/2x/4x above that, so the trace never
    /// shrinks to an unreadable sliver as the sweep climbs toward the top of the band.
    /// </summary>
    public static double SecondsFor(double fundamentalFreqHz)
    {
        if (!(fundamentalFreqHz > 0.0))
            return 0.0;

        double period = 1.0 / fundamentalFreqHz;
        double displayTime = period;

        if (period < 0.00005)
            displayTime = period * 4.0;
        else if (period < 0.0001)
            displayTime = period * 2.0;
        else if (period < 0.0002)
            displayTime = period * 1.5;

        return displayTime;
    }
}
