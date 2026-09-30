namespace Qa403.App.Services;

/// <summary>
/// The lowest frequency an analyzer can actually excite, and what to do with a sweep asked to start
/// below it.
///
/// <para><b>Why this exists.</b> A QA403 is DC-coupled and has no floor, so the chirp measurements
/// were written without one — the 2, 5 and 8 Hz starts used for transformer work all reach the DUT.
/// A sound card does not: it is AC-coupled at <i>both</i> ends, so below its corner the stimulus is
/// attenuated on the way out and what returns is attenuated again on the way in. Neither shows up as
/// a failure. The deconvolution divides the measured response by the stimulus it <i>intended</i> to
/// play, so the card's own high-pass appears on the trace as the DUT's roll-off, with normal
/// coherence and nothing to suggest it is not the amplifier.</para>
///
/// <para><b>Clamped rather than refused.</b> A default carried over from a QA403 project, or a type
/// saved on one bench and opened on another, must not become an error the user has to decode — the
/// measurement is still perfectly good over the band the card can drive. So the start is raised, the
/// run proceeds, and the run <i>says</i> it was raised. A DC-coupled front-end is worth noting here
/// too: it fixes the input side only, because the card's own output coupling still limits the
/// stimulus, which is why the floor is a property of the device rather than of the wiring.</para>
/// </summary>
public static class StimulusFloor
{
    /// <summary>
    /// The chirp start this device can actually deliver, and a note when that is not the one asked
    /// for. The message names both frequencies, because "the sweep started higher than you asked"
    /// is only actionable if it says where.
    /// </summary>
    public static (double StartHz, string? Warning) Clamp(double requestedHz, double deviceFloorHz)
    {
        if (!double.IsFinite(deviceFloorHz) || deviceFloorHz <= 0.0 || requestedHz >= deviceFloorHz)
            return (requestedHz, null);

        return (
            deviceFloorHz,
            $"This analyzer cannot produce a usable stimulus below {deviceFloorHz:0.#} Hz — it is AC-coupled at both ends — so the sweep starts there rather than at the {requestedHz:0.#} Hz asked for. Below that corner the card's own high-pass is measured and reported as the DUT's.");
    }

    /// <summary>Joins a floor warning with whatever else a run has to say, so neither is lost.</summary>
    public static string? Join(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return string.IsNullOrWhiteSpace(second) ? null : second;

        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";
    }
}
