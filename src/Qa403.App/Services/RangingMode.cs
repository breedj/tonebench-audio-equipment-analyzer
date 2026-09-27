namespace Qa403.App.Services;

/// <summary>
/// Who picks the two analog ranges — the input attenuator and the output attenuator.
///
/// <para>One mode for both on purpose. They are the two ends of the same analog path and the
/// reasons to pin them are the same reason: reproducing another analyzer's reading, or holding an
/// operating point still while something else is varied. A screen with an "auto input / manual
/// output" combination would be four states to explain and two of them are traps.</para>
///
/// <para><b>Not persisted, and not part of any measurement's <c>params_json</c>.</b> It is a bench
/// override, not a property of a measurement: an app that started in <see cref="Manual"/> with
/// yesterday's ranges would clip or under-range the first thing plugged into it, silently. Every
/// launch starts on <see cref="Auto"/>. The ranges a result was *taken* at are recorded as always
/// (<c>input_range_dbv</c> / <c>output_range_dbv</c>), which is the part a stored result needs.</para>
/// </summary>
public enum RangingMode
{
    /// <summary>
    /// The default. The input range tracks the signal — the device's own ranger on a QA403, the
    /// browser's peak monitor on the simulator — and the output range is derived from the generator
    /// amplitude by <see cref="Qa403.Device.OutputRangeSelector"/> on every generator change.
    /// </summary>
    Auto,

    /// <summary>
    /// Both ranges are whatever the user last set and nothing moves them: device autoranging is
    /// switched off and <c>ConfigureGeneratorAsync</c> stops deriving the output range.
    ///
    /// <para>Which is why a measurement run is refused while this is selected (see
    /// <c>MeasurementRunGuard</c>). Every runner ranges for itself — a probe from the widest range,
    /// a prediction from the target, a per-point ratchet — and a sweep that steps the generator
    /// across a pinned output range clips every point above it with nothing on screen saying so.
    /// </para>
    ///
    /// <para><b>The refusal is about the output range, not the input one.</b> Every runner already
    /// stands its own ranging down when <see cref="Qa403.Device.IAnalyzerDevice.AutoRangeEnabled"/>
    /// is false ("manual range control means manual: the user picked this deliberately"), and those
    /// branches stay tested. So if the output half is ever made safe for a run — deriving it for the
    /// duration, say, since the runner owns the stimulus — the input half needs no work and this
    /// refusal can be narrowed to the output range alone.</para>
    /// </summary>
    Manual,
}
