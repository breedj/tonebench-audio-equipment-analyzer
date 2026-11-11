using Tonebench.Data;
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
/// the four is about the analyzer rather than the configuration: the stored figure names whichever
/// sound card was last selected, and a <see cref="DeviceKind.Hardware"/> or
/// <see cref="DeviceKind.Simulated"/> session leaves that naming in place — so a QA403, which has
/// one clock, would be offered a correction measured between two cards that are not in the path.
/// Guarded by <c>ClockDriftCorrectionTests</c>.</para>
/// </summary>
public static class ClockDriftCorrection
{
    /// <summary>
    /// The drift that applies to the bench as it stands, or null when nothing does.
    ///
    /// <para>Null for each of: an analyzer that is not a sound card (one clock, so the quantity
    /// does not exist); a bench that has never run the drift test; a figure measured between a
    /// different pair of devices from the one now selected; and a figure too small to be worth
    /// correcting, which is the <i>same</i> threshold <see cref="ChirpClockDrift.For"/> refuses at
    /// — read from there rather than restated, so a bench cannot be offered a correction that the
    /// estimator then declines to make.</para>
    /// </summary>
    public static ClockDriftAvailable? ForBench(DeviceKind kind, SoundCardConfig? config)
    {
        if (kind != DeviceKind.SoundCard) return null;
        if (config?.ClockDriftPpm is not { } ppm) return null;
        if (!config.ClockDriftMatchesPairing) return null;
        if (!double.IsFinite(ppm) || Math.Abs(ppm) < ChirpClockDrift.NegligiblePpm) return null;

        return new ClockDriftAvailable(ppm, config.ClockDriftMeasuredUtc);
    }

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
    public static double Resolve(bool requested, DeviceKind kind, SoundCardConfig? config) =>
        requested ? ForBench(kind, config)?.Ppm ?? 0.0 : 0.0;
}
