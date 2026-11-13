using Tonebench.Data;
using Tonebench.Device.SoundCard;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// The stored drift figure, and when it was taken, as the response screens are offered it.
/// </summary>
/// <param name="Ppm">
/// How far the input's clock runs from the output's — <see cref="ToneFrequency.DriftPpm"/>'s sign
/// convention, negative when a played tone comes home low.
/// </param>
/// <param name="MeasuredUtc">When the drift test that produced it ran. Null on a figure stored without one.</param>
public sealed record ClockDriftAvailable(double Ppm, DateTimeOffset? MeasuredUtc);

/// <summary>
/// Whether a chirp measurement on this bench can be corrected for clock drift, and by how much.
///
/// <para><b>One function because there are four separate ways the answer is no</b>, and spreading
/// them over two Vue panels is how two screens come to disagree about whether a bench has a
/// correction. The response screens ask the device for its ranges and read the answer; nothing in
/// the client decides this.</para>
///
/// <para>It is in the App layer rather than on <see cref="SoundCardConfig"/> because the first of
/// them is about the <i>bench</i> rather than the configuration: a figure measured between two
/// devices means nothing on a bench those two devices are not both in. <see cref="BenchClocks"/> is
/// what answers that, and it is the only thing here that knows what a device is.
/// Guarded by <c>ClockDriftCorrectionTests</c>.</para>
///
/// <para><b>Keyed on a pairing since 2026-11-13</b>, where it used to be a single figure on the
/// sound card's own document behind a <c>kind == SoundCard</c> gate. The quantity is a property of
/// two crystals and neither has to be a card: a QA403 capturing while a USB DAC plays is two clocks
/// exactly as much as two cards are, and that bench was offered no correction at all.</para>
/// </summary>
public static class ClockDriftCorrection
{
    /// <summary>
    /// The drift that applies to the bench as it stands, or null when nothing does.
    ///
    /// <para>Null for each of: a bench with one clock (<see cref="BenchClocks.For"/> carries those
    /// cases); a pairing the drift test has never been run on, which includes one measured between
    /// devices that are no longer both selected — the keys simply do not match, so a figure for
    /// another pairing is not so much refused as not found; and a figure too small to be worth
    /// correcting, which is the <i>same</i> threshold <see cref="ChirpClockDrift.For"/> refuses at —
    /// read from there rather than restated, so a bench cannot be offered a correction that the
    /// estimator then declines to make.</para>
    /// </summary>
    public static ClockDriftAvailable? ForBench(BenchClocks? clocks, ClockDriftConfig? drift)
    {
        if (clocks is null) return null;
        if (drift?.For(clocks.OutputKey, clocks.InputKey) is not { } entry) return null;
        if (!double.IsFinite(entry.Ppm) || Math.Abs(entry.Ppm) < ChirpClockDrift.NegligiblePpm) return null;

        return new ClockDriftAvailable(entry.Ppm, entry.MeasuredUtc == default ? null : entry.MeasuredUtc);
    }

    /// <summary>
    /// Whether the two sides are two physical interfaces, which is the whole premise: the quantity
    /// is the <i>difference</i> between two crystals, and one card has one. A card used both ways
    /// reads ~0 ppm however it is driven — a resampler on it converts between the app's rate and
    /// the device's on the same clock in both directions, so nothing is time-scaled — which is why
    /// this check and the threshold above back each other up rather than duplicating.
    ///
    /// <para><b>It compares cards, not device names, and that distinction is the point.</b> One
    /// interface shows up as several endpoints with different names — <c>In 3-4 (MOTU M Series)</c>
    /// against <c>Out 3-4 (MOTU M Series)</c> — so comparing the stored names would call a single
    /// card two devices and offer a correction where no drift can exist.
    /// <see cref="SoundCardCatalog.CardOf"/> is the same rule the picker groups its card list by,
    /// and it is a pure string function, so this costs no PortAudio enumeration on a status
    /// read.</para>
    ///
    /// <para><b>Where the stem rule cannot tell, it errs towards "two cards"</b>, which is the safe
    /// direction. A WDM-KS name repeats its own endpoint (<c>In 1-2 (In 1-2)</c>) and so names no
    /// interface; this then falls back to the endpoint name, which differs between the two sides,
    /// and the pair reads as two cards. The cost is at most offering a correction on a bench that
    /// is really one card — where the measured figure is ~0 and the threshold refuses it anyway.
    /// The opposite mistake would be the expensive one: calling two cards one, and withholding a
    /// correction the bench genuinely needs.</para>
    /// </summary>
    public static bool UsesTwoCards(SoundCardConfig config) =>
        !string.Equals(config.InputHostApi, config.OutputHostApi, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(
            SoundCardCatalog.CardOf(config.InputDeviceName),
            SoundCardCatalog.CardOf(config.OutputDeviceName),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a run asking to be corrected should be corrected by: the figure above when one applies
    /// and the run asked for it, and 0 — which <see cref="ChirpClockDrift.For"/> reads as no
    /// correction — otherwise.
    ///
    /// <para>The <i>request</i> carries the decision and the server supplies the figure, which is
    /// the one ordering under which a measurement cannot be corrected by a stale number: a browser
    /// that has been open since before the drift test would otherwise send whatever it was told at
    /// mount. What the run actually used is then recorded with the result, so a corrected
    /// measurement can always say which ε corrected it.</para>
    /// </summary>
    public static double Resolve(bool requested, BenchClocks? clocks, ClockDriftConfig? drift) =>
        requested ? ForBench(clocks, drift)?.Ppm ?? 0.0 : 0.0;
}
